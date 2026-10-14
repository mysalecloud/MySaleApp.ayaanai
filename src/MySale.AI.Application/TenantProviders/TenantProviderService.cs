using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Application.TenantProviders;

/// <summary>
/// AI Settings → AI Providers &amp; API Keys: a company manages its own AI provider credentials.
/// Every operation is scoped to <see cref="TenantProviderActor.TenantKey"/> (from the validated MySaleBooks JWT) and
/// changes require a managing role. Credentials are write-only: encrypted at rest, decrypted only in memory to call
/// the provider, never returned, logged or audited. Existing global providers (operator console) are not touched.
/// </summary>
public sealed class TenantProviderService
{
    public const string AuditResource = "AIProviderKeys";

    private readonly ITenantProviderRepository _repository;
    private readonly ITenantSecretProtector _protector;
    private readonly ITenantProviderAdapterFactory _adapters;
    private readonly TenantAiProviderOptions _options;
    private readonly IAuditLogRepository _audit;
    private readonly ILogger<TenantProviderService> _logger;

    public TenantProviderService(ITenantProviderRepository repository, ITenantSecretProtector protector, ITenantProviderAdapterFactory adapters,
        TenantAiProviderOptions options, IAuditLogRepository audit, ILogger<TenantProviderService> logger)
    {
        _repository = repository;
        _protector = protector;
        _adapters = adapters;
        _options = options;
        _audit = audit;
        _logger = logger;
    }

    public TenantAiProviderOptions Options => _options;

    public bool CanManage(TenantProviderActor actor)
        => _options.Enabled && !string.IsNullOrWhiteSpace(actor.AyaanRole)
           && _options.ManageRoles.Contains(actor.AyaanRole, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ read

    public async Task<TenantProvidersOverviewDto> OverviewAsync(TenantProviderActor actor, CancellationToken ct)
    {
        EnsureTenant(actor);
        var items = _options.Enabled ? await _repository.ListAsync(actor.TenantKey, ct) : new List<TenantProviderConfig>();
        var active = items.FirstOrDefault(c => c.IsDefault && c.Enabled && c.RemovedAt is null);
        return new TenantProvidersOverviewDto
        {
            Enabled = _options.Enabled,
            CanManage = CanManage(actor),
            ActiveSource = active is null ? "system" : "company",
            ActiveConfigurationId = active?.Id,
            ActiveModel = active?.DefaultModel,
            Items = items.Where(c => c.RemovedAt is null)
                .OrderByDescending(c => c.IsDefault).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(c => ToDto(c)).ToList(),
            Catalog = Catalog()
        };
    }

    public IReadOnlyList<ProviderDescriptorDto> Catalog()
        => ProviderRegistry.All
            .Where(d => _options.AllowedProviders.Count == 0 || _options.AllowedProviders.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
            .Select(d => ToDto(d)).ToList();

    // ------------------------------------------------------------------ add / update / remove

    public async Task<TenantProviderSaveResultDto> CreateAsync(TenantProviderActor actor, TenantProviderUpsertRequest request, CancellationToken ct)
    {
        EnsureManage(actor);
        var n = TenantProviderValidation.Normalize(request, _options);
        if (n.Descriptor.KeyRequired && n.ApiKey is null)
            throw new AppValidationException($"Enter the {n.Descriptor.KeyLabel}.");

        var existing = await _repository.ListAsync(actor.TenantKey, ct);
        if (existing.Count(c => c.RemovedAt is null) >= Math.Max(1, _options.MaxConfigurationsPerCompany))
            throw new AppValidationException("The maximum number of AI provider configurations has been reached. Remove one first.");

        TenantProviderTestResultDto? test = null;
        if (!request.SaveWithoutTest)
        {
            test = await RunTestAsync(n.Descriptor, n.DisplayName, n.BaseUrl, n.Settings, n.ApiKey, n.DefaultModel, ct);
            if (!test.Success)
            {
                await AuditAsync(actor, "AIProviderKeyTestFailed", null, $"{n.DisplayName} ({n.Descriptor.Id}) — not saved", "Failure", ct);
                return new TenantProviderSaveResultDto { Saved = false, Test = test };
            }
        }

        var now = DateTime.UtcNow;
        var config = new TenantProviderConfig
        {
            Id = _repository.NewId(),
            TenantKey = actor.TenantKey,
            TenantRef = actor.TenantRef,
            ProviderId = n.Descriptor.Id,
            DisplayName = n.DisplayName,
            BaseUrl = n.BaseUrl,
            Settings = n.Settings,
            DefaultModel = n.DefaultModel,
            Enabled = n.Enabled,
            IsDefault = false, // never changes what chat uses until the user explicitly chooses it
            AllowSystemFallback = n.AllowSystemFallback,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = actor.Label,
            UpdatedBy = actor.Label,
            Version = 1
        };
        ApplyTest(config, test, now);

        TenantProviderSecret? secret = null;
        if (n.ApiKey is not null)
        {
            secret = NewSecret(actor.TenantKey, config.Id, n.ApiKey);
            await _repository.InsertSecretAsync(secret, ct);
            config.SecretId = secret.Id;
            config.SecretConfigured = true;
            config.SecretUpdatedAt = now;
        }
        try
        {
            await _repository.InsertAsync(config, ct);
        }
        catch
        {
            if (secret is not null) await SafeDeleteSecretsAsync(actor.TenantKey, config.Id, null);
            throw;
        }

        await AuditAsync(actor, "AIProviderKeyAdded", config.Id,
            $"{config.DisplayName} ({config.ProviderId}){(secret is null ? string.Empty : " — credential configured")}{(test is null ? " — saved without test" : " — connection verified")}",
            "Success", ct);
        return new TenantProviderSaveResultDto { Saved = true, Item = ToDto(config), Test = test };
    }

    public async Task<TenantProviderSaveResultDto> UpdateAsync(TenantProviderActor actor, string id, TenantProviderUpsertRequest request, CancellationToken ct)
    {
        EnsureManage(actor);
        var current = await LoadAsync(actor, id, ct);
        if (request.Version is { } v && v != current.Version) throw new TenantProviderConflictException();
        if (!string.IsNullOrWhiteSpace(request.ProviderId) && !string.Equals(request.ProviderId.Trim(), current.ProviderId, StringComparison.OrdinalIgnoreCase))
            throw new AppValidationException("The provider of a configuration cannot be changed. Add a new configuration instead.");
        var descriptor = ProviderRegistry.Find(current.ProviderId) ?? throw new AppValidationException("This provider is no longer supported.");
        var n = TenantProviderValidation.Normalize(request, _options, descriptor);

        // Sending the stored credential to a different endpoint requires entering it again (no key exfiltration by URL change).
        var endpointChanged = !string.Equals(n.BaseUrl, current.BaseUrl, StringComparison.OrdinalIgnoreCase);
        if (endpointChanged && current.SecretConfigured && n.ApiKey is null)
            throw new AppValidationException($"Enter the {descriptor.KeyLabel} again when changing the endpoint.");

        var key = n.ApiKey ?? (endpointChanged ? null : await ReadSecretAsync(current, ct));
        if (descriptor.KeyRequired && key is null)
            throw new AppValidationException(current.SecretConfigured
                ? $"The stored {descriptor.KeyLabel} can no longer be read. Enter it again."
                : $"Enter the {descriptor.KeyLabel}.");

        var connectionChanged = endpointChanged || n.ApiKey is not null || !SameSettings(n.Settings, current.Settings)
                                || !string.Equals(n.DefaultModel, current.DefaultModel, StringComparison.Ordinal);

        TenantProviderTestResultDto? test = null;
        if (!request.SaveWithoutTest && (connectionChanged || current.Status != TenantProviderStatus.Connected))
        {
            test = await RunTestAsync(descriptor, n.DisplayName, n.BaseUrl, n.Settings, key, n.DefaultModel, ct);
            if (!test.Success)
            {
                // Failed update: the stored configuration (and credential) stay exactly as they were.
                await AuditAsync(actor, "AIProviderKeyTestFailed", current.Id, $"{current.DisplayName} ({current.ProviderId}) — update not saved", "Failure", ct);
                return new TenantProviderSaveResultDto { Saved = false, Item = ToDto(current), Test = test };
            }
        }

        var now = DateTime.UtcNow;
        var expected = current.Version;
        var updated = Clone(current);
        updated.DisplayName = n.DisplayName;
        updated.BaseUrl = n.BaseUrl;
        updated.Settings = n.Settings;
        updated.DefaultModel = n.DefaultModel;
        updated.Enabled = n.Enabled;
        updated.AllowSystemFallback = n.AllowSystemFallback;
        updated.UpdatedAt = now;
        updated.UpdatedBy = actor.Label;
        updated.Version = expected + 1;
        if (test is not null) ApplyTest(updated, test, now);
        else if (connectionChanged) { updated.Status = TenantProviderStatus.NotConnected; updated.LastError = null; }

        TenantProviderSecret? secret = null;
        if (n.ApiKey is not null)
        {
            // New secret document first; the configuration switches to it atomically; the old one is destroyed afterwards.
            secret = NewSecret(actor.TenantKey, current.Id, n.ApiKey);
            await _repository.InsertSecretAsync(secret, ct);
            updated.SecretId = secret.Id;
            updated.SecretConfigured = true;
            updated.SecretUpdatedAt = now;
        }

        bool replaced;
        try
        {
            replaced = await _repository.ReplaceAsync(updated, expected, ct);
        }
        catch
        {
            if (secret is not null) await DiscardNewSecretAsync(actor.TenantKey, current.Id, current.SecretId);
            throw;
        }
        if (!replaced)
        {
            if (secret is not null) await DiscardNewSecretAsync(actor.TenantKey, current.Id, current.SecretId);
            throw new TenantProviderConflictException();
        }
        if (secret is not null) await SafeDeleteSecretsAsync(actor.TenantKey, current.Id, secret.Id);

        await AuditAsync(actor, "AIProviderKeyUpdated", current.Id,
            $"{updated.DisplayName} ({updated.ProviderId}){(secret is null ? string.Empty : " — credential replaced")}{(endpointChanged ? " — endpoint changed" : string.Empty)}{(request.SaveWithoutTest && connectionChanged ? " — saved without test" : string.Empty)}",
            "Success", ct);
        return new TenantProviderSaveResultDto { Saved = true, Item = ToDto(updated), Test = test };
    }

    /// <summary>Removes a configuration: the credential is destroyed, the metadata is kept (marked removed) for the audit trail.</summary>
    public async Task RemoveAsync(TenantProviderActor actor, string id, int? version, CancellationToken ct)
    {
        EnsureManage(actor);
        var current = await LoadAsync(actor, id, ct);
        if (version is { } v && v != current.Version) throw new TenantProviderConflictException();
        var removed = Clone(current);
        var now = DateTime.UtcNow;
        removed.RemovedAt = now;
        removed.RemovedBy = actor.Label;
        removed.Enabled = false;
        removed.IsDefault = false;
        removed.SecretConfigured = false;
        removed.SecretId = null;
        removed.UpdatedAt = now;
        removed.UpdatedBy = actor.Label;
        removed.Version = current.Version + 1;
        if (!await _repository.ReplaceAsync(removed, current.Version, ct)) throw new TenantProviderConflictException();
        await SafeDeleteSecretsAsync(actor.TenantKey, current.Id, null);
        await AuditAsync(actor, "AIProviderKeyRemoved", current.Id,
            $"{current.DisplayName} ({current.ProviderId}) — credential destroyed{(current.IsDefault ? "; AYAAN chat uses the AYAAN default again" : string.Empty)}", "Success", ct);
    }

    public async Task<TenantProviderDto> SetEnabledAsync(TenantProviderActor actor, string id, bool enabled, CancellationToken ct)
    {
        EnsureManage(actor);
        var current = await LoadAsync(actor, id, ct);
        if (current.Enabled == enabled) return ToDto(current);
        var updated = Clone(current);
        updated.Enabled = enabled;
        updated.UpdatedAt = DateTime.UtcNow;
        updated.UpdatedBy = actor.Label;
        updated.Version = current.Version + 1;
        if (!await _repository.ReplaceAsync(updated, current.Version, ct)) throw new TenantProviderConflictException();
        await AuditAsync(actor, enabled ? "AIProviderKeyEnabled" : "AIProviderKeyDisabled", current.Id,
            $"{current.DisplayName} ({current.ProviderId}){(!enabled && current.IsDefault ? " — AYAAN chat uses the AYAAN default while disabled" : string.Empty)}", "Success", ct);
        return ToDto(updated);
    }

    /// <summary>Chooses the provider + model AYAAN chat uses for this company, or (null id) returns to the AYAAN default.</summary>
    public async Task<TenantProvidersOverviewDto> SetDefaultAsync(TenantProviderActor actor, TenantProviderDefaultRequest request, CancellationToken ct)
    {
        EnsureManage(actor);
        if (string.IsNullOrWhiteSpace(request.ConfigurationId))
        {
            await _repository.ClearDefaultAsync(actor.TenantKey, null, ct);
            await AuditAsync(actor, "AIProviderDefaultChanged", null, "AYAAN chat uses the AYAAN default provider", "Success", ct);
            return await OverviewAsync(actor, ct);
        }

        var current = await LoadAsync(actor, request.ConfigurationId, ct);
        var descriptor = ProviderRegistry.Find(current.ProviderId) ?? throw new AppValidationException("This provider is no longer supported.");
        if (!current.Enabled) throw new AppValidationException("Enable this provider before using it for AYAAN chat.");
        if (descriptor.KeyRequired && !current.SecretConfigured) throw new AppValidationException($"Enter the {descriptor.KeyLabel} first.");
        var model = string.IsNullOrWhiteSpace(request.Model) ? current.DefaultModel : request.Model.Trim();
        if (!TenantProviderValidation.IsValidModel(model)) throw new AppValidationException("Choose the model AYAAN should use.");

        var updated = Clone(current);
        updated.IsDefault = true;
        updated.DefaultModel = model;
        updated.UpdatedAt = DateTime.UtcNow;
        updated.UpdatedBy = actor.Label;
        updated.Version = current.Version + 1;
        if (!await _repository.ReplaceAsync(updated, current.Version, ct)) throw new TenantProviderConflictException();
        await _repository.ClearDefaultAsync(actor.TenantKey, current.Id, ct);
        await AuditAsync(actor, "AIProviderDefaultChanged", current.Id, $"AYAAN chat uses {current.DisplayName} ({current.ProviderId}) · {model}", "Success", ct);
        return await OverviewAsync(actor, ct);
    }

    // ------------------------------------------------------------------ test / models

    public async Task<TenantProviderTestResultDto> TestSavedAsync(TenantProviderActor actor, string id, CancellationToken ct)
    {
        EnsureManage(actor);
        var current = await LoadAsync(actor, id, ct);
        var descriptor = ProviderRegistry.Find(current.ProviderId) ?? throw new AppValidationException("This provider is no longer supported.");
        var key = await ReadSecretAsync(current, ct);
        TenantProviderTestResultDto result;
        if (descriptor.KeyRequired && key is null)
            result = new TenantProviderTestResultDto
            {
                Success = false,
                Message = current.SecretConfigured ? $"The stored {descriptor.KeyLabel} can no longer be read. Enter it again." : $"Enter the {descriptor.KeyLabel}."
            };
        else
            result = await RunTestAsync(descriptor, current.DisplayName, current.BaseUrl, current.Settings, key, current.DefaultModel, ct);

        await _repository.UpdateStatusAsync(actor.TenantKey, current.Id, result.Success ? TenantProviderStatus.Connected : TenantProviderStatus.Error,
            result.Success ? null : result.Message, result.LatencyMs, result.ModelCount, DateTime.UtcNow, ct);
        await AuditAsync(actor, "AIProviderKeyTested", current.Id, $"{current.DisplayName} ({current.ProviderId}) — {(result.Success ? "connected" : "failed")}",
            result.Success ? "Success" : "Failure", ct);
        return result;
    }

    /// <summary>Tests an unsaved configuration (Add / Edit dialog). Nothing is stored.</summary>
    public async Task<TenantProviderTestResultDto> TestDraftAsync(TenantProviderActor actor, TenantProviderTestRequest request, CancellationToken ct)
    {
        EnsureManage(actor);
        TenantProviderConfig? existing = null;
        ProviderDescriptor? fixedDescriptor = null;
        if (!string.IsNullOrWhiteSpace(request.ExistingId))
        {
            existing = await LoadAsync(actor, request.ExistingId, ct);
            fixedDescriptor = ProviderRegistry.Find(existing.ProviderId) ?? throw new AppValidationException("This provider is no longer supported.");
        }
        var n = TenantProviderValidation.Normalize(request.Config, _options, fixedDescriptor);
        var key = n.ApiKey;
        if (key is null && existing is not null)
        {
            if (!string.Equals(n.BaseUrl, existing.BaseUrl, StringComparison.OrdinalIgnoreCase) && existing.SecretConfigured)
                throw new AppValidationException($"Enter the {n.Descriptor.KeyLabel} again when changing the endpoint.");
            key = await ReadSecretAsync(existing, ct);
        }
        if (n.Descriptor.KeyRequired && key is null)
            throw new AppValidationException($"Enter the {n.Descriptor.KeyLabel}.");
        var result = await RunTestAsync(n.Descriptor, n.DisplayName, n.BaseUrl, n.Settings, key, n.DefaultModel, ct);
        await AuditAsync(actor, "AIProviderKeyTested", existing?.Id, $"{n.DisplayName} ({n.Descriptor.Id}) — unsaved settings — {(result.Success ? "connected" : "failed")}",
            result.Success ? "Success" : "Failure", ct);
        return result;
    }

    /// <summary>Models of a saved configuration: live list (when the provider supports it) merged with suggestions.</summary>
    public async Task<TenantProviderTestResultDto> ModelsAsync(TenantProviderActor actor, string id, CancellationToken ct)
    {
        EnsureTenant(actor);
        var current = await LoadAsync(actor, id, ct);
        var descriptor = ProviderRegistry.Find(current.ProviderId) ?? throw new AppValidationException("This provider is no longer supported.");
        var suggested = Suggested(descriptor, current.DefaultModel);
        if (!descriptor.SupportsModelListing || !CanManage(actor))
            return new TenantProviderTestResultDto { Success = true, Message = "Suggested models.", Models = suggested };
        var key = await ReadSecretAsync(current, ct);
        if (descriptor.KeyRequired && key is null)
            return new TenantProviderTestResultDto { Success = false, Message = $"Enter the {descriptor.KeyLabel} to load the models.", Models = suggested };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TestTimeoutSeconds, 5, 120)));
        try
        {
            var provider = _adapters.Create(Connection(descriptor, current.DisplayName, current.BaseUrl, current.Settings, key, current.DefaultModel));
            var live = await provider.GetModelsAsync(timeout.Token);
            return new TenantProviderTestResultDto { Success = true, Message = $"{live.Count} model(s) available.", ModelCount = live.Count, Models = Merge(live, suggested) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new TenantProviderTestResultDto { Success = false, Message = SafeMessage(ex, descriptor, key), Models = suggested };
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<TenantProviderTestResultDto> RunTestAsync(ProviderDescriptor descriptor, string name, string baseUrl,
        IReadOnlyDictionary<string, string> settings, string? key, string model, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TestTimeoutSeconds, 5, 120)));
        try
        {
            var provider = _adapters.Create(Connection(descriptor, name, baseUrl, settings, key, model));
            var result = await provider.TestConnectionAsync(timeout.Token);
            sw.Stop();
            if (!result.Success)
                return new TenantProviderTestResultDto { Success = false, Message = SecretRedactor.Redact(result.Message, key), LatencyMs = sw.ElapsedMilliseconds };

            IReadOnlyList<AIModel> live = Array.Empty<AIModel>();
            if (descriptor.SupportsModelListing && result.ModelCount is > 0)
            {
                try { live = await provider.GetModelsAsync(timeout.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { /* listing is optional */ }
            }
            return new TenantProviderTestResultDto
            {
                Success = true,
                Message = SecretRedactor.Redact(result.Message, key),
                LatencyMs = result.LatencyMs > 0 ? result.LatencyMs : sw.ElapsedMilliseconds,
                ModelCount = result.ModelCount,
                Models = Merge(live, Suggested(descriptor, model))
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new TenantProviderTestResultDto { Success = false, Message = SafeMessage(ex, descriptor, key), LatencyMs = sw.ElapsedMilliseconds };
        }
    }

    private string SafeMessage(Exception ex, ProviderDescriptor descriptor, string? key)
    {
        switch (ex)
        {
            case AIProviderException:
                return SecretRedactor.Redact(ex.Message, key);
            case OperationCanceledException:
                return $"{descriptor.DisplayName} did not respond within {Math.Clamp(_options.TestTimeoutSeconds, 5, 120)} seconds.";
            default:
                // Type only — never the message or stack (could contain request details).
                _logger.LogWarning("AI provider key test: unexpected {ErrorType} for provider {ProviderId}", ex.GetType().Name, descriptor.Id);
                return $"{descriptor.DisplayName} returned an unexpected response. Check the endpoint and try again.";
        }
    }

    private static TenantProviderConnection Connection(ProviderDescriptor descriptor, string name, string baseUrl,
        IReadOnlyDictionary<string, string> settings, string? key, string model) => new()
    {
        Descriptor = descriptor,
        DisplayName = name,
        BaseUrl = baseUrl,
        Settings = settings,
        ApiKey = key,
        Model = model,
        TimeoutSeconds = 120
    };

    private static void ApplyTest(TenantProviderConfig config, TenantProviderTestResultDto? test, DateTime at)
    {
        if (test is null)
        {
            config.Status = TenantProviderStatus.NotConnected;
            config.LastError = null;
            return;
        }
        config.Status = test.Success ? TenantProviderStatus.Connected : TenantProviderStatus.Error;
        config.LastError = test.Success ? null : test.Message;
        config.LastTestedAt = at;
        config.LastLatencyMs = test.LatencyMs;
        config.ModelCount = test.ModelCount;
    }

    private TenantProviderSecret NewSecret(string tenantKey, string configId, string key) => new()
    {
        Id = _repository.NewId(),
        TenantKey = tenantKey,
        ConfigId = configId,
        Ciphertext = _protector.Protect(tenantKey, configId, key),
        CreatedAt = DateTime.UtcNow
    };

    private async Task<string?> ReadSecretAsync(TenantProviderConfig config, CancellationToken ct)
    {
        if (!config.SecretConfigured || string.IsNullOrEmpty(config.SecretId)) return null;
        var secret = await _repository.GetSecretAsync(config.TenantKey, config.Id, config.SecretId, ct);
        if (secret is null) return null;
        var key = _protector.Unprotect(config.TenantKey, config.Id, secret.Ciphertext);
        if (key is null)
            _logger.LogWarning("AI provider key of configuration {ConfigId} could not be decrypted (key ring changed or data moved)", config.Id);
        return key;
    }

    private async Task SafeDeleteSecretsAsync(string tenantKey, string configId, string? exceptSecretId)
    {
        try { await _repository.DeleteSecretsAsync(tenantKey, configId, exceptSecretId, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning("AI provider key cleanup failed for configuration {ConfigId} ({ErrorType})", configId, ex.GetType().Name); }
    }

    /// <summary>After a failed switch: deletes the new secret document, keeping the one the stored configuration still uses.</summary>
    private Task DiscardNewSecretAsync(string tenantKey, string configId, string? keepSecretId)
        => SafeDeleteSecretsAsync(tenantKey, configId, keepSecretId);

    private async Task<TenantProviderConfig> LoadAsync(TenantProviderActor actor, string id, CancellationToken ct)
    {
        EnsureTenant(actor);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) throw new NotFoundException("AI provider configuration not found.");
        var config = await _repository.GetAsync(actor.TenantKey, id.Trim(), ct);
        // Another company's id behaves exactly like an unknown id.
        if (config is null || config.RemovedAt is not null || !string.Equals(config.TenantKey, actor.TenantKey, StringComparison.Ordinal))
            throw new NotFoundException("AI provider configuration not found.");
        return config;
    }

    private void EnsureTenant(TenantProviderActor actor)
    {
        if (string.IsNullOrWhiteSpace(actor.TenantKey))
            throw new TenantProviderAccessException(403, "tenant_required", "AI provider settings are available only for a signed-in MySaleBooks company.");
    }

    private void EnsureManage(TenantProviderActor actor)
    {
        EnsureTenant(actor);
        if (!_options.Enabled)
            throw new TenantProviderAccessException(403, "disabled", "Company AI provider keys are not enabled on this server.");
        if (!CanManage(actor))
            throw new TenantProviderAccessException(403, "manage_forbidden", "Only AYAAN administrators can change AI provider keys.");
    }

    private async Task AuditAsync(TenantProviderActor actor, string action, string? resourceId, string details, string outcome, CancellationToken ct)
    {
        try
        {
            await _audit.InsertAsync(new AuditLog
            {
                Action = action,
                Details = SecretRedactor.Redact(details),
                UserId = actor.UserId,
                UserName = actor.Label,
                CompanyId = actor.TenantKey,
                TenantRef = actor.TenantRef,
                IpAddress = actor.IpAddress,
                Resource = AuditResource,
                ResourceId = resourceId,
                Outcome = outcome,
                CorrelationId = actor.CorrelationId
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("AI provider key audit entry could not be written ({ErrorType})", ex.GetType().Name);
        }
    }

    private static bool SameSettings(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static TenantProviderConfig Clone(TenantProviderConfig c) => new()
    {
        Id = c.Id,
        CreatedAt = c.CreatedAt,
        TenantKey = c.TenantKey,
        TenantRef = c.TenantRef,
        ProviderId = c.ProviderId,
        DisplayName = c.DisplayName,
        BaseUrl = c.BaseUrl,
        Settings = new Dictionary<string, string>(c.Settings, StringComparer.Ordinal),
        SecretConfigured = c.SecretConfigured,
        SecretId = c.SecretId,
        SecretUpdatedAt = c.SecretUpdatedAt,
        DefaultModel = c.DefaultModel,
        Enabled = c.Enabled,
        IsDefault = c.IsDefault,
        AllowSystemFallback = c.AllowSystemFallback,
        Status = c.Status,
        LastTestedAt = c.LastTestedAt,
        LastError = c.LastError,
        LastLatencyMs = c.LastLatencyMs,
        ModelCount = c.ModelCount,
        UpdatedAt = c.UpdatedAt,
        CreatedBy = c.CreatedBy,
        UpdatedBy = c.UpdatedBy,
        Version = c.Version,
        RemovedAt = c.RemovedAt,
        RemovedBy = c.RemovedBy
    };

    private static IReadOnlyList<TenantModelDto> Suggested(ProviderDescriptor d, string? current)
    {
        var list = d.SuggestedModels.Select(m => new TenantModelDto { Id = m, Name = m, Source = "suggested" }).ToList();
        if (!string.IsNullOrWhiteSpace(current) && list.All(m => m.Id != current))
            list.Insert(0, new TenantModelDto { Id = current, Name = current, Source = "configured" });
        return list;
    }

    private static IReadOnlyList<TenantModelDto> Merge(IReadOnlyList<AIModel> live, IReadOnlyList<TenantModelDto> suggested)
    {
        var result = live.Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => new TenantModelDto { Id = m.Id, Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name, Source = "live" })
            .GroupBy(m => m.Id, StringComparer.Ordinal).Select(g => g.First())
            .Take(500).ToList();
        foreach (var s in suggested)
            if (result.All(m => !string.Equals(m.Id, s.Id, StringComparison.Ordinal))) result.Add(s);
        return result;
    }

    public static TenantProviderDto ToDto(TenantProviderConfig c)
    {
        var d = ProviderRegistry.Find(c.ProviderId);
        var status = !c.Enabled ? "Disabled" : c.Status.ToString();
        return new TenantProviderDto
        {
            Id = c.Id,
            ProviderId = c.ProviderId,
            ProviderName = d?.DisplayName ?? c.ProviderId,
            DisplayName = c.DisplayName,
            LogoText = d?.LogoText ?? "AI",
            LogoColor = d?.LogoColor ?? "#6e759f",
            BaseUrl = c.BaseUrl,
            Settings = new Dictionary<string, string>(c.Settings, StringComparer.Ordinal),
            CredentialConfigured = c.SecretConfigured,
            Credential = c.SecretConfigured ? "Configured" : "Not configured",
            CredentialUpdatedAt = c.SecretUpdatedAt,
            DefaultModel = c.DefaultModel,
            Enabled = c.Enabled,
            IsDefault = c.IsDefault,
            AllowSystemFallback = c.AllowSystemFallback,
            Status = status,
            LastTestedAt = c.LastTestedAt,
            LastError = c.LastError,
            LastLatencyMs = c.LastLatencyMs,
            ModelCount = c.ModelCount,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            CreatedBy = c.CreatedBy,
            UpdatedBy = c.UpdatedBy,
            Version = c.Version,
            Supported = d is not null
        };
    }

    private static ProviderDescriptorDto ToDto(ProviderDescriptor d) => new()
    {
        Id = d.Id,
        DisplayName = d.DisplayName,
        LogoText = d.LogoText,
        LogoColor = d.LogoColor,
        Description = d.Description,
        DefaultBaseUrl = d.DefaultBaseUrl,
        BaseUrlEditable = d.BaseUrlEditable,
        BaseUrlRequired = d.BaseUrlRequired,
        BaseUrlLabel = d.BaseUrlLabel,
        BaseUrlPlaceholder = d.BaseUrlPlaceholder,
        KeyRequired = d.KeyRequired,
        KeyLabel = d.KeyLabel,
        KeyHelp = d.KeyHelp,
        KeyConsoleUrl = d.KeyConsoleUrl,
        Fields = d.Fields.Select(f => new ProviderFieldDto
        {
            Key = f.Key,
            Label = f.Label,
            Type = f.Type.ToString().ToLowerInvariant(),
            Required = f.Required,
            Placeholder = f.Placeholder,
            Help = f.Help,
            Options = f.Options,
            DefaultValue = f.DefaultValue,
            MaxLength = f.MaxLength
        }).ToList(),
        SuggestedModels = d.SuggestedModels,
        SupportsModelListing = d.SupportsModelListing,
        SelfHosted = d.SelfHosted
    };
}
