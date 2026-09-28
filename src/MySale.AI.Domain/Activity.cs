namespace MySale.AI.Domain;

// ---------------------------------------------------------------------------------------------
// AYAAN AI activity tracking — one AIActivity per AI request (the full lifecycle of a chat turn)
// and one AIConversationActivity summary per conversation. Stored in the agent's own system
// database (never in the customer's database). Contains no tokens, keys or connection strings.
// Structured JSON (queries, results) is stored as strings because MongoDB field names may not
// start with "$" (aggregation operators).
// ---------------------------------------------------------------------------------------------

public static class ActivityStatuses
{
    public const string Success = "Success";
    public const string NoResults = "NoResults";
    public const string Unsupported = "Unsupported";
    /// <summary>AYAAN asked the customer a question (not an error).</summary>
    public const string Clarification = "Clarification";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";
    public const string Timeout = "Timeout";
    public const string Cancelled = "Cancelled";
}

/// <summary>Pipeline stages, in order. Used for the timeline and for "failed at" / "slowest stage".</summary>
public static class ActivityStages
{
    public const string Request = "Request";
    public const string Authentication = "Authentication";
    public const string AIProvider = "AIProvider";
    public const string Prompt = "Prompt";
    public const string QueryGeneration = "QueryGeneration";
    public const string QueryValidation = "QueryValidation";
    public const string MongoExecution = "MongoExecution";
    public const string ResponseGeneration = "ResponseGeneration";
    public const string Completed = "Completed";
}

public static class ActivityErrorTypes
{
    public const string AuthenticationError = "AuthenticationError";
    public const string AIProviderError = "AIProviderError";
    public const string PromptError = "PromptError";
    public const string QueryGenerationError = "QueryGenerationError";
    public const string QueryValidationError = "QueryValidationError";
    public const string MongoDBError = "MongoDBError";
    public const string TimeoutError = "TimeoutError";
    public const string ResponseGenerationError = "ResponseGenerationError";
    public const string AttachmentError = "AttachmentError";
    public const string CancelledError = "CancelledError";
    public const string UnknownError = "UnknownError";
}

public sealed class AIActivity : Entity
{
    /// <summary>Public identifier (UUID, "N" format). The Mongo _id stays internal.</summary>
    public string ActivityId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? ConversationId { get; set; }
    /// <summary>Assistant message id (when conversations are saved).</summary>
    public string? MessageId { get; set; }
    public string? UserMessageId { get; set; }
    public string? QueryLogId { get; set; }
    public string? UserId { get; set; }
    /// <summary>Display name of the user (for the AI dashboard). Never a token or e-mail secret.</summary>
    public string? UserName { get; set; }
    /// <summary>Hashed tenant reference (customer database / company). Never the raw database name.</summary>
    public string TenantRef { get; set; } = string.Empty;
    public string AuthSource { get; set; } = "agent";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>See <see cref="ActivityStatuses"/>.</summary>
    public string Status { get; set; } = ActivityStatuses.Success;
    /// <summary>Agent chat status (Success, NoResults, InvalidQuery, ProviderError…).</summary>
    public string ChatStatus { get; set; } = string.Empty;
    /// <summary>Stage where the request failed (null when it succeeded). See <see cref="ActivityStages"/>.</summary>
    public string? FailedStage { get; set; }

    public ActivityRequestInfo Request { get; set; } = new();
    public ActivityAiInfo Ai { get; set; } = new();
    public ActivityQueryInfo? Query { get; set; }
    public ActivityValidationInfo? Validation { get; set; }
    public ActivityExecutionInfo? Execution { get; set; }
    public ActivityResultInfo? Result { get; set; }
    public ActivityResponseInfo? Response { get; set; }
    public List<ActivityErrorInfo> Errors { get; set; } = new();
    public ActivityPerformance Performance { get; set; } = new();
    public List<ActivityStageEntry> Timeline { get; set; } = new();

    /// <summary>
    /// Null = stored in plain text. "dataprotection-v1" = the MQL, raw model output, rejected query and stored rows are
    /// encrypted with ASP.NET Core Data Protection (Activity:EncryptPayloads); the admin API decrypts them.
    /// </summary>
    public string? PayloadEncryption { get; set; }

    /// <summary>The request id (same value as <see cref="ActivityId"/>).</summary>
    public string RequestId => ActivityId;
}

public sealed class ActivityRequestInfo
{
    public string Question { get; set; } = string.Empty;
    /// <summary>"en" | "ml" | "ar" | voice language code.</summary>
    public string Language { get; set; } = "en";
    /// <summary>"text" | "voice" | "attachment"</summary>
    public string InputType { get; set; } = "text";
    public int AttachmentCount { get; set; }
    /// <summary>Calling application (X-Client-App), e.g. "mysalebooks-web", "ai-dashboard".</summary>
    public string? ClientApp { get; set; }
    public string? ClientVersion { get; set; }
    /// <summary>"mysalebooks" | "dashboard" | "api" and the endpoint ("chat" / "chat-stream").</summary>
    public string Source { get; set; } = "api";
    public string? Endpoint { get; set; }
    public string? SessionId { get; set; }
    public bool Streamed { get; set; }
    public bool Regenerated { get; set; }
    /// <summary>Store context: Selected | AllStores | None | Missing, why, and the selected store (MySaleBooks "Store Location").</summary>
    public string? StoreMode { get; set; }
    public string? StoreReason { get; set; }
    public string? StoreId { get; set; }
    public string? StoreName { get; set; }
}

public sealed class ActivityAiInfo
{
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ProviderKind { get; set; }
    public bool IsLocal { get; set; }
    public string? Model { get; set; }
    /// <summary>Model version when the provider reports one (otherwise null).</summary>
    public string? ModelVersion { get; set; }
    /// <summary>Prompt template version (PromptBuilder.Version).</summary>
    public string? PromptVersion { get; set; }
    /// <summary>SHA-256 prefix of the rendered system prompt — identifies the exact prompt without storing it.</summary>
    public string? SystemPromptRef { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Total time spent waiting for the model (query generation + repairs + answer).</summary>
    public long DurationMs { get; set; }
    public int Attempts { get; set; }
    public int RepairAttempts { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    /// <summary>Structured intent derived by the application, e.g. "database:aggregate", "attachment:table", "unsupported".</summary>
    public string? Intent { get; set; }
    /// <summary>Only set when the model/application produces a confidence value (currently none do).</summary>
    public double? Confidence { get; set; }
    /// <summary>Explanation the model returned with the query (part of the JSON contract — not hidden reasoning).</summary>
    public string? Explanation { get; set; }
    public string? VisualizationHint { get; set; }
    /// <summary>Reason returned with an "unsupported" answer.</summary>
    public string? DeclineReason { get; set; }

    /// <summary>Action chosen for the request: "database-query" | "attachment-answer" | "attachment-table" |
    /// "attachment+database" | "declined" | "refused-technical-details" | "none".</summary>
    public string ToolSelected { get; set; } = "none";
    /// <summary>"NotRun" | "Generated" | "Unparseable" | "Declined" | "Failed"</summary>
    public string QueryGenerationStatus { get; set; } = "NotRun";
    /// <summary>"NotRun" | "Generated" | "Failed" | "Skipped" (fixed reply without an AI answer, e.g. no rows / blocked)</summary>
    public string ResponseGenerationStatus { get; set; } = "NotRun";
    /// <summary>Overall: "Completed" | "Failed" | "NotRun".</summary>
    public string GenerationStatus { get; set; } = "NotRun";
    /// <summary>Collections offered to the model (allowed schema) — the schema context of this request.</summary>
    public List<string> SchemaCollections { get; set; } = new();
    /// <summary>SHA-256 prefix of the schema context (collection + field names) — identifies the exact schema version used.</summary>
    public string? SchemaContextRef { get; set; }
}

public sealed class ActivityQueryInfo
{
    public string? Collection { get; set; }
    public string? Operation { get; set; }
    /// <summary>The structured query exactly as generated by the AI (JSON, before validation/scoping), redacted.</summary>
    public string? GeneratedQueryJson { get; set; }
    public string? PipelineJson { get; set; }
    public string? FilterJson { get; set; }
    public string? ProjectionJson { get; set; }
    public string? SortJson { get; set; }
    public int? Limit { get; set; }
    /// <summary>Validated + tenant-scoped pipeline that was sent to MongoDB (shell syntax), complete.</summary>
    public string? ExecutedMql { get; set; }
    /// <summary>The exact pipeline sent to MongoDB as Extended JSON (every stage, not truncated).</summary>
    public string? ExecutedPipelineJson { get; set; }
    /// <summary>When the first query returned no rows and was retried with tolerant text matching: the first executed MQL (exact).</summary>
    public string? InitialExecutedMql { get; set; }
    /// <summary>Fields whose exact text match was made case-/space-insensitive in the retry.</summary>
    public List<string> RelaxedFields { get; set; } = new();
    /// <summary>Stage operators of the executed pipeline in order, e.g. ["$match","$lookup","$group","$sort","$limit"].</summary>
    public List<string> Stages { get; set; } = new();
    /// <summary>Server-side query parameters: tenant scope, max records, timeout, date anchors, repair attempts…</summary>
    public string? QueryParametersJson { get; set; }
    /// <summary>Raw model output of every attempt (redacted, truncated).</summary>
    public List<ActivityQueryAttempt> Attempts { get; set; } = new();
    public DateTime? GeneratedAt { get; set; }
    public long GenerationDurationMs { get; set; }
    /// <summary>SHA-256 of the normalised generated query — groups identical queries.</summary>
    public string? QueryHash { get; set; }
    /// <summary>Query contract version (e.g. "mql-v1", "attachment-plan-v1").</summary>
    public string QueryVersion { get; set; } = "mql-v1";
    /// <summary>"query" | "attachment" | "table" | "combined" | "unsupported"</summary>
    public string Kind { get; set; } = "query";
}

public sealed class ActivityQueryAttempt
{
    public int Attempt { get; set; }
    public DateTime At { get; set; }
    public long DurationMs { get; set; }
    public string? RawOutput { get; set; }
    public bool Parsed { get; set; }
    public string? ValidationStatus { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class ActivityValidationInfo
{
    /// <summary>"Approved" | "Rejected" | "Blocked" | "NotRun"</summary>
    public string Status { get; set; } = "NotRun";
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> BlockedOperations { get; set; } = new();
    public bool Blocked { get; set; }
    public long DurationMs { get; set; }
    /// <summary>The rejected query (redacted) — kept for security review / debugging.</summary>
    public string? RejectedQueryJson { get; set; }
}

public sealed class ActivityExecutionInfo
{
    /// <summary>"Success" | "Failed" | "Timeout" | "NotRun"</summary>
    public string Status { get; set; } = "NotRun";
    public string TenantRef { get; set; } = string.Empty;
    public string? Collection { get; set; }
    public string? Operation { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long DurationMs { get; set; }
    /// <summary>Not collected (would need an explain() round-trip). Kept for schema completeness.</summary>
    public long? DocumentsExamined { get; set; }
    public int DocumentsReturned { get; set; }
    public bool Truncated { get; set; }
    public bool TimedOut { get; set; }
    public string? Error { get; set; }
    /// <summary>MongoDB comment attached to the command ("ayaan:{correlationId}") — find it in the profiler.</summary>
    public string? Comment { get; set; }
    /// <summary>Server time limit (maxTimeMS) applied to the query.</summary>
    public int TimeoutLimitMs { get; set; }
    /// <summary>Document cap requested from MongoDB (max records + 1 to detect truncation).</summary>
    public int MaxDocuments { get; set; }
    /// <summary>Execution retries (the agent does not retry a MongoDB query; repairs happen before execution).</summary>
    public int Retries { get; set; }
}

public sealed class ActivityResultInfo
{
    public int Count { get; set; }
    public List<string> Columns { get; set; } = new();
    /// <summary>{ column: type } JSON.</summary>
    public string? SchemaJson { get; set; }
    /// <summary>Per numeric column: count / sum / min / max (computed over all returned rows).</summary>
    public string? SummaryJson { get; set; }
    public long DataSizeBytes { get; set; }
    public string? ResultHash { get; set; }
    public bool Stored { get; set; }
    public int StoredDocuments { get; set; }
    public bool StoredTruncated { get; set; }
    /// <summary>Returned rows (capped, sensitive fields masked) when Activity:StoreFullResults is on.</summary>
    public string? DataJson { get; set; }
}

public sealed class ActivityResponseInfo
{
    /// <summary>The exact final response sent to the user.</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>The raw answer returned by the AI model (before the final trim / fallback), when an AI answer was generated.</summary>
    public string? AiGeneratedText { get; set; }
    /// <summary>True when the response to the client included technical details (developer/admin dashboard only).</summary>
    public bool TechnicalDetailsReturned { get; set; }
    public string Format { get; set; } = "markdown";
    public string? VisualizationType { get; set; }
    /// <summary>Visualization spec (KPI / chart / table config) as returned to the client.</summary>
    public string? VisualizationJson { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long DurationMs { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool GroundingChecked { get; set; }
    public List<string> GroundingWarnings { get; set; } = new();
}

public sealed class ActivityErrorInfo
{
    public string Type { get; set; } = ActivityErrorTypes.UnknownError;
    public string Stage { get; set; } = ActivityStages.Request;
    public string Message { get; set; } = string.Empty;
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string CorrelationId { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    /// <summary>Only stored when Activity:StoreStackTraces is enabled (development). Never returned to customers.</summary>
    public string? StackTrace { get; set; }
}

public sealed class ActivityPerformance
{
    public long TotalDurationMs { get; set; }
    public long AiGenerationDurationMs { get; set; }
    public long QueryValidationDurationMs { get; set; }
    public long MongoExecutionDurationMs { get; set; }
    public long ResponseGenerationDurationMs { get; set; }
    /// <summary>Everything else: auth context, schema, conversation load/save, attachments.</summary>
    public long OtherDurationMs { get; set; }
    /// <summary>"AI" | "QueryValidation" | "MongoDB" | "ResponseGeneration" | "Other"</summary>
    public string SlowestStage { get; set; } = "Other";
}

public sealed class ActivityStageEntry
{
    public string Stage { get; set; } = string.Empty;
    /// <summary>"ok" | "error" | "warning" | "skipped" | "info"</summary>
    public string Status { get; set; } = "ok";
    public DateTime At { get; set; }
    public long? DurationMs { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Conversation-level activity summary. Id = conversation id.</summary>
public sealed class AIConversationActivity
{
    public string Id { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string TenantRef { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public int MessageCount { get; set; }
    public int TotalRequests { get; set; }
    public int SuccessfulRequests { get; set; }
    public int FailedRequests { get; set; }
    public long TotalExecutionMs { get; set; }
    public long TotalAiMs { get; set; }
    public long TotalMongoMs { get; set; }
    public string? LastProvider { get; set; }
    public string? LastModel { get; set; }
    /// <summary>Status of the most recent request.</summary>
    public string Status { get; set; } = ActivityStatuses.Success;
}
