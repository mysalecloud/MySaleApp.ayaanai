using MySale.AI.Application.Abstractions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Application.AIDashboard;

// ---------------------------------------------------------------- DTOs

public sealed class AIDashboardModelDto
{
    public string Provider { get; init; } = string.Empty;
    public string ProviderType { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool Text { get; init; }
    public bool Vision { get; init; }
    public bool Audio { get; init; }
    public bool StructuredOutput { get; init; }
    public int Requests { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    public double AvgResponseTimeMs { get; init; }
    public double SuccessRate { get; init; }
    public decimal InputCostPer1M { get; init; }
    public decimal OutputCostPer1M { get; init; }
}

public sealed class AIDashboardSchemaDto
{
    public int CollectionCount { get; init; }
    public int DocumentedCount { get; init; }
    public int FieldCount { get; init; }
    public DateTime? LastUpdated { get; init; }
    public DateTime CheckedAt { get; init; }
    public List<AIDashboardCollectionDto> Collections { get; init; } = new();
}

public sealed class AIDashboardCollectionDto
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool Documented { get; init; }
    /// <summary>catalog | saved | discovered</summary>
    public string MetadataSource { get; init; } = string.Empty;
    public long? DocumentCount { get; init; }
    public DateTime? LastUpdated { get; init; }
    public List<AIDashboardFieldDto> Fields { get; init; } = new();
}

public sealed record AIDashboardFieldDto(string Name, string Type, string? Description, string? Relationship, bool Discovered);

public sealed class AIDashboardAgentDto
{
    public string Name { get; init; } = "AYAAN AI";
    public string Description { get; init; } = "AI Your Accounts & Asset Navigator — answers business questions from your MySaleBooks data.";
    /// <summary>Active | Degraded | NotConfigured</summary>
    public string Status { get; init; } = string.Empty;
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string PromptVersion { get; init; } = PromptBuilder.Version;
    public List<AIDashboardCapabilityDto> Capabilities { get; init; } = new();
    public List<AIDashboardToolDto> Tools { get; init; } = new();
    public List<AIDashboardPromptVersionDto> PromptVersions { get; init; } = new();
    public AIDashboardChannelsDto Channels { get; init; } = new();
    public int Days { get; init; }
}

public sealed record AIDashboardCapabilityDto(string Name, bool Enabled, string Detail);

public sealed class AIDashboardToolDto
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>Enabled | Disabled</summary>
    public string Status { get; init; } = string.Empty;
    public int Requests { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public double AvgDurationMs { get; init; }
    public DateTime? LastExecution { get; init; }
}

/// <summary>Prompt version in use (the prompt text itself is never returned; <see cref="PromptRef"/> is a hash).</summary>
public sealed class AIDashboardPromptVersionDto
{
    public string Version { get; init; } = string.Empty;
    public string? PromptRef { get; init; }
    public bool Current { get; init; }
    public int Requests { get; init; }
    public DateTime FirstUsed { get; init; }
    public DateTime LastUsed { get; init; }
}

public sealed class AIDashboardChannelsDto
{
    public int TextRequests { get; init; }
    public int VoiceRequests { get; init; }
    public int AttachmentRequests { get; init; }
    public int Attachments { get; init; }
    public List<NamedCountDto> Languages { get; init; } = new();
}

public sealed record NamedCountDto(string Name, int Count);

public sealed class AIDashboardSecurityDto
{
    public int Days { get; init; }
    public int ValidationFailures { get; init; }
    public int BlockedQueries { get; init; }
    public int TechnicalDetailRequests { get; init; }
    public int AccessDenied { get; init; }
    public int Errors { get; init; }
    public List<NamedCountDto> ErrorsByCategory { get; init; } = new();
    public List<AIDashboardSecurityEventDto> Events { get; init; } = new();
}

public sealed class AIDashboardSecurityEventDto
{
    public DateTime At { get; init; }
    /// <summary>QueryBlocked | ValidationFailed | TechnicalDetailsRequest | AccessDenied | Error</summary>
    public string Type { get; init; } = string.Empty;
    public string Severity { get; init; } = "Medium";
    public string User { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string? RequestId { get; init; }
}

public sealed class AIDashboardAuditDto
{
    public DateTime At { get; init; }
    public string User { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? Resource { get; init; }
    public string? ResourceId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? IpAddress { get; init; }
    public string? CorrelationId { get; init; }
    public string? Details { get; init; }
}

public sealed class AIDashboardActivityFiltersDto
{
    public List<NamedRef> Users { get; init; } = new();
    public List<string> Actions { get; init; } = new();
    public List<string> InputTypes { get; init; } = new();
}

// ---------------------------------------------------------------- service

/// <summary>
/// Models, schema, agent (capabilities, tools, prompt versions, channels), security events and audit log for the
/// AIDashboard. Reuses the existing provider, settings, usage, schema and activity services — nothing new is stored
/// except the audit entries. Every method is limited to the caller's tenant from <see cref="AIDashboardAccess"/>.
/// </summary>
public sealed class AIDashboardInsightsService
{
    private const int AnalysisLimit = 10_000;

    public static readonly IReadOnlyDictionary<string, string> ToolDescriptions = new Dictionary<string, string>
    {
        ["database-query"] = "Turns a business question into a validated, read-only MongoDB query on your company data.",
        ["attachment-answer"] = "Answers questions about an attached PDF, image or text file.",
        ["attachment-table"] = "Analyses an attached CSV / Excel sheet (totals, filters, grouping).",
        ["attachment+database"] = "Combines an attached file with your company data (e.g. compare a price list with sales).",
        ["voice-transcription"] = "Converts a recorded voice question to text (speech-to-text).",
        ["refused-technical-details"] = "Security guard: refuses requests for queries, prompts or other internal details.",
        ["declined"] = "Questions outside the supported business data (answered with a friendly decline)."
    };

    private readonly ProviderService _providers;
    private readonly SettingsService _settings;
    private readonly UsageService _usage;
    private readonly ISchemaService _schema;
    private readonly IAIActivityRepository _activities;
    private readonly IAuditLogRepository _audit;
    private readonly AIDashboardService _dashboard;
    private readonly TimeProvider _time;

    public AIDashboardInsightsService(ProviderService providers, SettingsService settings, UsageService usage, ISchemaService schema,
        IAIActivityRepository activities, IAuditLogRepository audit, AIDashboardService dashboard, TimeProvider time)
    {
        _providers = providers;
        _settings = settings;
        _usage = usage;
        _schema = schema;
        _activities = activities;
        _audit = audit;
        _dashboard = dashboard;
        _time = time;
    }

    private static string Scope(AIDashboardAccess access)
        => string.IsNullOrEmpty(access.CompanyId) || string.IsNullOrEmpty(access.TenantRef)
            ? throw AIDashboardAccessException.Forbidden("tenant_required", "AI Dashboard is available only inside MySaleBooks.")
            : access.CompanyId;

    // ------------------------------------------------------------ models (aidashboard.models)

    public async Task<List<AIDashboardModelDto>> ModelsAsync(AIDashboardAccess access, int days, CancellationToken ct)
    {
        var usage = await _usage.GetTenantReportAsync(Math.Clamp(days, 1, 365), Scope(access), ct);
        var providers = await _providers.ListAsync(ct);
        var result = new List<AIDashboardModelDto>();
        var seen = new HashSet<(string, string)>();

        AIDashboardModelDto Build(ProviderDto? p, string providerName, string model, UsageByProviderDto? u)
        {
            var caps = ModelCapabilityService.Heuristic(p?.Kind ?? u?.Kind ?? string.Empty, p?.BaseUrl, model);
            return new AIDashboardModelDto
            {
                Provider = providerName,
                ProviderType = p is null ? (u?.Kind ?? "—") : string.IsNullOrEmpty(p.KindDisplayName) ? p.Kind : p.KindDisplayName,
                Category = p?.Category.ToString() ?? (u?.IsLocal == true ? "Local" : "Cloud"),
                Model = model,
                IsDefault = p is not null && p.IsDefault && string.Equals(p.DefaultModel, model, StringComparison.OrdinalIgnoreCase),
                Status = p is null ? "Removed" : p.Status.ToString(),
                Text = caps.Text,
                Vision = caps.Vision,
                Audio = caps.Audio,
                StructuredOutput = caps.StructuredOutput,
                Requests = u?.Requests ?? 0,
                InputTokens = u?.InputTokens ?? 0,
                OutputTokens = u?.OutputTokens ?? 0,
                EstimatedCost = u?.EstimatedCost ?? 0,
                AvgResponseTimeMs = u?.AvgResponseTimeMs ?? 0,
                SuccessRate = u?.SuccessRate ?? 0,
                InputCostPer1M = p?.InputCostPer1M ?? 0,
                OutputCostPer1M = p?.OutputCostPer1M ?? 0
            };
        }

        foreach (var p in providers)
        {
            var models = usage.ByProvider.Where(u => string.Equals(u.ProviderName, p.Name, StringComparison.OrdinalIgnoreCase))
                .Select(u => u.Model ?? "-").ToList();
            if (!string.IsNullOrWhiteSpace(p.DefaultModel)) models.Insert(0, p.DefaultModel);
            foreach (var m in models.Where(m => m != "-").Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!seen.Add((p.Name.ToLowerInvariant(), m.ToLowerInvariant()))) continue;
                var u = usage.ByProvider.FirstOrDefault(x => string.Equals(x.ProviderName, p.Name, StringComparison.OrdinalIgnoreCase)
                                                             && string.Equals(x.Model, m, StringComparison.OrdinalIgnoreCase));
                result.Add(Build(p, p.Name, m, u));
            }
        }
        // Models used by this company whose provider was removed since.
        foreach (var u in usage.ByProvider.Where(u => u.Model is not null && u.Model != "-"))
            if (seen.Add((u.ProviderName.ToLowerInvariant(), u.Model!.ToLowerInvariant())))
                result.Add(Build(null, u.ProviderName, u.Model!, u));

        return result.OrderByDescending(m => m.IsDefault).ThenByDescending(m => m.Requests).ThenBy(m => m.Provider).ToList();
    }

    // ------------------------------------------------------------ schema (aidashboard.schema)

    /// <summary>
    /// Data areas the AI may use in the caller's own database (the database comes from the token). Hidden fields and
    /// fields with sensitive names (password, token, key…) are left out. No connection details are ever returned.
    /// </summary>
    public async Task<AIDashboardSchemaDto> SchemaAsync(AIDashboardAccess access, CancellationToken ct)
    {
        Scope(access);
        await _dashboard.AuditAsync(access, "Schema", null, ct);
        var settings = await _settings.GetAsync(ct);
        var allowed = new HashSet<string>(settings.Query.AllowedCollections, StringComparer.OrdinalIgnoreCase);
        var schema = await _schema.GetSchemaAsync(includeDiscovery: true, ct);

        var collections = schema
            .Where(c => allowed.Contains(c.Name))
            .OrderBy(c => c.Name)
            .Select(c => new AIDashboardCollectionDto
            {
                Name = c.Name,
                Description = SecretMasker.Text(c.Description),
                Documented = c.Documented,
                MetadataSource = c.MetadataSource,
                DocumentCount = c.DocumentCount,
                LastUpdated = c.MetadataUpdatedAt,
                Fields = c.Fields
                    .Where(f => !f.Hidden && !ActivityRedactor.IsSensitiveField(f.Name.Split('.').Last()))
                    .Select(f => new AIDashboardFieldDto(f.Name, f.Type, SecretMasker.Text(f.Description), f.Relationship, f.Discovered))
                    .ToList()
            }).ToList();

        return new AIDashboardSchemaDto
        {
            CollectionCount = collections.Count,
            DocumentedCount = collections.Count(c => c.Documented),
            FieldCount = collections.Sum(c => c.Fields.Count),
            LastUpdated = collections.Max(c => c.LastUpdated),
            CheckedAt = _time.GetUtcNow().UtcDateTime,
            Collections = collections
        };
    }

    // ------------------------------------------------------------ agent, tools, prompts, channels (aidashboard.agent)

    public async Task<AIDashboardAgentDto> AgentAsync(AIDashboardAccess access, int days, CancellationToken ct)
    {
        Scope(access);
        days = Math.Clamp(days, 1, 365);
        var settings = await _settings.GetAsync(ct);
        var providers = await _providers.ListAsync(ct);
        var provider = providers.FirstOrDefault(p => p.IsDefault && p.Enabled) ?? providers.FirstOrDefault(p => p.Enabled);
        var model = string.IsNullOrWhiteSpace(settings.Ai.DefaultModel) ? provider?.DefaultModel : settings.Ai.DefaultModel;
        var caps = provider is null || string.IsNullOrEmpty(model) ? null : ModelCapabilityService.Heuristic(provider.Kind, provider.BaseUrl, model);
        var rows = await _activities.RecentForAnalysisAsync(access.TenantRef, _time.GetUtcNow().UtcDateTime.AddDays(-days), AnalysisLimit, ct);

        var status = provider is null ? "NotConfigured"
            : provider.Status is ProviderStatus.Disconnected or ProviderStatus.NotConfigured ? "Degraded" : "Active";

        bool Succeeded(AIActivity a) => a.Status is ActivityStatuses.Success or ActivityStatuses.NoResults or ActivityStatuses.Unsupported;
        bool ToolEnabled(string tool) => tool switch
        {
            "attachment-answer" or "attachment-table" or "attachment+database" => settings.Attachments.Enabled,
            "voice-transcription" => settings.Speech.Enabled,
            _ => true
        };

        var toolRows = rows.Where(a => a.Ai.ToolSelected is { Length: > 0 } t && t != "none")
            .Select(a => (Tool: a.Ai.ToolSelected, Activity: a))
            .Concat(rows.Where(a => a.Request.InputType == "voice").Select(a => (Tool: "voice-transcription", Activity: a)))
            .ToList();
        var tools = ToolDescriptions.Keys
            .Concat(toolRows.Select(t => t.Tool))
            .Distinct(StringComparer.Ordinal)
            .Select(name =>
            {
                var used = toolRows.Where(t => t.Tool == name).Select(t => t.Activity).ToList();
                return new AIDashboardToolDto
                {
                    Name = name,
                    Description = ToolDescriptions.TryGetValue(name, out var d) ? d : "Agent action.",
                    Status = ToolEnabled(name) ? "Enabled" : "Disabled",
                    Requests = used.Count,
                    Succeeded = used.Count(Succeeded),
                    Failed = used.Count(a => !Succeeded(a)),
                    AvgDurationMs = used.Count == 0 ? 0 : Math.Round(used.Average(a => (double)a.Performance.TotalDurationMs), 0),
                    LastExecution = used.Count == 0 ? null : used.Max(a => a.Timestamp)
                };
            })
            .OrderByDescending(t => t.Requests).ThenBy(t => t.Name)
            .ToList();

        var versions = rows.Where(a => !string.IsNullOrEmpty(a.Ai.PromptVersion))
            .GroupBy(a => (a.Ai.PromptVersion!, a.Ai.SystemPromptRef))
            .Select(g => new AIDashboardPromptVersionDto
            {
                Version = g.Key.Item1,
                PromptRef = g.Key.SystemPromptRef,
                Current = g.Key.Item1 == PromptBuilder.Version,
                Requests = g.Count(),
                FirstUsed = g.Min(a => a.Timestamp),
                LastUsed = g.Max(a => a.Timestamp)
            })
            .OrderByDescending(v => v.LastUsed)
            .ToList();
        if (!versions.Any(v => v.Current))
            versions.Insert(0, new AIDashboardPromptVersionDto { Version = PromptBuilder.Version, Current = true });

        return new AIDashboardAgentDto
        {
            Days = days,
            Status = status,
            Provider = provider?.Name,
            Model = model,
            Capabilities = new List<AIDashboardCapabilityDto>
            {
                new("Business data questions", true, $"Read-only queries, max {settings.Query.MaxRecords} records, {settings.Query.QueryTimeoutMs} ms timeout"),
                new("Charts and KPIs", true, "Answers are shown as KPI, chart or table when useful"),
                new("Attachments", settings.Attachments.Enabled, $"PDF, images, CSV, Excel, text · max {settings.Attachments.MaxFileSizeMb} MB, {settings.Attachments.MaxFilesPerMessage} files"),
                new("Image understanding", caps?.Vision == true, caps?.Vision == true ? "Default model reads images" : "Uses a vision fallback model when configured"),
                new("Voice questions", settings.Speech.Enabled, settings.Speech.DefaultLanguage is { Length: > 0 } l ? $"Speech-to-text, default language {l}" : "Speech-to-text"),
                new("Streaming answers", settings.Chat.EnableStreaming, "Answer text appears while it is generated"),
                new("Conversation memory", settings.Chat.SaveConversations, $"Uses the last {settings.Chat.HistoryMessages} messages for follow-up questions"),
                new("Languages", true, "English, Arabic, Malayalam and mixed")
            },
            Tools = tools,
            PromptVersions = versions,
            Channels = new AIDashboardChannelsDto
            {
                VoiceRequests = rows.Count(a => a.Request.InputType == "voice"),
                AttachmentRequests = rows.Count(a => a.Request.AttachmentCount > 0),
                TextRequests = rows.Count(a => a.Request.InputType != "voice" && a.Request.AttachmentCount == 0),
                Attachments = rows.Sum(a => a.Request.AttachmentCount),
                Languages = rows.GroupBy(a => string.IsNullOrEmpty(a.Request.Language) ? "unknown" : a.Request.Language)
                    .Select(g => new NamedCountDto(g.Key, g.Count())).OrderByDescending(x => x.Count).ToList()
            }
        };
    }

    // ------------------------------------------------------------ security events (aidashboard.security)

    public async Task<AIDashboardSecurityDto> SecurityAsync(AIDashboardAccess access, int days, CancellationToken ct)
    {
        var companyId = Scope(access);
        days = Math.Clamp(days, 1, 365);
        await _dashboard.AuditAsync(access, "Security", null, ct);
        var from = _time.GetUtcNow().UtcDateTime.AddDays(-days);
        var rows = await _activities.RecentForAnalysisAsync(access.TenantRef, from, AnalysisLimit, ct);
        var (denied, deniedTotal) = await _audit.SearchForTenantAsync(new AuditLogFilter
        {
            CompanyId = companyId, TenantRef = access.TenantRef, Action = "AIDashboard.Denied", From = from, PageSize = 200
        }, ct);

        string User(AIActivity a) => a.UserName ?? a.UserId ?? string.Empty;
        var events = new List<AIDashboardSecurityEventDto>();
        foreach (var a in rows)
        {
            if (a.Validation?.Blocked == true)
                events.Add(new() { At = a.Timestamp, Type = "QueryBlocked", Severity = "High", User = User(a), RequestId = a.ActivityId,
                    Detail = a.Validation.BlockedOperations.Count > 0 ? "Blocked operation: " + string.Join(", ", a.Validation.BlockedOperations) : "Unsafe query blocked" });
            else if (a.Validation?.Status == "Rejected")
                events.Add(new() { At = a.Timestamp, Type = "ValidationFailed", Severity = "Medium", User = User(a), RequestId = a.ActivityId,
                    Detail = $"Query rejected by validation ({a.Validation.Errors.Count} issue(s))" });
            if (a.Ai.ToolSelected == "refused-technical-details")
                events.Add(new() { At = a.Timestamp, Type = "TechnicalDetailsRequest", Severity = "Low", User = User(a), RequestId = a.ActivityId,
                    Detail = "Asked for internal queries or system details — refused" });
            foreach (var e in a.Errors.Where(e => e.Type is not (ActivityErrorTypes.QueryValidationError or ActivityErrorTypes.CancelledError)))
                events.Add(new() { At = e.At, Type = "Error", Severity = "Low", User = User(a), RequestId = a.ActivityId, Detail = $"{e.Type} · {e.Stage}" });
        }
        events.AddRange(denied.Select(d => new AIDashboardSecurityEventDto
        {
            At = d.CreatedAt, Type = "AccessDenied", Severity = "High", User = d.UserName ?? d.UserId ?? string.Empty,
            Detail = $"AI Dashboard {d.Resource} refused ({d.Outcome})"
        }));

        return new AIDashboardSecurityDto
        {
            Days = days,
            ValidationFailures = rows.Count(a => a.Validation?.Status == "Rejected" && a.Validation.Blocked != true),
            BlockedQueries = rows.Count(a => a.Validation?.Blocked == true),
            TechnicalDetailRequests = rows.Count(a => a.Ai.ToolSelected == "refused-technical-details"),
            AccessDenied = (int)deniedTotal,
            Errors = rows.Count(a => a.Errors.Count > 0),
            ErrorsByCategory = rows.SelectMany(a => a.Errors).GroupBy(e => e.Type)
                .Select(g => new NamedCountDto(g.Key, g.Count())).OrderByDescending(x => x.Count).ToList(),
            Events = events.OrderByDescending(e => e.At).Take(200).ToList()
        };
    }

    // ------------------------------------------------------------ audit log (aidashboard.security)

    public async Task<PagedResult<AIDashboardAuditDto>> AuditLogAsync(AIDashboardAccess access, AuditLogFilter filter, CancellationToken ct)
    {
        filter.CompanyId = Scope(access);      // always the caller's tenant
        filter.TenantRef = access.TenantRef;
        filter.Page = Math.Max(1, filter.Page);
        filter.PageSize = Math.Clamp(filter.PageSize, 1, 100);
        await _dashboard.AuditAsync(access, "AuditLog", null, ct);
        var (items, total) = await _audit.SearchForTenantAsync(filter, ct);
        return new PagedResult<AIDashboardAuditDto>(items
            .Where(l => l.CompanyId == filter.CompanyId || l.TenantRef == filter.TenantRef)
            .Select(l => new AIDashboardAuditDto
            {
                At = l.CreatedAt,
                User = l.UserName ?? l.UserId ?? "—",
                Action = l.Action,
                Resource = l.Resource,
                ResourceId = l.ResourceId,
                Status = l.Outcome ?? "Allowed",
                IpAddress = l.IpAddress,
                CorrelationId = l.CorrelationId,
                Details = SecretMasker.Text(l.Details) is { Length: > 300 } d ? d[..300] + "…" : SecretMasker.Text(l.Details)
            }).ToList(), total, filter.Page, filter.PageSize);
    }

    // ------------------------------------------------------------ activity filter options (aidashboard.activity)

    public async Task<AIDashboardActivityFiltersDto> ActivityFiltersAsync(AIDashboardAccess access, CancellationToken ct)
    {
        Scope(access);
        var rows = await _activities.RecentForAnalysisAsync(access.TenantRef, _time.GetUtcNow().UtcDateTime.AddDays(-90), 5_000, ct);
        return new AIDashboardActivityFiltersDto
        {
            Users = rows.Where(a => !string.IsNullOrEmpty(a.UserId))
                .GroupBy(a => a.UserId!)
                .Select(g => new NamedRef(g.Key, g.Select(a => a.UserName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? g.Key))
                .OrderBy(u => u.Name).ToList(),
            Actions = ToolDescriptions.Keys.Where(k => k != "voice-transcription")
                .Concat(rows.Select(a => a.Ai.ToolSelected)).Where(t => !string.IsNullOrEmpty(t) && t != "none")
                .Distinct().OrderBy(t => t).ToList(),
            InputTypes = rows.Select(a => a.Request.InputType).Append("text").Append("voice")
                .Where(t => !string.IsNullOrEmpty(t)).Distinct().OrderBy(t => t).ToList()
        };
    }
}
