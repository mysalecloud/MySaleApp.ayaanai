using System.Text.Json.Nodes;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Services;
using MySale.AI.Domain;
using Xunit;

namespace MySale.AI.Tests;

/// <summary>Intent detection, question kinds, structured values and reply interpretation (no model, no database).</summary>
public class ConversationContextUnitTests
{
    private static PendingClarification DebtorsType() => new()
    {
        OriginalQuestion = "Debtors report",
        Question = "Do you want the debtors report by unpaid invoice age, or by ledger balance?",
        Options = new List<string> { "Unpaid invoice age", "Ledger balance" },
        OptionValues = new List<string> { "UNPAID_INVOICE_AGE", "LEDGER_BALANCE" },
        Kind = ClarificationTypes.Choice,
        Parameter = "reportType",
        Intent = "DEBTORS_REPORT"
    };

    private static PendingClarification YesNoQuestion() => new()
    {
        OriginalQuestion = "Customer list",
        Question = "Do you want me to include inactive customers?",
        Options = new List<string> { "Yes", "No" },
        OptionValues = new List<string> { "YES", "NO" },
        Kind = ClarificationTypes.YesNo,
        Parameter = "confirm",
        Intent = "CUSTOMERS"
    };

    [Theory]
    [InlineData("Debtors Report (Age Wise)", "DEBTORS_REPORT")]
    [InlineData("Who owes us money?", "DEBTORS_REPORT")]
    [InlineData("Creditors report", "CREDITORS_REPORT")]
    [InlineData("Show customer balance", "CUSTOMER_BALANCE")]
    [InlineData("Show sales between dates", "SALES")]
    [InlineData("Sales report", "SALES")]
    [InlineData("Show stock", "STOCK")]
    [InlineData("Main Warehouse", "STOCK")]
    [InlineData("Purchase return list", "PURCHASE_RETURN")]
    [InlineData("List service items", "SERVICE_ITEMS")]
    [InlineData("Cash balance", "ACCOUNTS")]
    [InlineData("Receipt vouchers this week", "VOUCHERS")]
    [InlineData("age wise", null)]
    [InlineData("1 September to 20 September", null)]
    [InlineData("ABC Trading", null)]
    public void Intent_is_detected_from_the_request(string text, string? expected)
        => Assert.Equal(expected, ConversationIntents.Detect(text));

    [Theory]
    [InlineData("Do you want the debtors report by unpaid invoice age, or by ledger balance?", "Unpaid invoice age|Ledger balance", "choice")]
    [InlineData("Do you want me to include inactive customers?", "", "yesNo")]
    [InlineData("Should I include cancelled invoices?", "", "yesNo")]
    [InlineData("Which warehouse?", "", "open")]
    [InlineData("Which period?", "", "open")]
    [InlineData("Please provide the start and end dates.", "", "open")]
    public void Question_kind_is_recognised(string question, string options, string expected)
    {
        var list = options.Length == 0 ? new List<string>() : options.Split('|').ToList();
        Assert.Equal(expected, ClarificationTypes.KindOf(question, list, "model"));
    }

    [Theory]
    [InlineData("Which warehouse?", "warehouse")]
    [InlineData("Which period?", "period")]
    [InlineData("Please provide the start and end dates.", "period")]
    [InlineData("Which customer do you mean?", "customer")]
    [InlineData("Which branch should I use?", "branch")]
    [InlineData("Which item do you mean?", "item")]
    [InlineData("Which account do you mean?", "account")]
    public void Question_parameter_is_recognised(string question, string expected)
        => Assert.Equal(expected, ClarificationTypes.ParameterOf(question, new List<string>(), ClarificationTypes.Open, null));

    [Fact]
    public void Options_get_structured_values()
    {
        Assert.Equal(new[] { "UNPAID_INVOICE_AGE", "LEDGER_BALANCE" }, ClarificationTypes.ValuesFor(new[] { "Unpaid invoice age", "Ledger balance" }, false));
        Assert.Equal(new[] { "OPTION_1", "OPTION_2" }, ClarificationTypes.ValuesFor(new[] { "Al Noor Trading", "Al Noor Stores" }, true));
        Assert.Equal(new[] { "OPTION_1", "OPTION_2" }, ClarificationTypes.ValuesFor(new[] { "വില", "എണ്ണം" }, false));
    }

    [Theory]
    [InlineData("age wise", "Unpaid invoice age", "UNPAID_INVOICE_AGE")]
    [InlineData("Age", "Unpaid invoice age", "UNPAID_INVOICE_AGE")]
    [InlineData("invoice age", "Unpaid invoice age", "UNPAID_INVOICE_AGE")]
    [InlineData("ageing", "Unpaid invoice age", "UNPAID_INVOICE_AGE")]
    [InlineData("first", "Unpaid invoice age", "UNPAID_INVOICE_AGE")]
    [InlineData("first one", "Unpaid invoice age", "UNPAID_INVOICE_AGE")]
    [InlineData("the second one", "Ledger balance", "LEDGER_BALANCE")]
    [InlineData("2", "Ledger balance", "LEDGER_BALANCE")]
    [InlineData("ledger", "Ledger balance", "LEDGER_BALANCE")]
    [InlineData("ledger balance", "Ledger balance", "LEDGER_BALANCE")]
    [InlineData("No, ledger balance", "Ledger balance", "LEDGER_BALANCE")]
    public void Choice_replies_select_an_option(string reply, string option, string value)
    {
        var r = ReplyInterpreter.Interpret(reply, DebtorsType(), null);
        Assert.Equal(ReplyOutcome.Resolved, r.Outcome);
        Assert.Equal(option, r.Resolved);
        Assert.Equal(value, r.Value);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("ok")]
    [InlineData("correct")]
    [InlineData("continue")]
    [InlineData("that one")]
    [InlineData("same")]
    [InlineData("previous one")]
    [InlineData("go ahead")]
    [InlineData("show it")]
    [InlineData("no")]
    [InlineData("monthly")]
    public void Choice_questions_are_never_answered_by_a_guess(string reply)
        => Assert.Equal(ReplyOutcome.NeedsChoice, ReplyInterpreter.Interpret(reply, DebtorsType(), null).Outcome);

    [Fact]
    public void Same_reuses_the_previous_requests_choice()
    {
        var r = ReplyInterpreter.Interpret("same as before", DebtorsType(), null, previousValue: "LEDGER_BALANCE");
        Assert.Equal(ReplyOutcome.Resolved, r.Outcome);
        Assert.Equal("Ledger balance", r.Resolved);
    }

    [Theory]
    [InlineData("yes", "YES")]
    [InlineData("Yes please", "YES")]
    [InlineData("ok", "YES")]
    [InlineData("correct", "YES")]
    [InlineData("go ahead", "YES")]
    [InlineData("include them", "YES")]
    [InlineData("no", "NO")]
    [InlineData("No thanks", "NO")]
    [InlineData("exclude them", "NO")]
    [InlineData("അതെ", "YES")]
    public void Yes_no_questions_resolve_to_a_boolean(string reply, string value)
    {
        var r = ReplyInterpreter.Interpret(reply, YesNoQuestion(), null);
        Assert.Equal(ReplyOutcome.Resolved, r.Outcome);
        Assert.Equal(value, r.Value);
    }

    [Fact]
    public void A_clicked_option_uses_its_structured_value_not_the_label()
    {
        var r = ReplyInterpreter.Interpret("something the label parser would not understand", DebtorsType(), new StructuredChoice("LEDGER_BALANCE", "Ledger balance"));
        Assert.Equal(ReplyOutcome.Resolved, r.Outcome);
        Assert.Equal("Ledger balance", r.Resolved);
        Assert.Equal("LEDGER_BALANCE", r.Value);
    }

    [Fact]
    public void An_unknown_structured_value_is_ignored()
        => Assert.Equal(ReplyOutcome.NeedsChoice, ReplyInterpreter.Interpret("yes", DebtorsType(), new StructuredChoice("DROP_DATABASE", "x")).Outcome);

    [Theory]
    [InlineData("What are today's sales?", ReplyKind.NewTopic)]
    [InlineData("Show my stock value", ReplyKind.NewTopic)]
    [InlineData("age wise", ReplyKind.Answer)]
    [InlineData("ledger", ReplyKind.Answer)]
    [InlineData("yes", ReplyKind.Answer)]
    [InlineData("show customer balances by ledger", ReplyKind.Answer)]   // same business area
    [InlineData("cancel", ReplyKind.Cancel)]
    public void Topic_changes_are_recognised_by_intent(string reply, ReplyKind expected)
        => Assert.Equal(expected, ClarificationFlow.Classify(reply, DebtorsType()));

    [Theory]
    [InlineData("Total debtors: AED 12,500.00. Do you want this by unpaid invoice age instead?", "Do you want this by unpaid invoice age instead?")]
    [InlineData("Here are your customers.\n\nWould you like to include inactive customers?", "Would you like to include inactive customers?")]
    [InlineData("Your total sales are AED 1,500.00.", null)]
    [InlineData("Is that right? Sales are AED 1,500.00.", null)]
    public void Follow_up_offers_are_found_at_the_end_of_an_answer(string answer, string? expected)
        => Assert.Equal(expected, AnswerFollowUps.TrailingQuestion(answer));

    [Theory]
    [InlineData("yes", true)]
    [InlineData("the second one", true)]
    [InlineData("ledger", true)]
    [InlineData("last month", true)]
    [InlineData("Show me the purchases of last month by supplier", false)]
    [InlineData("What are today's sales?", false)]
    public void Contextual_replies_are_recognised(string reply, bool expected)
        => Assert.Equal(expected, ReplyInterpreter.IsContextual(reply));

    [Fact]
    public void History_shows_the_model_what_the_customer_saw()
    {
        var turns = PromptBuilder.HistoryTurns(new List<ChatMessage>
        {
            new() { Role = MessageRole.User, Content = "Debtors report" },
            new() { Role = MessageRole.Assistant, Status = ChatStatus.Success, QueryJson = "{\"type\":\"query\",\"collection\":\"Ledger\"}",
                    Content = "Total receivables are AED 12,500.00 (ledger balance). Do you want it by unpaid invoice age?" },
            new() { Role = MessageRole.User, Content = "What's the weather?" },
            new() { Role = MessageRole.Assistant, Status = ChatStatus.Unsupported, Content = "I can only answer questions about your business data." }
        }, 12_000);
        Assert.Contains("\"shownToUser\":\"Total receivables are AED 12,500.00 (ledger balance). Do you want it by unpaid invoice age?\"", turns[1].Content);
        Assert.Contains("\"collection\":\"Ledger\"", turns[1].Content);
        Assert.Contains("I can only answer questions about your business data.", turns[3].Content);
        Assert.DoesNotContain("previous question could not be answered", turns[3].Content);
    }
}

/// <summary>
/// Conversation state across turns (orchestrator + in-memory state store + scripted model): flows A–H of the
/// conversation-context specification and the rules around them.
/// </summary>
public class ConversationStateFlowTests
{
    private const string LedgerQuery = """
    {"type":"query","operation":"aggregate","collection":"Sales","pipeline":[
      {"$match":{"Status":{"$ne":"Cancelled"}}},
      {"$group":{"_id":null,"totalSales":{"$sum":"$NetAmount"}}},
      {"$project":{"_id":0,"totalSales":1}}],
     "explanation":"Total","visualization":"kpi"}
    """;

    private const string AskDebtorsType =
        """{"type":"clarify","question":"Do you want the debtors report by unpaid invoice age, or by ledger balance?","options":["Unpaid invoice age","Ledger balance"],"missing":"report type"}""";

    private static Harness New()
    {
        var h = new Harness();
        h.Executor.Handler = (_, _) => new List<JsonObject> { new() { ["totalSales"] = 1500m } };
        return h;
    }

    private static Task<ChatResponse> Send(Harness h, string message, string? conversationId = null, string? replyTo = null,
        ClarificationChoiceDto? choice = null)
        => h.Orchestrator.RunAsync(new ChatRequest { Message = message, ConversationId = conversationId, ReplyToMessageId = replyTo, Choice = choice },
            NullChatEventSink.Instance, default);

    private static string LastPrompt(Harness h) => h.Provider.Requests.Where(r => r.JsonMode).Last().Messages[^1].Content;
    private static string LastSystem(Harness h) => h.Provider.Requests.Where(r => r.JsonMode).Last().Messages[0].Content;

    // A. Debtors report → "age wise" continues DEBTORS_REPORT in age-wise mode.
    [Fact]
    public async Task A_age_wise_continues_the_debtors_report()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskDebtorsType);
        var first = await Send(h, "Debtors report");

        Assert.Equal(ChatStatus.Clarification, first.Status);
        Assert.Equal("choice", first.Clarification!.Kind);
        Assert.Equal(new[] { "UNPAID_INVOICE_AGE", "LEDGER_BALANCE" }, first.Clarification.Choices.Select(c => c.Value));
        var open = h.States.Peek(first.ConversationId)!;
        Assert.Equal("DEBTORS_REPORT", open.Intent);
        Assert.Equal("DEBTORS_REPORT", open.Pending!.Intent);
        Assert.Equal("reportType", open.Pending.Parameter);
        Assert.Equal("WAITING_FOR_USER", open.Stage);
        Assert.Contains("reportType", open.Unresolved);

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, "age wise", first.ConversationId, first.MessageId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Equal(first.ConversationId, second.ConversationId);
        var prompt = LastPrompt(h);
        Assert.StartsWith("Question: Debtors report", prompt);
        Assert.Contains("(meaning: Unpaid invoice age)", prompt);
        Assert.Contains("[selected: reportType = UNPAID_INVOICE_AGE]", prompt);
        Assert.Contains("Current intent: DEBTORS_REPORT", LastSystem(h));
        Assert.Contains("reportType = UNPAID_INVOICE_AGE", LastSystem(h));
        var done = h.States.Peek(first.ConversationId)!;
        Assert.Null(done.Pending);
        Assert.Equal("PRESENT_RESULT", done.Stage);
        Assert.Equal("UNPAID_INVOICE_AGE", done.PreviousParameters["reportType"]);
    }

    // B. "yes" to a choice question: asked to choose, nothing guessed, context kept.
    [Theory]
    [InlineData("yes")]
    [InlineData("ok")]
    [InlineData("that one")]
    [InlineData("continue")]
    public async Task B_yes_to_a_choice_question_asks_to_choose(string reply)
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskDebtorsType);
        var first = await Send(h, "Debtors report");
        var calls = h.Provider.Requests.Count;

        var again = await Send(h, reply, first.ConversationId, first.MessageId);

        Assert.Equal(ChatStatus.Clarification, again.Status);
        Assert.Equal("Please choose one: Unpaid invoice age or Ledger balance.", again.Answer);
        Assert.Equal(new[] { "Unpaid invoice age", "Ledger balance" }, again.Clarification!.Options);
        Assert.DoesNotContain("no earlier question", again.Answer);
        Assert.Equal(calls, h.Provider.Requests.Count);                 // no model call, no guess
        var state = h.States.Peek(first.ConversationId)!;
        Assert.Equal("DEBTORS_REPORT", state.Pending!.Intent);          // parent request kept
        Assert.Equal("Debtors report", state.Pending.OriginalQuestion);
        Assert.Equal(again.MessageId, state.Pending.AssistantMessageId);

        // …and the choice made afterwards still continues the debtors report.
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var done = await Send(h, "ledger", first.ConversationId, again.MessageId);
        Assert.Equal(ChatStatus.Success, done.Status);
        Assert.Contains("[selected: reportType = LEDGER_BALANCE]", LastPrompt(h));
    }

    // C. Yes/no question: "yes" resolves to true (and chips Yes / No are offered).
    [Fact]
    public async Task C_yes_resolves_a_yes_no_question()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"Do you want me to include inactive customers?"}""");
        var first = await Send(h, "Customer list");

        Assert.Equal("yesNo", first.Clarification!.Kind);
        Assert.Equal(new[] { "Yes", "No" }, first.Clarification.Options);
        Assert.Equal(new[] { "YES", "NO" }, first.Clarification.Choices.Select(c => c.Value));

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, "yes", first.ConversationId, first.MessageId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Contains("You asked: \"Do you want me to include inactive customers?\" → the user answered: \"yes\"", LastPrompt(h));
        Assert.Contains("[selected: confirm = YES]", LastPrompt(h));
    }

    [Fact]
    public async Task C_no_resolves_a_yes_no_question_to_false()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"Should I include cancelled invoices?"}""");
        var first = await Send(h, "Sales this month");
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        await Send(h, "no", first.ConversationId);
        Assert.Contains("[selected: confirm = NO]", LastPrompt(h));
    }

    // D. Stock → which warehouse? → "Main Warehouse" continues the stock request.
    [Fact]
    public async Task D_warehouse_answer_continues_the_stock_request()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"Which warehouse?"}""");
        var first = await Send(h, "Show stock");
        Assert.Equal("warehouse", h.States.Peek(first.ConversationId)!.Pending!.Parameter);

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, "Main Warehouse", first.ConversationId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.StartsWith("Question: Show stock", LastPrompt(h));
        Assert.Contains("the user answered: \"Main Warehouse\"", LastPrompt(h));
        Assert.Contains("Current intent: STOCK", LastSystem(h));
        Assert.Contains("warehouse = Main Warehouse", LastSystem(h));
    }

    // E. Sales report → which period? → "this month" continues the sales request with this month's range.
    [Fact]
    public async Task E_period_answer_continues_the_sales_request()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"Which period?"}""");
        var first = await Send(h, "Sales report");

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, "this month", first.ConversationId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Equal("Sales report — this month", h.Store.Messages.Where(m => m.Role == MessageRole.User).Last().ResolvedQuestion);
        Assert.Contains("Current intent: SALES", LastSystem(h));
        Assert.Contains("period = this month", LastSystem(h));
    }

    // Requirement 14: dates given after "between dates" apply to the same SALES request.
    [Fact]
    public async Task Dates_given_later_apply_to_the_sales_request()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"Please provide the start and end dates."}""");
        var first = await Send(h, "Show sales between dates.");
        Assert.Equal("period", h.States.Peek(first.ConversationId)!.Pending!.Parameter);

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, "1 September to 20 September", first.ConversationId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.StartsWith("Question: Show sales between dates", LastPrompt(h));
        Assert.Contains("\"1 September to 20 September\"", LastPrompt(h));
        Assert.Contains("Current intent: SALES", LastSystem(h));
    }

    // F. Customer balance → which customer? → "ABC Trading".
    [Fact]
    public async Task F_customer_answer_continues_the_customer_balance()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"Which customer?"}""");
        var first = await Send(h, "Show customer balance");
        Assert.Equal("customer", h.States.Peek(first.ConversationId)!.Pending!.Parameter);

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, "ABC Trading", first.ConversationId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Contains("Show customer balance", LastPrompt(h));
        Assert.Contains("customer = ABC Trading", LastSystem(h));
    }

    // G. A reply after several minutes still continues the request.
    [Fact]
    public async Task G_a_reply_after_minutes_keeps_the_context()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskDebtorsType);
        var first = await Send(h, "Debtors report");
        h.States.Mutate(first.ConversationId, s => s.Pending!.AskedAt = DateTime.UtcNow.AddMinutes(-45));

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, "age wise", first.ConversationId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Contains("[selected: reportType = UNPAID_INVOICE_AGE]", LastPrompt(h));
    }

    // H. Refresh / reconnect: the open question can be restored, and a reply without the id is recovered.
    [Fact]
    public async Task H_the_open_question_is_restored_after_a_reload()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskDebtorsType);
        var first = await Send(h, "Debtors report");

        var service = new ConversationService(new MemConversations(h.Store), new MemMessages(h.Store), h.User, h.States);
        var context = await service.GetContextAsync(first.ConversationId, default);

        Assert.Equal("DEBTORS_REPORT", context.Intent);
        Assert.Equal("WAITING_FOR_USER", context.Stage);
        Assert.NotNull(context.Pending);
        Assert.Equal(first.MessageId, context.Pending!.MessageId);
        Assert.Equal(new[] { "UNPAID_INVOICE_AGE", "LEDGER_BALANCE" }, context.Pending.Choices.Select(c => c.Value));

        // The restored chip is clicked: structured value, same conversation.
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, "Ledger balance", first.ConversationId, context.Pending.MessageId,
            new ClarificationChoiceDto { DisplayText = "Ledger balance", Value = "LEDGER_BALANCE" });
        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Contains("[selected: reportType = LEDGER_BALANCE]", LastPrompt(h));

        var afterwards = await service.GetContextAsync(first.ConversationId, default);
        Assert.Null(afterwards.Pending);
    }

    [Fact]
    public async Task H_a_reply_that_lost_the_conversation_id_is_recovered_from_the_question()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskDebtorsType);
        var first = await Send(h, "Debtors report");

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, "age wise", conversationId: null, replyTo: first.MessageId);

        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Contains("(meaning: Unpaid invoice age)", LastPrompt(h));
    }

    [Fact]
    public async Task Another_users_context_is_not_found()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskDebtorsType);
        var first = await Send(h, "Debtors report");

        h.User.UserId = "66b000000000000000000002";
        var service = new ConversationService(new MemConversations(h.Store), new MemMessages(h.Store), h.User, h.States);
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetContextAsync(first.ConversationId, default));
    }

    // Requirement 12: never "no earlier question" while the backend holds one.
    [Theory]
    [InlineData("yes")]
    [InlineData("first one")]
    [InlineData("same")]
    [InlineData("previous")]
    public async Task Short_replies_never_claim_there_is_no_context(string reply)
    {
        var h = New();
        h.Store.Settings.Chat.SaveConversations = false;               // no saved history: the state alone must be enough
        h.Provider.QueryResponses.Enqueue(_ => AskDebtorsType);
        var first = await Send(h, "Debtors report", "local-conv-1");
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);

        var r = await Send(h, reply, first.ConversationId);

        Assert.DoesNotContain("no earlier question", r.Answer);
        Assert.DoesNotContain("not sure what", r.Answer);
    }

    // Requirement 11: a clearly different request is recognised; the open request is suspended and can be resumed.
    [Fact]
    public async Task A_topic_change_suspends_and_continue_resumes()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskDebtorsType);
        var first = await Send(h, "Debtors report");

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var sales = await Send(h, "What are today's sales?", first.ConversationId);
        Assert.Equal(ChatStatus.Success, sales.Status);
        Assert.Equal("Question: What are today's sales?", LastPrompt(h).Split("\n\n")[0]);
        var state = h.States.Peek(first.ConversationId)!;
        Assert.Null(state.Pending);
        Assert.Equal("Debtors report", state.Suspended!.OriginalQuestion);
        Assert.Equal("SALES", state.Intent);

        var calls = h.Provider.Requests.Count;
        var resumed = await Send(h, "continue", first.ConversationId);
        Assert.Equal(ChatStatus.Clarification, resumed.Status);
        Assert.Equal("Do you want the debtors report by unpaid invoice age, or by ledger balance?", resumed.Answer);
        Assert.Equal(calls, h.Provider.Requests.Count);

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        await Send(h, "age wise", first.ConversationId, resumed.MessageId);
        Assert.StartsWith("Question: Debtors report", LastPrompt(h));
        Assert.Contains("UNPAID_INVOICE_AGE", LastPrompt(h));
    }

    // Answers that end with a question: the offer is kept and linked to the request.
    [Fact]
    public async Task A_follow_up_offer_in_an_answer_is_continued_by_yes()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        h.Provider.Answer = _ => "Total receivables are AED 12,500.00 (ledger balance). Do you want it by unpaid invoice age instead?";
        var first = await Send(h, "Debtors report");

        Assert.Equal(ChatStatus.Success, first.Status);
        Assert.NotNull(first.Clarification);
        Assert.True(first.Clarification!.FollowUp);
        Assert.Equal(new[] { "Yes", "No" }, first.Clarification.Options);
        var offer = h.States.Peek(first.ConversationId)!.Pending!;
        Assert.True(offer.Soft);
        Assert.Equal("DEBTORS_REPORT", offer.Intent);

        h.Provider.Answer = _ => "Here is the ageing.";
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var yes = await Send(h, "yes", first.ConversationId);

        Assert.Equal(ChatStatus.Success, yes.Status);
        Assert.StartsWith("Question: Debtors report", LastPrompt(h));
        Assert.Contains("You asked: \"Do you want it by unpaid invoice age instead?\" → the user answered: \"yes\"", LastPrompt(h));
    }

    [Fact]
    public async Task A_follow_up_offer_does_not_capture_a_new_request()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        h.Provider.Answer = _ => "Total receivables are AED 12,500.00. Do you want it by unpaid invoice age instead?";
        var first = await Send(h, "Debtors report");

        h.Provider.Answer = _ => "Done.";
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        await Send(h, "Show me the purchases of last month by supplier", first.ConversationId);

        Assert.Equal("Question: Show me the purchases of last month by supplier", LastPrompt(h).Split("\n\n")[0]);
        Assert.Null(h.States.Peek(first.ConversationId)!.Suspended);   // an offer is dropped, not suspended
    }

    [Fact]
    public async Task No_to_a_follow_up_offer_runs_nothing()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        h.Provider.Answer = _ => "Total receivables are AED 12,500.00. Do you want it by unpaid invoice age instead?";
        var first = await Send(h, "Debtors report");
        var calls = h.Provider.Requests.Count;

        var no = await Send(h, "no", first.ConversationId);

        Assert.Equal(ChatStatus.Success, no.Status);
        Assert.Equal(calls, h.Provider.Requests.Count);
        Assert.Null(h.States.Peek(first.ConversationId)!.Pending);
    }

    [Fact]
    public async Task The_answer_prompt_asks_for_no_trailing_questions()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        await Send(h, "Debtors report");
        var answerRequest = h.Provider.Requests.Last(r => !r.JsonMode);
        Assert.Contains("Do not end with a question", answerRequest.Messages[0].Content);
    }

    // Modules: the same clarification behaviour for every intent.
    [Theory]
    [InlineData("Purchase report", "PURCHASES", "Which period?", "last month")]
    [InlineData("Supplier balance", "SUPPLIER_BALANCE", "Which supplier?", "Gulf Traders")]
    [InlineData("Show ledger", "LEDGER", "Which account do you mean?", "Cash in hand")]
    [InlineData("Show receipt vouchers", "VOUCHERS", "Which branch should I use?", "Main branch")]
    [InlineData("Item details", "ITEMS", "Which item do you mean?", "Rice 5kg")]
    [InlineData("List service items", "SERVICE_ITEMS", "Which category?", "Repairs")]
    [InlineData("Creditors report", "CREDITORS_REPORT", "Do you want it by unpaid invoice age or by ledger balance?", "the first one")]
    [InlineData("Outstanding report", "OUTSTANDING_REPORT", "Do you want customers or suppliers?", "customers")]
    [InlineData("Expense report", "EXPENSES", "Which period?", "this year")]
    public async Task Every_module_continues_its_request(string request, string intent, string question, string reply)
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => $$"""{"type":"clarify","question":"{{question}}"}""");
        var first = await Send(h, request);
        Assert.Equal(ChatStatus.Clarification, first.Status);
        Assert.Equal(intent, h.States.Peek(first.ConversationId)!.Pending!.Intent);

        h.Provider.QueryResponses.Enqueue(_ => LedgerQuery);
        var second = await Send(h, reply, first.ConversationId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.StartsWith("Question: " + request, LastPrompt(h));
        Assert.Contains("Current intent: " + intent, LastSystem(h));
    }
}
