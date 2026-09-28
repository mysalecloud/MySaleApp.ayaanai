using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Contracts;
using MySale.AI.Domain;

namespace MySale.AI.Application.Services;

public sealed record ResolvedProvider(ProviderConfig Config, IAIProvider Provider, string Model, ProviderKindInfo? KindInfo)
{
    public bool IsLocal => Config.Category == ProviderCategory.Local;

    public decimal EstimateCost(int inputTokens, int outputTokens)
        => IsLocal ? 0m : (inputTokens * Config.InputCostPer1M + outputTokens * Config.OutputCostPer1M) / 1_000_000m;
}

public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

public sealed class AppValidationException : Exception
{
    public AppValidationException(string message) : base(message) { }
}

/// <summary>
/// A chat request that cannot run in its conversation: unknown / foreign conversation (404, ConversationNotFound),
/// a previous message still being answered (409, ConversationBusy) or a message sent twice (409, DuplicateMessage).
/// The message is safe to show to the customer; the code lets the client react (e.g. start a new conversation).
/// </summary>
public sealed class ChatConversationException : Exception
{
    public const string NotFound = "ConversationNotFound";
    public const string Busy = "ConversationBusy";
    public const string Duplicate = "DuplicateMessage";

    public ChatConversationException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }

    public static ChatConversationException ConversationNotFound() => new(404, NotFound,
        "This conversation is no longer available. Please ask your question again — it will start a new conversation.");
    public static ChatConversationException ConversationBusy() => new(409, Busy,
        "I'm still answering your previous message in this conversation. Please wait for it to finish.");
    public static ChatConversationException DuplicateMessage() => new(409, Duplicate,
        "This message was already sent and is being answered.");
}

public sealed class ProviderService
{
    private readonly IProviderRepository _repository;
    private readonly IAIProviderFactory _factory;
    private readonly ISecretProtector _secrets;
    private readonly AuditService _audit;

    private readonly IProviderOverrides? _overrides;

    public ProviderService(IProviderRepository repository, IAIProviderFactory factory, ISecretProtector secrets, AuditService audit,
        IProviderOverrides? overrides = null)
    {
        _repository = repository;
        _factory = factory;
        _secrets = secrets;
        _audit = audit;
        _overrides = overrides;
    }

    public IReadOnlyList<ProviderKindInfo> Kinds => _factory.Kinds;

    public async Task<List<ProviderDto>> ListAsync(CancellationToken ct)
    {
        var list = await _repository.ListAsync(ct);
        return list.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).Select(ToDto).ToList();
    }

    public async Task<ProviderDto> GetAsync(string id, CancellationToken ct)
        => ToDto(await Load(id, ct));

    public async Task<ProviderDto> CreateAsync(ProviderUpsertRequest request, CancellationToken ct)
    {
        EnsureKind(request.Kind);
        var config = new ProviderConfig();
        Apply(config, request);
        await _repository.InsertAsync(config, ct);
        if (config.IsDefault) await _repository.ClearDefaultAsync(config.Id, ct);
        await _audit.LogAsync("ProviderCreated", $"{config.Name} ({config.Kind})", ct);
        return ToDto(config);
    }

    public async Task<ProviderDto> UpdateAsync(string id, ProviderUpsertRequest request, CancellationToken ct)
    {
        EnsureKind(request.Kind);
        var config = await Load(id, ct);
        Apply(config, request);
        config.UpdatedAt = DateTime.UtcNow;
        config.LastTestStatus = ProviderStatus.Unknown;
        await _repository.UpdateAsync(config, ct);
        if (config.IsDefault) await _repository.ClearDefaultAsync(config.Id, ct);
        await _audit.LogAsync("ProviderUpdated", $"{config.Name} ({config.Kind}){(request.ApiKey is { Length: > 0 } ? " — API key changed" : string.Empty)}", ct);
        return ToDto(config);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var config = await Load(id, ct);
        await _repository.DeleteAsync(id, ct);
        await _audit.LogAsync("ProviderDeleted", config.Name, ct);
    }

    public async Task<ProviderDto> SetDefaultAsync(string id, CancellationToken ct)
    {
        var config = await Load(id, ct);
        config.IsDefault = true;
        config.Enabled = true;
        config.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(config, ct);
        await _repository.ClearDefaultAsync(id, ct);
        await _audit.LogAsync("ProviderUpdated", $"{config.Name} set as default", ct);
        return ToDto(config);
    }

    public async Task<ProviderTestResult> TestAsync(string id, CancellationToken ct)
    {
        var config = await Load(id, ct);
        ProviderTestResult result;
        if (RequiresKey(config) && string.IsNullOrEmpty(config.ApiKeyEncrypted))
        {
            result = new ProviderTestResult { Success = false, Message = "API key is not configured." };
            await _repository.UpdateTestStatusAsync(id, ProviderStatus.NotConfigured, result.Message, DateTime.UtcNow, ct);
            return result;
        }
        result = await SafeTest(_factory.Create(config), ct);
        await _repository.UpdateTestStatusAsync(id, result.Success ? ProviderStatus.Connected : ProviderStatus.Disconnected, result.Message, DateTime.UtcNow, ct);
        return result;
    }

    /// <summary>Live models from the provider merged with the built-in catalog of well-known models.</summary>
    public async Task<IReadOnlyList<AIModel>> GetModelsAsync(string id, CancellationToken ct)
    {
        var config = await Load(id, ct);
        IReadOnlyList<AIModel>? live = null;
        try
        {
            if (!(RequiresKey(config) && string.IsNullOrEmpty(config.ApiKeyEncrypted)))
                live = await _factory.Create(config).GetModelsAsync(ct);
        }
        catch (AIProviderException) when (ModelCatalog.For(config.Kind, config.BaseUrl).Count > 0)
        {
            // Provider unreachable: still show the catalog so a model can be chosen.
        }
        return ModelCatalog.Merge(config.Kind, config.BaseUrl, live);
    }

    /// <summary>Catalog suggestions for a provider type / URL (used by the editor before a connection test).</summary>
    public IReadOnlyList<AIModel> GetCatalog(string kind, string? baseUrl) => ModelCatalog.Merge(kind, baseUrl, null);

    /// <summary>Downloads a model on providers that support it (Ollama `pull`).</summary>
    public async Task<string> PullModelAsync(string id, string model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(ch => char.IsWhiteSpace(ch)))
            throw new AppValidationException("Invalid model name.");
        var config = await Load(id, ct);
        if (_factory.Create(config) is not IModelInstaller installer)
            throw new AppValidationException($"'{config.Name}' does not support downloading models. Use the provider's own console.");
        var result = await installer.PullModelAsync(model.Trim(), ct);
        await _audit.LogAsync("ModelPulled", $"{config.Name}: {model}", ct);
        return result;
    }

    /// <summary>Tests an unsaved configuration (e.g. a custom Ollama URL typed on the Providers page).</summary>
    public async Task<ProviderDraftTestResponse> TestDraftAsync(ProviderDraftTestRequest request, CancellationToken ct)
    {
        EnsureKind(request.Config.Kind);
        var draft = new ProviderConfig();
        ProviderConfig? existing = null;
        if (!string.IsNullOrEmpty(request.ExistingId))
            existing = await _repository.GetAsync(request.ExistingId, ct);
        if (existing is not null)
        {
            draft.ApiKeyEncrypted = existing.ApiKeyEncrypted;
            draft.ApiKeyHint = existing.ApiKeyHint;
        }
        Apply(draft, request.Config);

        var provider = _factory.Create(draft);
        var test = await SafeTest(provider, ct);
        IReadOnlyList<AIModel> models = Array.Empty<AIModel>();
        if (test.Success)
        {
            try { models = await provider.GetModelsAsync(ct); }
            catch (AIProviderException) { /* listing is optional */ }
        }
        return new ProviderDraftTestResponse(test, ModelCatalog.Merge(draft.Kind, draft.BaseUrl, test.Success ? models : null));
    }

    /// <summary>Selects the provider + model for a chat turn: explicit request → settings default → provider default → first enabled.</summary>
    public async Task<ResolvedProvider> ResolveAsync(string? providerId, string? model, AppSettings settings, CancellationToken ct)
    {
        var all = await LoadWithOverridesAsync(ct);
        ProviderConfig? config;
        var deploymentDefault = string.IsNullOrWhiteSpace(_overrides?.DefaultProviderName)
            ? null
            : all.FirstOrDefault(p => string.Equals(p.Name, _overrides!.DefaultProviderName!.Replace('_', ' ').Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            config = all.FirstOrDefault(p => p.Id == providerId)
                     ?? throw new AIProviderException("The selected AI provider no longer exists.");
            if (!config.Enabled) throw new AIProviderException($"AI provider '{config.Name}' is disabled.");
        }
        else if (deploymentDefault is not null)
        {
            // Deployment configuration wins over the dashboard default (e.g. Cloud Run uses OpenAI, local uses Ollama).
            config = deploymentDefault;
            if (string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(_overrides!.DefaultModel))
                model = _overrides.DefaultModel;
        }
        else
        {
            config = all.FirstOrDefault(p => p.Enabled && p.Id == settings.Ai.DefaultProviderId)
                     ?? all.FirstOrDefault(p => p.Enabled && p.IsDefault)
                     ?? all.FirstOrDefault(p => p.Enabled)
                     ?? throw new AIProviderException("No AI provider is configured. Add one on the AI Providers page.");
        }

        if (!_factory.Supports(config.Kind))
            throw new AIProviderException($"Provider type '{config.Kind}' is not supported by this server.");
        if (RequiresKey(config) && string.IsNullOrEmpty(config.ApiKeyEncrypted))
            throw new AIProviderException($"AI provider '{config.Name}' has no API key configured.");

        var effectiveModel = !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : config.Id == settings.Ai.DefaultProviderId && !string.IsNullOrWhiteSpace(settings.Ai.DefaultModel)
                ? settings.Ai.DefaultModel!
                : config.DefaultModel;
        if (string.IsNullOrWhiteSpace(effectiveModel))
            throw new AIProviderException($"No model is selected for '{config.Name}'.");

        var kind = _factory.Kinds.FirstOrDefault(k => k.Kind == config.Kind);
        return new ResolvedProvider(config, _factory.Create(config), effectiveModel, kind);
    }

    /// <summary>
    /// Provider used for speech-to-text: the configured one, else the first enabled OpenAI provider with a key.
    /// Speech uses the provider's base URL and API key, independently of the chat model.
    /// </summary>
    public async Task<ProviderConfig?> ResolveSpeechProviderAsync(string? providerId, CancellationToken ct)
    {
        var all = await LoadWithOverridesAsync(ct);
        bool Usable(ProviderConfig p) => p.Kind is "openai" or "openai-compatible"
                                         && (!RequiresKey(p) || !string.IsNullOrEmpty(p.ApiKeyEncrypted));
        if (!string.IsNullOrWhiteSpace(providerId))
            return all.FirstOrDefault(p => p.Id == providerId && Usable(p));
        return all.FirstOrDefault(p => p.Enabled && p.Kind == "openai" && Usable(p));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Provider list for runtime use, with deployment overrides applied in memory only (never persisted).
    /// An API key from the environment is protected with this server's key ring so the factory can use it.
    /// </summary>
    private async Task<List<ProviderConfig>> LoadWithOverridesAsync(CancellationToken ct)
    {
        var all = await _repository.ListAsync(ct);
        if (_overrides is null) return all;
        foreach (var p in all)
        {
            var key = _overrides.ApiKeyFor(p.Name);
            if (!string.IsNullOrWhiteSpace(key)) p.ApiKeyEncrypted = _secrets.Protect(key.Trim());
            var url = _overrides.BaseUrlFor(p.Name);
            if (!string.IsNullOrWhiteSpace(url)) p.BaseUrl = url.Trim().TrimEnd('/');
            var m = _overrides.ModelFor(p.Name);
            if (!string.IsNullOrWhiteSpace(m)) p.DefaultModel = m.Trim();
        }
        return all;
    }

    private async Task<ProviderConfig> Load(string id, CancellationToken ct)
        => await _repository.GetAsync(id, ct) ?? throw new NotFoundException("AI provider not found.");

    private void EnsureKind(string kind)
    {
        if (!_factory.Supports(kind))
            throw new AppValidationException($"Unsupported provider type '{kind}'. Supported: {string.Join(", ", _factory.Kinds.Select(k => k.Kind))}.");
    }

    private bool RequiresKey(ProviderConfig config)
        => _factory.Kinds.FirstOrDefault(k => k.Kind == config.Kind)?.RequiresApiKey ?? false;

    private void Apply(ProviderConfig config, ProviderUpsertRequest r)
    {
        var info = _factory.Kinds.FirstOrDefault(k => k.Kind == r.Kind);
        config.Name = r.Name.Trim();
        config.Kind = r.Kind;
        config.Category = info?.Category ?? ProviderCategory.Cloud;
        config.BaseUrl = r.BaseUrl.Trim().TrimEnd('/');
        config.DefaultModel = r.DefaultModel?.Trim() ?? string.Empty;
        config.Temperature = r.Temperature;
        config.MaxTokens = r.MaxTokens;
        config.TimeoutSeconds = r.TimeoutSeconds;
        config.Enabled = r.Enabled;
        config.IsDefault = r.IsDefault;
        config.InputCostPer1M = config.Category == ProviderCategory.Local ? 0 : r.InputCostPer1M;
        config.OutputCostPer1M = config.Category == ProviderCategory.Local ? 0 : r.OutputCostPer1M;

        if (r.ClearApiKey)
        {
            config.ApiKeyEncrypted = null;
            config.ApiKeyHint = null;
        }
        else if (!string.IsNullOrWhiteSpace(r.ApiKey))
        {
            var key = r.ApiKey.Trim();
            config.ApiKeyEncrypted = _secrets.Protect(key);
            config.ApiKeyHint = key.Length > 8 ? "…" + key[^4..] : "set";
        }
    }

    private static async Task<ProviderTestResult> SafeTest(IAIProvider provider, CancellationToken ct)
    {
        try
        {
            return await provider.TestConnectionAsync(ct);
        }
        catch (AIProviderException ex)
        {
            return new ProviderTestResult { Success = false, Message = ex.Message };
        }
    }

    private ProviderDto ToDto(ProviderConfig c)
    {
        var info = _factory.Kinds.FirstOrDefault(k => k.Kind == c.Kind);
        var requiresKey = info?.RequiresApiKey ?? false;
        var status = !c.Enabled ? ProviderStatus.Disabled
            : requiresKey && string.IsNullOrEmpty(c.ApiKeyEncrypted) ? ProviderStatus.NotConfigured
            : c.LastTestStatus;
        return new ProviderDto
        {
            Id = c.Id,
            Name = c.Name,
            Kind = c.Kind,
            KindDisplayName = info?.DisplayName ?? c.Kind,
            Category = c.Category,
            BaseUrl = c.BaseUrl,
            HasApiKey = !string.IsNullOrEmpty(c.ApiKeyEncrypted),
            ApiKeyHint = c.ApiKeyHint,
            RequiresApiKey = requiresKey,
            DefaultModel = c.DefaultModel,
            Temperature = c.Temperature,
            MaxTokens = c.MaxTokens,
            TimeoutSeconds = c.TimeoutSeconds,
            Enabled = c.Enabled,
            IsDefault = c.IsDefault,
            InputCostPer1M = c.InputCostPer1M,
            OutputCostPer1M = c.OutputCostPer1M,
            Status = status,
            LastTestedAt = c.LastTestedAt,
            LastTestMessage = c.LastTestMessage,
            Supported = info is not null
        };
    }
}
