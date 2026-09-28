using System.Text.Json.Nodes;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Services;
using MySale.AI.Domain;
using Xunit;

namespace MySale.AI.Tests;

/// <summary>Short replies, option matching, cancel / new-topic detection and merging (no model, no database).</summary>
public class ClarificationFlowTests
{
    private static PendingClarification StockQuestion() => new()
    {
        OriginalQuestion = "Show my stock",
        Question = "Do you want stock quantity or stock value?",
        Options = new List<string> { "Stock quantity", "Stock value" }
    };

    [Theory]
    [InlineData("Do you want stock quantity or stock value?", "stock quantity|stock value")]
    [InlineData("Do you mean customers (parties who owe you money) or suppliers (parties you owe money to)?", "customers|suppliers")]
    [InlineData("Which ledger do you mean: Al Noor Trading, Al Noor Stores, Noor & Sons?", "Al Noor Trading|Al Noor Stores|Noor & Sons")]
    [InlineData("Did you mean 1 Sep 2026 or 9 Jan 2026 for \"01/09/2026\"?", "1 Sep 2026|9 Jan 2026")]
    [InlineData("For which period?", "")]
    [InlineData("Which ledger (customer, supplier, cash, bank or other account) should I show the statement for?", "")]
    public void Options_are_read_from_the_question(string question, string expected)
        => Assert.Equal(expected, string.Join('|', ClarificationFlow.OptionsFrom(question)));

    [Theory]
    [InlineData("Value", "Stock value")]
    [InlineData("value", "Stock value")]
    [InlineData("Quantity", "Stock quantity")]
    [InlineData("qty", "Stock quantity")]
    [InlineData("stock value please", "Stock value")]
    [InlineData("the second one", "Stock value")]
    [InlineData("first", "Stock quantity")]
    [InlineData("Both", "Stock quantity and Stock value")]
    [InlineData("രണ്ടും", "Stock quantity and Stock value")]
    [InlineData("വില", "Stock value")]                  // Malayalam: price / value
    [InlineData("മൂല്യം", "Stock value")]
    [InlineData("എണ്ണം", "Stock quantity")]             // Malayalam: count
    [InlineData("value mathi", "Stock value")]          // mixed: "value is enough"
    [InlineData("No, I meant quantity", "Stock quantity")]
    [InlineData("Actually value", "Stock value")]
    public void Short_replies_resolve_to_an_option(string reply, string expected)
        => Assert.Equal(expected, ClarificationFlow.Resolve(reply, StockQuestion()));

    [Theory]
    [InlineData("ഇന്നലെ", "yesterday")]
    [InlineData("ഈ മാസം", "this month")]
    [InlineData("കഴിഞ്ഞ മാസം", "last month")]
    [InlineData("Yes", "yes")]
    [InlineData("അതെ", "yes")]
    public void Open_questions_get_the_normalised_meaning(string reply, string expected)
        => Assert.Equal(expected, ClarificationFlow.Resolve(reply, new PendingClarification { OriginalQuestion = "Sales", Question = "For which period?" }));

    [Fact]
    public void Plain_answers_to_open_questions_are_kept_as_written()
        => Assert.Null(ClarificationFlow.Resolve("Al Noor Trading", new PendingClarification { OriginalQuestion = "Ledger", Question = "Which ledger?" }));

    [Theory]
    [InlineData("Value", ReplyKind.Answer)]
    [InlineData("Show stock value", ReplyKind.Answer)]
    [InlineData("No, I meant quantity", ReplyKind.Answer)]
    [InlineData("Yesterday", ReplyKind.Answer)]
    [InlineData("cancel", ReplyKind.Cancel)]
    [InlineData("Never mind", ReplyKind.Cancel)]
    [InlineData("വേണ്ട", ReplyKind.Cancel)]
    [InlineData("Show my sales this month", ReplyKind.NewTopic)]
    [InlineData("How much cash do I have?", ReplyKind.NewTopic)]
    [InlineData("ഈ മാസത്തെ sales എത്ര?", ReplyKind.NewTopic)]
    [InlineData("Sales", ReplyKind.NewTopic)]           // a subject that is not one of the choices
    public void Replies_are_classified(string reply, ReplyKind expected)
        => Assert.Equal(expected, ClarificationFlow.Classify(reply, StockQuestion()));

    [Fact]
    public void A_short_subject_answers_an_open_question()
    {
        var open = new PendingClarification { OriginalQuestion = "Show the account balance", Question = "Which account do you mean?" };
        Assert.Equal(ReplyKind.Answer, ClarificationFlow.Classify("Bank", open));
    }

    [Theory]
    [InlineData("Value", true)]
    [InlineData("Yes", true)]
    [InlineData("Both", true)]
    [InlineData("Yesterday", true)]
    [InlineData("ഇന്നലെ", true)]
    [InlineData("last month", true)]
    [InlineData("Show my stock", false)]
    [InlineData("Sales", false)]
    [InlineData("Stock value", false)]
    public void Bare_replies_are_recognised(string text, bool expected)
        => Assert.Equal(expected, ClarificationFlow.IsBareReply(text));

    [Fact]
    public void Answers_are_merged_into_the_original_request()
    {
        var merged = ClarificationFlow.Merge("Show my stock", new List<ClarificationAnswer>
        {
            new() { Question = "Do you want stock quantity or stock value?", Reply = "Value", Resolved = "Stock value" },
            new() { Question = "For which period?", Reply = "ഇന്നലെ", Resolved = "yesterday" }
        });
        Assert.Equal("Show my stock — Stock value; yesterday", merged.Analysis);
        Assert.StartsWith("Show my stock", merged.Prompt);
        Assert.Contains("You asked: \"Do you want stock quantity or stock value?\" → the user answered: \"Value\" (meaning: Stock value)", merged.Prompt);
        Assert.Contains("a later answer replaces an earlier one", merged.Prompt);
    }

    [Fact]
    public void A_chosen_date_replaces_the_ambiguous_text()
    {
        var (original, answers) = ClarificationFlow.ApplyDate("Sales on 01/09/2026 by item", new List<ClarificationAnswer>(), "01/09/2026", "1 Sep 2026");
        Assert.Equal("Sales on 1 Sep 2026 by item", original);
        Assert.Empty(answers);
    }

    [Theory]
    [InlineData(ChatStatus.Success, "answer")]
    [InlineData(ChatStatus.NoResults, "answer")]
    [InlineData(ChatStatus.Clarification, "clarification")]
    [InlineData(ChatStatus.Unsupported, "info")]
    [InlineData(ChatStatus.InvalidQuery, "blocked")]
    [InlineData(ChatStatus.ProviderError, "error")]
    [InlineData(ChatStatus.DatabaseError, "error")]
    [InlineData(ChatStatus.Timeout, "error")]
    public void Only_real_failures_are_errors(ChatStatus status, string expected)
        => Assert.Equal(expected, ResponseTypes.For(status));

    [Fact]
    public void History_keeps_the_newest_turns_within_the_budget()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 8; i++)
        {
            history.Add(new ChatMessage { Role = MessageRole.User, Content = $"question {i} " + new string('x', 700) });
            history.Add(new ChatMessage { Role = MessageRole.Assistant, Status = ChatStatus.Success, QueryJson = "{\"type\":\"query\",\"n\":" + i + ",\"p\":\"" + new string('y', 2500) + "\"}" });
        }
        var turns = PromptBuilder.HistoryTurns(history, 12_000);
        Assert.True(turns.Count is >= 2 and < 16);
        Assert.Contains("question 7", turns[^2].Content);                 // newest turn always kept
        Assert.DoesNotContain(turns, t => t.Content.Contains("question 0"));
        Assert.Equal("user", turns[0].Role);
    }

    [Fact]
    public void Clarification_turns_are_shown_to_the_model_as_questions()
    {
        var turns = PromptBuilder.HistoryTurns(new List<ChatMessage>
        {
            new() { Role = MessageRole.User, Content = "Show my stock" },
            new() { Role = MessageRole.Assistant, Status = ChatStatus.Clarification, Content = "Do you want stock quantity or stock value?", ClarificationOptions = new() { "Stock quantity", "Stock value" } },
            new() { Role = MessageRole.User, Content = "Value", ResolvedQuestion = "Show my stock — Stock value" },
            new() { Role = MessageRole.Assistant, Status = ChatStatus.Success, QueryJson = "{\"type\":\"query\"}" }
        }, 12_000);
        Assert.Equal(4, turns.Count);
        Assert.Contains("\"type\":\"clarify\"", turns[1].Content);
        Assert.Contains("Stock value", turns[1].Content);
        Assert.Equal("Question: Show my stock — Stock value", turns[2].Content);
    }
}

/// <summary>Complete conversations through the orchestrator (in-memory store, scripted model).</summary>
public class ConversationClarificationTests
{
    private const string SalesQuery = """
    {"type":"query","operation":"aggregate","collection":"Sales","pipeline":[
      {"$match":{"Status":{"$ne":"Cancelled"}}},
      {"$group":{"_id":null,"totalSales":{"$sum":"$NetAmount"}}},
      {"$project":{"_id":0,"totalSales":1}}],
     "explanation":"Total","visualization":"kpi"}
    """;

    private const string AskQuantityOrValue =
        """{"type":"clarify","question":"Do you want stock quantity or stock value?","options":["Stock quantity","Stock value"],"missing":"quantity or value"}""";

    private static Harness New()
    {
        var h = new Harness();
        h.Executor.Handler = (_, _) => new List<JsonObject> { new() { ["totalSales"] = 1500m } };
        return h;
    }

    private static Task<ChatResponse> Send(Harness h, string message, string? conversationId = null, string? replyTo = null, string? clientId = null)
        => h.Orchestrator.RunAsync(new ChatRequest { Message = message, ConversationId = conversationId, ReplyToMessageId = replyTo, ClientMessageId = clientId },
            NullChatEventSink.Instance, default);

    private static string LastPrompt(Harness h) => h.Provider.Requests.Where(r => r.JsonMode).Last().Messages[^1].Content;

    [Fact]
    public async Task Value_continues_the_stock_request()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");

        Assert.Equal(ChatStatus.Clarification, first.Status);
        Assert.Equal("clarification", first.ResponseType);
        Assert.True(first.Success);                                      // a question, not an error
        Assert.Equal(new[] { "Stock quantity", "Stock value" }, first.Clarification!.Options);
        Assert.Equal(1, first.Clarification.Step);

        h.Provider.QueryResponses.Enqueue(req =>
        {
            var last = req.Messages[^1].Content;
            Assert.StartsWith("Question: Show my stock", last);
            Assert.Contains("\"Value\" (meaning: Stock value)", last);
            // the earlier question is in the history as a clarification, not as "could not be answered"
            Assert.Contains(req.Messages, m => m.Role == "assistant" && m.Content.Contains("\"type\":\"clarify\""));
            Assert.DoesNotContain(req.Messages, m => m.Content.Contains("previous question could not be answered"));
            return SalesQuery;
        });
        var second = await Send(h, "Value", first.ConversationId, first.MessageId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Equal(first.ConversationId, second.ConversationId);
        var user = h.Store.Messages.Where(m => m.Role == MessageRole.User).ToList();
        Assert.Equal("Value", user[1].Content);                           // what the customer typed is kept
        Assert.Equal("Show my stock — Stock value", user[1].ResolvedQuestion);
        Assert.Equal("Show my stock — Stock value", h.Store.Logs.Last().ResolvedQuestion);
        Assert.Null(h.States.Peek(first.ConversationId)!.Pending);       // request completed
        Assert.Contains(h.Provider.Requests.Where(r => !r.JsonMode), r => r.Messages.Any(m => m.Content.Contains("Show my stock — Stock value")));
    }

    [Theory]
    [InlineData("വില", "Stock value")]
    [InlineData("value mathi", "Stock value")]
    [InlineData("എണ്ണം", "Stock quantity")]
    [InlineData("Quantity", "Stock quantity")]
    [InlineData("Both", "Stock quantity and Stock value")]
    public async Task Malayalam_and_mixed_replies_are_understood(string reply, string meaning)
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "എന്റെ stock കാണിക്കൂ");
        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        var second = await Send(h, reply, first.ConversationId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Contains($"(meaning: {meaning})", LastPrompt(h));
    }

    [Fact]
    public async Task Several_clarification_steps_keep_every_answer()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"For which date?"}""");
        var second = await Send(h, "Value", first.ConversationId);

        Assert.Equal(ChatStatus.Clarification, second.Status);
        Assert.Equal(2, second.Clarification!.Step);
        var pending = h.States.Peek(first.ConversationId)!.Pending!;
        Assert.Equal("Show my stock", pending.OriginalQuestion);
        Assert.Single(pending.Answers);

        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        var third = await Send(h, "Yesterday", first.ConversationId);

        Assert.Equal(ChatStatus.Success, third.Status);
        var prompt = LastPrompt(h);
        Assert.Contains("\"Value\" (meaning: Stock value)", prompt);
        Assert.Contains("You asked: \"For which date?\" → the user answered: \"Yesterday\"", prompt);
        Assert.Equal("Show my stock — Stock value; Yesterday", h.Store.Messages.Where(m => m.Role == MessageRole.User).Last().ResolvedQuestion);
    }

    [Fact]
    public async Task A_correction_is_added_and_the_later_answer_wins()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"For which date?"}""");
        await Send(h, "Value", first.ConversationId);
        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        await Send(h, "No, I meant quantity for today", first.ConversationId);

        var prompt = LastPrompt(h);
        Assert.Contains("\"Value\"", prompt);
        Assert.Contains("\"No, I meant quantity for today\"", prompt);
        Assert.Contains("a later answer replaces an earlier one", prompt);
    }

    [Fact]
    public async Task Cancel_drops_the_open_request_without_the_model()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");
        var calls = h.Provider.Requests.Count;

        var cancel = await Send(h, "cancel", first.ConversationId);

        Assert.Equal(ChatStatus.Success, cancel.Status);
        Assert.Contains("cancelled", cancel.Answer);
        Assert.Equal(calls, h.Provider.Requests.Count);
        Assert.Null(h.States.Peek(first.ConversationId)!.Pending);
    }

    [Fact]
    public async Task A_new_question_is_not_forced_into_the_open_clarification()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");
        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);

        var next = await Send(h, "Show my sales this month", first.ConversationId);

        Assert.Equal(ChatStatus.Success, next.Status);
        Assert.Equal("Question: Show my sales this month", LastPrompt(h).Split("\n\n")[0]);
        Assert.DoesNotContain("Answers the user gave", LastPrompt(h));
        Assert.Null(h.Store.Messages.Where(m => m.Role == MessageRole.User).Last().ResolvedQuestion);
        Assert.Null(h.States.Peek(first.ConversationId)!.Pending);
    }

    [Fact]
    public async Task Follow_ups_use_the_completed_request()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");
        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        await Send(h, "Value", first.ConversationId);

        h.Provider.QueryResponses.Enqueue(req =>
        {
            Assert.Contains(req.Messages, m => m.Role == "user" && m.Content == "Question: Show my stock — Stock value");
            Assert.Contains(req.Messages, m => m.Role == "assistant" && m.Content.Contains("\"collection\":\"Sales\""));
            return SalesQuery;
        });
        var followUp = await Send(h, "Show the same for last month", first.ConversationId);
        Assert.Equal(ChatStatus.Success, followUp.Status);
    }

    [Fact]
    public async Task Follow_ups_work_when_history_is_not_saved()
    {
        var h = New();
        h.Store.Settings.Chat.SaveConversations = false;
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock", "local-conversation-1");
        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        var second = await Send(h, "Value", first.ConversationId);

        Assert.Equal("local-conversation-1", second.ConversationId);
        Assert.Contains("(meaning: Stock value)", LastPrompt(h));

        h.Provider.QueryResponses.Enqueue(req =>
        {
            Assert.Contains(req.Messages, m => m.Content == "Question: Show my stock — Stock value");
            return SalesQuery;
        });
        await Send(h, "Only last month", first.ConversationId);
    }

    [Fact]
    public async Task A_customer_or_ledger_is_selected_from_the_list()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"Which customer do you mean: Al Noor Trading, Al Noor Stores?"}""");
        var first = await Send(h, "Balance of Al Noor");
        Assert.Equal(new[] { "Al Noor Trading", "Al Noor Stores" }, first.Clarification!.Options);

        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        await Send(h, "the first one", first.ConversationId);
        Assert.Contains("(meaning: Al Noor Trading)", LastPrompt(h));
    }

    [Fact]
    public async Task An_invalid_date_is_asked_again_and_replaced()
    {
        var h = New();
        var first = await Send(h, "Sales on 31/02/2026");
        Assert.Equal(ChatStatus.Clarification, first.Status);
        Assert.Contains("isn't a valid date", first.Answer);
        Assert.Empty(h.Provider.Requests);

        var unclear = await Send(h, "hmm", first.ConversationId);         // does not settle the date
        Assert.Equal(ChatStatus.Clarification, unclear.Status);
        Assert.Empty(h.Provider.Requests);

        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        var done = await Send(h, "28/02/2026", first.ConversationId);
        Assert.Equal(ChatStatus.Success, done.Status);
        Assert.StartsWith("Question: Sales on 28/02/2026", LastPrompt(h));
    }

    [Fact]
    public async Task A_short_reply_without_context_is_not_guessed()
    {
        var h = New();
        var r = await Send(h, "Value");
        Assert.Equal(ChatStatus.Clarification, r.Status);
        Assert.Contains("not sure what", r.Answer);
        Assert.Empty(h.Provider.Requests);
    }

    [Fact]
    public async Task An_expired_question_is_explained()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");
        h.States.Mutate(first.ConversationId, s => s.Pending!.AskedAt = DateTime.UtcNow.AddHours(-2));
        var calls = h.Provider.Requests.Count;

        var r = await Send(h, "Value", first.ConversationId);

        Assert.Equal(ChatStatus.Clarification, r.Status);
        Assert.Contains("Show my stock", r.Answer);
        Assert.Contains("complete request", r.Answer);
        Assert.Equal(calls, h.Provider.Requests.Count);
        Assert.Null(h.States.Peek(first.ConversationId)!.Pending);
    }

    [Fact]
    public async Task A_reply_to_an_older_question_is_not_merged()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");
        var calls = h.Provider.Requests.Count;

        var r = await Send(h, "Value", first.ConversationId, replyTo: "66d000000000000000000999");

        Assert.Equal(ChatStatus.Clarification, r.Status);
        Assert.Contains("Do you want stock quantity or stock value?", r.Answer);
        Assert.Equal(calls, h.Provider.Requests.Count);
        Assert.NotNull(h.States.Peek(first.ConversationId)!.Pending);   // still open (asked again)
    }

    [Fact]
    public async Task A_failed_turn_keeps_the_open_question()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");

        h.Provider.FailQuery = true;
        var failed = await Send(h, "Value", first.ConversationId);
        Assert.Equal(ChatStatus.ProviderError, failed.Status);
        Assert.Equal("error", failed.ResponseType);
        Assert.NotNull(h.States.Peek(first.ConversationId)!.Pending);

        h.Provider.FailQuery = false;
        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        var retry = await Send(h, "Value", first.ConversationId);
        Assert.Equal(ChatStatus.Success, retry.Status);
        Assert.Contains("(meaning: Stock value)", LastPrompt(h));
    }

    [Fact]
    public async Task A_duplicate_message_is_answered_once()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        var first = await Send(h, "Sales this month?", clientId: "client-msg-1");
        var calls = h.Provider.Requests.Count;
        var messages = h.Store.Messages.Count;

        var again = await Send(h, "Sales this month?", first.ConversationId, clientId: "client-msg-1");

        Assert.Equal(first.MessageId, again.MessageId);
        Assert.Equal(calls, h.Provider.Requests.Count);
        Assert.Equal(messages, h.Store.Messages.Count);
    }

    [Fact]
    public async Task A_second_message_while_the_first_is_running_is_refused()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        var first = await Send(h, "Sales this month?");
        await h.States.TryBeginTurnAsync(first.ConversationId, h.User.CompanyId, h.User.UserId, null, "other-turn", DateTime.UtcNow, TimeSpan.FromMinutes(5), default);

        var ex = await Assert.ThrowsAsync<ChatConversationException>(() => Send(h, "And last month?", first.ConversationId));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(ChatConversationException.Busy, ex.Code);
    }

    [Fact]
    public async Task Another_users_conversation_is_never_continued()
    {
        var h = New();
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");

        h.User.UserId = "66b000000000000000000002";
        var ex = await Assert.ThrowsAsync<ChatConversationException>(() => Send(h, "Value", first.ConversationId));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal(ChatConversationException.NotFound, ex.Code);
    }

    [Fact]
    public async Task Another_tenants_unsaved_conversation_id_is_refused()
    {
        var h = New();
        h.Store.Settings.Chat.SaveConversations = false;
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        await Send(h, "Show my stock", "shared-id");

        h.User.CompanyId = TestData.CompanyB;
        var ex = await Assert.ThrowsAsync<ChatConversationException>(() => Send(h, "Value", "shared-id"));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Too_many_questions_ask_for_the_complete_request()
    {
        var h = New();
        h.Store.Settings.Chat.MaxClarificationSteps = 1;
        h.Provider.QueryResponses.Enqueue(_ => AskQuantityOrValue);
        var first = await Send(h, "Show my stock");
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"For which date?"}""");

        var second = await Send(h, "Value", first.ConversationId);

        Assert.Equal(ChatStatus.Clarification, second.Status);
        Assert.Contains("complete request in one message", second.Answer);
        Assert.Null(h.States.Peek(first.ConversationId)!.Pending);
    }

    [Fact]
    public async Task Business_term_clarification_continues_with_the_choice()
    {
        var h = New();
        var first = await Send(h, "Show party list");
        Assert.Equal(ChatStatus.Clarification, first.Status);
        Assert.Empty(h.Provider.Requests);

        h.Provider.QueryResponses.Enqueue(_ => SalesQuery);
        var second = await Send(h, "customers", first.ConversationId);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Contains("Show party list", LastPrompt(h));
        Assert.Equal("Show party list — customers", h.Store.Messages.Where(m => m.Role == MessageRole.User).Last().ResolvedQuestion);
    }
}
