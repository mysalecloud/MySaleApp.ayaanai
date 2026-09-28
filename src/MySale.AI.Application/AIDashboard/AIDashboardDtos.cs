using MySale.AI.Application.Contracts;

namespace MySale.AI.Application.AIDashboard;

// DTOs of the AIDashboard inside MySaleBooks. Each screen gets only the fields it needs ("minimum data"):
// no secrets, tokens, connection strings, database names, stack traces or raw exception messages anywhere.

public sealed class AIDashboardAccessDto
{
    public string UserName { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    /// <summary>Granted aidashboard.* permissions.</summary>
    public List<string> Permissions { get; init; } = new();
    /// <summary>Sections the user may open: Overview, Conversations, QueryLogs, Activity, Usage, Providers, Settings.</summary>
    public List<string> Sections { get; init; } = new();
    public int SessionIdleMinutes { get; init; }
    public bool CanSeeMql { get; init; }
    /// <summary>AYAAN Dashboard account of the current session (second authentication layer) and when that session ends.</summary>
    public string? AyaanUserName { get; init; }
    public DateTime? AyaanSessionExpiresAt { get; init; }
}

public sealed class AIDashboardOverviewDto
{
    public int Days { get; init; }
    public long Conversations { get; init; }
    public int Questions { get; init; }
    public int Successful { get; init; }
    public int Failed { get; init; }
    public double AvgResponseTimeMs { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    public int QueriesGenerated { get; init; }
    public int QueriesExecuted { get; init; }
    public int BlockedQueries { get; init; }
    public double AvgExecutionTimeMs { get; init; }
    public List<AIDashboardDailyDto> Daily { get; init; } = new();
    public List<StatusCountDto> ByStatus { get; init; } = new();
}

public sealed record AIDashboardDailyDto(string Date, int Requests, int Failed);

public sealed class AIDashboardConversationDto
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public int MessageCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class AIDashboardMessageDto
{
    public string Id { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string Status { get; init; } = string.Empty;
    public int? ResultCount { get; init; }
}

public sealed class AIDashboardConversationDetailDto
{
    public AIDashboardConversationDto Conversation { get; init; } = new();
    public List<AIDashboardMessageDto> Messages { get; init; } = new();
}

public class AIDashboardQueryLogDto
{
    public string Id { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string UserName { get; init; } = string.Empty;
    public string Question { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    /// <summary>NotGenerated | Passed | Failed | Blocked</summary>
    public string ValidationStatus { get; init; } = string.Empty;
    /// <summary>NotRun | Succeeded | Failed</summary>
    public string ExecutionStatus { get; init; } = string.Empty;
    public long ExecutionTimeMs { get; init; }
    public long TotalTimeMs { get; init; }
    public int ResultCount { get; init; }
    public string? Model { get; init; }
}

public sealed class AIDashboardQueryLogDetailDto : AIDashboardQueryLogDto
{
    public string? ConversationId { get; init; }
    public string? Operation { get; init; }
    public string? Collection { get; init; }
    /// <summary>Structured query produced by the AI (secrets masked).</summary>
    public string? GeneratedMql { get; init; }
    /// <summary>Validated, tenant-scoped MQL that ran (secrets masked).</summary>
    public string? ExecutedMql { get; init; }
    public List<string> ValidationErrors { get; init; } = new();
    public int RepairAttempts { get; init; }
    public string? ErrorCategory { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

public class AIDashboardActivityDto
{
    public string RequestId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string? ConversationId { get; init; }
    public string User { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string Question { get; init; } = string.Empty;
    public string? Intent { get; init; }
    /// <summary>Tool / action the agent chose (database-query, attachment-table, refused-technical-details…).</summary>
    public string Action { get; init; } = string.Empty;
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string Status { get; init; } = string.Empty;
    public string ExecutionStatus { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public int? ResultCount { get; init; }
    public string? ErrorCategory { get; init; }
    public string? FailedStage { get; init; }
}

public sealed class AIDashboardActivityDetailDto : AIDashboardActivityDto
{
    public string Language { get; init; } = "en";
    public string InputType { get; init; } = "text";
    public string? ClientApp { get; init; }
    public string QueryGenerationStatus { get; init; } = string.Empty;
    public string ResponseGenerationStatus { get; init; } = string.Empty;
    public long AiDurationMs { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? ValidationStatus { get; init; }
    public bool Blocked { get; init; }
    public long ValidationDurationMs { get; init; }
    public DateTime? ExecutionStartedAt { get; init; }
    public DateTime? ExecutionCompletedAt { get; init; }
    public long ExecutionDurationMs { get; init; }
    public bool Truncated { get; init; }
    public bool TimedOut { get; init; }
    public int Retries { get; init; }
    public string? Answer { get; init; }
    public string? VisualizationType { get; init; }
    public List<AIDashboardErrorDto> Errors { get; init; } = new();
    public List<AIDashboardStageDto> Timeline { get; init; } = new();
    public AIDashboardPerformanceDto Performance { get; init; } = new();
    /// <summary>Only for users with aidashboard.querylogs.</summary>
    public AIDashboardActivityQueryDto? Query { get; init; }
}

public sealed record AIDashboardErrorDto(string Category, string Stage, DateTime At);
public sealed record AIDashboardStageDto(string Stage, string Status, DateTime At, long? DurationMs);

public sealed class AIDashboardPerformanceDto
{
    public long TotalMs { get; init; }
    public long AiMs { get; init; }
    public long ValidationMs { get; init; }
    public long DatabaseMs { get; init; }
    public long ResponseMs { get; init; }
    public long OtherMs { get; init; }
    public string SlowestStage { get; init; } = string.Empty;
}

public sealed class AIDashboardActivityQueryDto
{
    public string? Collection { get; init; }
    public string? Operation { get; init; }
    public string? GeneratedQuery { get; init; }
    public string? ExecutedMql { get; init; }
    public List<string> Stages { get; init; } = new();
    public string? Parameters { get; init; }
    public List<string> ValidationErrors { get; init; } = new();
}

public sealed class AIDashboardProviderDto
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    /// <summary>Local | Cloud</summary>
    public string Category { get; init; } = string.Empty;
    public string DefaultModel { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool IsDefault { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime? LastCheckedAt { get; init; }
    /// <summary>"Not set" or a masked hint such as "••••••••1234" — never the key.</summary>
    public string ApiKey { get; init; } = string.Empty;
    public decimal InputCostPer1M { get; init; }
    public decimal OutputCostPer1M { get; init; }
}

public sealed class AIDashboardSettingsDto
{
    public double Temperature { get; init; }
    public int MaxTokens { get; init; }
    public int AiTimeoutSeconds { get; init; }
    public int MaxRepairAttempts { get; init; }
    public int MaxRecords { get; init; }
    public int QueryTimeoutMs { get; init; }
    public int MaxPipelineStages { get; init; }
    public int AllowedCollectionCount { get; init; }
    public bool StreamingEnabled { get; init; }
    public bool SaveConversations { get; init; }
    public int HistoryMessages { get; init; }
    public bool VoiceEnabled { get; init; }
    public string? VoiceLanguage { get; init; }
    public bool AttachmentsEnabled { get; init; }
    public int MaxFileSizeMb { get; init; }
    public int ActivityRetentionDays { get; init; }
    /// <summary>Providers and settings are shared by all MySaleBooks companies; they are changed by the AYAAN operators only.</summary>
    public bool ReadOnly { get; init; } = true;
}
