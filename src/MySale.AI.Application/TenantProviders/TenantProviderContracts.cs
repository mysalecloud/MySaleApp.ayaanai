using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;

namespace MySale.AI.Application.TenantProviders;

// ---------------------------------------------------------------- options

/// <summary>Configuration section "TenantAiProviders" (AI Settings → AI Providers &amp; API Keys).</summary>
public sealed class TenantAiProviderOptions
{
    public const string Section = "TenantAiProviders";

    /// <summary>Master switch. When false the page is read-only/hidden and chat ignores company providers.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>AYAAN Dashboard roles (Admin / Tester / Viewer) allowed to add, change, test or remove keys. Everyone else: read-only.</summary>
    public List<string> ManageRoles { get; set; } = new() { "Admin" };
    /// <summary>Allow endpoints on private / loopback / link-local networks (self-hosted gateways). Off: blocks SSRF to internal hosts.</summary>
    public bool AllowPrivateEndpoints { get; set; }
    /// <summary>Allow plain http:// endpoints (development only). Off: HTTPS only.</summary>
    public bool AllowInsecureHttp { get; set; }
    /// <summary>Reject management calls that did not arrive over HTTPS (X-Forwarded-Proto honoured). Not enforced in Development.</summary>
    public bool RequireHttpsRequests { get; set; } = true;
    public int TestTimeoutSeconds { get; set; } = 20;
    public int MaxConfigurationsPerCompany { get; set; } = 25;
    /// <summary>Restrict the catalogue (registry ids). Empty = every provider.</summary>
    public List<string> AllowedProviders { get; set; } = new();
}

// ---------------------------------------------------------------- abstractions

/// <summary>Persistence of company provider configurations. Every method is scoped by tenantKey.</summary>
public interface ITenantProviderRepository
{
    /// <summary>New document id (generated before insert so the secret can be bound to it).</summary>
    string NewId();
    Task<List<TenantProviderConfig>> ListAsync(string tenantKey, CancellationToken ct);
    Task<TenantProviderConfig?> GetAsync(string tenantKey, string id, CancellationToken ct);
    Task<TenantProviderConfig?> GetDefaultAsync(string tenantKey, CancellationToken ct);
    Task InsertAsync(TenantProviderConfig config, CancellationToken ct);
    /// <summary>Replaces the document when its Version still equals <paramref name="expectedVersion"/>; false = changed meanwhile.</summary>
    Task<bool> ReplaceAsync(TenantProviderConfig config, int expectedVersion, CancellationToken ct);
    Task ClearDefaultAsync(string tenantKey, string? exceptId, CancellationToken ct);
    Task UpdateStatusAsync(string tenantKey, string id, TenantProviderStatus status, string? error, long? latencyMs, int? modelCount, DateTime at, CancellationToken ct);

    Task<TenantProviderSecret?> GetSecretAsync(string tenantKey, string configId, string secretId, CancellationToken ct);
    Task InsertSecretAsync(TenantProviderSecret secret, CancellationToken ct);
    Task DeleteSecretsAsync(string tenantKey, string configId, string? exceptSecretId, CancellationToken ct);
}

/// <summary>Encrypts company credentials. The ciphertext is bound to the tenant and the configuration id.</summary>
public interface ITenantSecretProtector
{
    string Protect(string tenantKey, string configId, string plaintext);
    /// <summary>Null when the ciphertext cannot be decrypted (other tenant / config, key ring lost, tampered).</summary>
    string? Unprotect(string tenantKey, string configId, string ciphertext);
}

/// <summary>Everything an adapter needs to build a provider. The key is plaintext only in memory, for this call.</summary>
public sealed class TenantProviderConnection
{
    public required ProviderDescriptor Descriptor { get; init; }
    public required string DisplayName { get; init; }
    public required string BaseUrl { get; init; }
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
    public string? ApiKey { get; init; }
    public string Model { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 120;
}

/// <summary>Builds an <see cref="IAIProvider"/> for a company configuration (SSRF-safe HTTP client, provider-specific auth).</summary>
public interface ITenantProviderAdapterFactory
{
    IAIProvider Create(TenantProviderConnection connection);
}

/// <summary>Chat hook used by <see cref="Services.ProviderService.ResolveAsync"/>: the company's own default provider, if any.</summary>
public interface ITenantProviderResolver
{
    /// <summary>
    /// Null when the caller is not a MySaleBooks company user, the feature is off, or the company has not chosen its own
    /// provider (→ existing behaviour). Throws <see cref="AIProviderException"/> when the chosen provider cannot be used and
    /// the company did not allow the system fallback (no silent switching).
    /// </summary>
    Task<Services.ResolvedProvider?> ResolveAsync(string? model, AppSettings settings, CancellationToken ct);
}

// ---------------------------------------------------------------- DTOs (never contain a credential)

public sealed class ProviderFieldDto
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Type { get; init; } = "text";
    public bool Required { get; init; }
    public string? Placeholder { get; init; }
    public string? Help { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public string? DefaultValue { get; init; }
    public int MaxLength { get; init; }
}

public sealed class ProviderDescriptorDto
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string LogoText { get; init; } = string.Empty;
    public string LogoColor { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DefaultBaseUrl { get; init; } = string.Empty;
    public bool BaseUrlEditable { get; init; }
    public bool BaseUrlRequired { get; init; }
    public string BaseUrlLabel { get; init; } = string.Empty;
    public string? BaseUrlPlaceholder { get; init; }
    public bool KeyRequired { get; init; }
    public string KeyLabel { get; init; } = string.Empty;
    public string? KeyHelp { get; init; }
    public string? KeyConsoleUrl { get; init; }
    public IReadOnlyList<ProviderFieldDto> Fields { get; init; } = Array.Empty<ProviderFieldDto>();
    public IReadOnlyList<string> SuggestedModels { get; init; } = Array.Empty<string>();
    public bool SupportsModelListing { get; init; }
    public bool SelfHosted { get; init; }
}

public sealed class TenantProviderDto
{
    public string Id { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string LogoText { get; init; } = string.Empty;
    public string LogoColor { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
    /// <summary>"Configured" or "Not configured" — the credential itself (or any part of it) is never returned.</summary>
    public string Credential { get; init; } = "Not configured";
    public bool CredentialConfigured { get; init; }
    public DateTime? CredentialUpdatedAt { get; init; }
    public string DefaultModel { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool IsDefault { get; init; }
    public bool AllowSystemFallback { get; init; }
    /// <summary>Connected / NotConnected / Error / Disabled.</summary>
    public string Status { get; init; } = "NotConnected";
    public DateTime? LastTestedAt { get; init; }
    public string? LastError { get; init; }
    public long? LastLatencyMs { get; init; }
    public int? ModelCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; init; }
    public int Version { get; init; }
    public bool Supported { get; init; }
}

public sealed class TenantProvidersOverviewDto
{
    public bool Enabled { get; init; }
    public bool CanManage { get; init; }
    /// <summary>What AYAAN chat uses for this company right now ("company" = one of the configurations below, "system" = AYAAN default).</summary>
    public string ActiveSource { get; init; } = "system";
    public string? ActiveConfigurationId { get; init; }
    public string? ActiveModel { get; init; }
    public IReadOnlyList<TenantProviderDto> Items { get; init; } = Array.Empty<TenantProviderDto>();
    public IReadOnlyList<ProviderDescriptorDto> Catalog { get; init; } = Array.Empty<ProviderDescriptorDto>();
}

/// <summary>Add / update request. ApiKey is write-only: omitted or empty = keep the stored credential.</summary>
public sealed class TenantProviderUpsertRequest
{
    public string ProviderId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? BaseUrl { get; set; }
    public Dictionary<string, string>? Settings { get; set; }
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AllowSystemFallback { get; set; }
    /// <summary>Save even when the connection test fails or is skipped (the configuration is then "Not connected" / "Error").</summary>
    public bool SaveWithoutTest { get; set; }
    /// <summary>Optimistic concurrency token from the list (update only).</summary>
    public int? Version { get; set; }
}

public sealed class TenantProviderTestRequest
{
    /// <summary>Unsaved configuration to test. With ExistingId and no ApiKey the stored credential is used.</summary>
    public TenantProviderUpsertRequest Config { get; set; } = new();
    public string? ExistingId { get; set; }
}

public sealed class TenantProviderTestResultDto
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long? LatencyMs { get; init; }
    public int? ModelCount { get; init; }
    public IReadOnlyList<TenantModelDto> Models { get; init; } = Array.Empty<TenantModelDto>();
}

public sealed class TenantModelDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    /// <summary>"live" (reported by the provider) or "suggested".</summary>
    public string Source { get; init; } = "live";
}

public sealed class TenantProviderSaveResultDto
{
    /// <summary>False when the connection test failed and the request did not ask to save untested — nothing was changed.</summary>
    public bool Saved { get; init; }
    /// <summary>The saved configuration (the current, unchanged one when Saved is false on an update).</summary>
    public TenantProviderDto? Item { get; init; }
    public TenantProviderTestResultDto? Test { get; init; }
}

/// <summary>Who performs a management call. Built by the API layer from the validated tokens only.</summary>
public sealed class TenantProviderActor
{
    public required string TenantKey { get; init; }
    public string? TenantRef { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string? AyaanUserName { get; init; }
    /// <summary>Role of the AYAAN Dashboard session (Admin / Tester / Viewer).</summary>
    public string? AyaanRole { get; init; }
    public string? IpAddress { get; init; }
    public string? CorrelationId { get; init; }

    public string Label => string.IsNullOrWhiteSpace(AyaanUserName) ? UserName : $"{UserName} (AYAAN: {AyaanUserName})";
}

public sealed class TenantProviderDefaultRequest
{
    /// <summary>Configuration to use for AYAAN chat, or null/empty to go back to the AYAAN system default.</summary>
    public string? ConfigurationId { get; set; }
    public string? Model { get; set; }
}

public sealed class TenantProviderEnableRequest
{
    public bool Enabled { get; set; }
}

/// <summary>A management call that is not allowed (role, feature off, HTTPS). Message is user-safe.</summary>
public sealed class TenantProviderAccessException : Exception
{
    public TenantProviderAccessException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}

/// <summary>The configuration was changed by someone else since it was loaded (409).</summary>
public sealed class TenantProviderConflictException : Exception
{
    public TenantProviderConflictException() : base("This provider was changed by someone else. Reload the page and try again.") { }
}
