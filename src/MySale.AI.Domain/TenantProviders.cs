namespace MySale.AI.Domain;

/// <summary>Connection state of a company's own AI provider configuration (AI Settings → AI Providers &amp; API Keys).</summary>
public enum TenantProviderStatus
{
    /// <summary>Saved but never tested (or saved untested after an edit).</summary>
    NotConnected,
    /// <summary>Last connection test succeeded.</summary>
    Connected,
    /// <summary>Last connection test (or a chat request) failed — see LastError (sanitised, never contains a key).</summary>
    Error
}

/// <summary>
/// One AI provider configured by ONE MySaleBooks company (tenant) with its own credentials.
/// Collection "tenant_ai_providers" (system database). Metadata only: the encrypted credential lives in a separate
/// document (<see cref="TenantProviderSecret"/>, collection "tenant_ai_provider_secrets") so listing, auditing and
/// the dashboard never load ciphertext. Every query is filtered by <see cref="TenantKey"/>, which is taken from the
/// validated MySaleBooks JWT (dbName / company claim) — never from the request.
/// Removing a configuration marks it removed (<see cref="RemovedAt"/>) and destroys its secret; conversations,
/// activity and audit history are never touched.
/// </summary>
public sealed class TenantProviderConfig : Entity
{
    /// <summary>Tenant key ("db:{dbName}" or the company claim) — the same key conversations and query logs use.</summary>
    public string TenantKey { get; set; } = string.Empty;
    /// <summary>Hash of the tenant (activity log key), for cross-referencing without exposing the database name.</summary>
    public string? TenantRef { get; set; }
    /// <summary>Registry id of the provider ("openai", "anthropic", "azure-openai", "bedrock", …).</summary>
    public string ProviderId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Endpoint (registry default when the provider has a fixed endpoint). HTTPS only.</summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>Non-secret provider-specific fields (deployment, apiVersion, region, organization, project …).</summary>
    public Dictionary<string, string> Settings { get; set; } = new();
    /// <summary>True when a credential is stored. The credential itself is never on this document.</summary>
    public bool SecretConfigured { get; set; }
    /// <summary>Id of the current <see cref="TenantProviderSecret"/> document.</summary>
    public string? SecretId { get; set; }
    public DateTime? SecretUpdatedAt { get; set; }
    public string DefaultModel { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    /// <summary>Used for this company's AYAAN chat. At most one per tenant. False until the user explicitly chooses it.</summary>
    public bool IsDefault { get; set; }
    /// <summary>
    /// Fallback policy: when this provider cannot be used (credential unreadable, disabled by an error) the AYAAN system
    /// default is used instead. Off by default — no silent provider switching unless the company allows it.
    /// </summary>
    public bool AllowSystemFallback { get; set; }
    public TenantProviderStatus Status { get; set; } = TenantProviderStatus.NotConnected;
    public DateTime? LastTestedAt { get; set; }
    /// <summary>User-safe, sanitised message of the last failure (secrets redacted).</summary>
    public string? LastError { get; set; }
    public long? LastLatencyMs { get; set; }
    public int? ModelCount { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>"MySaleBooks user (AYAAN account)" of the creator / last editor — display names only, no tokens.</summary>
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    /// <summary>Optimistic concurrency: incremented by every update.</summary>
    public int Version { get; set; }
    /// <summary>Set when the configuration was removed (kept for audit; the secret is destroyed).</summary>
    public DateTime? RemovedAt { get; set; }
    public string? RemovedBy { get; set; }
}

/// <summary>
/// Encrypted credential of one <see cref="TenantProviderConfig"/> (collection "tenant_ai_provider_secrets").
/// The ciphertext is produced with ASP.NET Core Data Protection under a purpose bound to the tenant AND the
/// configuration id, so it cannot be decrypted for another company or another configuration even if copied.
/// Never returned by any API, never logged.
/// </summary>
public sealed class TenantProviderSecret : Entity
{
    public string TenantKey { get; set; } = string.Empty;
    public string ConfigId { get; set; } = string.Empty;
    public string Ciphertext { get; set; } = string.Empty;
    /// <summary>Protection scheme version ("dp-v1").</summary>
    public string Scheme { get; set; } = "dp-v1";
}
