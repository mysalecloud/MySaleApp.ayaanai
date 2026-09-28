using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Attachments;
using MySale.AI.Application.Common;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Application.Stores;
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

    /// <summary>Trial Balance / P&amp;L / Balance Sheet: not recalculated by AYAAN, so figures never differ from MySaleBooks.</summary>
    public const string FinancialStatement =
        "I don't recalculate the Trial Balance, Profit & Loss or Balance Sheet — they depend on the MySaleBooks stock valuation and closing rules, " +
        "so please open that report in MySaleBooks (Reports → Accounts) for the exact figures. I can show ledger and group balances, " +
        "income and expense totals, cash and bank balances, receivables and payables for any period.";

    /// <summary>Specific explanation for declined questions whose data or rule is missing (null = use the generic reply).</summary>
    public static string? ExplainUnsupported(string? reason)
    {
        var r = (reason ?? string.Empty).ToLowerInvariant();
        if (r.Contains("valuation") || r.Contains("cost of goods") || r.Contains("cogs") || r.Contains("profit"))
            return "Stock value on past dates, cost of goods sold and profit follow the company's costing method (FIFO / average / last purchase) " +
                   "in the MySaleBooks Stock Register and Profit & Loss reports. I don't recalculate them, so the figures never differ from MySaleBooks — " +
                   "please open those reports. I can show current stock value, quantities on any date, and sales or purchase quantities and amounts.";
        if (r.Contains("financial statement") || r.Contains("trial balance") || r.Contains("balance sheet"))
            return FinancialStatement;
        return null;
    }
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
    private readonly ActivityTracker? _activity;
    private readonly StoreContextResolver? _stores;
    private readonly BusinessCalendarOptions _calendar;
    private readonly BusinessTermOptions _terms;
    private readonly CompanyContextService? _companies;
    private readonly MySaleBooksReports? _reports;

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
        ModelCapabilityService? capabilities = null,
        ActivityTracker? activity = null,
        StoreContextResolver? stores = null,
        BusinessCalendarOptions? calendar = null,
        BusinessTermOptions? terms = null,
        CompanyContextService? companies = null,
        MySaleBooksReports? reports = null)
    {
        _companies = companies;
        _reports = reports;
        _calendar = calendar ?? new BusinessCalendarOptions();
        _terms = terms ?? new BusinessTermOptions();
        _activity = activity;
        _stores = stores;
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
        // Activity tracking: every request leaves an audit record. Tracking never changes or breaks the answer.
        var activity = _activity?.Begin(request, sink.StreamTokens) ?? ActivityRecorder.Disabled;
        var started = Stopwatch.StartNew();
        try
        {
            return await RunCoreAsync(request, sink, activity, ct);
        }
        catch (Exception ex)
        {
            activity.Fail(ex, started.ElapsedMilliseconds); // no-op when the turn already completed normally
            throw;
        }
    }

    private async Task<ChatResponse> RunCoreAsync(ChatRequest request, IChatEventSink sink, ActivityRecorder activity, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        // Technical details (MQL, pipeline, collections, trace) only for AI-dashboard developers/admins — never customers.
        // The complete interaction, including the exact MQL, is always stored in the activity log.
        var showTechnical = TechnicalDetailsPolicy.CanSeeTechnicalDetails(_user, _activity?.Options.AllowMySaleBooksAdmins ?? false);
        QueryInfoDto QueryInfoFor(ChatMessage m)
            => showTechnical ? MessageMapper.ToQueryInfo(m) : TechnicalDetailsPolicy.ForCustomer(MessageMapper.ToQueryInfo(m));
        var stage = ActivityStages.Request;
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
        activity.SetConversation(conversation, persist);
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
        activity.SetQuestion(question, inputType);

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
        activity.UserMessage(userMessage.Id);

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
        StoreScope? storeScope = null;
        QuestionDates questionDates = new();
        PreparedQuery? lastPrepared = null;
        SemanticInterpretation semantics = new();
        // Configured currency only for databases without MySaleBooks company settings; replaced by the verified company currency.
        string? currencyCode = string.IsNullOrWhiteSpace(_user.Currency) ? null : _user.Currency;
        var currencyDecimals = 2;
        var attachmentContext = new AttachmentContext(attachmentsCurrent, attachmentsEarlier);
        ModelCapabilities? capabilities = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, settings.Ai.TimeoutSeconds)));
        var token = timeout.Token;

        // "Show me the query you used" / system prompt / database names → fixed refusal (no AI call, nothing internal returned).
        if (attachmentsCurrent.Count == 0 && TechnicalDetailsPolicy.IsTechnicalDetailsRequest(question))
        {
            activity.RefusedTechnicalDetails();
            trace.Add("policy", "Technical details request", "Refused: the question asks for internal queries or system details.", "warning");
            return await FinishAsync(ChatStatus.Unsupported, TechnicalDetailsPolicy.RefusalMessage, "Technical details request refused");
        }

        try
        {
            await sink.OnStatusAsync("thinking", "Understanding your question…", token);

            // 2. Provider
            stage = ActivityStages.AIProvider;
            provider = await _providers.ResolveAsync(request.ProviderId, request.Model, settings, token);
            ApplyProvider(assistant, log, provider);
            activity.SetProvider(provider);
            trace.Add("provider", "AI provider",
                $"{provider.Config.Name} ({provider.KindInfo?.DisplayName ?? provider.Config.Kind}, {provider.Config.Category})\nModel: {provider.Model}\nEndpoint: {provider.Config.BaseUrl}");

            // 3. Schema
            stage = ActivityStages.Prompt;
            var schema = await _engine.GetAllowedSchemaAsync(settings, token);
            activity.SetSchema(schema);
            if (schema.Count == 0)
                throw new QueryExecutionException("No collections are available for querying. Check the Database and Settings pages.");

            // MySaleBooks stores the local business date/time as UTC ("wall clock"): days are [date 00:00Z, next 00:00Z).
            var wallClock = _calendar.IsWallClock(_user.IsMySaleBooksUser);
            var anchors = DateAnchors.Compute(now, _user.TimeZone, _calendar.FinancialYearStartMonth, wallClock);

            // 3. Dates typed in the question (01/09/2026, Sep 1 to Sep 15 …) are resolved here, in the business locale.
            questionDates = QuestionDates.Parse(question, anchors.LocalToday, _calendar.DateOrder);
            if (!questionDates.IsEmpty)
                trace.Add("dates", "Dates in the question",
                    string.Join("\n", questionDates.Spans.Select(s => $"\"{s.Text}\" → {QuestionDates.Describe(s.From)} … {QuestionDates.Describe(s.To)} (inclusive)"))
                    + (questionDates.Invalid.Count > 0 ? "\nInvalid: " + string.Join(", ", questionDates.Invalid) : string.Empty)
                    + (questionDates.Ambiguous.Count > 0 ? "\nAmbiguous: " + string.Join(", ", questionDates.Ambiguous.Select(a => a.Text)) : string.Empty),
                    questionDates.Invalid.Count + questionDates.Ambiguous.Count > 0 ? "warning" : "ok");
            if (questionDates.Invalid.Count > 0)
                return await FinishAsync(ChatStatus.Unsupported,
                    $"\"{questionDates.Invalid[0]}\" isn't a valid date. Please check it and try again.", "Invalid date in the question");
            if (questionDates.Ambiguous.FirstOrDefault() is { } ambiguous)
                return await FinishAsync(ChatStatus.Unsupported,
                    $"Did you mean {QuestionDates.Describe(ambiguous.DayFirst)} or {QuestionDates.Describe(ambiguous.MonthFirst)} for \"{ambiguous.Text}\"? Please write the date like 1 September 2026.",
                    "Ambiguous date in the question");

            // 3a. Store context: selected MySaleBooks store (verified), company-level accounting statements, all stores.
            storeScope = _stores is null ? null : await _stores.ResolveAsync(question, settings, token);
            if (storeScope is not null)
            {
                trace.Add("store", "Store context", $"{storeScope.Mode} ({storeScope.Reason}){(storeScope.StoreName is { Length: > 0 } sn ? " · " + sn : string.Empty)}",
                    storeScope.Mode == StoreMode.Missing ? "warning" : "ok");
                activity.StoreResolved(storeScope.Mode.ToString(), storeScope.Reason, storeScope.StoreId, storeScope.StoreName);
            }
            // 3c. Business terms: user language → business entity → accounting concept → collections / group fields /
            //     stored group values of THIS database ("customer" → Sundry Debtors → Ledgers.groupName "SUNDRY DEBTORS").
            semantics = BusinessTerms.Interpret(question, schema, _terms);
            if (semantics.Clarification is { } clarification && !attachmentContext.Any)
            {
                trace.Add("businessTerms", "Business terms", semantics.Describe(), "warning");
                activity.Clarification(clarification);
                return await FinishAsync(ChatStatus.Unsupported, clarification, "Clarification needed: ambiguous business entity");
            }
            if (semantics.Mappings.Count > 0)
            {
                var termsSw = Stopwatch.StartNew();
                var groupLookups = _terms.VerifyGroupValues ? await ReadStoredGroupValuesAsync(semantics, schema, settings, token) : 0;
                termsSw.Stop();
                trace.Add("businessTerms", "Business terms", semantics.Describe(), "ok", termsSw.ElapsedMilliseconds);
                // Only database lookups count as MongoDB time in the activity performance breakdown.
                activity.TermsResolved(semantics.Describe(), groupLookups > 0 ? termsSw.ElapsedMilliseconds : null);
            }

            // 3d. Company context (MySaleBooks): base currency (code, decimals) and financial year from the company's own
            //     settings — never assumed. Missing data is stated, not replaced by a default.
            var domainActive = MySaleBooksDomain.IsActive(schema);
            CompanyContext? company = _companies is null ? null : await _companies.ResolveAsync(schema, storeScope, settings, token);
            if (company is not null)
            {
                currencyCode = company.CurrencyResolved ? company.CurrencyCode : null;
                currencyDecimals = company.Decimals ?? 2;
                if (company.FinancialYearFrom is { } fyFrom && fyFrom.Month != anchors.FinancialYearStartMonth)
                    anchors = DateAnchors.Compute(now, _user.TimeZone, fyFrom.Month, wallClock);
                var companySummary = company.Describe() + (wallClock ? " · dates stored as local wall-clock time" : string.Empty);
                trace.Add("company", "Company context", companySummary, company.CurrencyResolved ? "ok" : "warning");
                activity.CompanyContextResolved(companySummary);
            }

            // Trial Balance / Profit & Loss / Balance Sheet are not recalculated: the figures come from the MySaleBooks reports.
            if (domainActive && !attachmentContext.Any && StoreQuestionPolicy.IsAccountingStatement(question))
            {
                trace.Add("financialStatement", "Financial statement", "Not recalculated by AYAAN (MySaleBooks report required).", "warning");
                return await FinishAsync(ChatStatus.Unsupported, UserMessages.FinancialStatement, "Financial statement requested");
            }

            promptContext = new PromptContext(_user.CompanyName, currencyCode ?? string.Empty, _user.TimeZone, anchors, settings.Query.MaxRecords, _engine.TenantField)
            {
                CurrencyDecimals = currencyDecimals,
                CurrencyMissing = company is { CurrencyResolved: false } ? company.Missing ?? "the company currency is not configured" : null,
                DomainRules = domainActive ? MySaleBooksDomain.PromptRules(schema) : null,
                Semantics = semantics,
                StoreMode = storeScope?.Mode.ToString(),
                StoreName = storeScope?.StoreName,
                AccountingStatement = storeScope?.AccountingStatement == true,
                QuestionDates = questionDates,
                BusinessDateFields = _calendar.BusinessDateFields
            };

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
            activity.SetPrompt(messages);

            var validationContext = _engine.CreateContext(schema, settings, storeScope,
                new DateCoercionContext
                {
                    TimeZone = DateAnchors.ResolveTimeZone(anchors.BoundaryTimeZoneId),
                    InclusiveEndDays = questionDates.InclusiveEndDays,
                    MentionedDays = questionDates.MentionedDays
                },
                question, _calendar.BusinessDateFields);
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

                stage = ActivityStages.QueryGeneration;
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
                activity.QueryGenerated(attempt, ai.Text, sw.ElapsedMilliseconds, ai.Model);
                stage = ActivityStages.QueryValidation;

                // Attachment plans (answer from files / calculate over a table / combine with the database)
                var plan = attachmentContext.Any ? AttachmentPlan.TryParse(ai.Text) : null;
                if (plan is not null)
                {
                    query = null;
                    prepared = null;
                    var planSw = Stopwatch.StartNew();
                    errors = PrepareAttachmentPlan(plan, attachmentContext, validationContext, settings, out planTarget, out tableResult, out prepared);
                    activity.Validated(plan.Type == "combined" ? prepared?.Query : null, prepared?.Validation, errors, planSw.ElapsedMilliseconds, parsed: true,
                        kind: plan.Type);
                    if (errors.Count == 0) activity.PlanChosen(plan);
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
                    activity.Validated(null, null, errors, 0, parsed: false);
                    trace.Add("validation", "Validation result", "✗ " + errors[0], "error");
                }
                else
                {
                    query = parsed.Query!;
                    if (query.IsNonQuery) break;

                    await sink.OnStatusAsync("validating", "Validating the query…", token);
                    var validationSw = Stopwatch.StartNew();
                    prepared = _engine.Prepare(query, validationContext, settings);
                    errors = prepared.Validation.Errors.ToList();
                    activity.Validated(query, prepared.Validation, errors, validationSw.ElapsedMilliseconds, parsed: true);
                    if (prepared.Validation.Blocked) log.Blocked = true;
                    trace.Add("validation", attempt == 0 ? "Validation result" : $"Validation (repair {attempt})",
                        prepared.Validation.IsValid
                            ? "✓ Passed" + (prepared.Validation.Warnings.Count > 0 ? "\nWarnings:\n- " + string.Join("\n- ", prepared.Validation.Warnings) : string.Empty)
                            : (prepared.Validation.Blocked ? "⛔ Blocked\n- " : "✗ Failed\n- ") + string.Join("\n- ", errors),
                        prepared.Validation.IsValid ? "ok" : "error");
                    if (prepared.IsExecutable || prepared.StoreContextMissing) break; // no store → no repair, ask for a store
                }

                if (attempt >= settings.Ai.MaxRepairAttempts) break;
                attempt++;
                messages.Add(AIChatMessage.Assistant(ai.Text));
                messages.Add(AIChatMessage.User(_prompts.BuildRepairMessage(errors)));
            }

            assistant.RepairAttempts = attempt;
            log.RepairAttempts = attempt;

            if (prepared?.StoreContextMissing == true)
            {
                trace.Add("store", "Store required", $"The query needs the selected store ({storeScope?.Reason}); nothing was run.", "warning");
                return await FinishAsync(ChatStatus.Unsupported, StoreScope.MissingMessage, "Store context missing: " + storeScope?.Reason);
            }

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
                        await sink.OnQueryAsync(QueryInfoFor(assistant), token);
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

            if (query is { IsClarification: true })
            {
                var askBack = SafeClarification(query.Reason, schema);
                activity.Clarification(askBack);
                trace.Add("clarify", "Clarification requested", query.Reason ?? string.Empty, "warning");
                return await FinishAsync(ChatStatus.Unsupported, askBack, "Clarification needed");
            }

            // Server reports (ledger statement / stock movement): deterministic, same validation and scoping as queries.
            if (query is { IsReport: true })
            {
                if (_reports is null || !domainActive)
                    return await FinishAsync(ChatStatus.Unsupported, UserMessages.Unsupported, "Report plan without MySaleBooks data");
                stage = ActivityStages.MongoExecution;
                await sink.OnStatusAsync("executing", "Preparing the report…", token);
                var outcome = await _reports.RunAsync(query, validationContext, anchors, settings, currencyDecimals, token,
                    _activity is null ? null : ActivityRecorder.MongoComment(_activity.CorrelationId));
                assistant.QueryJson = query.ToJson().ToCompact();
                assistant.Operation = log.Operation = "report";
                assistant.Mql = log.FinalMql = string.Join("\n\n", outcome.Queries);
                trace.Add("report", "MySaleBooks report", $"{query.Arguments?["report"]} → {outcome.Kind}\n{assistant.Mql}", outcome.Kind == "ok" ? "ok" : "warning", outcome.ElapsedMs);
                if (outcome.StoreContextMissing)
                    return await FinishAsync(ChatStatus.Unsupported, StoreScope.MissingMessage, "Store context missing (report)");
                switch (outcome.Kind)
                {
                    case "clarify":
                        activity.Clarification(outcome.Message ?? string.Empty);
                        return await FinishAsync(ChatStatus.Unsupported, outcome.Message ?? UserMessages.Unsupported, "Report needs a clarification");
                    case "notfound":
                        return await FinishAsync(ChatStatus.NoResults, outcome.Message ?? "No matching records were found.", null);
                    case "ok":
                        break;
                    default:
                        _logger.LogWarning("MySaleBooks report failed: {Error}", outcome.Message);
                        return await FinishAsync(ChatStatus.InvalidQuery, "I couldn't prepare that report. Please try asking in a different way.", outcome.Message);
                }
                assistant.QueryGenerated = assistant.QueryValidated = assistant.QueryExecuted = true;
                log.QueryGenerated = log.ValidationPassed = log.Executed = log.ExecutionSucceeded = true;
                assistant.Collection = log.Collection = query.Arguments?["report"]?.ToString();
                assistant.Explanation = outcome.Explanation;
                activity.ReportExecuted(query, outcome.Queries, outcome.Rows.Count, outcome.Truncated, outcome.ElapsedMs);
                await sink.OnQueryAsync(QueryInfoFor(assistant), token);
                lastPrepared = null;
                return await AnswerFromRowsAsync(outcome.Rows, outcome.Columns, outcome.Truncated, outcome.Explanation, "table", outcome.ElapsedMs);
            }

            if (query is { IsUnsupported: true } && domainActive && UserMessages.ExplainUnsupported(query.Reason) is { } explained)
            {
                activity.Unsupported(query);
                trace.Add("unsupported", "Model declined", query.Reason ?? "No reason given.", "warning");
                return await FinishAsync(ChatStatus.Unsupported, explained, null);
            }

            if (query is { IsUnsupported: true })
            {
                activity.Unsupported(query);
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
                activity.Rejected(query, errors, log.Blocked);
                if (log.Blocked)
                    await _audit.LogAsync("QueryBlocked", $"Question: {question}\nReasons: {string.Join("; ", errors)}", CancellationToken.None);
                await sink.OnQueryAsync(QueryInfoFor(assistant), token);
                // Friendly message for the user; the technical reasons stay in the query log / developer trace.
                return await FinishAsync(ChatStatus.InvalidQuery, UserMessages.BlockedQuery(question), string.Join(" ", errors));
            }

            assistant.QueryValidated = log.ValidationPassed = true;
            assistant.QueryJson = prepared.Query.ToJson().ToCompact();
            assistant.Mql = log.FinalMql = prepared.Mql;
            assistant.ValidationErrors = prepared.Validation.Warnings;
            trace.Add("mongoQuery", "MongoDB query (validated + tenant-scoped)", prepared.Mql ?? string.Empty, "ok");
            activity.Approved(prepared, new
            {
                tenantScope = _user.TenantIsDatabase ? "customer-database" : "company-filter",
                tenantField = _user.TenantIsDatabase ? null : _engine.TenantField,
                maxRecords = settings.Query.MaxRecords,
                maxPipelineStages = settings.Query.MaxPipelineStages,
                timeoutMs = settings.Query.QueryTimeoutMs,
                repairAttempts = attempt,
                maxRepairAttempts = settings.Ai.MaxRepairAttempts,
                timeZone = _user.TimeZone,
                currency = currencyCode,
                currencyDecimals,
                dateStorage = anchors.WallClockStorage ? "WallClockUtc" : "Instant",
                dateAnchors = anchors,
                storeMode = storeScope?.Mode.ToString() ?? "None",
                storeReason = storeScope?.Reason,
                storeId = storeScope?.StoreId,
                storeName = storeScope?.StoreName,
                storeFilterField = prepared.StoreFilterField
            });
            await sink.OnQueryAsync(QueryInfoFor(assistant), token);

            lastPrepared = prepared;

            // 7. Execute
            await sink.OnStatusAsync("executing", "Running the query…", token);
            log.Executed = true;
            stage = ActivityStages.MongoExecution;
            activity.ExecutionStarted(prepared.Validation.Collection, query?.Operation, settings.Query.QueryTimeoutMs, settings.Query.MaxRecords + 1);
            ExecutedQuery executed;
            try
            {
                executed = await _engine.ExecuteAsync(prepared, settings, token,
                    _activity is null ? null : ActivityRecorder.MongoComment(_activity.CorrelationId));
            }
            catch (QueryTimeoutException ex) { activity.ExecutionFailed(ex, timeout: true); throw; }
            catch (QueryExecutionException ex) { activity.ExecutionFailed(ex, timeout: false); throw; }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { activity.ExecutionFailed(ex, timeout: true); throw; }
            activity.ExecutionSucceeded(executed);
            assistant.QueryExecuted = log.ExecutionSucceeded = true;

            // 7a. No rows? Exact text matches ("SUNDRY DEBTORS" vs stored "Sundry Debtors ") are the usual cause:
            //     retry once with case-/space-insensitive matching of the same values (validated + tenant-scoped again).
            if (executed.Rows.Count == 0
                && _engine.RelaxTextMatches(prepared, validationContext, settings, out var relaxedFields) is { } relaxedQuery)
            {
                try
                {
                    var retry = await _engine.ExecuteAsync(relaxedQuery, settings, token,
                        _activity is null ? null : ActivityRecorder.MongoComment(_activity.CorrelationId));
                    var used = retry.Rows.Count > 0;
                    trace.Add("textMatchRetry", "Retry with case-insensitive text match",
                        $"{string.Join(", ", relaxedFields)} → {retry.Rows.Count} row(s)\n{relaxedQuery.Mql}", used ? "ok" : "warning", retry.ElapsedMs);
                    activity.TextMatchRetried(relaxedQuery, relaxedFields, retry, used);
                    if (used)
                    {
                        prepared = relaxedQuery;
                        executed = retry;
                        assistant.QueryJson = prepared.Query.ToJson().ToCompact();
                        assistant.Mql = log.FinalMql = prepared.Mql;
                    }
                }
                catch (Exception ex) when (ex is QueryExecutionException or QueryTimeoutException)
                {
                    _logger.LogInformation("Text-match retry failed: {Error}", ex.Message);
                    trace.Add("textMatchRetry", "Retry with case-insensitive text match", ex.Message, "error");
                }
            }

            // 7c. Still no rows and the question used business terms ("customer list")? Do not conclude "none" yet: ask the
            //     model once to re-check entity, collection, accounting group (stored spelling), store and dates. The new
            //     query is validated, store- and tenant-scoped like any other; it is used only when it returns rows.
            if (executed.Rows.Count == 0 && _terms.RetryOnZeroResults && query is not null && attachmentPlan is null
                && semantics.Mappings.Any(m => m.Concept.AccountGroups.Count > 0 && m.HasTarget))
            {
                try
                {
                    await sink.OnStatusAsync("rechecking", "No records yet — re-checking the business mapping…", token);
                    var recheckMessages = new List<AIChatMessage>(messages)
                    {
                        AIChatMessage.Assistant(prepared.Query.ToJson().ToCompact()),
                        AIChatMessage.User(_prompts.BuildZeroResultRecheckMessage(semantics, StoreNote(storeScope), DatesNote(questionDates)))
                    };
                    var recheckSw = Stopwatch.StartNew();
                    var ai = await provider.Provider.GenerateQueryAsync(new AIChatRequest
                    {
                        Messages = recheckMessages, Model = provider.Model, Temperature = provider.Config.Temperature,
                        MaxTokens = provider.Config.MaxTokens, JsonMode = true
                    }, token);
                    recheckSw.Stop();
                    assistant.AiQueryTimeMs += recheckSw.ElapsedMilliseconds;
                    AddUsage(assistant, ai);
                    log.GeneratedMql += "\n\n--- zero-result re-check ---\n" + ai.Text;
                    activity.QueryGenerated(attempt + 1, ai.Text, recheckSw.ElapsedMilliseconds, ai.Model);
                    var reParsed = MqlParser.Parse(ai.Text);
                    var reQuery = reParsed.Success && reParsed.Query is { IsNonQuery: false } rq ? rq : null;
                    var rePrepared = reQuery is null ? null : _engine.Prepare(reQuery, validationContext, settings);
                    if (rePrepared is { IsExecutable: true } && !string.Equals(rePrepared.Mql, prepared.Mql, StringComparison.Ordinal))
                    {
                        var retry = await _engine.ExecuteAsync(rePrepared, settings, token,
                            _activity is null ? null : ActivityRecorder.MongoComment(_activity.CorrelationId));
                        var used = retry.Rows.Count > 0;
                        trace.Add("semanticRetry", "Zero-result re-check (business mapping)",
                            $"{rePrepared.Validation.Collection} → {retry.Rows.Count} row(s)\n{rePrepared.Mql}", used ? "ok" : "warning", retry.ElapsedMs);
                        activity.SemanticRetried(rePrepared, retry, used);
                        if (used)
                        {
                            prepared = rePrepared;
                            query = reQuery!;
                            executed = retry;
                            lastPrepared = rePrepared;
                            assistant.Collection = log.Collection = rePrepared.Validation.Collection ?? reQuery!.Collection;
                            assistant.Operation = log.Operation = reQuery!.Operation;
                            assistant.Explanation = reQuery.Explanation;
                            assistant.QueryJson = prepared.Query.ToJson().ToCompact();
                            assistant.Mql = log.FinalMql = prepared.Mql;
                        }
                    }
                    else
                    {
                        trace.Add("semanticRetry", "Zero-result re-check (business mapping)",
                            rePrepared is null ? "The model kept or could not improve the query." :
                            rePrepared.IsExecutable ? "Same query — the mapping was already correct." : "✗ " + string.Join("; ", rePrepared.Validation.Errors),
                            "info", recheckSw.ElapsedMilliseconds);
                    }
                }
                catch (Exception ex) when (ex is AIProviderException or QueryExecutionException or QueryTimeoutException)
                {
                    _logger.LogInformation("Zero-result re-check failed: {Error}", ex.Message);
                    trace.Add("semanticRetry", "Zero-result re-check (business mapping)", ex.GetType().Name, "error");
                }
            }

            // 7b. Show names instead of database ids ("supplierId" → supplier name), read-only + tenant-scoped.
            var references = await _engine.ResolveReferencesAsync(prepared, executed, schema, settings, token,
                _activity is null ? null : ActivityRecorder.MongoComment(_activity.CorrelationId));
            if (references.Changed)
            {
                executed = references.Result;
                trace.Add("references", "IDs → names", string.Join("\n", references.Notes), "ok", references.ElapsedMs);
                activity.ReferencesResolved(references.Notes, references.ElapsedMs);
            }
            return await AnswerFromRowsAsync(executed.Rows, executed.Columns, executed.Truncated, query?.Explanation, query?.Visualization, executed.ElapsedMs);
        }
        catch (AttachmentException ex)
        {
            activity.Error(stage, ActivityErrorTypes.AttachmentError, ex);
            trace.Add("error", "Attachment error", ex.Message, "error");
            return await FinishAsync(ChatStatus.Error, ex.Message, ex.Message);
        }
        catch (AIProviderException ex)
        {
            _logger.LogWarning(ex, "AI provider failure for {Provider}", provider?.Config.Name);
            activity.Error(stage == ActivityStages.QueryValidation ? ActivityStages.QueryGeneration : stage, ActivityErrorTypes.AIProviderError, ex);
            trace.Add("error", "AI provider error", ex.Message, "error");
            return await FinishAsync(ChatStatus.ProviderError, UserMessages.ProviderUnavailable, ex.Message);
        }
        catch (QueryTimeoutException ex)
        {
            if (stage != ActivityStages.MongoExecution) activity.Error(stage, ActivityErrorTypes.TimeoutError, ex);
            trace.Add("error", "MongoDB timeout", ex.Message, "error");
            return await FinishAsync(ChatStatus.Timeout, UserMessages.QueryTimeout, ex.Message);
        }
        catch (QueryExecutionException ex)
        {
            _logger.LogWarning(ex, "Query execution failed");
            // Execution errors are recorded by ExecutionFailed; earlier ones (e.g. no collections available) are prompt/schema errors.
            if (stage != ActivityStages.MongoExecution) activity.Error(stage, ActivityErrorTypes.PromptError, ex);
            trace.Add("error", "MongoDB error", ex.Message, "error");
            return await FinishAsync(ChatStatus.DatabaseError, UserMessages.DatabaseError, ex.Message);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            if (stage != ActivityStages.MongoExecution) activity.Error(stage, ActivityErrorTypes.TimeoutError, ex, $"Request timeout after {settings.Ai.TimeoutSeconds} s");
            trace.Add("error", "Timeout", $"The request exceeded {settings.Ai.TimeoutSeconds} seconds.", "error");
            return await FinishAsync(ChatStatus.Timeout, UserMessages.RequestTimeout, "Request timeout");
        }
        catch (OperationCanceledException ex)
        {
            activity.Error(stage, ActivityErrorTypes.CancelledError, ex, "Cancelled by client");
            trace.Add("error", "Cancelled", "The client cancelled the request.", "warning");
            return await FinishAsync(ChatStatus.Error, UserMessages.Cancelled, "Cancelled by client");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in AI agent pipeline");
            activity.Error(ex is TenantResolutionException ? ActivityStages.Authentication : stage,
                ex is TenantResolutionException ? ActivityErrorTypes.AuthenticationError : ActivityErrorTypes.UnknownError, ex);
            trace.Add("error", "Unexpected error", ex.GetType().Name + ": " + ex.Message, "error");
            return await FinishAsync(ChatStatus.Error, UserMessages.Unexpected, ex.GetType().Name);
        }

        // ------------------------------------------------------------------ local: rows → answer (database, table and combined)

        async Task<ChatResponse> AnswerFromRowsAsync(List<JsonObject> rows, List<string> columns, bool truncated, string? explanation, string? vizHint, long elapsedMs)
        {
            // Zero rows from a valid query: a clear sentence (topic + period), and for totals the real zero row
            // ("Today's sales are AED 0.00") — never an empty answer. Averages stay null (not available), not 0.
            ZeroResult? zero = null;
            if (rows.Count == 0)
            {
                zero = ZeroResultPolicy.Describe(question, lastPrepared?.Validation.Pipeline, lastPrepared?.Validation.Collection, questionDates, currencyCode, currencyDecimals);
                if (zero.ZeroRow is { } zeroRow)
                {
                    rows = new List<JsonObject> { zeroRow };
                    columns = zeroRow.Select(kv => kv.Key).ToList();
                    vizHint = "kpi";
                }
                trace.Add("zeroResult", "No matching records", zero.Message + (zero.ZeroRow is null ? string.Empty : "\nTotals: " + zero.ZeroRow.ToJsonString()), "ok");
            }

            assistant.ExecutionTimeMs = log.ExecutionTimeMs = elapsedMs;
            assistant.ResultCount = log.ResultCount = rows.Count;
            activity.SetResult(rows, columns, truncated);
            assistant.Truncated = truncated;
            assistant.Columns = columns;

            var dataArray = new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray());
            assistant.DataJson = dataArray.ToCompact();
            trace.Add("mongoResult", $"Result ({rows.Count} rows{(truncated ? ", truncated" : string.Empty)})",
                JsonHelpers.Truncate(dataArray.ToIndented(), 8000), "ok", elapsedMs);

            if (zero is not null)
            {
                assistant.ResultCount = log.ResultCount = 0;
                if (zero.ZeroRow is not null)
                    assistant.VisualizationJson = JsonSerializer.Serialize(VisualizationAdvisor.Decide(rows, columns, vizHint), MessageMapper.Web);
                return await FinishAsync(ChatStatus.NoResults, zero.Message, null);
            }

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
            stage = ActivityStages.ResponseGeneration;
            activity.ResponseStarted();
            var answerSw = Stopwatch.StartNew();
            try
            {
                answer = await GenerateAnswerAsync(provider!, answerMessages, sink, assistant, token);
                if (storeScope?.Note is { Length: > 0 } storeNote) answer = storeNote + "\n\n" + answer;
                activity.AnswerGenerated(answer);
            }
            catch (AIProviderException ex)
            {
                answerSw.Stop();
                assistant.AiAnswerTimeMs = answerSw.ElapsedMilliseconds;
                activity.ResponseFailed(ex, answerSw.ElapsedMilliseconds);
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

            // Activity log: queued for the background writer (never blocks or fails the answer).
            activity.TechnicalDetailsReturned(showTechnical);
            activity.Complete(status, assistant.Content, error, assistant, log, conversation.MessageCount, persist, total.ElapsedMilliseconds);

            var response = MessageMapper.ToChatResponse(conversation, userMessage, assistant,
                settings.Chat.DeveloperMode && showTechnical ? trace : null);
            return showTechnical ? response : TechnicalDetailsPolicy.ForCustomer(response);
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

    /// <summary>Reads how the accounting groups of the question are stored in this company's data (best effort, tenant-scoped).</summary>
    private async Task<int> ReadStoredGroupValuesAsync(SemanticInterpretation semantics, IReadOnlyList<CollectionSchema> schema, AppSettings settings, CancellationToken ct)
    {
        var cache = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var lookups = 0;
        foreach (var mapping in semantics.Mappings.Where(m => m.Concept.AccountGroups.Count > 0))
        {
            var patterns = mapping.Concept.AccountGroups.Select(BusinessTerms.GroupPattern).ToList();
            // Group fields in the concept's own collections first (Ledgers.groupName before Items.groupName).
            var fields = mapping.GroupFields
                .OrderByDescending(g => mapping.Collections.Contains(g.Collection, StringComparer.OrdinalIgnoreCase))
                .Take(3);
            foreach (var field in fields)
            {
                if (lookups >= 6) return lookups;
                var key = field.Collection + "|" + field.Field + "|" + string.Join("|", patterns);
                if (!cache.TryGetValue(key, out var values))
                {
                    lookups++;
                    values = await _engine.StoredValuesAsync(field.Collection, field.Field, patterns, schema, settings, ct,
                        _activity is null ? null : ActivityRecorder.MongoComment(_activity.CorrelationId));
                    cache[key] = values;
                }
                if (values.Count > 0) mapping.StoredGroupValues[field] = values;
            }
        }
        return lookups;
    }

    /// <summary>A clarification the model wrote goes to the user only when it contains no technical details.</summary>
    internal static string SafeClarification(string? text, IReadOnlyList<CollectionSchema> schema)
    {
        const string fallback = "Could you tell me a little more about which records you mean (for example customers, suppliers, items or accounts)?";
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > 300 || t.IndexOfAny(new[] { '{', '}', '$', '[', ']' }) >= 0) return fallback;
        // Field-like tokens (groupName, CustomerId, sales_date) are internal names.
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\b[a-z]+[A-Z]\w*\b|\b\w+_\w+\b")) return fallback;
        foreach (var c in schema)
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\b" + System.Text.RegularExpressions.Regex.Escape(c.Name) + @"\b")
                && c.Name.Any(char.IsUpper) && c.Name.Length > 3 && !IsPlainWord(c.Name))
                return fallback;
        return t;

        static bool IsPlainWord(string name) => name.Skip(1).All(char.IsLower); // "Customers" is also an ordinary word
    }

    private static string? StoreNote(StoreScope? scope) => scope?.Mode switch
    {
        StoreMode.Selected => $"The data must stay limited to the selected store{(scope.StoreName is { Length: > 0 } n ? " \"" + n + "\"" : string.Empty)} (the server applies it).",
        StoreMode.AllStores => "The user asked for all stores.",
        _ when scope?.AccountingStatement == true => "This is a company-level accounting statement: no store filter.",
        _ => null
    };

    private static string? DatesNote(QuestionDates dates)
        => dates.Spans.Any()
            ? "Requested period: " + string.Join("; ", dates.Spans.Select(s => s.From == s.To ? QuestionDates.Describe(s.From) : $"{QuestionDates.Describe(s.From)} to {QuestionDates.Describe(s.To)}")) + " (end day included)."
            : null;

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
            UserName = _user.DisplayName,
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
