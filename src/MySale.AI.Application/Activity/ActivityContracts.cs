using MySale.AI.Domain;

namespace MySale.AI.Application.ActivityTracking;

/// <summary>
/// Per-request context shared by every pipeline stage (API → AI → MQL → validation → MongoDB → answer → activity log).
/// Implemented by the API from the HTTP request; <see cref="NullRequestContext"/> is used outside HTTP (tests, jobs).
/// </summary>
public interface IRequestContext
{
    /// <summary>Unique id of this request (X-Correlation-Id, or generated). Returned in the response header.</summary>
    string CorrelationId { get; }
    /// <summary>X-Client-App header, e.g. "mysalebooks-web", "ai-dashboard".</summary>
    string? ClientApp { get; }
    /// <summary>X-Client-Version header.</summary>
    string? ClientVersion { get; }
    /// <summary>X-Session-Id header (browser tab session; not an auth token).</summary>
    string? SessionId { get; }
    /// <summary>Request path, e.g. "/api/ai/chat/stream".</summary>
    string? Endpoint { get; }
}

public sealed class NullRequestContext : IRequestContext
{
    public string CorrelationId { get; } = Guid.NewGuid().ToString("N");
    public string? ClientApp => null;
    public string? ClientVersion => null;
    public string? SessionId => null;
    public string? Endpoint => null;
}

/// <summary>
/// Write-only entry point used by the AI agent. It can only append records: the agent has no way to read,
/// modify or delete activity logs. Implementations must never throw and must not block the request.
/// </summary>
public interface IAIActivitySink
{
    /// <summary>Queues a finished activity (and its conversation summary update). Returns false if it was dropped.</summary>
    bool TryEnqueue(AIActivity activity, ConversationActivityUpdate? conversation);
}

/// <summary>Increment for the conversation summary produced by one activity.</summary>
public sealed class ConversationActivityUpdate
{
    public string ConversationId { get; init; } = string.Empty;
    public string? UserId { get; init; }
    public string TenantRef { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int MessageCount { get; init; }
    public DateTime At { get; init; }
    public bool Succeeded { get; init; }
    public string Status { get; init; } = ActivityStatuses.Success;
    public long TotalMs { get; init; }
    public long AiMs { get; init; }
    public long MongoMs { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
}

public sealed class ActivityFilter
{
    public string? Search { get; set; }
    public string? ActivityId { get; set; }
    public string? ConversationId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    /// <summary>ActivityStatuses value, or "failed" for everything except Success/NoResults.</summary>
    public string? Status { get; set; }
    public string? FailedStage { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? TenantRef { get; set; }
    /// <summary>AI dashboard filters: user id, action (tool selected), input type (text / voice …).</summary>
    public string? UserId { get; set; }
    public string? Action { get; set; }
    public string? InputType { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

/// <summary>
/// Storage of the activity log. The initial implementation uses the agent's system MongoDB (separate from the
/// customer database); swap the implementation to move the log elsewhere (SQLite, SQL Server, …).
/// </summary>
public interface IAIActivityRepository
{
    Task InsertAsync(AIActivity activity, CancellationToken ct);
    Task UpsertConversationAsync(ConversationActivityUpdate update, CancellationToken ct);

    Task<(List<AIActivity> Items, long Total)> SearchAsync(ActivityFilter filter, CancellationToken ct);
    Task<AIActivity?> GetAsync(string activityId, CancellationToken ct);
    /// <summary><paramref name="tenantRef"/> null = all tenants (AI-dashboard developer screens only).</summary>
    Task<List<AIActivity>> ListByConversationAsync(string conversationId, int limit, CancellationToken ct, string? tenantRef = null);
    Task<(List<AIConversationActivity> Items, long Total)> SearchConversationsAsync(string? search, int page, int pageSize, CancellationToken ct, string? tenantRef = null);
    Task<AIConversationActivity?> GetConversationAsync(string conversationId, CancellationToken ct);
    Task<List<string>> DistinctAsync(string field, CancellationToken ct, string? tenantRef = null);
    /// <summary>Lightweight rows for the performance overview.</summary>
    Task<List<AIActivity>> RecentForStatsAsync(DateTime from, int limit, CancellationToken ct, string? tenantRef = null);

    /// <summary>
    /// AI dashboard analytics of one tenant: records without the large payloads (query, rows, answer, timeline).
    /// </summary>
    Task<List<AIActivity>> RecentForAnalysisAsync(string tenantRef, DateTime from, int limit, CancellationToken ct);

    /// <summary>Retention only (called by the background cleanup job, never by the agent).</summary>
    Task<long> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct);
}

/// <summary>Configuration section "Activity". AI_ACTIVITY_RETENTION_DAYS overrides RetentionDays.</summary>
public sealed class ActivityOptions
{
    public const string Section = "Activity";

    public bool Enabled { get; set; } = true;
    /// <summary>Activity records older than this are deleted automatically (minimum 1).</summary>
    public int RetentionDays { get; set; } = 90;
    /// <summary>Store the returned rows (capped + masked). Off = only count, schema, summary and hash.</summary>
    public bool StoreFullResults { get; set; } = true;
    public int MaximumDocumentsToStore { get; set; } = 25;
    /// <summary>Maximum size of the stored rows JSON (bytes); rows beyond it are dropped.</summary>
    public int MaximumResultSize { get; set; } = 32_768;
    /// <summary>Keep the raw model output of each attempt (redacted, truncated).</summary>
    public bool StoreRawModelOutput { get; set; } = true;
    public int MaximumTextLength { get; set; } = 20_000;
    /// <summary>Stack traces for internal development logs only. Keep false in production.</summary>
    public bool StoreStackTraces { get; set; }
    /// <summary>Extra field names (case-insensitive) that are always masked in stored queries/results.</summary>
    public List<string> SensitiveFields { get; set; } = new();
    /// <summary>Field names removed entirely from stored results.</summary>
    public List<string> ExcludedResultFields { get; set; } = new();
    /// <summary>Max. activities waiting to be written; more are dropped (the chat never waits for the log).</summary>
    public int QueueCapacity { get; set; } = 2_000;
    /// <summary>
    /// Allow MySaleBooks-token Admin/Tester users to use the developer screens (activity log, logs, providers…) and to
    /// receive technical details (MQL, trace) in chat responses. Default false: customers never see technical details.
    /// </summary>
    public bool AllowMySaleBooksAdmins { get; set; }
    /// <summary>
    /// Encrypt the stored MQL, raw model output, rejected query and stored result rows with ASP.NET Core Data Protection.
    /// Enable only when Data Protection keys are persisted (DataProtection:KeysPath on durable storage) — otherwise
    /// older records become unreadable after a restart.
    /// </summary>
    public bool EncryptPayloads { get; set; }
}
