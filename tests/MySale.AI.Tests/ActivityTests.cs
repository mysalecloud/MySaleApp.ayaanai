using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Domain;

namespace MySale.AI.Tests;

public class ActivityTests
{
    private const string TotalSalesQuery = """
    {"type":"query","operation":"aggregate","collection":"Sales","pipeline":[
      {"$match":{"Status":{"$ne":"Cancelled"}}},
      {"$group":{"_id":null,"totalSales":{"$sum":"$NetAmount"},"invoiceCount":{"$sum":1}}},
      {"$project":{"_id":0,"totalSales":1,"invoiceCount":1}}],
     "explanation":"Total sales","visualization":"kpi"}
    """;

    private static ChatRequest Ask(string q) => new() { Message = q };

    [Fact]
    public async Task Successful_request_records_the_full_lifecycle()
    {
        var sink = new MemActivitySink();
        var h = new Harness(sink);
        h.Provider.QueryResponses.Enqueue(_ => TotalSalesQuery);
        h.Provider.Answer = _ => "Your total sales are AED 1,500.00 from 12 invoices.";

        var r = await h.Orchestrator.RunAsync(Ask("What are my total sales?"), NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, r.Status);
        var a = Assert.Single(sink.Activities);
        Assert.Equal(ActivityStatuses.Success, a.Status);
        Assert.Null(a.FailedStage);
        Assert.Equal("corr-test-0001", a.CorrelationId);
        Assert.Equal(32, a.ActivityId.Length);
        Assert.Equal(r.ConversationId, a.ConversationId);
        Assert.Equal(r.MessageId, a.MessageId);
        Assert.Equal(r.QueryLogId, a.QueryLogId);
        Assert.StartsWith("t_", a.TenantRef);
        Assert.DoesNotContain(TestData.CompanyA, a.TenantRef);

        // Request
        Assert.Equal("What are my total sales?", a.Request.Question);
        Assert.Equal("en", a.Request.Language);
        Assert.Equal("mysalebooks-web", a.Request.ClientApp);
        Assert.Equal("1.0.212", a.Request.ClientVersion);
        Assert.Equal("session-abc", a.Request.SessionId);

        // AI
        Assert.Equal("Ollama", a.Ai.ProviderName);
        Assert.Equal("llama3.1", a.Ai.Model);
        Assert.Equal(PromptBuilder.Version, a.Ai.PromptVersion);
        Assert.StartsWith("sha256:", a.Ai.SystemPromptRef);
        Assert.Equal("database:aggregate", a.Ai.Intent);
        Assert.Equal("Total sales", a.Ai.Explanation);
        Assert.Equal(150, a.Ai.InputTokens);

        // Generated query (original JSON, before tenant scoping)
        Assert.NotNull(a.Query);
        Assert.Equal("Sales", a.Query!.Collection);
        Assert.Equal("aggregate", a.Query.Operation);
        var generated = JsonNode.Parse(a.Query.GeneratedQueryJson!)!;
        Assert.Equal("$NetAmount", generated["pipeline"]![1]!["$group"]!["totalSales"]!["$sum"]!.GetValue<string>());
        Assert.StartsWith("sha256:", a.Query.QueryHash);
        Assert.Contains("CompanyId", a.Query.ExecutedMql); // executed pipeline is tenant-scoped
        Assert.Single(a.Query.Attempts);

        // Validation + execution + result + response
        Assert.Equal("Approved", a.Validation!.Status);
        Assert.Equal("Success", a.Execution!.Status);
        Assert.Equal("ayaan:corr-test-0001", a.Execution.Comment);
        Assert.Equal(1, a.Execution.DocumentsReturned);
        Assert.Equal(1, a.Result!.Count);
        Assert.True(a.Result.Stored);
        Assert.Contains("totalSales", a.Result.DataJson);
        Assert.Contains("\"sum\":1500", a.Result.SummaryJson);
        Assert.StartsWith("sha256:", a.Result.ResultHash);
        Assert.Equal("Your total sales are AED 1,500.00 from 12 invoices.", a.Response!.Text);
        Assert.Equal("kpi", a.Response.VisualizationType);

        // Timeline + performance
        Assert.Contains(a.Timeline, t => t.Stage == ActivityStages.QueryGeneration);
        Assert.Contains(a.Timeline, t => t.Stage == ActivityStages.QueryValidation);
        Assert.Contains(a.Timeline, t => t.Stage == ActivityStages.MongoExecution);
        Assert.Contains(a.Timeline, t => t.Stage == ActivityStages.ResponseGeneration);
        Assert.Equal(ActivityStages.Completed, a.Timeline[^1].Stage);
        Assert.True(a.Performance.TotalDurationMs >= a.Performance.MongoExecutionDurationMs);

        // Conversation summary
        var c = Assert.Single(sink.Conversations);
        Assert.Equal(r.ConversationId, c.ConversationId);
        Assert.True(c.Succeeded);
    }

    [Fact]
    public async Task Blocked_query_is_recorded_with_the_rejected_query()
    {
        var sink = new MemActivitySink();
        var h = new Harness(sink);
        h.Store.Settings.Ai.MaxRepairAttempts = 0;
        h.Provider.QueryResponses.Enqueue(_ => """{"operation":"aggregate","collection":"Sales","pipeline":[{"$out":"stolen"}]}""");

        var r = await h.Orchestrator.RunAsync(Ask("Copy sales to a new collection"), NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.InvalidQuery, r.Status);
        var a = Assert.Single(sink.Activities);
        Assert.Equal(ActivityStatuses.Rejected, a.Status);
        Assert.Equal(ActivityStages.QueryValidation, a.FailedStage);
        Assert.True(a.Validation!.Blocked);
        Assert.Equal("Blocked", a.Validation.Status);
        Assert.Contains("$out", a.Validation.BlockedOperations);
        Assert.Contains("$out", a.Validation.RejectedQueryJson);
        Assert.Null(a.Execution);
        Assert.Contains(a.Errors, e => e.Type == ActivityErrorTypes.QueryValidationError && e.CorrelationId == "corr-test-0001");
    }

    [Fact]
    public async Task Database_error_is_recorded_at_the_mongodb_stage()
    {
        var sink = new MemActivitySink();
        var h = new Harness(sink);
        h.Provider.QueryResponses.Enqueue(_ => TotalSalesQuery);
        h.Executor.Throw = new QueryExecutionException("connection refused");

        var r = await h.Orchestrator.RunAsync(Ask("Sales?"), NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.DatabaseError, r.Status);
        var a = Assert.Single(sink.Activities);
        Assert.Equal(ActivityStatuses.Failed, a.Status);
        Assert.Equal(ActivityStages.MongoExecution, a.FailedStage);
        Assert.Equal("Failed", a.Execution!.Status);
        var e = Assert.Single(a.Errors);
        Assert.Equal(ActivityErrorTypes.MongoDBError, e.Type);
        Assert.Null(e.StackTrace); // stack traces only when Activity:StoreStackTraces is on
    }

    [Fact]
    public async Task Provider_error_is_recorded_at_the_ai_stage()
    {
        var sink = new MemActivitySink();
        var h = new Harness(sink);
        h.Provider.FailQuery = true;

        await h.Orchestrator.RunAsync(Ask("Sales today?"), NullChatEventSink.Instance, default);

        var a = Assert.Single(sink.Activities);
        Assert.Equal(ActivityStatuses.Failed, a.Status);
        Assert.Equal(ActivityStages.QueryGeneration, a.FailedStage);
        Assert.Equal(ActivityErrorTypes.AIProviderError, a.Errors.Single().Type);
    }

    [Fact]
    public async Task Activity_log_failure_never_breaks_the_ai_request()
    {
        var sink = new MemActivitySink { Throw = true };
        var h = new Harness(sink);
        h.Provider.QueryResponses.Enqueue(_ => TotalSalesQuery);

        var r = await h.Orchestrator.RunAsync(Ask("What are my total sales?"), NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, r.Status);
    }

    [Fact]
    public async Task Disabled_tracking_records_nothing()
    {
        var sink = new MemActivitySink();
        var h = new Harness(sink, new ActivityOptions { Enabled = false });
        h.Provider.QueryResponses.Enqueue(_ => TotalSalesQuery);

        var r = await h.Orchestrator.RunAsync(Ask("What are my total sales?"), NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, r.Status);
        Assert.Empty(sink.Activities);
    }

    [Fact]
    public async Task Results_storage_is_capped_and_can_be_turned_off()
    {
        static List<JsonObject> Rows() => Enumerable.Range(1, 100).Select(i => new JsonObject { ["Customer"] = $"C{i}", ["Total"] = i }).ToList();

        var capped = new MemActivitySink();
        var h1 = new Harness(capped, new ActivityOptions { MaximumDocumentsToStore = 5 });
        h1.Provider.QueryResponses.Enqueue(_ => TotalSalesQuery);
        h1.Executor.Handler = (_, _) => Rows();
        await h1.Orchestrator.RunAsync(Ask("Sales by customer"), NullChatEventSink.Instance, default);
        var r1 = capped.Activities.Single().Result!;
        Assert.Equal(100, r1.Count);
        Assert.Equal(5, r1.StoredDocuments);
        Assert.True(r1.StoredTruncated);
        Assert.Equal(5, JsonNode.Parse(r1.DataJson!)!.AsArray().Count);

        var off = new MemActivitySink();
        var h2 = new Harness(off, new ActivityOptions { StoreFullResults = false });
        h2.Provider.QueryResponses.Enqueue(_ => TotalSalesQuery);
        h2.Executor.Handler = (_, _) => Rows();
        await h2.Orchestrator.RunAsync(Ask("Sales by customer"), NullChatEventSink.Instance, default);
        var r2 = off.Activities.Single().Result!;
        Assert.False(r2.Stored);
        Assert.Null(r2.DataJson);
        Assert.Equal(100, r2.Count);
        Assert.NotNull(r2.ResultHash);
    }

    [Fact]
    public void Redactor_removes_tokens_keys_passwords_and_connection_strings()
    {
        var text = "Authorization: Bearer abcdefghijklmnop123 token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.sig " +
                   "mongodb+srv://user:pass@cluster0.example.net/db password=Secret123 key sk-abcdefghijklmnopqrstuvwx";
        var clean = ActivityRedactor.RedactText(text);

        Assert.DoesNotContain("abcdefghijklmnop123", clean);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", clean);
        Assert.DoesNotContain("user:pass", clean);
        Assert.DoesNotContain("Secret123", clean);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwx", clean);
        Assert.Contains(ActivityRedactor.Redacted, clean);
    }

    [Fact]
    public void Redactor_masks_sensitive_fields_in_json()
    {
        var json = JsonNode.Parse("""
        {"Name":"Ali","Password":"p@ss","api_key":"k-1","Email":"ali@example.com","Total":123456789,
         "Nested":{"AccessToken":"x"},"Internal":"drop me"}
        """);

        var clean = ActivityRedactor.RedactJson(json, excluded: new[] { "Internal" }, maskPersonalData: true)!.AsObject();

        Assert.Equal(ActivityRedactor.Redacted, clean["Password"]!.GetValue<string>());
        Assert.Equal(ActivityRedactor.Redacted, clean["api_key"]!.GetValue<string>());
        Assert.Equal(ActivityRedactor.Redacted, clean["Nested"]!["AccessToken"]!.GetValue<string>());
        Assert.False(clean.ContainsKey("Internal"));
        Assert.DoesNotContain("ali@example.com", clean["Email"]!.GetValue<string>());
        Assert.Equal(123456789, clean["Total"]!.GetValue<int>()); // numbers are kept for debugging
        Assert.Equal("Ali", clean["Name"]!.GetValue<string>());
        Assert.Equal("p@ss", json!["Password"]!.GetValue<string>()); // original untouched
    }

    [Fact]
    public void Tenant_reference_is_stable_and_not_reversible()
    {
        var a = ActivityHashing.TenantRef("customer_db_42", "db:customer_db_42");
        Assert.Equal(a, ActivityHashing.TenantRef("customer_db_42", "anything"));
        Assert.NotEqual(a, ActivityHashing.TenantRef("customer_db_43", null));
        Assert.DoesNotContain("customer", a);
        Assert.Equal(18, a.Length);
    }

    [Theory]
    [InlineData("What are my sales today?", null, "en")]
    [InlineData("ഇന്ന് എന്റെ വിൽപ്പന എത്ര?", null, "ml")]
    [InlineData("ما هي مبيعاتي اليوم؟", null, "ar")]
    [InlineData("anything", "ml", "ml")]
    public void Language_is_detected(string text, string? voice, string expected)
        => Assert.Equal(expected, ActivityRecorder.DetectLanguage(text, voice));

    [Fact]
    public void Retention_days_can_be_set_with_the_environment_variable()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Activity:RetentionDays"] = "30",
            ["AI_ACTIVITY_RETENTION_DAYS"] = "14"
        }).Build();
        Assert.Equal(14, MySale.AI.Infrastructure.DependencyInjection.BindActivityOptions(config).RetentionDays);
    }
}
