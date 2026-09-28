using MySale.AI.Application.Abstractions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Application.AIDashboard;

/// <summary>Masks secrets before anything reaches the dashboard.</summary>
public static class SecretMasker
{
    public const string NotSet = "Not set";
    private const string Dots = "••••••••";

    /// <summary>Stored key hint ("…1234" / "set") → "••••••••1234". The full key is never available here.</summary>
    public static string MaskKeyHint(bool hasKey, string? hint)
    {
        if (!hasKey) return NotSet;
        var tail = new string((hint ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        if (string.Equals(tail, "set", StringComparison.OrdinalIgnoreCase)) tail = string.Empty;
        return Dots + (tail.Length is > 0 and <= 4 ? tail : string.Empty);
    }

    /// <summary>Masks a raw secret value, keeping at most its last 4 characters (sk-••••••••1234).</summary>
    public static string MaskSecret(string? value)
    {
        if (string.IsNullOrEmpty(value)) return NotSet;
        var prefix = value.StartsWith("sk-", StringComparison.Ordinal) ? "sk-" : string.Empty;
        return value.Length <= 12 ? prefix + Dots : prefix + Dots + value[^4..];
    }

    /// <summary>JWTs, bearer tokens, Mongo URIs, API keys and key=value secrets inside text → [REDACTED].</summary>
    public static string? Text(string? text) => string.IsNullOrEmpty(text) ? text : ActivityRedactor.RedactText(text);
}

/// <summary>
/// Data of the AIDashboard. Every method takes the <see cref="AIDashboardAccess"/> produced by
/// <see cref="AIDashboardAccessService"/>; the tenant scope (CompanyId / TenantRef) always comes from it — never from the
/// request. Records of another tenant are reported as "not found".
/// </summary>
public sealed class AIDashboardService
{
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly IQueryLogRepository _logs;
    private readonly IAIActivityRepository _activities;
    private readonly ProviderService _providers;
    private readonly SettingsService _settings;
    private readonly UsageService _usage;
    private readonly IAuditLogRepository _audit;
    private readonly ActivityOptions _activityOptions;
    private readonly ISecretProtector? _protector;
    private readonly TimeProvider _time;

    public AIDashboardService(IConversationRepository conversations, IMessageRepository messages, IQueryLogRepository logs,
        IAIActivityRepository activities, ProviderService providers, SettingsService settings, UsageService usage,
        IAuditLogRepository audit, ActivityOptions activityOptions, TimeProvider time, ISecretProtector? protector = null)
    {
        _conversations = conversations;
        _messages = messages;
        _logs = logs;
        _activities = activities;
        _providers = providers;
        _settings = settings;
        _usage = usage;
        _audit = audit;
        _activityOptions = activityOptions;
        _time = time;
        _protector = protector;
    }

    // ------------------------------------------------------------------ access

    public static AIDashboardAccessDto ToAccessDto(AIDashboardAccess access, AIDashboardOptions options) => new()
    {
        UserName = access.UserName,
        CompanyName = access.CompanyName,
        Permissions = AIDashboardPermissions.All.Where(access.Has).ToList(),
        Sections = AIDashboardPermissions.All.Where(access.Has).Select(AIDashboardPermissions.SectionOf).ToList(),
        SessionIdleMinutes = Math.Max(1, options.SessionIdleMinutes),
        CanSeeMql = access.Has(AIDashboardPermissions.QueryLogs),
        AyaanUserName = access.AyaanUserName,
        AyaanSessionExpiresAt = access.AyaanSessionExpiresAt
    };

    // ------------------------------------------------------------------ overview (aidashboard.view)

    public async Task<AIDashboardOverviewDto> OverviewAsync(AIDashboardAccess access, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 90);
        var report = await _usage.GetTenantReportAsync(days, Scope(access), ct);
        var s = report.Summary;
        var from = _time.GetUtcNow().UtcDateTime.AddDays(-days);
        return new AIDashboardOverviewDto
        {
            Days = days,
            Conversations = await _conversations.CountByCompanyAsync(Scope(access), from, ct),
            Questions = s.TotalRequests,
            Successful = s.SuccessfulRequests,
            Failed = s.FailedRequests,
            AvgResponseTimeMs = s.AvgResponseTimeMs,
            InputTokens = s.InputTokens,
            OutputTokens = s.OutputTokens,
            EstimatedCost = s.EstimatedCost,
            QueriesGenerated = s.QueriesGenerated,
            QueriesExecuted = s.ValidQueries,
            BlockedQueries = s.BlockedQueries,
            AvgExecutionTimeMs = s.AvgExecutionTimeMs,
            Daily = report.Daily.Select(d => new AIDashboardDailyDto(d.Date, d.Requests, d.Failed)).ToList(),
            ByStatus = report.ByStatus
        };
    }

    // ------------------------------------------------------------------ usage (aidashboard.usage)

    public Task<UsageReportDto> UsageAsync(AIDashboardAccess access, int days, CancellationToken ct)
        => _usage.GetTenantReportAsync(Math.Clamp(days, 1, 365), Scope(access), ct);

    // ------------------------------------------------------------------ conversations (aidashboard.conversations)

    public async Task<PagedResult<AIDashboardConversationDto>> ConversationsAsync(AIDashboardAccess access, string? search,
        DateTime? from, DateTime? to, int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize) = Paging(page, pageSize);
        var (items, total) = await _conversations.SearchByCompanyAsync(Scope(access), search, from, to, page, pageSize, ct);
        return new PagedResult<AIDashboardConversationDto>(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<AIDashboardConversationDetailDto> ConversationAsync(AIDashboardAccess access, string id, CancellationToken ct)
    {
        var conversation = await _conversations.GetForCompanyAsync(id, Scope(access), ct)
                           ?? throw new NotFoundException("Conversation not found.");
        await AuditAsync(access, "Conversations", conversation.Id, ct);
        var messages = await _messages.ListAsync(conversation.Id, ct);
        return new AIDashboardConversationDetailDto
        {
            Conversation = ToDto(conversation),
            Messages = messages
                .Where(m => m.CompanyId == conversation.CompanyId) // defence in depth
                .Select(m => new AIDashboardMessageDto
                {
                    Id = m.Id,
                    Role = m.Role.ToString(),
                    Text = SecretMasker.Text(m.Content) ?? string.Empty,
                    CreatedAt = m.CreatedAt,
                    Status = m.Role == MessageRole.Assistant ? m.Status.ToString() : string.Empty,
                    ResultCount = m.Role == MessageRole.Assistant && m.QueryExecuted ? m.ResultCount : null
                }).ToList()
        };
    }

    // ------------------------------------------------------------------ query logs + MQL (aidashboard.querylogs)

    public async Task<PagedResult<AIDashboardQueryLogDto>> QueryLogsAsync(AIDashboardAccess access, QueryLogFilter filter, CancellationToken ct)
    {
        (filter.Page, filter.PageSize) = Paging(filter.Page, filter.PageSize);
        filter.CompanyId = Scope(access); // always the caller's tenant, whatever the client sent
        filter.ProviderId = null;
        var (items, total) = await _logs.SearchAsync(filter, ct);
        await AuditAsync(access, "QueryLogs", null, ct);
        return new PagedResult<AIDashboardQueryLogDto>(
            items.Where(l => l.CompanyId == filter.CompanyId).Select(l => Fill(new AIDashboardQueryLogDto(), l)).ToList(),
            total, filter.Page, filter.PageSize);
    }

    public async Task<AIDashboardQueryLogDetailDto> QueryLogAsync(AIDashboardAccess access, string id, CancellationToken ct)
    {
        var log = await _logs.GetAsync(id, ct);
        if (log is null || log.CompanyId != Scope(access)) throw new NotFoundException("Query log not found.");
        await AuditAsync(access, "QueryLogs.Mql", log.Id, ct);
        return Fill(new AIDashboardQueryLogDetailDto
        {
            ConversationId = log.ConversationId,
            Operation = log.Operation,
            Collection = log.Collection,
            GeneratedMql = SecretMasker.Text(log.GeneratedMql),
            ExecutedMql = SecretMasker.Text(log.FinalMql),
            ValidationErrors = log.ValidationErrors.Select(e => SecretMasker.Text(e) ?? string.Empty).ToList(),
            RepairAttempts = log.RepairAttempts,
            ErrorCategory = ErrorCategory(log.Status),
            InputTokens = log.InputTokens,
            OutputTokens = log.OutputTokens
        }, log);
    }

    // ------------------------------------------------------------------ activity (aidashboard.activity)

    public async Task<PagedResult<AIDashboardActivityDto>> ActivityAsync(AIDashboardAccess access, ActivityFilter filter, CancellationToken ct)
    {
        (filter.Page, filter.PageSize) = Paging(filter.Page, filter.PageSize);
        filter.TenantRef = access.TenantRef; // always the caller's tenant
        var (items, total) = await _activities.SearchAsync(filter, ct);
        return new PagedResult<AIDashboardActivityDto>(
            items.Where(a => a.TenantRef == access.TenantRef).Select(a => Fill(new AIDashboardActivityDto(), a)).ToList(),
            total, filter.Page, filter.PageSize);
    }

    public async Task<AIDashboardActivityDetailDto> ActivityDetailAsync(AIDashboardAccess access, string requestId, CancellationToken ct)
    {
        var a = await _activities.GetAsync(requestId, ct);
        if (a is null || string.IsNullOrEmpty(access.TenantRef) || a.TenantRef != access.TenantRef)
            throw new NotFoundException("Activity not found.");
        var withMql = access.Has(AIDashboardPermissions.QueryLogs);
        await AuditAsync(access, withMql ? "Activity.Mql" : "Activity", a.ActivityId, ct);
        if (withMql && a.PayloadEncryption is not null && _protector is not null) ActivityRecorder.DecryptPayloads(a, _protector);

        return Fill(new AIDashboardActivityDetailDto
        {
            Language = a.Request.Language,
            InputType = a.Request.InputType,
            ClientApp = a.Request.ClientApp,
            QueryGenerationStatus = a.Ai.QueryGenerationStatus,
            ResponseGenerationStatus = a.Ai.ResponseGenerationStatus,
            AiDurationMs = a.Ai.DurationMs,
            InputTokens = a.Ai.InputTokens,
            OutputTokens = a.Ai.OutputTokens,
            EstimatedCost = a.Ai.EstimatedCost,
            ValidationStatus = a.Validation?.Status,
            Blocked = a.Validation?.Blocked ?? false,
            ValidationDurationMs = a.Validation?.DurationMs ?? 0,
            ExecutionStartedAt = a.Execution?.StartedAt,
            ExecutionCompletedAt = a.Execution?.CompletedAt,
            ExecutionDurationMs = a.Execution?.DurationMs ?? 0,
            Truncated = a.Execution?.Truncated ?? false,
            TimedOut = a.Execution?.TimedOut ?? false,
            Retries = a.Execution?.Retries ?? 0,
            Answer = a.PayloadEncryption is not null && !withMql ? null : SecretMasker.Text(a.Response?.Text),
            VisualizationType = a.Response?.VisualizationType,
            // Category + stage only: raw exception messages and stack traces stay in server diagnostics.
            Errors = a.Errors.Select(e => new AIDashboardErrorDto(e.Type, e.Stage, e.At)).ToList(),
            Timeline = a.Timeline.Select(t => new AIDashboardStageDto(t.Stage, t.Status, t.At, t.DurationMs)).ToList(),
            Performance = new AIDashboardPerformanceDto
            {
                TotalMs = a.Performance.TotalDurationMs,
                AiMs = a.Performance.AiGenerationDurationMs,
                ValidationMs = a.Performance.QueryValidationDurationMs,
                DatabaseMs = a.Performance.MongoExecutionDurationMs,
                ResponseMs = a.Performance.ResponseGenerationDurationMs,
                OtherMs = a.Performance.OtherDurationMs,
                SlowestStage = a.Performance.SlowestStage
            },
            Query = withMql && a.Query is { } q
                ? new AIDashboardActivityQueryDto
                {
                    Collection = q.Collection,
                    Operation = q.Operation,
                    GeneratedQuery = SecretMasker.Text(q.GeneratedQueryJson),
                    ExecutedMql = SecretMasker.Text(q.ExecutedMql),
                    Stages = q.Stages.ToList(),
                    Parameters = SecretMasker.Text(q.QueryParametersJson),
                    ValidationErrors = (a.Validation?.Errors ?? new List<string>()).Select(e => SecretMasker.Text(e) ?? string.Empty).ToList()
                }
                : null
        }, a);
    }

    // ------------------------------------------------------------------ providers (aidashboard.providers) — read-only, masked

    public async Task<List<AIDashboardProviderDto>> ProvidersAsync(AIDashboardAccess access, CancellationToken ct)
    {
        var providers = await _providers.ListAsync(ct);
        await AuditAsync(access, "Providers", null, ct);
        return providers.Select(p => new AIDashboardProviderDto
        {
            Name = p.Name,
            Type = string.IsNullOrEmpty(p.KindDisplayName) ? p.Kind : p.KindDisplayName,
            Category = p.Category.ToString(),
            DefaultModel = p.DefaultModel,
            Enabled = p.Enabled,
            IsDefault = p.IsDefault,
            Status = p.Status.ToString(),
            LastCheckedAt = p.LastTestedAt,
            ApiKey = SecretMasker.MaskKeyHint(p.HasApiKey, p.ApiKeyHint),
            InputCostPer1M = p.InputCostPer1M,
            OutputCostPer1M = p.OutputCostPer1M
            // BaseUrl (internal hosts, possible credentials) and LastTestMessage (raw errors) are not returned.
        }).ToList();
    }

    // ------------------------------------------------------------------ settings (aidashboard.settings) — read-only

    public async Task<AIDashboardSettingsDto> SettingsAsync(AIDashboardAccess access, CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        await AuditAsync(access, "Settings", null, ct);
        return new AIDashboardSettingsDto
        {
            Temperature = s.Ai.Temperature,
            MaxTokens = s.Ai.MaxTokens,
            AiTimeoutSeconds = s.Ai.TimeoutSeconds,
            MaxRepairAttempts = s.Ai.MaxRepairAttempts,
            MaxRecords = s.Query.MaxRecords,
            QueryTimeoutMs = s.Query.QueryTimeoutMs,
            MaxPipelineStages = s.Query.MaxPipelineStages,
            AllowedCollectionCount = s.Query.AllowedCollections.Count, // names are internal structure: count only
            StreamingEnabled = s.Chat.EnableStreaming,
            SaveConversations = s.Chat.SaveConversations,
            HistoryMessages = s.Chat.HistoryMessages,
            VoiceEnabled = s.Speech.Enabled,
            VoiceLanguage = s.Speech.DefaultLanguage,
            AttachmentsEnabled = s.Attachments.Enabled,
            MaxFileSizeMb = s.Attachments.MaxFileSizeMb,
            ActivityRetentionDays = _activityOptions.RetentionDays
        };
    }

    // ------------------------------------------------------------------ audit of sensitive access

    /// <summary>Who / when / which tenant / which resource / what action. Never the token.</summary>
    public Task AuditAsync(AIDashboardAccess access, string resource, string? resourceId, CancellationToken ct)
        => _audit.InsertAsync(new AuditLog
        {
            Action = "AIDashboard.Read",
            Resource = resource,
            ResourceId = resourceId,
            Outcome = "Allowed",
            UserId = access.UserId,
            UserName = access.UserName,
            CompanyId = access.CompanyId,
            TenantRef = access.TenantRef,
            IpAddress = access.IpAddress,
            CorrelationId = access.CorrelationId,
            Details = access.AyaanUserName is { Length: > 0 } ayaan ? "AYAAN user: " + ayaan : null
        }, ct);

    /// <summary>Refused request (403/401) — recorded for security review.</summary>
    public static AuditLog DeniedEntry(AIDashboardCaller caller, string resource, string code) => new()
    {
        Action = "AIDashboard.Denied",
        Resource = resource,
        Outcome = code,
        UserId = string.IsNullOrEmpty(caller.UserId) ? null : caller.UserId,
        UserName = string.IsNullOrEmpty(caller.UserName) ? null : caller.UserName,
        CompanyId = string.IsNullOrEmpty(caller.CompanyId) ? null : caller.CompanyId,
        TenantRef = caller.IsMySaleBooksUser ? caller.TenantRef : null,
        IpAddress = caller.IpAddress,
        CorrelationId = caller.CorrelationId
    };

    // ------------------------------------------------------------------ helpers

    private static string Scope(AIDashboardAccess access)
        => string.IsNullOrEmpty(access.CompanyId)
            ? throw AIDashboardAccessException.Forbidden("tenant_required", "AI Dashboard is available only inside MySaleBooks.")
            : access.CompanyId;

    private static (int Page, int PageSize) Paging(int page, int pageSize) => (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));

    private static AIDashboardConversationDto ToDto(Conversation c) => new()
    {
        Id = c.Id,
        Title = SecretMasker.Text(c.Title) ?? string.Empty,
        UserName = string.IsNullOrEmpty(c.UserName) ? c.UserId : c.UserName,
        MessageCount = c.MessageCount,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt
    };

    private static T Fill<T>(T dto, QueryLog l) where T : AIDashboardQueryLogDto
    {
        // init-only properties of the base type are set through a copy
        var baseValues = new AIDashboardQueryLogDto
        {
            Id = l.Id,
            CreatedAt = l.CreatedAt,
            UserName = l.UserName,
            Question = SecretMasker.Text(l.Question) ?? string.Empty,
            Status = l.Status.ToString(),
            ValidationStatus = !l.QueryGenerated ? "NotGenerated" : l.Blocked ? "Blocked" : l.ValidationPassed ? "Passed" : "Failed",
            ExecutionStatus = !l.Executed ? "NotRun" : l.ExecutionSucceeded ? "Succeeded" : "Failed",
            ExecutionTimeMs = l.ExecutionTimeMs,
            TotalTimeMs = l.TotalTimeMs,
            ResultCount = l.ResultCount,
            Model = l.Model
        };
        return CopyBase(dto, baseValues);
    }

    private static T Fill<T>(T dto, AIActivity a) where T : AIDashboardActivityDto
    {
        var baseValues = new AIDashboardActivityDto
        {
            RequestId = a.ActivityId,
            CorrelationId = a.CorrelationId,
            ConversationId = a.ConversationId,
            User = a.UserName ?? a.UserId ?? string.Empty,
            Timestamp = a.Timestamp,
            Question = SecretMasker.Text(a.Request.Question.Length > 300 ? a.Request.Question[..300] + "…" : a.Request.Question) ?? string.Empty,
            Intent = a.Ai.Intent,
            Action = a.Ai.ToolSelected,
            Provider = a.Ai.ProviderName,
            Model = a.Ai.Model,
            Status = a.Status,
            ExecutionStatus = a.Execution?.Status ?? "NotRun",
            DurationMs = a.Performance.TotalDurationMs,
            ResultCount = a.Result?.Count,
            ErrorCategory = a.Errors.LastOrDefault()?.Type,
            FailedStage = a.FailedStage
        };
        return CopyBase(dto, baseValues);
    }

    private static T CopyBase<T, TBase>(T target, TBase source) where T : TBase
    {
        foreach (var p in typeof(TBase).GetProperties().Where(p => p.CanWrite))
            p.SetValue(target, p.GetValue(source));
        return target;
    }

    private static string? ErrorCategory(ChatStatus status) => status switch
    {
        ChatStatus.ProviderError => "AI provider error",
        ChatStatus.DatabaseError => "Database error",
        ChatStatus.Timeout => "Timeout",
        ChatStatus.InvalidQuery => "Query validation",
        ChatStatus.Error => "Internal error",
        _ => null
    };
}
