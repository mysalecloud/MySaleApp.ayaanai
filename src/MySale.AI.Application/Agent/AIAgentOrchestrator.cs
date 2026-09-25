using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Attachments;
using MySale.AI.Application.Common;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

/// <summary>Receives progress events while a chat turn runs (SSE streaming). Non-streaming callers use <see cref="NullChatEventSink"/>.</summary>
public interface IChatEventSink
{
    bool StreamTokens { get; }
    Task OnStatusAsync(string stage, string message, CancellationToken ct);
    Task OnQueryAsync(QueryInfoDto query, CancellationToken ct);
    Task OnTokenAsync(string text, CancellationToken ct);
}

public sealed class NullChatEventSink : IChatEventSink
{
    public static readonly NullChatEventSink Instance = new();
    public bool StreamTokens => false;
    public Task OnStatusAsync(string stage, string message, CancellationToken ct) => Task.CompletedTask;
    public Task OnQueryAsync(QueryInfoDto query, CancellationToken ct) => Task.CompletedTask;
    public Task OnTokenAsync(string text, CancellationToken ct) => Task.CompletedTask;
}

public static class UserMessages
{
    public const string ProviderUnavailable = "AI service is currently unavailable.";
    /// <summary>Kept for logs/compatibility; users see one of <see cref="BlockedQueryMessages"/> instead.</summary>
    public const string InvalidQuery = "Unable to safely execute the generated query.";

    /// <summary>
    /// Friendly, playful "blocked for safety" replies (one picked at random). They never reveal the query,
    /// the rules or any internals, never blame the user, and don't suggest the database is down.
    /// </summary>
    public static readonly string[] BlockedQueryMessages =
    {
        "🤖 Oops! That query got a little too adventurous, so I stopped it for safety. Let me try a safer approach — try asking again or rephrasing.",
        "😂 My database security guard said, \"Not today!\" The query was blocked for safety — try rephrasing and I'll take another route.",
        "Oops! I came up with something your database wasn't comfortable running, so I stopped it. Ask again and I'll take a safer route.",
        "🛡️ Query stopped! Safety first — let's try that question a slightly different way.",
        "Looks like that query crossed the safety line, so I hit the brakes. No worries — try asking it another way!",
        "I drafted a query that didn't pass my safety check, so it never ran. Your data is fine — let's try a different approach.",
        "🚦 Red light! I stopped that query before it ran, just to be safe. Try rephrasing and I'll find a safer path.",
        "My inner safety inspector wasn't happy with that query, so I blocked it. Let's try the question from another angle.",
        "🙈 That one's a no-go — I stopped the query for safety. Give me another try with a slightly different question.",
        "Safety check said \"hmm, not like that.\" The query was blocked — try asking in a different way and I'll have another go.",
    };

    private static readonly string[] BlockedQueryMessagesMl =
    {
        "🤖 അയ്യോ! ആ query അൽപ്പം സാഹസികമായിപ്പോയി, സുരക്ഷയ്ക്കായി ഞാൻ അത് നിർത്തി. ചോദ്യം മറ്റൊരു രീതിയിൽ ചോദിച്ചു നോക്കൂ!",
        "🛡️ സുരക്ഷ ആദ്യം! ആ query ഞാൻ തടഞ്ഞു. മറ്റൊരു രീതിയിൽ ശ്രമിക്കാം.",
        "ആ query സുരക്ഷാ പരിശോധന കടന്നില്ല, അതുകൊണ്ട് ഞാൻ അത് run ചെയ്തില്ല. നിങ്ങളുടെ data സുരക്ഷിതമാണ് — ചോദ്യം ഒന്ന് മാറ്റി ചോദിക്കൂ.",
    };

    private static readonly string[] BlockedQueryMessagesAr =
    {
        "🛡️ الأمان أولاً! أوقفتُ هذا الاستعلام احتياطاً. جرّب صياغة السؤال بطريقة أخرى.",
        "أوه! لم يجتز الاستعلام فحص الأمان فأوقفته. بياناتك بخير — لنجرّب طريقة مختلفة.",
    };

    /// <summary>A random friendly "blocked for safety" reply, in the language of the question.</summary>
    public static string BlockedQuery(string question)
    {
        var pool = System.Text.RegularExpressions.Regex.IsMatch(question, @"[\u0D00-\u0D7F]") ? BlockedQueryMessagesMl
            : System.Text.RegularExpressions.Regex.IsMatch(question, @"[\u0600-\u06FF]") ? BlockedQueryMessagesAr
            : BlockedQueryMessages;
        return pool[Random.Shared.Next(pool.Length)];
    }
    public const string DatabaseError = "Unable to access your business data.";
    public const string NoResults = "I couldn't find any matching records.";

    /// <summary>"No records" in the language of the question (Malayalam / Arabic / English).</summary>
    public static string NoResultsFor(string question)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"[\u0D00-\u0D7F]"))
            return "ഈ ചോദ്യത്തിന് പൊരുത്തപ്പെടുന്ന രേഖകളൊന്നും കണ്ടെത്തിയില്ല. മറ്റൊരു തീയതിയോ കാലയളവോ (ഉദാ: ഈ മാസം, കഴിഞ്ഞ മാസം) ഉപയോഗിച്ച് ശ്രമിക്കുക.";
        if (System.Text.RegularExpressions.Regex.IsMatch(question, @"[\u0600-\u06FF]"))
            return "لم أجد أي سجلات مطابقة. جرّب فترة زمنية أخرى (مثل هذا الشهر أو الشهر الماضي).";
        return NoResults;
    }
    public const string Unsupported = "I don't have enough information to answer that question.";
    public const string QueryTimeout = "The query took too long to run. Try a narrower question or date range.";
    public const string RequestTimeout = "The request timed out. Please try again or choose a faster model.";
    public const string Cancelled = "The request was cancelled.";
    public const string Unexpected = "Something went wrong while processing your question. Please try again.";
    public const string AnswerFailed = "I retrieved the data, but the AI provider could not summarise it. The results are shown below.";
}

/// <summary>
/// Central agent workflow: conversation → schema → provider → MQL → validation (+repair) → tenant scope →
/// MongoDB → result processing → grounded answer → persistence, logging and usage.
/// </summary>
public sealed class AIAgentOrchestrator
{
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly IQueryLogRepository _logs;
    private readonly ProviderService _providers;
    private readonly SettingsService _settings;
    private readonly QueryEngine _engine;
    private readonly PromptBuilder _prompts;
    private readonly AuditService _audit;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;
    private readonly ILogger<AIAgentOrchestrator> _logger;
    private readonly AttachmentService? _attachments;
    private readonly ModelCapabilityService? _capabilities;

    public AIAgentOrchestrator(
        IConversationRepository conversations,
        IMessageRepository messages,
        IQueryLogRepository logs,
        ProviderService providers,
        SettingsService settings,
        QueryEngine engine,
        PromptBuilder prompts,
        AuditService audit,
        IUserContext user,
        TimeProvider time,
        ILogger<AIAgentOrchestrator> logger,
        AttachmentService? attachments = null,
        ModelCapabilityService? capabilities = null)
    {
        _attachments = attachments;
        _capabilities = capabilities;
        _conversations = conversations;
        _messages = messages;
        _logs = logs;
        _providers = providers;
        _settings = settings;
        _engine = engine;
        _prompts = prompts;
        _audit = audit;
        _user = user;
        _time = time;
        _logger = logger;
    }

    public async Task<ChatResponse> RunAsync(ChatRequest request, IChatEventSink sink, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var settings = await _settings.GetAsync(ct);
        var persist = settings.Chat.SaveConversations;
        var question = (request.Message ?? string.Empty).Trim();
        if (question.Length == 0)
        {
            if ((request.Attachments?.Count ?? 0) == 0) throw new AppValidationException("Please enter a question.");
            question = "Summarize the attached file(s) and highlight the key figures.";
        }
        var trace = new DebugTrace();
        trace.Add("question", "User question", question);
        if (_user.IsMySaleBooksUser)
            trace.Add("tenant", "Customer database",
                $"JWT: ✓ Valid\nDatabase: {(_user.DatabaseName is null ? "✗ Not resolved" : "✓ Resolved from the JWT dbName claim")}\nDatabase name: {_user.DatabaseName ?? "-"}\n" +
                (_user.TenantIsDatabase ? "Isolation: customer database" : $"Isolation: customer database + company filter"),
                _user.DatabaseName is null ? "error" : "ok");

        // 1. Conversation + context
        var conversation = await LoadOrCreateConversationAsync(request, question, persist, ct);
        if (persist && !string.IsNullOrEmpty(request.RegenerateMessageId))
            await RemoveForRegenerateAsync(conversation, request.RegenerateMessageId, ct);

        var history = persist && settings.Chat.HistoryMessages > 0 && conversation.MessageCount > 0
            ? await _messages.ListRecentAsync(conversation.Id, settings.Chat.HistoryMessages, ct)
            : new List<ChatMessage>();

        // Unified input: typed text, voice (already transcribed) and attachments all become one request.
        var attachmentsCurrent = new List<Attachment>();
        var attachmentsEarlier = new List<Attachment>();
        if (_attachments is not null && ((request.Attachments?.Count ?? 0) > 0 || history.Any(m => m.Attachments.Count > 0)))
            (attachmentsCurrent, attachmentsEarlier) = await _attachments.ResolveTurnAsync(
                request.Attachments, history, conversation.Id, persist, settings.Attachments.MaxFilesPerMessage, ct);

        var inputType = request.Voice is not null ? "voice" : attachmentsCurrent.Count > 0 ? "attachment" : "text";
        if (request.InputType is "text" or "voice" or "attachment") inputType = request.InputType;

        var now = _time.GetUtcNow().UtcDateTime;
        var userMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            UserId = _user.UserId,
            CompanyId = _user.CompanyId,
            Role = MessageRole.User,
            Content = question,
            Status = ChatStatus.Success,
            CreatedAt = now,
            InputType = inputType,
            Attachments = attachmentsCurrent.Select(AttachmentService.ToRef).ToList(),
            Voice = request.Voice is null ? null : new VoiceInfo
            {
                AttachmentId = request.Voice.AttachmentId,
                Language = VoiceService.NormalizeLanguage(request.Voice.Language),
                DurationSeconds = request.Voice.DurationSeconds
            }
        };
        if (inputType != "text" || userMessage.Voice is not null)
            trace.Add("input", "Input",
                $"Input type: {inputType}" +
                (userMessage.Voice is { } v ? $"\nTranscription: \"{question}\"\nLanguage: {v.Language ?? "auto"}\nDuration: {v.DurationSeconds:0.0}s\nEngine: {request.Voice?.Engine ?? "server"}" : string.Empty) +
                (attachmentsCurrent.Count > 0 ? "\nAttachments: " + string.Join(", ", attachmentsCurrent.Select(a => $"{a.FileName} ({a.Kind})")) : string.Empty) +
                (attachmentsEarlier.Count > 0 ? "\nEarlier attachments in context: " + string.Join(", ", attachmentsEarlier.Select(a => a.FileName)) : string.Empty));
        if (persist) await _messages.InsertAsync(userMessage, ct);
        else userMessage.Id = Guid.NewGuid().ToString("N");

        var assistant = new ChatMessage
        {
            ConversationId = conversation.Id,
            UserId = _user.UserId,
            CompanyId = _user.CompanyId,
            Role = MessageRole.Assistant,
            Status = ChatStatus.Pending
        };
        var log = new QueryLog
        {
            Question = question,
            UserId = _user.UserId,
            UserName = _user.DisplayName,
            CompanyId = _user.CompanyId,
            ConversationId = persist ? conversation.Id : null,
            Streamed = sink.StreamTokens
        };

        ResolvedProvider? provider = null;
        PromptContext promptContext = null!;
        var attachmentContext = new AttachmentContext(attachmentsCurrent, attachmentsEarlier);
        ModelCapabilities? capabilities = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, settings.Ai.TimeoutSeconds)));
        var token = timeout.Token;

        try
        {
            await sink.OnStatusAsync("thinking", "Understanding your question…", token);

            // 2. Provider
            provider = await _providers.ResolveAsync(request.ProviderId, request.Model, settings, token);
            ApplyProvider(assistant, log, provider);
            trace.Add("provider", "AI provider",
                $"{provider.Config.Name} ({provider.KindInfo?.DisplayName ?? provider.Config.Kind}, {provider.Config.Category})\nModel: {provider.Model}\nEndpoint: {provider.Config.BaseUrl}");

            // 3. Schema
            var schema = await _engine.GetAllowedSchemaAsync(settings, token);
            if (schema.Count == 0)
                throw new QueryExecutionException("No collections are available for querying. Check the Database and Settings pages.");

            var anchors = DateAnchors.Compute(now, _user.TimeZone);
            promptContext = new PromptContext(_user.CompanyName, _user.Currency, _user.TimeZone, anchors, settings.Query.MaxRecords, _engine.TenantField);

            // 3b. Attachments: read images with a vision model (cached), describe files to the planner
            if (attachmentContext.Any && _attachments is not null && _capabilities is not null)
            {
                capabilities = await _capabilities.GetAsync(provider, token);
                var unreadImages = attachmentContext.Items.Where(i => i.Attachment.Kind == AttachmentKinds.Image && i.Attachment.Chunks.Count == 0).ToList();
                if (unreadImages.Count > 0)
                {
                    var vision = await _capabilities.ResolveVisionAsync(provider, settings, token);
                    if (vision is null)
                    {
                        trace.Add("attachments", "Attachments", $"Model {provider.Model} has no vision capability and no vision fallback is configured.", "error");
                        return await FinishAsync(ChatStatus.Unsupported,
                            $"This model ({provider.Model}) does not support image attachments. Choose a vision model (e.g. llama3.2-vision, gemma3, gpt-4o-mini) or set a vision provider under Settings → Attachments.",
                            "No vision model");
                    }
                    await sink.OnStatusAsync("reading_attachments", $"Reading {unreadImages.Count} image(s) with {vision.Model}…", token);
                    var sw = Stopwatch.StartNew();
                    foreach (var img in unreadImages) await _attachments.EnsureImageContentAsync(img.Attachment, vision, token);
                    trace.Add("vision", "Image reading", $"Provider: {vision.Config.Name} · {vision.Model}{(vision == provider ? "" : " (vision fallback)")}", "ok", sw.ElapsedMilliseconds);
                }
                trace.Add("attachments", "Attachments",
                    string.Join("\n\n", attachmentContext.Items.Select(i =>
                        $"{i.Alias}: {i.Attachment.FileName} · {i.Attachment.Kind} · {i.Attachment.Size:N0} bytes{(i.Current ? "" : " (earlier)")}\n" +
                        (i.Attachment.Table is { } t
                            ? $"Table: {t.TotalRows} rows · {string.Join(", ", t.Columns)}"
                            : "Extracted content: " + SensitiveDataMasker.Mask(JsonHelpers.Truncate(string.Join("\n", i.Attachment.Chunks.Take(2).Select(c => c.Text)), 800))))));
            }

            // 4-6. Generate → validate → (repair) → scope
            var messages = _prompts.BuildQueryMessages(promptContext, schema, history, question,
                attachmentContext.Any ? attachmentContext.BuildPromptSection() : null);
            trace.Add("queryPrompt", "Generated prompt", PromptBuilder.Render(messages));

            var validationContext = _engine.CreateContext(schema, settings);
            PreparedQuery? prepared = null;
            MqlQuery? query = null;
            AttachmentPlan? attachmentPlan = null;
            TableQueryResult? tableResult = null;
            AttachmentContext.Item? planTarget = null;
            var errors = new List<string>();
            var attempt = 0;

            while (true)
            {
                await sink.OnStatusAsync(attempt == 0 ? "generating_query" : "repairing",
                    attempt == 0 ? $"Generating query with {provider.Model}…" : "The query needed a fix — asking the model to correct it…", token);

                var sw = Stopwatch.StartNew();
                var ai = await provider.Provider.GenerateQueryAsync(new AIChatRequest
                {
                    Messages = messages,
                    Model = provider.Model,
                    Temperature = provider.Config.Temperature,
                    MaxTokens = provider.Config.MaxTokens,
                    JsonMode = true
                }, token);
                sw.Stop();

                assistant.AiQueryTimeMs += sw.ElapsedMilliseconds;
                AddUsage(assistant, ai);
                log.GeneratedMql = attempt == 0 ? ai.Text : log.GeneratedMql + $"\n\n--- repair attempt {attempt} ---\n" + ai.Text;
                trace.Add(attempt == 0 ? "generatedMql" : $"repair{attempt}",
                    attempt == 0 ? "Generated MQL (raw model output)" : $"Repair attempt {attempt} (raw model output)",
                    ai.Text, "info", sw.ElapsedMilliseconds);

                // Attachment plans (answer from files / calculate over a table / combine with the database)
                var plan = attachmentContext.Any ? AttachmentPlan.TryParse(ai.Text) : null;
                if (plan is not null)
                {
                    query = null;
                    prepared = null;
                    errors = PrepareAttachmentPlan(plan, attachmentContext, validationContext, settings, out planTarget, out tableResult, out prepared);
                    trace.Add("validation", attempt == 0 ? "Plan validation" : $"Plan validation (repair {attempt})",
                        errors.Count == 0 ? $"✓ {plan.Type} plan" : "✗ Failed\n- " + string.Join("\n- ", errors), errors.Count == 0 ? "ok" : "error");
                    if (errors.Count == 0) { attachmentPlan = plan; break; }
                    if (prepared?.Validation.Blocked == true) log.Blocked = true;
                    if (attempt >= settings.Ai.MaxRepairAttempts) break;
                    attempt++;
                    messages.Add(AIChatMessage.Assistant(ai.Text));
                    messages.Add(AIChatMessage.User(_prompts.BuildRepairMessage(errors)));
                    continue;
                }

                var parsed = MqlParser.Parse(ai.Text);
                if (!parsed.Success)
                {
                    errors = new List<string> { parsed.Error ?? "Could not parse the model output." };
                    query = null;
                    prepared = null;
                    trace.Add("validation", "Validation result", "✗ " + errors[0], "error");
                }
                else
                {
                    query = parsed.Query!;
                    if (query.IsUnsupported) break;

                    await sink.OnStatusAsync("validating", "Validating the query…", token);
                    prepared = _engine.Prepare(query, validationContext, settings);
                    errors = prepared.Validation.Errors.ToList();
                    if (prepared.Validation.Blocked) log.Blocked = true;
                    trace.Add("validation", attempt == 0 ? "Validation result" : $"Validation (repair {attempt})",
                        prepared.Validation.IsValid
                            ? "✓ Passed" + (prepared.Validation.Warnings.Count > 0 ? "\nWarnings:\n- " + string.Join("\n- ", prepared.Validation.Warnings) : string.Empty)
                            : (prepared.Validation.Blocked ? "⛔ Blocked\n- " : "✗ Failed\n- ") + string.Join("\n- ", errors),
                        prepared.Validation.IsValid ? "ok" : "error");
                    if (prepared.IsExecutable) break;
                }

                if (attempt >= settings.Ai.MaxRepairAttempts) break;
                attempt++;
                messages.Add(AIChatMessage.Assistant(ai.Text));
                messages.Add(AIChatMessage.User(_prompts.BuildRepairMessage(errors)));
            }

            assistant.RepairAttempts = attempt;
            log.RepairAttempts = attempt;

            if (attachmentPlan is not null)
            {
                assistant.Explanation = attachmentPlan.Explanation;
                switch (attachmentPlan.Type)
                {
                    case "attachment":
                        return await AnswerFromFilesAsync(attachmentPlan);

                    case "table":
                    {
                        var target = planTarget!.Attachment;
                        assistant.QueryGenerated = assistant.QueryValidated = assistant.QueryExecuted = true;
                        log.QueryGenerated = log.ValidationPassed = log.Executed = log.ExecutionSucceeded = true;
                        assistant.Operation = log.Operation = "table";
                        assistant.Collection = log.Collection = target.FileName;
                        assistant.Mql = log.FinalMql = $"table({target.FileName}) " + attachmentPlan.Table!.ToIndented();
                        assistant.QueryJson = JsonSerializer.Serialize(new { type = "table", attachment = planTarget.Alias, table = attachmentPlan.Table }, MessageMapper.Web);
                        trace.Add("tableQuery", "Table calculation (server-side)", assistant.Mql, "ok");
                        await sink.OnQueryAsync(MessageMapper.ToQueryInfo(assistant), token);
                        var (rows, columns) = ResultShaper.Shape(tableResult!.Rows);
                        return await AnswerFromRowsAsync(rows, columns, false, attachmentPlan.Explanation + $" (from {target.FileName}, {tableResult.MatchedRows} matching rows)", attachmentPlan.Visualization, 0);
                    }

                    case "combined":
                        // prepared = the database query with the attachment values injected (already validated)
                        query = prepared!.Query;
                        assistant.Explanation = attachmentPlan.Explanation;
                        trace.Add("combined", "Attachment values → database",
                            $"{planTarget!.Attachment.FileName} · column {attachmentPlan.Column} · {tableResult!.Rows.Count} distinct value(s) injected into the query", "ok");
                        break;
                }
            }

            if (query is { IsUnsupported: true })
            {
                trace.Add("unsupported", "Model declined", query.Reason ?? "No reason given.", "warning");
                return await FinishAsync(ChatStatus.Unsupported, UserMessages.Unsupported, null);
            }

            assistant.QueryGenerated = log.QueryGenerated = query is not null;
            if (query is not null)
            {
                assistant.Operation = log.Operation = query.Operation;
                assistant.Collection = log.Collection = prepared?.Validation.Collection ?? query.Collection;
                assistant.Explanation = query.Explanation;
            }

            if (prepared is null || !prepared.IsExecutable)
            {
                assistant.ValidationErrors = log.ValidationErrors = errors;
                if (log.Blocked)
                    await _audit.LogAsync("QueryBlocked", $"Question: {question}\nReasons: {string.Join("; ", errors)}", CancellationToken.None);
                await sink.OnQueryAsync(MessageMapper.ToQueryInfo(assistant), token);
                // Friendly message for the user; the technical reasons stay in the query log / developer trace.
                return await FinishAsync(ChatStatus.InvalidQuery, UserMessages.BlockedQuery(question), string.Join(" ", errors));
            }

            assistant.QueryValidated = log.ValidationPassed = true;
            assistant.QueryJson = prepared.Query.ToJson().ToCompact();
            assistant.Mql = log.FinalMql = prepared.Mql;
            assistant.ValidationErrors = prepared.Validation.Warnings;
            trace.Add("mongoQuery", "MongoDB query (validated + tenant-scoped)", prepared.Mql ?? string.Empty, "ok");
            await sink.OnQueryAsync(MessageMapper.ToQueryInfo(assistant), token);

            // 7. Execute
            await sink.OnStatusAsync("executing", "Running the query…", token);
            log.Executed = true;
            var executed = await _engine.ExecuteAsync(prepared, settings, token);
            assistant.QueryExecuted = log.ExecutionSucceeded = true;
            return await AnswerFromRowsAsync(executed.Rows, executed.Columns, executed.Truncated, query?.Explanation, query?.Visualization, executed.ElapsedMs);
        }
        catch (AttachmentException ex)
        {
            trace.Add("error", "Attachment error", ex.Message, "error");
            return await FinishAsync(ChatStatus.Error, ex.Message, ex.Message);
        }
        catch (AIProviderException ex)
        {
            _logger.LogWarning(ex, "AI provider failure for {Provider}", provider?.Config.Name);
            trace.Add("error", "AI provider error", ex.Message, "error");
            return await FinishAsync(ChatStatus.ProviderError, UserMessages.ProviderUnavailable, ex.Message);
        }
        catch (QueryTimeoutException ex)
        {
            trace.Add("error", "MongoDB timeout", ex.Message, "error");
            return await FinishAsync(ChatStatus.Timeout, UserMessages.QueryTimeout, ex.Message);
        }
        catch (QueryExecutionException ex)
        {
            _logger.LogWarning(ex, "Query execution failed");
            trace.Add("error", "MongoDB error", ex.Message, "error");
            return await FinishAsync(ChatStatus.DatabaseError, UserMessages.DatabaseError, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            trace.Add("error", "Timeout", $"The request exceeded {settings.Ai.TimeoutSeconds} seconds.", "error");
            return await FinishAsync(ChatStatus.Timeout, UserMessages.RequestTimeout, "Request timeout");
        }
        catch (OperationCanceledException)
        {
            trace.Add("error", "Cancelled", "The client cancelled the request.", "warning");
            return await FinishAsync(ChatStatus.Error, UserMessages.Cancelled, "Cancelled by client");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in AI agent pipeline");
            trace.Add("error", "Unexpected error", ex.GetType().Name + ": " + ex.Message, "error");
            return await FinishAsync(ChatStatus.Error, UserMessages.Unexpected, ex.GetType().Name);
        }

        // ------------------------------------------------------------------ local: rows → answer (database, table and combined)

        async Task<ChatResponse> AnswerFromRowsAsync(List<JsonObject> rows, List<string> columns, bool truncated, string? explanation, string? vizHint, long elapsedMs)
        {
            assistant.ExecutionTimeMs = log.ExecutionTimeMs = elapsedMs;
            assistant.ResultCount = log.ResultCount = rows.Count;
            assistant.Truncated = truncated;
            assistant.Columns = columns;

            var dataArray = new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray());
            assistant.DataJson = dataArray.ToCompact();
            trace.Add("mongoResult", $"Result ({rows.Count} rows{(truncated ? ", truncated" : string.Empty)})",
                JsonHelpers.Truncate(dataArray.ToIndented(), 8000), "ok", elapsedMs);

            if (rows.Count == 0)
                return await FinishAsync(ChatStatus.NoResults, UserMessages.NoResultsFor(question), null);

            var visualization = VisualizationAdvisor.Decide(rows, columns, vizHint);
            assistant.VisualizationJson = JsonSerializer.Serialize(visualization, MessageMapper.Web);

            await sink.OnStatusAsync("answering", "Writing the answer…", token);
            var answerMessages = _prompts.BuildAnswerMessages(promptContext, question, explanation, rows, truncated, settings.Query.MaxRowsForAnswer);
            trace.Add("answerPrompt", "Final AI prompt", PromptBuilder.Render(answerMessages));
            return await GenerateAndFinishAsync(answerMessages, rows);
        }

        // ------------------------------------------------------------------ local: answer from files (documents / images / table preview)

        async Task<ChatResponse> AnswerFromFilesAsync(AttachmentPlan plan)
        {
            var targets = plan.Attachments.Select(a => attachmentContext.Resolve(a)).Where(i => i is not null).Select(i => i!).Distinct().ToList();
            if (targets.Count == 0) targets = attachmentContext.Items.Where(i => i.Current).ToList();
            if (targets.Count == 0) targets = attachmentContext.Items.ToList();

            var budget = Math.Clamp(settings.Attachments.MaxContextChars, 2000, 60_000);
            var perFile = budget / Math.Max(1, targets.Count);
            var content = new StringBuilder();
            foreach (var t in targets)
            {
                var a = t.Attachment;
                content.AppendLine($"=== {t.Alias}: {a.FileName} ({a.Kind}) ===");
                if (a.Table is { } table)
                {
                    content.AppendLine($"Table with {table.TotalRows} rows. First rows:");
                    content.AppendLine(TableTypes.Preview(table, 40, perFile));
                }
                else
                {
                    foreach (var c in ContentRetriever.Relevant(a.Chunks, question, perFile))
                        content.AppendLine(c.Page is int page ? $"[page {page}] {c.Text}" : c.Text);
                    if (a.Chunks.Count == 0) content.AppendLine("(no text could be extracted)");
                }
                content.AppendLine();
            }

            // Send the images themselves too when the chat model can see them.
            var images = new List<AIImage>();
            if (capabilities?.Vision == true && _attachments is not null)
                foreach (var t in targets.Where(t => t.Attachment.Kind == AttachmentKinds.Image).Take(3))
                    if (await _attachments.LoadImageAsync(t.Attachment, token) is { } img) images.Add(img);

            var contentText = content.ToString();
            trace.Add("relevantContent", "Relevant attachment content", SensitiveDataMasker.Mask(JsonHelpers.Truncate(contentText, 6000)));
            assistant.Operation = log.Operation = "attachment";
            assistant.Collection = log.Collection = string.Join(", ", targets.Select(t => t.Attachment.FileName));

            await sink.OnStatusAsync("answering", "Reading the attachment…", token);
            var answerMessages = _prompts.BuildAttachmentAnswerMessages(promptContext, question, contentText, images);
            trace.Add("answerPrompt", "Final AI prompt", SensitiveDataMasker.Mask(PromptBuilder.Render(answerMessages)));
            return await GenerateAndFinishAsync(answerMessages, null);
        }

        async Task<ChatResponse> GenerateAndFinishAsync(List<AIChatMessage> answerMessages, List<JsonObject>? groundingRows)
        {
            string answer;
            var answerSw = Stopwatch.StartNew();
            try
            {
                answer = await GenerateAnswerAsync(provider!, answerMessages, sink, assistant, token);
            }
            catch (AIProviderException ex)
            {
                answerSw.Stop();
                assistant.AiAnswerTimeMs = answerSw.ElapsedMilliseconds;
                trace.Add("finalResponse", "Final AI response", "Provider failed while writing the answer: " + ex.Message, "error", answerSw.ElapsedMilliseconds);
                return await FinishAsync(ChatStatus.ProviderError, UserMessages.AnswerFailed, ex.Message);
            }
            answerSw.Stop();
            assistant.AiAnswerTimeMs = answerSw.ElapsedMilliseconds;

            if (string.IsNullOrWhiteSpace(answer))
                answer = UserMessages.AnswerFailed;

            if (groundingRows is not null)
            {
                var grounding = GroundingChecker.Check(answer, groundingRows, question);
                assistant.GroundingChecked = true;
                assistant.GroundingWarnings = grounding.Warnings;
                log.GroundingWarningCount = grounding.Warnings.Count;
                trace.Add("finalResponse", "Final AI response", answer, grounding.Warnings.Count > 0 ? "warning" : "ok", assistant.AiAnswerTimeMs);
                if (grounding.Warnings.Count > 0)
                    trace.Add("grounding", "Grounding check", "Numbers not found in the data:\n- " + string.Join("\n- ", grounding.Warnings), "warning");
            }
            else
            {
                trace.Add("finalResponse", "Final AI response", answer, "ok", assistant.AiAnswerTimeMs);
            }
            return await FinishAsync(ChatStatus.Success, answer.Trim(), null);
        }

        // ------------------------------------------------------------------ local: persistence

        async Task<ChatResponse> FinishAsync(ChatStatus status, string answer, string? error)
        {
            total.Stop();
            var none = CancellationToken.None; // always persist, even if the client disconnected

            assistant.Status = status;
            assistant.Content = answer;
            assistant.ErrorMessage = error;
            assistant.ResponseTimeMs = total.ElapsedMilliseconds;
            assistant.CreatedAt = _time.GetUtcNow().UtcDateTime;
            assistant.EstimatedCost = provider?.EstimateCost(assistant.InputTokens, assistant.OutputTokens) ?? 0m;

            log.Status = status;
            log.ErrorMessage = error;
            log.InputTokens = assistant.InputTokens;
            log.OutputTokens = assistant.OutputTokens;
            log.EstimatedCost = assistant.EstimatedCost;
            log.AiQueryTimeMs = assistant.AiQueryTimeMs;
            log.AiAnswerTimeMs = assistant.AiAnswerTimeMs;
            log.TotalTimeMs = assistant.ResponseTimeMs;
            log.DebugTraceJson = JsonSerializer.Serialize(trace, MessageMapper.Web);

            try
            {
                await _logs.InsertAsync(log, none);
                assistant.QueryLogId = log.Id;

                if (persist)
                {
                    await _messages.InsertAsync(assistant, none);
                    log.MessageId = assistant.Id;
                    await _logs.UpdateAsync(log, none);

                    conversation.MessageCount = (int)await _messages.CountAsync(conversation.Id, none);
                    conversation.UpdatedAt = assistant.CreatedAt;
                    conversation.LastProviderName = assistant.ProviderName ?? conversation.LastProviderName;
                    conversation.LastModel = assistant.Model ?? conversation.LastModel;
                    await _conversations.UpdateAsync(conversation, none);
                }
                else
                {
                    assistant.Id = Guid.NewGuid().ToString("N");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist chat turn");
                if (string.IsNullOrEmpty(assistant.Id)) assistant.Id = Guid.NewGuid().ToString("N");
            }

            return MessageMapper.ToChatResponse(conversation, userMessage, assistant, settings.Chat.DeveloperMode ? trace : null);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Validates an attachment plan; for table/combined plans runs the table step (cheap, deterministic).</summary>
    private List<string> PrepareAttachmentPlan(AttachmentPlan plan, AttachmentContext context, MqlValidationContext validation, AppSettings settings,
        out AttachmentContext.Item? target, out TableQueryResult? table, out PreparedQuery? prepared)
    {
        var errors = new List<string>();
        target = null;
        table = null;
        prepared = null;

        if (plan.Type == "attachment")
        {
            if (plan.Attachments.Count > 0 && plan.Attachments.All(a => context.Resolve(a) is null))
                errors.Add($"Unknown attachment reference. Use one of: {string.Join(", ", context.Items.Select(i => i.Alias))}.");
            return errors;
        }

        target = plan.Attachments.Select(context.Resolve).FirstOrDefault(i => i is not null)
                 ?? context.Items.FirstOrDefault(i => i.Attachment.Table is not null);
        if (target?.Attachment.Table is null)
        {
            errors.Add("The referenced attachment is not a CSV/Excel table.");
            return errors;
        }

        if (plan.Type == "table")
        {
            if (plan.Table is null) { errors.Add("A table plan needs a \"table\" object."); return errors; }
            table = TableQueryEngine.Execute(target.Attachment.Table, plan.Table, settings.Query.MaxRecords);
            errors.AddRange(table.Errors);
            return errors;
        }

        // combined: distinct values of a column → injected into a normal (validated, tenant-scoped) database query
        if (string.IsNullOrWhiteSpace(plan.Column)) { errors.Add("A combined plan needs \"column\"."); return errors; }
        if (plan.QueryJson is null) { errors.Add("A combined plan needs a \"query\" object."); return errors; }
        table = TableQueryEngine.Execute(target.Attachment.Table, new JsonObject { ["distinct"] = plan.Column }, 1000);
        if (!table.IsValid) { errors.AddRange(table.Errors); return errors; }
        var values = table.Rows.Select(r => r.First().Value?.ToString() ?? "").Where(v => v.Length > 0).ToList();
        if (values.Count == 0) { errors.Add($"Column '{plan.Column}' has no values."); return errors; }

        var queryJson = (JsonObject)plan.QueryJson.DeepClone();
        if (AttachmentPlan.InjectValues(queryJson, values) == 0)
            errors.Add($"The query must use \"{AttachmentPlan.ValuesPlaceholder}\" where the attachment values go.");
        var parsed = MqlParser.Parse(queryJson.ToJsonString());
        if (!parsed.Success || parsed.Query is null) { errors.Add(parsed.Error ?? "Invalid query."); return errors; }
        prepared = _engine.Prepare(parsed.Query, validation, settings);
        errors.AddRange(prepared.Validation.Errors);
        return errors;
    }

    private async Task<string> GenerateAnswerAsync(ResolvedProvider provider, List<AIChatMessage> messages, IChatEventSink sink, ChatMessage assistant, CancellationToken ct)
    {
        var request = new AIChatRequest
        {
            Messages = messages,
            Model = provider.Model,
            Temperature = Math.Min(provider.Config.Temperature, 0.4),
            MaxTokens = provider.Config.MaxTokens,
            JsonMode = false
        };

        if (!sink.StreamTokens)
        {
            var response = await provider.Provider.GenerateResponseAsync(request, ct);
            AddUsage(assistant, response);
            return response.Text;
        }

        var sb = new StringBuilder();
        await foreach (var chunk in provider.Provider.StreamResponseAsync(request, ct))
        {
            if (!string.IsNullOrEmpty(chunk.Text))
            {
                sb.Append(chunk.Text);
                await sink.OnTokenAsync(chunk.Text, ct);
            }
            if (chunk.InputTokens is int input) assistant.InputTokens += input;
            if (chunk.OutputTokens is int output) assistant.OutputTokens += output;
        }
        return sb.ToString();
    }

    private static void AddUsage(ChatMessage assistant, AIResponse response)
    {
        assistant.InputTokens += response.InputTokens;
        assistant.OutputTokens += response.OutputTokens;
    }

    private static void ApplyProvider(ChatMessage assistant, QueryLog log, ResolvedProvider p)
    {
        assistant.ProviderId = log.ProviderId = p.Config.Id;
        assistant.ProviderName = log.ProviderName = p.Config.Name;
        assistant.ProviderKind = log.ProviderKind = p.Config.Kind;
        assistant.Model = log.Model = p.Model;
        log.IsLocalProvider = p.IsLocal;
    }

    private async Task<Conversation> LoadOrCreateConversationAsync(ChatRequest request, string question, bool persist, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.ConversationId) && persist)
        {
            var existing = await _conversations.GetAsync(request.ConversationId, _user.UserId, _user.CompanyId, ct);
            if (existing is not null)
            {
                if (existing.MessageCount == 0 && existing.Title == "New conversation")
                    existing.Title = ConversationService.TitleFrom(question);
                return existing;
            }
            throw new NotFoundException("Conversation not found.");
        }

        var conversation = new Conversation
        {
            UserId = _user.UserId,
            CompanyId = _user.CompanyId,
            Title = ConversationService.TitleFrom(question)
        };
        if (persist) await _conversations.InsertAsync(conversation, ct);
        else conversation.Id = request.ConversationId ?? Guid.NewGuid().ToString("N");
        return conversation;
    }

    /// <summary>Regenerate: drop the assistant message being replaced and the user question before it.</summary>
    private async Task RemoveForRegenerateAsync(Conversation conversation, string assistantMessageId, CancellationToken ct)
    {
        var messages = await _messages.ListAsync(conversation.Id, ct);
        var index = messages.FindIndex(m => m.Id == assistantMessageId && m.Role == MessageRole.Assistant);
        if (index < 0) return;
        var ids = new List<string> { messages[index].Id };
        if (index > 0 && messages[index - 1].Role == MessageRole.User) ids.Add(messages[index - 1].Id);
        await _messages.DeleteAsync(ids, ct);
        conversation.MessageCount = Math.Max(0, conversation.MessageCount - ids.Count);
    }
}
