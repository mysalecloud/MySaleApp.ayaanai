namespace MySale.AI.Domain;

/// <summary>Base for all persisted agent entities. Ids are 24-char hex strings (Mongo ObjectId).</summary>
public abstract class Entity
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum UserRole { Admin, Tester, Viewer }

/// <summary>Agent login user (test authentication). Replaced by MySaleBooks users later.</summary>
public sealed class AppUser : Entity
{
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Tester;
    public string CompanyId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string Currency { get; set; } = "AED";
    public string TimeZone { get; set; } = "Asia/Dubai";
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
}

public enum ProviderCategory { Local, Cloud }

public enum ProviderStatus { Unknown, Connected, Disconnected, NotConfigured, Disabled }

public sealed class ProviderConfig : Entity
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Implementation key: "ollama", "openai", "openai-compatible".</summary>
    public string Kind { get; set; } = "ollama";
    public ProviderCategory Category { get; set; } = ProviderCategory.Local;
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>Data-Protection ciphertext. Never leaves the backend.</summary>
    public string? ApiKeyEncrypted { get; set; }
    /// <summary>Last 4 characters of the key for display ("…abcd").</summary>
    public string? ApiKeyHint { get; set; }
    public string DefaultModel { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.1;
    public int MaxTokens { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    /// <summary>USD per 1M input tokens (0 for local models).</summary>
    public decimal InputCostPer1M { get; set; }
    public decimal OutputCostPer1M { get; set; }
    public ProviderStatus LastTestStatus { get; set; } = ProviderStatus.Unknown;
    public DateTime? LastTestedAt { get; set; }
    public string? LastTestMessage { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Conversation : Entity
{
    public string UserId { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    /// <summary>Display name of the owner (AI dashboard lists conversations of the whole tenant).</summary>
    public string? UserName { get; set; }
    public string Title { get; set; } = "New conversation";
    /// <summary>Customer database of the owner (MySaleBooks JWT "dbName"); a conversation is never used under another database.</summary>
    public string? DatabaseName { get; set; }
    public bool Archived { get; set; }
    public int MessageCount { get; set; }
    public string? LastProviderName { get; set; }
    public string? LastModel { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum MessageRole { User, Assistant }

public enum ChatStatus
{
    Pending,
    Success,
    NoResults,
    Unsupported,
    InvalidQuery,
    ProviderError,
    DatabaseError,
    Timeout,
    Error,
    /// <summary>AYAAN asked the customer a question (quantity or value? which ledger?) — a normal reply, not an error.</summary>
    Clarification
}

public sealed class ChatMessage : Entity
{
    public string ConversationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    /// <summary>User turns: the complete request after merging clarification answers ("Show my stock — stock value").</summary>
    public string? ResolvedQuestion { get; set; }
    /// <summary>Assistant clarification turns: the choices offered to the customer.</summary>
    public List<string> ClarificationOptions { get; set; } = new();
    /// <summary>Structured value of each choice (same order): "UNPAID_INVOICE_AGE", "YES", "OPTION_2" — sent back when clicked.</summary>
    public List<string> ClarificationValues { get; set; } = new();
    /// <summary>choice | yesNo | open | date — how the question is answered.</summary>
    public string? ClarificationKind { get; set; }
    /// <summary>"text" | "voice" | "attachment"</summary>
    public string InputType { get; set; } = "text";
    public List<AttachmentRef> Attachments { get; set; } = new();
    public VoiceInfo? Voice { get; set; }
    public ChatStatus Status { get; set; } = ChatStatus.Success;
    public string? ErrorMessage { get; set; }

    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ProviderKind { get; set; }
    public string? Model { get; set; }

    public string? Operation { get; set; }
    public string? Collection { get; set; }
    public string? Explanation { get; set; }
    /// <summary>The validated query as produced by the AI (compact JSON). Used as context for follow-ups.</summary>
    public string? QueryJson { get; set; }
    /// <summary>Tenant-scoped pipeline in shell syntax (db.X.aggregate([...])).</summary>
    public string? Mql { get; set; }
    public bool QueryGenerated { get; set; }
    public bool QueryValidated { get; set; }
    public bool QueryExecuted { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
    public int RepairAttempts { get; set; }

    /// <summary>Result rows (JSON array string, capped).</summary>
    public string? DataJson { get; set; }
    public List<string> Columns { get; set; } = new();
    public string? VisualizationJson { get; set; }
    public int ResultCount { get; set; }
    public bool Truncated { get; set; }

    public long ExecutionTimeMs { get; set; }
    public long ResponseTimeMs { get; set; }
    public long AiQueryTimeMs { get; set; }
    public long AiAnswerTimeMs { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public bool GroundingChecked { get; set; }
    public List<string> GroundingWarnings { get; set; } = new();
    public string? QueryLogId { get; set; }
}

public sealed class QueryLog : Entity
{
    public string Question { get; set; } = string.Empty;
    /// <summary>The request actually planned when the question answered a clarification (original request + answers).</summary>
    public string? ResolvedQuestion { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    public string? ConversationId { get; set; }
    public string? MessageId { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ProviderKind { get; set; }
    public bool IsLocalProvider { get; set; }
    public string? Model { get; set; }
    public string? Operation { get; set; }
    public string? Collection { get; set; }
    /// <summary>Raw text returned by the model for the query step.</summary>
    public string? GeneratedMql { get; set; }
    /// <summary>Final tenant-scoped query (shell syntax).</summary>
    public string? FinalMql { get; set; }
    public bool QueryGenerated { get; set; }
    public bool ValidationPassed { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
    public bool Blocked { get; set; }
    public int RepairAttempts { get; set; }
    public bool Executed { get; set; }
    public bool ExecutionSucceeded { get; set; }
    public long ExecutionTimeMs { get; set; }
    public int ResultCount { get; set; }
    public ChatStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public long AiQueryTimeMs { get; set; }
    public long AiAnswerTimeMs { get; set; }
    public long TotalTimeMs { get; set; }
    public bool Streamed { get; set; }
    public int GroundingWarningCount { get; set; }
    public string? DebugTraceJson { get; set; }
}

public sealed class AuditLog : Entity
{
    public string Action { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? CompanyId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    /// <summary>AI dashboard access audit: section / resource type (e.g. "QueryLogs"), resource id, tenant ref, result.</summary>
    public string? Resource { get; set; }
    public string? ResourceId { get; set; }
    public string? TenantRef { get; set; }
    public string? Outcome { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>Business database connection (the database the AI queries).</summary>
public sealed class DatabaseConfig
{
    public string Id { get; set; } = "business";
    public string ConnectionStringEncrypted { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string DisplayHost { get; set; } = string.Empty;
    public bool HasCredentials { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
    public string? LastTestStatus { get; set; }
    public DateTime? LastTestedAt { get; set; }
}

/// <summary>Saved (AI-generated or hand-edited) metadata for one business collection. Id = collection name.</summary>
public sealed class SchemaMetadata
{
    public string Id { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>null = default tenant field; "" = shared collection (no company filter).</summary>
    public string? TenantField { get; set; }
    public List<SchemaFieldMetadata> Fields { get; set; } = new();
    /// <summary>"ai" | "manual"</summary>
    public string Source { get; set; } = "manual";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

public sealed class SchemaFieldMetadata
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Example { get; set; }
    public string? Relationship { get; set; }
    /// <summary>Hidden fields are not sent to the AI (e.g. internal / sensitive fields).</summary>
    public bool Hidden { get; set; }
}

public sealed class AttachmentRef
{
    public string AttachmentId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public long Size { get; set; }
}

public sealed class VoiceInfo
{
    public string? AttachmentId { get; set; }
    public string? Language { get; set; }
    public double? DurationSeconds { get; set; }
}

public static class AttachmentKinds
{
    public const string Image = "image";
    public const string Pdf = "pdf";
    public const string Text = "text";
    public const string Csv = "csv";
    public const string Excel = "excel";
    public const string Audio = "audio";
}

public enum AttachmentStatus { Uploaded, Processing, Processed, Failed }

/// <summary>An uploaded file. The binary lives in isolated storage (never a user-supplied path); content extracted here.</summary>
public sealed class Attachment : Entity
{
    public string UserId { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    public string? ConversationId { get; set; }
    /// <summary>Sanitized display name.</summary>
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    /// <summary>MIME type detected from the file content (not the browser's claim).</summary>
    public string ContentType { get; set; } = "application/octet-stream";
    public string Kind { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>Opaque storage key (relative, server-generated).</summary>
    public string StorageKey { get; set; } = string.Empty;
    public AttachmentStatus Status { get; set; } = AttachmentStatus.Uploaded;
    public string? Error { get; set; }
    public int? PageCount { get; set; }
    /// <summary>Extracted text split into chunks (documents, or image content read by a vision model).</summary>
    public List<AttachmentChunk> Chunks { get; set; } = new();
    public bool Truncated { get; set; }
    /// <summary>Parsed table for CSV / Excel (capped).</summary>
    public AttachmentTable? Table { get; set; }
    public string? Summary { get; set; }
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
}

public sealed class AttachmentChunk
{
    public int Index { get; set; }
    public int? Page { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class AttachmentTable
{
    public string? Sheet { get; set; }
    public List<string> SheetNames { get; set; } = new();
    public List<string> Columns { get; set; } = new();
    /// <summary>"number" | "date" | "text" per column.</summary>
    public List<string> ColumnTypes { get; set; } = new();
    public List<List<string?>> Rows { get; set; } = new();
    public int TotalRows { get; set; }
}

// ---------------------------------------------------------------- conversation state (clarifications, turn order)

/// <summary>
/// Server-side state of one conversation, keyed by the conversation id and owned by one tenant + user + customer
/// database. Holds the open clarification (original request, answers so far, the question asked), the last completed
/// request (for "show the same for last month" when history is off), a lease that serialises turns, and the ids of the
/// recent client messages (duplicate protection). Never sent to the client as a whole.
/// </summary>
public sealed class ConversationState
{
    /// <summary>= conversation id (ObjectId text for saved conversations, a GUID for unsaved ones).</summary>
    public string Id { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string? DatabaseName { get; set; }
    /// <summary>Turn currently being answered (lease). Null = idle.</summary>
    public string? ActiveTurnId { get; set; }
    public DateTime? ActiveTurnStartedAt { get; set; }
    public long Version { get; set; }
    public PendingClarification? Pending { get; set; }
    /// <summary>
    /// An open question that was set aside because the customer changed the subject ("What are today's sales?" while
    /// AYAAN waited for the debtors report type). "continue" / "go back" brings it back; a new question replaces it.
    /// </summary>
    public PendingClarification? Suspended { get; set; }
    public LastRequestInfo? LastRequest { get; set; }
    /// <summary>Business intent of the active (or last) request: DEBTORS_REPORT, SALES, STOCK … (see ConversationIntents).</summary>
    public string? Intent { get; set; }
    /// <summary>Clarification state machine: NEW_REQUEST → … → WAITING_FOR_USER → CLARIFICATION_RESOLVED → … → PRESENT_RESULT.</summary>
    public string Stage { get; set; } = "NEW_REQUEST";
    /// <summary>Parameters collected for the active request (reportType = UNPAID_INVOICE_AGE, period = this month …).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();
    /// <summary>Parameters of the last completed request ("same" / "previous" reuse them).</summary>
    public Dictionary<string, string> PreviousParameters { get; set; } = new();
    /// <summary>Parameters AYAAN still asks for (the open question's parameter).</summary>
    public List<string> Unresolved { get; set; } = new();
    /// <summary>The previous user request (complete, after clarification answers) and the reply shown for it.</summary>
    public string? LastUserRequest { get; set; }
    public string? LastAssistantText { get; set; }
    /// <summary>Short summary of the latest completed requests (newest last, at most 5) — context without unlimited history.</summary>
    public List<string> Summary { get; set; } = new();
    public List<TurnRecord> RecentTurns { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>TTL: state of idle conversations is removed after this time.</summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(30);
}

/// <summary>An open question AYAAN asked, with everything needed to continue the original request.</summary>
public sealed class PendingClarification
{
    /// <summary>The customer's original request (dates already corrected by earlier answers).</summary>
    public string OriginalQuestion { get; set; } = string.Empty;
    /// <summary>The question AYAAN asked (shown to the customer).</summary>
    public string Question { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
    /// <summary>What is missing, in business words ("stock quantity or value").</summary>
    public string? Missing { get; set; }
    /// <summary>model | businessTerms | ambiguousDate | invalidDate | report | answerFollowUp</summary>
    public string Source { get; set; } = "model";
    /// <summary>choice (fixed options) | yesNo | open (a name, period, amount …) | date. See ClarificationTypes.</summary>
    public string Kind { get; set; } = "open";
    /// <summary>What the answer fills: reportType, measure, period, warehouse, branch, customer, supplier, item, account, confirm …</summary>
    public string? Parameter { get; set; }
    /// <summary>Business intent of the request this question belongs to (DEBTORS_REPORT, SALES, STOCK …).</summary>
    public string? Intent { get; set; }
    /// <summary>Structured value of each option (same order as Options): "UNPAID_INVOICE_AGE", "YES", "OPTION_1" …</summary>
    public List<string> OptionValues { get; set; } = new();
    /// <summary>
    /// A follow-up offer at the end of an answer ("Do you want it by ledger balance instead?"). Only a short contextual
    /// reply ("yes", "the second one", "ledger") answers it; any other message is a new request.
    /// </summary>
    public bool Soft { get; set; }
    /// <summary>How many times the same question was asked again because the reply did not choose an option.</summary>
    public int Repeats { get; set; }
    /// <summary>Date clarifications: the text of the original request that the answer replaces.</summary>
    public string? ReplaceText { get; set; }
    /// <summary>Verified record ids of the options (same order), e.g. the matching items of a stock question.</summary>
    public List<string> OptionIds { get; set; } = new();
    /// <summary>Server report plan (JSON arguments) that asked the question; the answer re-runs it directly.</summary>
    public string? Plan { get; set; }
    /// <summary>Report argument the answer fills ("item", "ledger").</summary>
    public string? PlanArgument { get; set; }
    /// <summary>Answers already given to earlier questions of the same request (multi-step clarification).</summary>
    public List<ClarificationAnswer> Answers { get; set; } = new();
    public int Step { get; set; } = 1;
    public string? StoreId { get; set; }
    public string? StoreName { get; set; }
    /// <summary>Assistant message that asked the question (a reply to an older message is not merged).</summary>
    public string? AssistantMessageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime AskedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ClarificationAnswer
{
    public string Question { get; set; } = string.Empty;
    public string Reply { get; set; } = string.Empty;
    /// <summary>The option / normalised meaning of the reply ("Value" → "stock value", "ഇന്നലെ" → "yesterday").</summary>
    public string? Resolved { get; set; }
    /// <summary>Structured value of the chosen option ("UNPAID_INVOICE_AGE", "YES") when the reply chose one.</summary>
    public string? Value { get; set; }
    /// <summary>Parameter the answer filled (reportType, period, warehouse …).</summary>
    public string? Parameter { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public sealed class LastRequestInfo
{
    public string Question { get; set; } = string.Empty;
    public string? QueryJson { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public sealed class TurnRecord
{
    public string? ClientMessageId { get; set; }
    public string? UserMessageId { get; set; }
    public string? AssistantMessageId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}
