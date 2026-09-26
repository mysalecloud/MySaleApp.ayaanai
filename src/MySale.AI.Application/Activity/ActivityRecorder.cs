using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Attachments;
using MySale.AI.Application.Common;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Application.ActivityTracking;

/// <summary>
/// Creates one <see cref="ActivityRecorder"/> per AI request. Scoped: it carries the request context
/// (correlation id, client app/version, session) and the authenticated user.
/// </summary>
public sealed class ActivityTracker
{
    private readonly IAIActivitySink _sink;
    private readonly ActivityOptions _options;
    private readonly IRequestContext _request;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ActivityTracker(IAIActivitySink sink, ActivityOptions options, IRequestContext request, IUserContext user,
        TimeProvider time, ILogger<ActivityTracker>? logger = null)
    {
        _sink = sink;
        _options = options;
        _request = request;
        _user = user;
        _time = time;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string CorrelationId => _request.CorrelationId;

    public ActivityRecorder Begin(ChatRequest request, bool streamed)
    {
        if (!_options.Enabled) return ActivityRecorder.Disabled;
        try
        {
            return new ActivityRecorder(_sink, _options, _request, _user, _time, _logger, request, streamed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Activity tracking could not start; the request continues without it");
            return ActivityRecorder.Disabled;
        }
    }
}

/// <summary>
/// Collects what happens during one AI request and hands the finished <see cref="AIActivity"/> to the write-only
/// sink. Every method is exception-safe: tracking problems are logged and never affect the AI answer.
/// Only data the application itself produces is recorded (no hidden model reasoning, no tokens or secrets).
/// </summary>
public sealed class ActivityRecorder
{
    public static readonly ActivityRecorder Disabled = new();

    private readonly bool _enabled;
    private readonly IAIActivitySink? _sink;
    private readonly ActivityOptions _options = new();
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly AIActivity _a = new();
    private readonly List<string> _sensitive = new();

    private string? _conversationTitle;
    private long _mongoMs;
    private long _validationMs;
    private bool _completed;

    private ActivityRecorder() { }

    internal ActivityRecorder(IAIActivitySink sink, ActivityOptions options, IRequestContext request, IUserContext user,
        TimeProvider time, ILogger logger, ChatRequest chat, bool streamed)
    {
        _enabled = true;
        _sink = sink;
        _options = options;
        _time = time;
        _logger = logger;
        _sensitive.AddRange(options.SensitiveFields);

        var now = Now;
        _a.ActivityId = Guid.NewGuid().ToString("N");
        _a.CorrelationId = request.CorrelationId;
        _a.Timestamp = now;
        _a.CreatedAt = now;
        _a.UserId = string.IsNullOrEmpty(user.UserId) ? null : user.UserId;
        _a.TenantRef = ActivityHashing.TenantRef(user.DatabaseName, user.CompanyId);
        _a.AuthSource = user.IsMySaleBooksUser ? "mysalebooks" : "agent";
        _a.ConversationId = string.IsNullOrWhiteSpace(chat.ConversationId) ? null : chat.ConversationId;
        _a.Request = new ActivityRequestInfo
        {
            Question = Text(chat.Message ?? string.Empty),
            InputType = chat.Voice is not null ? "voice" : (chat.Attachments?.Count ?? 0) > 0 ? "attachment" : "text",
            AttachmentCount = chat.Attachments?.Count ?? 0,
            ClientApp = Clip(request.ClientApp, 64),
            ClientVersion = Clip(request.ClientVersion, 64),
            SessionId = Clip(request.SessionId, 128),
            Endpoint = Clip(request.Endpoint, 128),
            Source = user.IsMySaleBooksUser ? "mysalebooks" : string.IsNullOrEmpty(request.ClientApp) ? "api" : "dashboard",
            Streamed = streamed,
            Regenerated = !string.IsNullOrEmpty(chat.RegenerateMessageId)
        };
        _a.Request.Language = DetectLanguage(chat.Message, chat.Voice?.Language);
        AddStage(ActivityStages.Request, "ok", null, $"{_a.Request.InputType} · {_a.Request.Source}");
        AddStage(ActivityStages.Authentication, "ok", null,
            user.IsMySaleBooksUser ? "MySaleBooks JWT validated · tenant " + _a.TenantRef : "Agent account · tenant " + _a.TenantRef);
    }

    public bool IsEnabled => _enabled;
    public string ActivityId => _a.ActivityId;
    public string CorrelationId => _a.CorrelationId;
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ request / conversation

    public void SetQuestion(string question, string inputType) => Safe(() =>
    {
        _a.Request.Question = Text(question);
        _a.Request.InputType = inputType;
        if (_a.Request.Language == "en") _a.Request.Language = DetectLanguage(question, null);
    });

    public void SetConversation(Conversation conversation, bool persisted) => Safe(() =>
    {
        _a.ConversationId = conversation.Id;
        _conversationTitle = Text(conversation.Title, 200);
        AddStage("Conversation", "ok", null, persisted ? $"Saved conversation · {conversation.MessageCount} earlier messages" : "Not saved (history off)");
    });

    public void UserMessage(string id) => Safe(() => _a.UserMessageId = id);

    // ------------------------------------------------------------------ AI provider + prompt

    public void SetProvider(ResolvedProvider p) => Safe(() =>
    {
        _a.Ai.ProviderId = p.Config.Id;
        _a.Ai.ProviderName = p.Config.Name;
        _a.Ai.ProviderKind = p.Config.Kind;
        _a.Ai.IsLocal = p.IsLocal;
        _a.Ai.Model = p.Model;
        AddStage(ActivityStages.AIProvider, "ok", null, $"{p.Config.Name} · {p.Model}");
    });

    public void SetPrompt(IReadOnlyList<AIChatMessage> messages) => Safe(() =>
    {
        _a.Ai.PromptVersion = PromptBuilder.Version;
        var system = messages.FirstOrDefault(m => m.Role == "system")?.Content;
        if (system is not null) _a.Ai.SystemPromptRef = "sha256:" + ActivityHashing.Sha256(system)[..16];
        AddStage(ActivityStages.Prompt, "ok", null, $"{PromptBuilder.Version} · {messages.Count} messages");
    });

    // ------------------------------------------------------------------ query generation + validation

    /// <summary>One model call of the query step (first attempt or a repair).</summary>
    public void QueryGenerated(int attempt, string rawOutput, long durationMs, string? modelVersion) => Safe(() =>
    {
        var now = Now;
        _a.Ai.StartedAt ??= now.AddMilliseconds(-durationMs);
        _a.Ai.Attempts = attempt + 1;
        if (!string.IsNullOrWhiteSpace(modelVersion) && !string.Equals(modelVersion, _a.Ai.Model, StringComparison.OrdinalIgnoreCase))
            _a.Ai.ModelVersion = Clip(modelVersion, 128);
        var q = _a.Query ??= new ActivityQueryInfo();
        q.GeneratedAt = now;
        q.GenerationDurationMs += durationMs;
        q.Attempts.Add(new ActivityQueryAttempt
        {
            Attempt = attempt,
            At = now,
            DurationMs = durationMs,
            RawOutput = _options.StoreRawModelOutput ? Text(rawOutput) : null
        });
        AddStage(ActivityStages.QueryGeneration, "ok", durationMs, attempt == 0 ? "Query generated" : $"Repair attempt {attempt}");
    });

    /// <summary>Validation of the latest attempt. <paramref name="query"/> may be null when the output could not be parsed.</summary>
    public void Validated(MqlQuery? query, MqlValidationResult? validation, IReadOnlyList<string> errors, long durationMs, bool parsed, string kind = "query")
        => Safe(() =>
        {
            _validationMs += durationMs;
            var status = errors.Count == 0 ? "Approved" : validation?.Blocked == true ? "Blocked" : parsed ? "Rejected" : "Unparseable";
            if (_a.Query?.Attempts.LastOrDefault() is { } last)
            {
                last.Parsed = parsed;
                last.ValidationStatus = status;
                last.Errors = errors.Select(e => Text(e, 1000)).ToList();
            }
            var v = _a.Validation ??= new ActivityValidationInfo();
            v.Status = status == "Unparseable" ? "Rejected" : status;
            v.Errors = errors.Select(e => Text(e, 1000)).ToList();
            v.Warnings = validation?.Warnings.Select(w => Text(w, 1000)).ToList() ?? new List<string>();
            v.Blocked = validation?.Blocked == true;
            v.BlockedOperations = v.Blocked ? ExtractOperators(errors) : new List<string>();
            v.DurationMs = _validationMs;
            if (query is not null) SetQuery(query, kind);
            AddStage(ActivityStages.QueryValidation, errors.Count == 0 ? "ok" : "error", durationMs,
                errors.Count == 0
                    ? "Approved" + (v.Warnings.Count > 0 ? $" · {v.Warnings.Count} warning(s)" : string.Empty)
                    : $"{status}: {Text(errors[0], 300)}");
        });

    public void Unsupported(MqlQuery query) => Safe(() =>
    {
        SetQuery(query, "unsupported");
        _a.Ai.Intent = "unsupported";
        _a.Ai.DeclineReason = Text(query.Reason ?? string.Empty, 1000);
        AddStage(ActivityStages.QueryGeneration, "warning", null, "Model declined: " + Text(query.Reason ?? "no reason", 300));
    });

    public void PlanChosen(AttachmentPlan plan) => Safe(() =>
    {
        var q = _a.Query ??= new ActivityQueryInfo();
        q.Kind = plan.Type;
        q.QueryVersion = "attachment-plan-v1";
        var json = new JsonObject { ["type"] = plan.Type, ["attachments"] = new JsonArray(plan.Attachments.Select(a => (JsonNode?)a).ToArray()) };
        if (plan.Table is not null) json["table"] = plan.Table.DeepClone();
        if (plan.Column is not null) json["column"] = plan.Column;
        if (plan.QueryJson is not null) json["query"] = plan.QueryJson.DeepClone();
        q.GeneratedQueryJson = ActivityRedactor.RedactJsonString(json, _sensitive, _options.MaximumTextLength);
        q.QueryHash = ActivityHashing.Sha256(json.ToCompact());
        _a.Ai.Intent = "attachment:" + plan.Type;
        _a.Ai.Explanation = Text(plan.Explanation ?? string.Empty, 2000);
        _a.Ai.VisualizationHint = plan.Visualization;
    });

    /// <summary>The query passed validation and was tenant-scoped (what MongoDB will run).</summary>
    public void Approved(PreparedQuery prepared) => Safe(() =>
    {
        SetQuery(prepared.Query, _a.Query?.Kind == "combined" ? "combined" : "query");
        var q = _a.Query!;
        q.Collection = prepared.Validation.Collection ?? q.Collection;
        q.ExecutedMql = Text(prepared.Mql ?? string.Empty);
        if (_a.Validation is { } v) v.RejectedQueryJson = null;
    });

    /// <summary>No executable query after all repair attempts.</summary>
    public void Rejected(MqlQuery? query, IReadOnlyList<string> errors, bool blocked) => Safe(() =>
    {
        var v = _a.Validation ??= new ActivityValidationInfo();
        v.Status = blocked ? "Blocked" : "Rejected";
        v.Blocked = blocked;
        v.Errors = errors.Select(e => Text(e, 1000)).ToList();
        if (blocked && v.BlockedOperations.Count == 0) v.BlockedOperations = ExtractOperators(errors);
        v.RejectedQueryJson = query is null
            ? (_a.Query?.Attempts.LastOrDefault()?.RawOutput)
            : ActivityRedactor.RedactJsonString(query.ToJson(), _sensitive, _options.MaximumTextLength);
        _a.Ai.Intent ??= blocked ? "blocked" : "invalid-query";
        AddError(ActivityStages.QueryValidation, ActivityErrorTypes.QueryValidationError,
            (blocked ? "Blocked: " : "Rejected: ") + string.Join("; ", errors), null);
    });

    // ------------------------------------------------------------------ MongoDB

    public void ExecutionStarted(string? collection, string? operation) => Safe(() =>
    {
        _a.Execution = new ActivityExecutionInfo
        {
            Status = "Running",
            TenantRef = _a.TenantRef,
            Collection = collection,
            Operation = operation,
            StartedAt = Now,
            Comment = MongoComment(_a.CorrelationId)
        };
    });

    public void ExecutionSucceeded(ExecutedQuery executed) => Safe(() =>
    {
        var e = _a.Execution ??= new ActivityExecutionInfo { TenantRef = _a.TenantRef, StartedAt = Now };
        e.Status = "Success";
        e.CompletedAt = Now;
        e.DurationMs = executed.ElapsedMs;
        e.DocumentsReturned = executed.Rows.Count;
        e.Truncated = executed.Truncated;
        _mongoMs = executed.ElapsedMs;
        AddStage(ActivityStages.MongoExecution, "ok", executed.ElapsedMs, $"{executed.Rows.Count} document(s){(executed.Truncated ? ", truncated" : string.Empty)}");
    });

    public void ExecutionFailed(Exception ex, bool timeout) => Safe(() =>
    {
        var e = _a.Execution ??= new ActivityExecutionInfo { TenantRef = _a.TenantRef, StartedAt = Now };
        e.Status = timeout ? "Timeout" : "Failed";
        e.TimedOut = timeout;
        e.CompletedAt = Now;
        e.DurationMs = e.StartedAt is { } s ? (long)(e.CompletedAt.Value - s).TotalMilliseconds : 0;
        e.Error = Text(ex.Message, 2000);
        _mongoMs = e.DurationMs;
        AddStage(ActivityStages.MongoExecution, "error", e.DurationMs, Text(ex.Message, 300));
        AddError(ActivityStages.MongoExecution, timeout ? ActivityErrorTypes.TimeoutError : ActivityErrorTypes.MongoDBError, ex.Message, ex);
    });

    /// <summary>Database ids in the result were replaced by names (extra read-only lookups).</summary>
    public void ReferencesResolved(IReadOnlyList<string> notes, long durationMs) => Safe(() =>
    {
        _mongoMs += durationMs;
        AddStage("ReferenceResolution", "ok", durationMs, string.Join("; ", notes));
    });

    /// <summary>Rows the answer is based on (database result, or a server-side table calculation).</summary>
    public void SetResult(List<JsonObject> rows, List<string> columns, bool truncated) => Safe(() =>
    {
        var r = new ActivityResultInfo { Count = rows.Count, Columns = columns.ToList() };
        var all = new JsonArray(rows.Select(x => (JsonNode?)x.DeepClone()).ToArray());
        var compact = all.ToCompact();
        r.DataSizeBytes = Encoding.UTF8.GetByteCount(compact);
        r.ResultHash = "sha256:" + ActivityHashing.Sha256(compact);
        r.SchemaJson = JsonSerializer.Serialize(InferSchema(rows, columns));
        r.SummaryJson = Summarize(rows, columns);

        if (_options.StoreFullResults && rows.Count > 0)
        {
            var max = Math.Max(0, _options.MaximumDocumentsToStore);
            var limit = Math.Max(1024, _options.MaximumResultSize);
            var stored = new JsonArray();
            var size = 2;
            foreach (var row in rows.Take(max))
            {
                var clean = ActivityRedactor.RedactJson(row, _sensitive, _options.ExcludedResultFields, maskPersonalData: true);
                var bytes = Encoding.UTF8.GetByteCount(clean!.ToCompact()) + 1;
                if (size + bytes > limit) break;
                size += bytes;
                stored.Add(clean);
            }
            r.Stored = stored.Count > 0;
            r.StoredDocuments = stored.Count;
            r.StoredTruncated = stored.Count < rows.Count || truncated;
            r.DataJson = r.Stored ? stored.ToCompact() : null;
        }
        _a.Result = r;
        if (_a.Execution is null)
            AddStage("Result", "ok", null, $"{rows.Count} row(s) (no database query)");
    });

    // ------------------------------------------------------------------ answer

    public void ResponseStarted() => Safe(() =>
    {
        _a.Response ??= new ActivityResponseInfo();
        _a.Response.StartedAt = Now;
    });

    public void ResponseFailed(Exception ex, long durationMs) => Safe(() =>
    {
        _a.Response ??= new ActivityResponseInfo();
        _a.Response.DurationMs = durationMs;
        _a.Response.CompletedAt = Now;
        AddStage(ActivityStages.ResponseGeneration, "error", durationMs, Text(ex.Message, 300));
        AddError(ActivityStages.ResponseGeneration, ActivityErrorTypes.ResponseGenerationError, ex.Message, ex);
    });

    public void Error(string stage, string type, Exception? ex, string? message = null)
        => Safe(() => AddError(stage, type, message ?? ex?.Message ?? type, ex));

    // ------------------------------------------------------------------ completion

    /// <summary>
    /// Final step: fills the activity from the persisted turn and queues it. Safe to call more than once
    /// (only the first call is recorded).
    /// </summary>
    public void Complete(ChatStatus status, string answer, string? error, ChatMessage assistant, QueryLog log,
        int messageCount, bool conversationPersisted, long totalMs)
    {
        if (!_enabled || _completed) return;
        _completed = true;
        try
        {
            var now = Now;
            _a.CompletedAt = now;
            _a.ChatStatus = status.ToString();
            _a.Status = MapStatus(status, error);
            _a.MessageId = conversationPersisted ? assistant.Id : null;
            _a.QueryLogId = string.IsNullOrEmpty(log.Id) ? null : log.Id;

            // AI totals
            _a.Ai.RepairAttempts = assistant.RepairAttempts;
            _a.Ai.InputTokens = assistant.InputTokens;
            _a.Ai.OutputTokens = assistant.OutputTokens;
            _a.Ai.EstimatedCost = assistant.EstimatedCost;
            _a.Ai.DurationMs = assistant.AiQueryTimeMs + assistant.AiAnswerTimeMs;
            if (_a.Ai.StartedAt is not null) _a.Ai.CompletedAt = _a.Response?.CompletedAt ?? now;
            if (!string.IsNullOrWhiteSpace(assistant.Explanation) && string.IsNullOrEmpty(_a.Ai.Explanation))
                _a.Ai.Explanation = Text(assistant.Explanation, 2000);
            _a.Ai.Intent ??= _a.Query?.Operation is { } op ? $"database:{op}" : null;

            // Response
            var resp = _a.Response ??= new ActivityResponseInfo();
            resp.Text = Text(answer);
            resp.Status = status.ToString();
            resp.Format = "markdown";
            resp.VisualizationJson = assistant.VisualizationJson;
            resp.VisualizationType = VisualizationType(assistant.VisualizationJson);
            resp.GroundingChecked = assistant.GroundingChecked;
            resp.GroundingWarnings = assistant.GroundingWarnings.Select(w => Text(w, 300)).ToList();
            if (resp.StartedAt is not null)
            {
                resp.DurationMs = resp.DurationMs > 0 ? resp.DurationMs : assistant.AiAnswerTimeMs;
                resp.CompletedAt ??= now;
                if (!_a.Timeline.Any(t => t.Stage == ActivityStages.ResponseGeneration))
                    AddStage(ActivityStages.ResponseGeneration, "ok", resp.DurationMs,
                        resp.GroundingWarnings.Count > 0 ? $"{resp.GroundingWarnings.Count} grounding warning(s)" : "Answer written");
            }

            // Errors derived from the final status (when no stage recorded one)
            if (_a.Status is ActivityStatuses.Failed or ActivityStatuses.Timeout or ActivityStatuses.Cancelled && _a.Errors.Count == 0)
                AddError(StageFor(status), TypeFor(status), error ?? status.ToString(), null);
            if (_a.Status is not (ActivityStatuses.Success or ActivityStatuses.NoResults or ActivityStatuses.Unsupported))
                _a.FailedStage = _a.Errors.LastOrDefault()?.Stage ?? StageFor(status);

            // Performance
            var p = _a.Performance;
            p.TotalDurationMs = totalMs;
            p.AiGenerationDurationMs = assistant.AiQueryTimeMs;
            p.QueryValidationDurationMs = _validationMs;
            p.MongoExecutionDurationMs = _mongoMs;
            p.ResponseGenerationDurationMs = assistant.AiAnswerTimeMs;
            p.OtherDurationMs = Math.Max(0, totalMs - p.AiGenerationDurationMs - p.QueryValidationDurationMs - p.MongoExecutionDurationMs - p.ResponseGenerationDurationMs);
            p.SlowestStage = new[]
            {
                ("AI", p.AiGenerationDurationMs), ("QueryValidation", p.QueryValidationDurationMs), ("MongoDB", p.MongoExecutionDurationMs),
                ("ResponseGeneration", p.ResponseGenerationDurationMs), ("Other", p.OtherDurationMs)
            }.OrderByDescending(x => x.Item2).First().Item1;

            AddStage(ActivityStages.Completed, _a.Status is ActivityStatuses.Success or ActivityStatuses.NoResults ? "ok" : _a.Status == ActivityStatuses.Unsupported ? "warning" : "error",
                totalMs, $"{_a.Status} · {totalMs} ms");

            ConversationActivityUpdate? conversation = null;
            if (!string.IsNullOrEmpty(_a.ConversationId))
                conversation = new ConversationActivityUpdate
                {
                    ConversationId = _a.ConversationId!,
                    UserId = _a.UserId,
                    TenantRef = _a.TenantRef,
                    Title = _conversationTitle ?? Text(_a.Request.Question, 80),
                    MessageCount = messageCount,
                    At = now,
                    Succeeded = _a.Status is ActivityStatuses.Success or ActivityStatuses.NoResults,
                    Status = _a.Status,
                    TotalMs = totalMs,
                    AiMs = _a.Ai.DurationMs,
                    MongoMs = _mongoMs,
                    Provider = _a.Ai.ProviderName,
                    Model = _a.Ai.Model
                };

            if (!_sink!.TryEnqueue(_a, conversation))
                _logger.LogWarning("Activity {ActivityId} (correlation {CorrelationId}) was dropped: the activity queue is full", _a.ActivityId, _a.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Activity tracking failed for correlation {CorrelationId}; the AI answer is unaffected", _a.CorrelationId);
        }
    }

    /// <summary>Records a request that ended with an exception before a normal completion (e.g. invalid input).</summary>
    public void Fail(Exception ex, long totalMs)
    {
        if (!_enabled || _completed) return;
        try
        {
            var (stage, type) = ex switch
            {
                TenantResolutionException => (ActivityStages.Authentication, ActivityErrorTypes.AuthenticationError),
                AIProviderException => (ActivityStages.AIProvider, ActivityErrorTypes.AIProviderError),
                OperationCanceledException => (ActivityStages.Request, ActivityErrorTypes.CancelledError),
                _ => (ActivityStages.Request, ActivityErrorTypes.UnknownError)
            };
            AddError(stage, type, ex.Message, ex);
            Complete(ChatStatus.Error, string.Empty, ex is OperationCanceledException ? "Cancelled by client" : ex.GetType().Name,
                new ChatMessage(), new QueryLog(), 0, false, totalMs);
        }
        catch (Exception inner)
        {
            _logger.LogWarning(inner, "Activity tracking failed while recording an error");
        }
    }

    // ------------------------------------------------------------------ helpers

    private void SetQuery(MqlQuery query, string kind)
    {
        var q = _a.Query ??= new ActivityQueryInfo();
        if (q.Kind != "combined") q.Kind = kind;
        q.QueryVersion = q.Kind == "combined" ? "attachment-plan-v1+mql-v1" : "mql-v1";
        q.Collection = query.Collection;
        q.Operation = query.Operation;
        var json = query.ToJson();
        if (q.Kind != "combined") q.GeneratedQueryJson = ActivityRedactor.RedactJsonString(json, _sensitive, _options.MaximumTextLength);
        q.PipelineJson = ActivityRedactor.RedactJsonString(query.Pipeline, _sensitive, _options.MaximumTextLength);
        q.FilterJson = ActivityRedactor.RedactJsonString(query.Filter, _sensitive, _options.MaximumTextLength);
        q.ProjectionJson = ActivityRedactor.RedactJsonString(query.Projection, _sensitive, _options.MaximumTextLength);
        q.SortJson = ActivityRedactor.RedactJsonString(query.Sort, _sensitive, _options.MaximumTextLength);
        q.Limit = query.Limit;
        // Hash only the executable part (not the explanation text), so identical queries group together.
        var hashable = new JsonObject { ["operation"] = query.Operation, ["collection"] = query.Collection };
        if (query.Pipeline is not null) hashable["pipeline"] = query.Pipeline.DeepClone();
        if (query.Filter is not null) hashable["filter"] = query.Filter.DeepClone();
        if (query.Projection is not null) hashable["projection"] = query.Projection.DeepClone();
        if (query.Sort is not null) hashable["sort"] = query.Sort.DeepClone();
        if (query.Limit is not null) hashable["limit"] = query.Limit;
        if (query.Field is not null) hashable["field"] = query.Field;
        q.QueryHash = "sha256:" + ActivityHashing.Sha256(hashable.ToCompact());
        if (!string.IsNullOrWhiteSpace(query.Explanation)) _a.Ai.Explanation = Text(query.Explanation, 2000);
        if (!string.IsNullOrWhiteSpace(query.Visualization)) _a.Ai.VisualizationHint = query.Visualization;
        if (!query.IsUnsupported && q.Kind == "query") _a.Ai.Intent = $"database:{query.Operation}";
        else if (q.Kind == "combined") _a.Ai.Intent = "attachment:combined";
    }

    private void AddStage(string stage, string status, long? durationMs, string? detail)
        => _a.Timeline.Add(new ActivityStageEntry { Stage = stage, Status = status, At = Now, DurationMs = durationMs, Detail = detail is null ? null : Text(detail, 500) });

    private void AddError(string stage, string type, string message, Exception? ex)
        => _a.Errors.Add(new ActivityErrorInfo
        {
            Stage = stage,
            Type = type,
            Message = Text(message, 2000),
            At = Now,
            CorrelationId = _a.CorrelationId,
            ExceptionType = ex?.GetType().Name,
            StackTrace = _options.StoreStackTraces && ex?.StackTrace is { } st ? Text(st, 8000) : null
        });

    private void Safe(Action action)
    {
        if (!_enabled || _completed) return;
        try { action(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Activity tracking step failed (correlation {CorrelationId})", _a.CorrelationId); }
    }

    private string Text(string? text, int? max = null) => ActivityRedactor.RedactText(text, max ?? _options.MaximumTextLength);

    private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : ActivityRedactor.RedactText(s.Trim(), max);

    public static string MongoComment(string correlationId) => "ayaan:" + correlationId;

    public static string MapStatus(ChatStatus status, string? error) => status switch
    {
        ChatStatus.Success => ActivityStatuses.Success,
        ChatStatus.NoResults => ActivityStatuses.NoResults,
        ChatStatus.Unsupported => ActivityStatuses.Unsupported,
        ChatStatus.InvalidQuery => ActivityStatuses.Rejected,
        ChatStatus.Timeout => ActivityStatuses.Timeout,
        ChatStatus.Error when error == "Cancelled by client" => ActivityStatuses.Cancelled,
        _ => ActivityStatuses.Failed
    };

    private static string StageFor(ChatStatus status) => status switch
    {
        ChatStatus.ProviderError => ActivityStages.AIProvider,
        ChatStatus.InvalidQuery => ActivityStages.QueryValidation,
        ChatStatus.DatabaseError or ChatStatus.Timeout => ActivityStages.MongoExecution,
        _ => ActivityStages.Request
    };

    private static string TypeFor(ChatStatus status) => status switch
    {
        ChatStatus.ProviderError => ActivityErrorTypes.AIProviderError,
        ChatStatus.InvalidQuery => ActivityErrorTypes.QueryValidationError,
        ChatStatus.DatabaseError => ActivityErrorTypes.MongoDBError,
        ChatStatus.Timeout => ActivityErrorTypes.TimeoutError,
        _ => ActivityErrorTypes.UnknownError
    };

    private static List<string> ExtractOperators(IEnumerable<string> errors)
        => errors.SelectMany(e => System.Text.RegularExpressions.Regex.Matches(e, @"\$[A-Za-z][A-Za-z0-9]*").Select(m => m.Value))
            .Distinct(StringComparer.Ordinal).Take(20).ToList();

    private static string? VisualizationType(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json)?["type"]?.GetValue<string>(); }
        catch (Exception) { return null; }
    }

    public static string DetectLanguage(string? text, string? voiceLanguage)
    {
        if (!string.IsNullOrWhiteSpace(voiceLanguage) && voiceLanguage.Length <= 10) return voiceLanguage.ToLowerInvariant();
        if (string.IsNullOrEmpty(text)) return "en";
        int ml = 0, ar = 0, latin = 0;
        foreach (var c in text)
        {
            if (c >= 'ഀ' && c <= 'ൿ') ml++;
            else if (c >= '؀' && c <= 'ۿ') ar++;
            else if (char.IsLetter(c) && c < 'ɐ') latin++;
        }
        if (ml > 0 && ml >= ar) return latin > ml ? "ml+en" : "ml";
        if (ar > 0) return latin > ar ? "ar+en" : "ar";
        return "en";
    }

    private static Dictionary<string, string> InferSchema(List<JsonObject> rows, List<string> columns)
    {
        var schema = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in columns)
        {
            string type = "null";
            foreach (var row in rows.Take(50))
            {
                if (!row.TryGetPropertyValue(c, out var v) || v is null) continue;
                type = v switch
                {
                    JsonObject => "object",
                    JsonArray => "array",
                    JsonValue val => val.GetValueKind() switch
                    {
                        JsonValueKind.Number => "number",
                        JsonValueKind.True or JsonValueKind.False => "boolean",
                        JsonValueKind.String => "string",
                        _ => "null"
                    },
                    _ => "null"
                };
                if (type != "null") break;
            }
            schema[c] = type;
        }
        return schema;
    }

    private static string? Summarize(List<JsonObject> rows, List<string> columns)
    {
        if (rows.Count == 0) return null;
        var summary = new JsonObject { ["rows"] = rows.Count };
        foreach (var c in columns)
        {
            int n = 0;
            double sum = 0, min = double.MaxValue, max = double.MinValue;
            foreach (var row in rows)
            {
                if (!row.TryGetPropertyValue(c, out var v) || !JsonHelpers.TryGetNumber(v, out var d)) continue;
                n++;
                sum += d;
                if (d < min) min = d;
                if (d > max) max = d;
            }
            if (n > 0) summary[c] = new JsonObject { ["count"] = n, ["sum"] = Math.Round(sum, 4), ["min"] = min, ["max"] = max };
        }
        return summary.ToCompact();
    }
}
