using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Application.ActivityTracking;

public sealed class ActivitySummaryDto
{
    public string ActivityId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string? ConversationId { get; init; }
    public DateTime Timestamp { get; init; }
    public string Question { get; init; } = string.Empty;
    public string Language { get; init; } = "en";
    public string Source { get; init; } = string.Empty;
    public string? ClientApp { get; init; }
    public string TenantRef { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ChatStatus { get; init; } = string.Empty;
    public string? FailedStage { get; init; }
    public string? ErrorType { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Intent { get; init; }
    public string? Collection { get; init; }
    public string? Operation { get; init; }
    public string? ValidationStatus { get; init; }
    public int? ResultCount { get; init; }
    public string? VisualizationType { get; init; }
    public long TotalMs { get; init; }
    public long AiMs { get; init; }
    public long ValidationMs { get; init; }
    public long MongoMs { get; init; }
    public long ResponseMs { get; init; }
    public string SlowestStage { get; init; } = string.Empty;
}

public sealed class ActivityConversationDetailDto
{
    public AIConversationActivity? Conversation { get; init; }
    public List<ActivitySummaryDto> Activities { get; init; } = new();
}

public sealed class ActivityFiltersDto
{
    public List<string> Providers { get; init; } = new();
    public List<string> Models { get; init; } = new();
    public List<string> Statuses { get; init; } = new();
    public List<string> Stages { get; init; } = new();
    public int RetentionDays { get; init; }
    public bool StoreFullResults { get; init; }
}

public sealed class StageStatsDto
{
    public string Stage { get; init; } = string.Empty;
    public double AverageMs { get; init; }
    public long P95Ms { get; init; }
    public long MaxMs { get; init; }
    /// <summary>How many requests had this as their slowest stage.</summary>
    public int SlowestCount { get; init; }
}

public sealed class ActivityStatsDto
{
    public int Days { get; init; }
    public int Requests { get; init; }
    public Dictionary<string, int> ByStatus { get; init; } = new();
    public Dictionary<string, int> ErrorsByStage { get; init; } = new();
    public List<StageStatsDto> Stages { get; init; } = new();
}

/// <summary>Read-only access to the activity log for developer/admin screens. No update or delete operations.</summary>
public sealed class ActivityQueryService
{
    private readonly IAIActivityRepository _repository;
    private readonly ActivityOptions _options;
    private readonly ISecretProtector? _protector;

    public ActivityQueryService(IAIActivityRepository repository, ActivityOptions options, ISecretProtector? protector = null)
    {
        _repository = repository;
        _options = options;
        _protector = protector;
    }

    /// <summary>Tenant reference for a customer database name (to filter the log by customer without storing the name).</summary>
    public static string TenantRefFor(string databaseName) => ActivityHashing.TenantRef(databaseName.Trim(), null);

    public async Task<PagedResult<ActivitySummaryDto>> SearchAsync(ActivityFilter filter, CancellationToken ct)
    {
        filter.Page = Math.Max(1, filter.Page);
        filter.PageSize = Math.Clamp(filter.PageSize, 1, 200);
        var (items, total) = await _repository.SearchAsync(filter, ct);
        return new PagedResult<ActivitySummaryDto>(items.Select(ToSummary).ToList(), total, filter.Page, filter.PageSize);
    }

    /// <summary>Full record including the complete MQL (decrypted when Activity:EncryptPayloads was on).</summary>
    public async Task<AIActivity> GetAsync(string activityId, CancellationToken ct)
    {
        var activity = await _repository.GetAsync(activityId, ct) ?? throw new NotFoundException("Activity not found.");
        if (activity.PayloadEncryption is not null && _protector is not null) ActivityRecorder.DecryptPayloads(activity, _protector);
        return activity;
    }

    public async Task<PagedResult<AIConversationActivity>> ConversationsAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var (items, total) = await _repository.SearchConversationsAsync(search, page, pageSize, ct);
        return new PagedResult<AIConversationActivity>(items, total, page, pageSize);
    }

    public async Task<ActivityConversationDetailDto> ConversationAsync(string conversationId, CancellationToken ct)
    {
        var conversation = await _repository.GetConversationAsync(conversationId, ct);
        var activities = await _repository.ListByConversationAsync(conversationId, 500, ct);
        if (conversation is null && activities.Count == 0) throw new NotFoundException("Conversation not found in the activity log.");
        return new ActivityConversationDetailDto { Conversation = conversation, Activities = activities.Select(ToSummary).ToList() };
    }

    public async Task<ActivityFiltersDto> FiltersAsync(CancellationToken ct) => new()
    {
        Providers = await _repository.DistinctAsync("provider", ct),
        Models = await _repository.DistinctAsync("model", ct),
        Statuses = new List<string>
        {
            ActivityStatuses.Success, ActivityStatuses.NoResults, ActivityStatuses.Clarification, ActivityStatuses.Unsupported, ActivityStatuses.Rejected,
            ActivityStatuses.Failed, ActivityStatuses.Timeout, ActivityStatuses.Cancelled
        },
        Stages = new List<string>
        {
            ActivityStages.Authentication, ActivityStages.AIProvider, ActivityStages.Prompt, ActivityStages.QueryGeneration,
            ActivityStages.QueryValidation, ActivityStages.MongoExecution, ActivityStages.ResponseGeneration, ActivityStages.Request
        },
        RetentionDays = _options.RetentionDays,
        StoreFullResults = _options.StoreFullResults
    };

    public async Task<ActivityStatsDto> StatsAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var rows = await _repository.RecentForStatsAsync(DateTime.UtcNow.AddDays(-days), 5000, ct);
        StageStatsDto Stage(string name, Func<ActivityPerformance, long> pick)
        {
            var values = rows.Select(r => pick(r.Performance)).OrderBy(v => v).ToList();
            return new StageStatsDto
            {
                Stage = name,
                AverageMs = values.Count == 0 ? 0 : Math.Round(values.Average(), 1),
                P95Ms = values.Count == 0 ? 0 : values[(int)Math.Min(values.Count - 1, Math.Ceiling(values.Count * 0.95) - 1)],
                MaxMs = values.Count == 0 ? 0 : values[^1],
                SlowestCount = rows.Count(r => r.Performance.SlowestStage == name)
            };
        }
        return new ActivityStatsDto
        {
            Days = days,
            Requests = rows.Count,
            ByStatus = rows.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()),
            ErrorsByStage = rows.Where(r => r.FailedStage is not null).GroupBy(r => r.FailedStage!).ToDictionary(g => g.Key, g => g.Count()),
            Stages = new List<StageStatsDto>
            {
                Stage("Total", p => p.TotalDurationMs),
                Stage("AI", p => p.AiGenerationDurationMs),
                Stage("QueryValidation", p => p.QueryValidationDurationMs),
                Stage("MongoDB", p => p.MongoExecutionDurationMs),
                Stage("ResponseGeneration", p => p.ResponseGenerationDurationMs),
                Stage("Other", p => p.OtherDurationMs)
            }
        };
    }

    public static ActivitySummaryDto ToSummary(AIActivity a) => new()
    {
        ActivityId = a.ActivityId,
        CorrelationId = a.CorrelationId,
        ConversationId = a.ConversationId,
        Timestamp = a.Timestamp,
        Question = a.Request.Question.Length > 240 ? a.Request.Question[..240] + "…" : a.Request.Question,
        Language = a.Request.Language,
        Source = a.Request.Source,
        ClientApp = a.Request.ClientApp,
        TenantRef = a.TenantRef,
        Status = a.Status,
        ChatStatus = a.ChatStatus,
        FailedStage = a.FailedStage,
        ErrorType = a.Errors.LastOrDefault()?.Type,
        Provider = a.Ai.ProviderName,
        Model = a.Ai.Model,
        Intent = a.Ai.Intent,
        Collection = a.Query?.Collection,
        Operation = a.Query?.Operation,
        ValidationStatus = a.Validation?.Status,
        ResultCount = a.Result?.Count,
        VisualizationType = a.Response?.VisualizationType,
        TotalMs = a.Performance.TotalDurationMs,
        AiMs = a.Performance.AiGenerationDurationMs,
        ValidationMs = a.Performance.QueryValidationDurationMs,
        MongoMs = a.Performance.MongoExecutionDurationMs,
        ResponseMs = a.Performance.ResponseGenerationDurationMs,
        SlowestStage = a.Performance.SlowestStage
    };
}
