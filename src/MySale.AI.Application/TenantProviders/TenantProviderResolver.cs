using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Application.TenantProviders;

/// <summary>
/// Chat integration: when the signed-in MySaleBooks company has explicitly chosen one of its own providers for AYAAN
/// chat, the request uses it (with the company's key, decrypted here on the server only). In every other case it
/// returns null and the existing provider selection runs unchanged.
/// </summary>
public sealed class TenantProviderResolver : ITenantProviderResolver
{
    private readonly IUserContext _user;
    private readonly ITenantProviderRepository _repository;
    private readonly ITenantSecretProtector _protector;
    private readonly ITenantProviderAdapterFactory _adapters;
    private readonly TenantAiProviderOptions _options;
    private readonly ILogger<TenantProviderResolver> _logger;

    public TenantProviderResolver(IUserContext user, ITenantProviderRepository repository, ITenantSecretProtector protector,
        ITenantProviderAdapterFactory adapters, TenantAiProviderOptions options, ILogger<TenantProviderResolver> logger)
    {
        _user = user;
        _repository = repository;
        _protector = protector;
        _adapters = adapters;
        _options = options;
        _logger = logger;
    }

    /// <summary>Prefix of the provider id recorded on chat messages / logs for company providers.</summary>
    public const string IdPrefix = "tenant:";

    public async Task<ResolvedProvider?> ResolveAsync(string? model, AppSettings settings, CancellationToken ct)
    {
        if (!_options.Enabled || !_user.IsAuthenticated || !_user.IsMySaleBooksUser
            || string.IsNullOrEmpty(_user.DatabaseName) || string.IsNullOrEmpty(_user.CompanyId))
            return null;

        var tenantKey = _user.CompanyId;
        TenantProviderConfig? config;
        try
        {
            config = await _repository.GetDefaultAsync(tenantKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Company AI provider lookup failed ({ErrorType}); the AYAAN default provider is used", ex.GetType().Name);
            return null;
        }
        // Nothing chosen, removed or disabled by the company → existing behaviour (AYAAN default provider).
        if (config is null || !config.IsDefault || !config.Enabled || config.RemovedAt is not null
            || !string.Equals(config.TenantKey, tenantKey, StringComparison.Ordinal))
            return null;

        var descriptor = ProviderRegistry.Find(config.ProviderId);
        if (descriptor is null)
            return Fail(config, "Your company's AI provider is no longer supported. Choose another one in AI Settings → AI Providers & API Keys.");

        string? key = null;
        if (config.SecretConfigured && !string.IsNullOrEmpty(config.SecretId))
        {
            var secret = await _repository.GetSecretAsync(tenantKey, config.Id, config.SecretId, ct);
            key = secret is null ? null : _protector.Unprotect(tenantKey, config.Id, secret.Ciphertext);
        }
        if (descriptor.KeyRequired && key is null)
            return Fail(config, "Your company's AI provider key cannot be used. Enter it again in AI Settings → AI Providers & API Keys.");
        if (string.IsNullOrWhiteSpace(config.DefaultModel))
            return Fail(config, "No model is selected for your company's AI provider. Choose one in AI Settings → AI Providers & API Keys.");

        // The model of a company provider is the one the company chose (a model name of the AYAAN providers would not exist there).
        _ = model;
        var effectiveModel = config.DefaultModel;
        var synthetic = new ProviderConfig
        {
            Id = IdPrefix + config.Id,
            Name = config.DisplayName,
            Kind = descriptor.Kind,
            Category = ProviderCategory.Cloud,
            BaseUrl = config.BaseUrl,
            DefaultModel = effectiveModel,
            Temperature = settings.Ai.Temperature,
            MaxTokens = Math.Max(settings.Ai.MaxTokens, 1500),
            TimeoutSeconds = Math.Clamp(settings.Ai.TimeoutSeconds, 10, 600),
            Enabled = true,
            // No ciphertext on the shared shape: the key never leaves this resolver / the adapter instance.
            ApiKeyEncrypted = null,
            ApiKeyHint = null,
            InputCostPer1M = 0,
            OutputCostPer1M = 0,
            LastTestStatus = config.Status == TenantProviderStatus.Connected ? ProviderStatus.Connected : ProviderStatus.Unknown
        };

        IAIProvider inner;
        try
        {
            inner = _adapters.Create(new TenantProviderConnection
            {
                Descriptor = descriptor,
                DisplayName = config.DisplayName,
                BaseUrl = config.BaseUrl,
                Settings = config.Settings,
                ApiKey = key,
                Model = effectiveModel,
                TimeoutSeconds = synthetic.TimeoutSeconds
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Company AI provider {ProviderId} could not be prepared ({ErrorType})", descriptor.Id, ex.GetType().Name);
            return Fail(config, "Your company's AI provider settings are not valid. Check them in AI Settings → AI Providers & API Keys.");
        }

        var info = new ProviderKindInfo
        {
            Kind = descriptor.Kind,
            DisplayName = $"{descriptor.DisplayName} (company key)",
            Category = ProviderCategory.Cloud,
            DefaultBaseUrl = descriptor.DefaultBaseUrl,
            RequiresApiKey = descriptor.KeyRequired,
            SupportsModelListing = descriptor.SupportsModelListing,
            Description = descriptor.Description
        };
        var tracked = new TenantTrackedProvider(inner, _repository, tenantKey, config.Id, config.Status, key, _logger);
        return new ResolvedProvider(synthetic, tracked, effectiveModel, info);
    }

    private ResolvedProvider? Fail(TenantProviderConfig config, string message)
    {
        if (config.AllowSystemFallback)
        {
            _logger.LogWarning("Company AI provider {ConfigId} unusable; the company allowed the AYAAN default as fallback", config.Id);
            return null;
        }
        throw new AIProviderException(message);
    }
}

/// <summary>
/// Decorator around a company provider: keeps the configuration's status current (Error with a sanitised message when a
/// request fails — invalid key, quota, outage; Connected again after a success). Never retries elsewhere.
/// </summary>
internal sealed class TenantTrackedProvider : IAIProvider
{
    private readonly IAIProvider _inner;
    private readonly ITenantProviderRepository _repository;
    private readonly string _tenantKey;
    private readonly string _configId;
    private readonly string? _key;
    private readonly ILogger _logger;
    private TenantProviderStatus _status;

    public TenantTrackedProvider(IAIProvider inner, ITenantProviderRepository repository, string tenantKey, string configId,
        TenantProviderStatus status, string? key, ILogger logger)
    {
        _inner = inner;
        _repository = repository;
        _tenantKey = tenantKey;
        _configId = configId;
        _status = status;
        _key = key;
        _logger = logger;
    }

    public string Kind => _inner.Kind;

    public async Task<AIQueryResponse> GenerateQueryAsync(AIChatRequest request, CancellationToken ct)
    {
        try
        {
            var r = await _inner.GenerateQueryAsync(request, ct);
            await RecordAsync(null);
            return r;
        }
        catch (AIProviderException ex)
        {
            await RecordAsync(ex.Message);
            throw Sanitized(ex);
        }
    }

    public async Task<AIResponse> GenerateResponseAsync(AIChatRequest request, CancellationToken ct)
    {
        try
        {
            var r = await _inner.GenerateResponseAsync(request, ct);
            await RecordAsync(null);
            return r;
        }
        catch (AIProviderException ex)
        {
            await RecordAsync(ex.Message);
            throw Sanitized(ex);
        }
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamResponseAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var e = _inner.StreamResponseAsync(request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            AIStreamChunk chunk;
            try
            {
                if (!await e.MoveNextAsync()) break;
                chunk = e.Current;
            }
            catch (AIProviderException ex)
            {
                await RecordAsync(ex.Message);
                throw Sanitized(ex);
            }
            yield return chunk;
        }
        await RecordAsync(null);
    }

    public Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct) => _inner.TestConnectionAsync(ct);
    public Task<IReadOnlyList<AIModel>> GetModelsAsync(CancellationToken ct) => _inner.GetModelsAsync(ct);

    private AIProviderException Sanitized(AIProviderException ex)
    {
        var safe = SecretRedactor.Redact(ex.Message, _key);
        return safe == ex.Message ? ex : new AIProviderException(safe);
    }

    private async Task RecordAsync(string? error)
    {
        var next = error is null ? TenantProviderStatus.Connected : TenantProviderStatus.Error;
        if (next == _status && error is null) return; // nothing changed: no write per request
        _status = next;
        try
        {
            await _repository.UpdateStatusAsync(_tenantKey, _configId, next, error is null ? null : SecretRedactor.Redact(error, _key),
                null, null, DateTime.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Company AI provider status could not be updated ({ErrorType})", ex.GetType().Name);
        }
    }
}
