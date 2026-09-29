using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using MySale.AI.Domain;

namespace MySale.AI.Application.Contracts;

// ---------------- Auth ----------------

public sealed class LoginRequest
{
    [Required, StringLength(100)] public string UserName { get; set; } = string.Empty;
    [Required, StringLength(200)] public string Password { get; set; } = string.Empty;
}

public sealed record UserDto(string Id, string UserName, string DisplayName, UserRole Role, string CompanyId, string CompanyName, string Currency, string TimeZone,
    string? DatabaseName = null, string AuthSource = "demo");

public sealed record LoginResponse(string Token, DateTime ExpiresAt, UserDto User);

public sealed class SwitchCompanyRequest
{
    [Required, StringLength(64, MinimumLength = 1)] public string CompanyId { get; set; } = string.Empty;
    [StringLength(120)] public string? CompanyName { get; set; }
    [StringLength(8)] public string? Currency { get; set; }
    [StringLength(64)] public string? TimeZone { get; set; }
}

public sealed record DemoUserDto(string UserName, string DisplayName, UserRole Role, string CompanyName);

// ---------------- Chat ----------------

public sealed class ChatRequest
{
    [StringLength(64)] public string? ConversationId { get; set; }
    /// <summary>May be empty when attachments are sent (a default instruction is used).</summary>
    [StringLength(2000)] public string Message { get; set; } = string.Empty;
    [StringLength(64)] public string? ProviderId { get; set; }
    [StringLength(200)] public string? Model { get; set; }
    /// <summary>Assistant message to replace (regenerate).</summary>
    [StringLength(64)] public string? RegenerateMessageId { get; set; }
    /// <summary>Uploaded attachment ids sent with this message.</summary>
    public List<string>? Attachments { get; set; }
    /// <summary>"text" | "voice" | "attachment" (derived when omitted).</summary>
    [StringLength(20)] public string? InputType { get; set; }
    public VoiceInputDto? Voice { get; set; }
    /// <summary>Client-generated id of this message: a resent (duplicate) message is answered once, never processed twice.</summary>
    [StringLength(64)] public string? ClientMessageId { get; set; }
    /// <summary>Assistant message this reply answers (the clarification question shown). An answer to an older question is not merged.</summary>
    [StringLength(64)] public string? ReplyToMessageId { get; set; }
    /// <summary>
    /// A clicked option of the open question: its structured value ("UNPAID_INVOICE_AGE", "YES") decides the answer —
    /// the visible label is never parsed. Only values the server offered for the open question are accepted.
    /// </summary>
    public ClarificationChoiceDto? Choice { get; set; }
}

/// <summary>One selectable answer of a clarification question.</summary>
public sealed class ClarificationChoiceDto
{
    [StringLength(200)] public string DisplayText { get; set; } = string.Empty;
    [StringLength(100)] public string Value { get; set; } = string.Empty;
}

/// <summary>Conversation context for restoring the chat after a reload (never the whole server state).</summary>
public sealed class ConversationContextDto
{
    public string ConversationId { get; set; } = string.Empty;
    /// <summary>Business intent of the active request (DEBTORS_REPORT, SALES, STOCK …).</summary>
    public string? Intent { get; set; }
    public string Stage { get; set; } = "NEW_REQUEST";
    /// <summary>The open question, when AYAAN is waiting for an answer; null otherwise.</summary>
    public PendingClarificationDto? Pending { get; set; }
}

public sealed class PendingClarificationDto
{
    public string Question { get; set; } = string.Empty;
    /// <summary>choice | yesNo | open | date</summary>
    public string Kind { get; set; } = "open";
    public List<ClarificationChoiceDto> Choices { get; set; } = new();
    public int Step { get; set; } = 1;
    /// <summary>Assistant message that asked the question (the client sends it back as replyToMessageId).</summary>
    public string? MessageId { get; set; }
    /// <summary>True for a follow-up offer at the end of an answer ("Do you want it by ledger balance?").</summary>
    public bool FollowUp { get; set; }
    public DateTime AskedAt { get; set; }
}

public sealed class VoiceInputDto
{
    [StringLength(64)] public string? AttachmentId { get; set; }
    [StringLength(10)] public string? Language { get; set; }
    [Range(0, 3600)] public double? DurationSeconds { get; set; }
    /// <summary>"server" (backend speech-to-text) or "browser" (Web Speech API fallback).</summary>
    [StringLength(20)] public string? Engine { get; set; }
}

public sealed class VisualizationDto
{
    /// <summary>kpi | table | bar | line | pie | none</summary>
    public string Type { get; set; } = "none";
    public string? XField { get; set; }
    public List<string> YFields { get; set; } = new();
}

public sealed class QueryInfoDto
{
    public bool Generated { get; set; }
    public bool Validated { get; set; }
    public bool Executed { get; set; }
    public string? Operation { get; set; }
    public string? Collection { get; set; }
    public string? Mql { get; set; }
    public string? Explanation { get; set; }
    public long ExecutionTimeMs { get; set; }
    public int ResultCount { get; set; }
    public bool Truncated { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
    public int RepairAttempts { get; set; }
}

public sealed record ProviderRefDto(string? Id, string? Name, string? Kind, string? Model);

public sealed class UsageDto
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens => InputTokens + OutputTokens;
    public decimal EstimatedCost { get; set; }
}

public sealed class TimingDto
{
    public long TotalMs { get; set; }
    public long AiQueryMs { get; set; }
    public long DbMs { get; set; }
    public long AiAnswerMs { get; set; }
}

public sealed class GroundingDto
{
    public bool Checked { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class DebugStep
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long? DurationMs { get; set; }
    /// <summary>ok | warning | error | info</summary>
    public string Status { get; set; } = "info";
}

public sealed class DebugTrace
{
    public List<DebugStep> Steps { get; set; } = new();

    public void Add(string name, string title, string content, string status = "info", long? durationMs = null)
        => Steps.Add(new DebugStep { Name = name, Title = title, Content = content, Status = status, DurationMs = durationMs });
}

public sealed class ChatResponse
{
    public string ConversationId { get; set; } = string.Empty;
    public string ConversationTitle { get; set; } = string.Empty;
    public string UserMessageId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public ChatStatus Status { get; set; }
    public string Answer { get; set; } = string.Empty;
    public JsonArray? Data { get; set; }
    public List<string> Columns { get; set; } = new();
    public VisualizationDto Visualization { get; set; } = new();
    public QueryInfoDto Query { get; set; } = new();
    public ProviderRefDto Provider { get; set; } = new(null, null, null, null);
    public UsageDto Usage { get; set; } = new();
    public TimingDto Timing { get; set; } = new();
    public GroundingDto Grounding { get; set; } = new();
    public string? QueryLogId { get; set; }
    public DebugTrace? Debug { get; set; }
    /// <summary>Developer mode only: how the customer database was resolved (never the JWT or credentials).</summary>
    public TenantDebugDto? Tenant { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>True when the request was handled normally: answered (including "no matching records") or a question asked back.</summary>
    public bool Success => Status is ChatStatus.Success or ChatStatus.NoResults or ChatStatus.Clarification;
    /// <summary>How the client shows the reply: answer | clarification | info | blocked | error. Only "error" is an error.</summary>
    public string ResponseType => ResponseTypes.For(Status);
    /// <summary>Set when AYAAN asked a question: the question and the choices (shown as buttons).</summary>
    public ClarificationDto? Clarification { get; set; }
}

public sealed class ClarificationDto
{
    public string Question { get; set; } = string.Empty;
    /// <summary>Labels of the choices (kept for older clients; new clients use <see cref="Choices"/>).</summary>
    public List<string> Options { get; set; } = new();
    /// <summary>The choices with their structured values — send the value back as <c>choice</c> when one is clicked.</summary>
    public List<ClarificationChoiceDto> Choices { get; set; } = new();
    /// <summary>choice | yesNo | open | date</summary>
    public string Kind { get; set; } = "open";
    /// <summary>True when the question is a follow-up offer at the end of an answer (the answer itself is complete).</summary>
    public bool FollowUp { get; set; }
    /// <summary>1 for the first question of a request, 2 for a follow-up question, …</summary>
    public int Step { get; set; } = 1;
}

public static class ResponseTypes
{
    public const string Answer = "answer";
    public const string Clarification = "clarification";
    public const string Info = "info";
    public const string Blocked = "blocked";
    public const string Error = "error";

    public static string For(ChatStatus status) => status switch
    {
        ChatStatus.Success or ChatStatus.NoResults => Answer,
        ChatStatus.Clarification => Clarification,
        ChatStatus.Unsupported => Info,
        ChatStatus.InvalidQuery => Blocked,
        ChatStatus.Pending => Info,
        _ => Error
    };
}

public sealed class TenantDebugDto
{
    public bool JwtValid { get; set; }
    public bool DatabaseResolved { get; set; }
    public string? DatabaseName { get; set; }
    public string Source { get; set; } = "jwt:dbName";
    public List<string> CollectionsUsed { get; set; } = new();
}

// ---------------- Conversations ----------------

public sealed record ConversationSummaryDto(
    string Id, string Title, bool Archived, int MessageCount, string? LastProviderName, string? LastModel,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed class MessageDto
{
    public string Id { get; set; } = string.Empty;
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public ChatStatus Status { get; set; }
    public string ResponseType => ResponseTypes.For(Status);
    /// <summary>Clarification questions: the choices offered.</summary>
    public List<string> Options { get; set; } = new();
    /// <summary>Structured value of each option (same order).</summary>
    public List<string> OptionValues { get; set; } = new();
    public ProviderRefDto? Provider { get; set; }
    public QueryInfoDto? Query { get; set; }
    public JsonArray? Data { get; set; }
    public List<string> Columns { get; set; } = new();
    public VisualizationDto? Visualization { get; set; }
    public UsageDto? Usage { get; set; }
    public TimingDto? Timing { get; set; }
    public GroundingDto? Grounding { get; set; }
    public string? QueryLogId { get; set; }
    public string InputType { get; set; } = "text";
    public List<AttachmentRef> Attachments { get; set; } = new();
    public VoiceInfo? Voice { get; set; }
}

public sealed record ConversationDetailDto(ConversationSummaryDto Conversation, List<MessageDto> Messages);

public sealed class CreateConversationRequest
{
    [StringLength(120)] public string? Title { get; set; }
}

public sealed class UpdateConversationRequest
{
    [StringLength(120, MinimumLength = 1)] public string? Title { get; set; }
    public bool? Archived { get; set; }
}

// ---------------- Providers ----------------

public sealed class ProviderDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string KindDisplayName { get; set; } = string.Empty;
    public ProviderCategory Category { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public string? ApiKeyHint { get; set; }
    public bool RequiresApiKey { get; set; }
    public string DefaultModel { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int MaxTokens { get; set; }
    public int TimeoutSeconds { get; set; }
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
    public decimal InputCostPer1M { get; set; }
    public decimal OutputCostPer1M { get; set; }
    public ProviderStatus Status { get; set; }
    public DateTime? LastTestedAt { get; set; }
    public string? LastTestMessage { get; set; }
    public bool Supported { get; set; } = true;
}

public sealed class ProviderUpsertRequest
{
    [Required, StringLength(80)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(40)] public string Kind { get; set; } = "ollama";
    [Required, StringLength(500), Url] public string BaseUrl { get; set; } = string.Empty;
    /// <summary>Write-only. Null/empty keeps the stored key.</summary>
    [StringLength(500)] public string? ApiKey { get; set; }
    public bool ClearApiKey { get; set; }
    [StringLength(200)] public string DefaultModel { get; set; } = string.Empty;
    [Range(0, 2)] public double Temperature { get; set; } = 0.1;
    [Range(64, 32768)] public int MaxTokens { get; set; } = 1024;
    [Range(5, 600)] public int TimeoutSeconds { get; set; } = 120;
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    [Range(0, 1000)] public decimal InputCostPer1M { get; set; }
    [Range(0, 1000)] public decimal OutputCostPer1M { get; set; }
}

public sealed class ProviderDraftTestRequest
{
    [Required] public ProviderUpsertRequest Config { get; set; } = new();
    /// <summary>When editing an existing provider, the stored key is reused if no key is supplied.</summary>
    [StringLength(64)] public string? ExistingId { get; set; }
}

public sealed record ProviderDraftTestResponse(Abstractions.ProviderTestResult Test, IReadOnlyList<Abstractions.AIModel> Models);

// ---------------- Query sandbox ----------------

public sealed class GenerateQueryRequest
{
    [Required, StringLength(2000, MinimumLength = 1)] public string Question { get; set; } = string.Empty;
    [StringLength(64)] public string? ProviderId { get; set; }
    [StringLength(200)] public string? Model { get; set; }
}

public sealed class QueryBodyRequest
{
    [Required] public JsonObject Query { get; set; } = new();
}

public sealed class ValidationResultDto
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? Mql { get; set; }
    public JsonArray? NormalizedPipeline { get; set; }
    public JsonArray? ScopedPipeline { get; set; }
}

public sealed class GenerateQueryResponse
{
    public string RawOutput { get; set; } = string.Empty;
    public JsonObject? Parsed { get; set; }
    public string? ParseError { get; set; }
    public ValidationResultDto? Validation { get; set; }
    public ProviderRefDto Provider { get; set; } = new(null, null, null, null);
    public UsageDto Usage { get; set; } = new();
    public long DurationMs { get; set; }
    public string Prompt { get; set; } = string.Empty;
}

public sealed class ExecuteQueryResponse
{
    public ValidationResultDto Validation { get; set; } = new();
    public JsonArray Rows { get; set; } = new();
    public int ResultCount { get; set; }
    public bool Truncated { get; set; }
    public long ExecutionTimeMs { get; set; }
    public string? Error { get; set; }
}

// ---------------- Logs / usage / dashboard ----------------

public sealed class QueryLogSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public string? ProviderKind { get; set; }
    public string? Model { get; set; }
    public string? Operation { get; set; }
    public string? Collection { get; set; }
    public string? FinalMql { get; set; }
    public bool ValidationPassed { get; set; }
    public bool Blocked { get; set; }
    public bool Executed { get; set; }
    public bool ExecutionSucceeded { get; set; }
    public ChatStatus Status { get; set; }
    public long ExecutionTimeMs { get; set; }
    public int ResultCount { get; set; }
    public long TotalTimeMs { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int RepairAttempts { get; set; }
    public int GroundingWarningCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class QueryLogDetailDto
{
    public QueryLogSummaryDto Summary { get; set; } = new();
    public string? GeneratedMql { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public decimal EstimatedCost { get; set; }
    public long AiQueryTimeMs { get; set; }
    public long AiAnswerTimeMs { get; set; }
    public string? ConversationId { get; set; }
    public DebugTrace? Debug { get; set; }
}

public sealed record PagedResult<T>(List<T> Items, long Total, int Page, int PageSize);

public sealed record NamedRef(string Id, string Name);

public sealed record LogFiltersDto(List<NamedRef> Providers, List<string> Models, List<NamedRef> Users, List<string> Statuses, List<string> Operations);

public sealed class UsageSummaryDto
{
    public int TotalRequests { get; set; }
    public int RequestsToday { get; set; }
    public int SuccessfulRequests { get; set; }
    public int FailedRequests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens => InputTokens + OutputTokens;
    public decimal EstimatedCost { get; set; }
    public double AvgResponseTimeMs { get; set; }
    public double AvgQueryTimeMs { get; set; }
    public int Errors { get; set; }
    public int QueriesGenerated { get; set; }
    public int ValidQueries { get; set; }
    public int BlockedQueries { get; set; }
    public double AvgExecutionTimeMs { get; set; }
}

public sealed class UsageDailyDto
{
    public string Date { get; set; } = string.Empty;
    public int Requests { get; set; }
    public int Failed { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
}

public sealed class UsageByProviderDto
{
    public string ProviderName { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public string? Model { get; set; }
    public bool IsLocal { get; set; }
    public int Requests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public double AvgResponseTimeMs { get; set; }
    public int Errors { get; set; }
    public double SuccessRate { get; set; }
}

public sealed class UsageReportDto
{
    public UsageSummaryDto Summary { get; set; } = new();
    public List<UsageDailyDto> Daily { get; set; } = new();
    public List<UsageByProviderDto> ByProvider { get; set; } = new();
    public List<StatusCountDto> ByStatus { get; set; } = new();
}

public sealed record StatusCountDto(string Status, int Count);

public sealed class RecentActivityDto
{
    public string Id { get; set; } = string.Empty;
    public string? ConversationId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public string? Model { get; set; }
    public ChatStatus Status { get; set; }
    public long ResponseTimeMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class DashboardDto
{
    public UsageSummaryDto Summary { get; set; } = new();
    public List<UsageDailyDto> Daily { get; set; } = new();
    public List<UsageByProviderDto> ByProvider { get; set; } = new();
    public List<ProviderDto> Providers { get; set; } = new();
    public List<RecentActivityDto> Recent { get; set; } = new();
    public List<StatusCountDto> ByStatus { get; set; } = new();
}

// ---------------- Settings ----------------

public sealed class SettingsDto
{
    public AiSettings Ai { get; set; } = new();
    public QuerySettings Query { get; set; } = new();
    public ChatSettings Chat { get; set; } = new();
    /// <summary>Null = keep the stored value (older clients).</summary>
    public SpeechSettings? Speech { get; set; }
    public AttachmentSettings? Attachments { get; set; }
}

// ---------------- Test questions ----------------

public sealed record TestQuestionGroup(string Category, string Icon, List<string> Questions);
