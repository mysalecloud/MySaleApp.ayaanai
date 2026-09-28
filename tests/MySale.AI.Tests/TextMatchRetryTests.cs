using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Tests;

/// <summary>A query that returns nothing because of text case/spacing ("SUNDRY DEBTORS" vs "Sundry Debtors ") is retried tolerantly.</summary>
public class TextMatchRetryTests
{
    private const string SundryDebtorsQuery = """
    {"type":"query","operation":"aggregate","collection":"Ledger","pipeline":[
      {"$match":{"groupName":"SUNDRY DEBTORS"}},
      {"$group":{"_id":"$groupId","groupName":{"$first":"$groupName"},"ledgerCount":{"$sum":1},"totalBalance":{"$sum":"$ledgerBalance"}}},
      {"$project":{"_id":0,"groupId":"$_id","groupName":1,"ledgerCount":1,"totalBalance":{"$round":["$totalBalance",2]}}},
      {"$sort":{"totalBalance":-1}}],
     "explanation":"Sundry debtors balance","visualization":"table"}
    """;

    private static readonly CollectionSchema Ledger = new()
    {
        Name = "Ledger",
        Fields = new()
        {
            new FieldSchema { Name = "_id", Type = "objectId" },
            new FieldSchema { Name = "CompanyId", Type = "objectId" },
            new FieldSchema { Name = "ledgerName", Type = "string" },
            new FieldSchema { Name = "groupName", Type = "string" },
            new FieldSchema { Name = "groupId", Type = "objectId" },
            new FieldSchema { Name = "ledgerBalance", Type = "double" }
        }
    };

    private static (QueryEngine Engine, MqlValidationContext Context) Engine()
    {
        var tenant = new TenantOptions();
        var engine = new QueryEngine(new FakeSchema(), new MqlValidator(), new TenantQueryGuard(tenant), tenant, new FakeExecutor(), new FakeUser());
        var context = new MqlValidationContext { Collections = new[] { Ledger }, TenantField = "CompanyId", MaxRecords = 200, MaxStages = 12 };
        return (engine, context);
    }

    [Fact]
    public void Exact_text_match_is_relaxed_to_a_case_and_space_insensitive_whole_value_match()
    {
        var (engine, context) = Engine();
        var prepared = engine.Prepare(TestData.Parse(SundryDebtorsQuery), context, new AppSettings());
        Assert.True(prepared.IsExecutable, string.Join("; ", prepared.Validation.Errors));

        var relaxed = engine.RelaxTextMatches(prepared, context, new AppSettings(), out var fields);

        Assert.NotNull(relaxed);
        Assert.True(relaxed!.IsExecutable);
        Assert.Equal(new[] { "groupName" }, fields);
        var match = relaxed.Query.Pipeline![0]!["$match"]!["groupName"]!;
        Assert.Equal("^\\s*SUNDRY\\s+DEBTORS\\s*$", match["$regex"]!.GetValue<string>());
        Assert.Equal("i", match["$options"]!.GetValue<string>());
        // Business logic unchanged: same grouping, output and tenant scoping
        Assert.Contains("\"$groupId\"", relaxed.Mql);
        Assert.Contains("CompanyId", relaxed.ScopedPipeline!.ToJsonString());
    }

    [Fact]
    public void Ids_numbers_and_dates_are_never_relaxed()
    {
        var (engine, context) = Engine();
        var q = TestData.Parse("""
            {"type":"query","operation":"aggregate","collection":"Ledger","pipeline":[
              {"$match":{"groupId":{"$oid":"680abc42566d7d2000000001"},"ledgerName":"1001"}}]}
            """);
        var prepared = engine.Prepare(q, context, new AppSettings());
        Assert.Null(engine.RelaxTextMatches(prepared, context, new AppSettings(), out _));
    }

    [Fact]
    public async Task Empty_result_is_retried_and_the_retry_is_used_and_logged()
    {
        var h = new Harness();
        // Sample schema: Customers.CustomerName is a string field.
        h.Provider.QueryResponses.Enqueue(_ => """
            {"type":"query","operation":"find","collection":"Customers","filter":{"CustomerName":"ABC TRADING"},
             "projection":{"_id":0,"CustomerName":1,"Balance":1},"limit":10,"visualization":"table"}
            """);
        h.Executor.Handler = (_, pipeline) => pipeline.ToJsonString().Contains("$regex")
            ? new List<JsonObject> { new() { ["CustomerName"] = "ABC Trading", ["Balance"] = 4500.0 } }
            : new List<JsonObject>();
        h.Provider.Answer = _ => "ABC Trading has a balance of AED 4,500.00.";

        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Balance of ABC TRADING?" }, NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, r.Status);
        Assert.Contains("$regex", h.Executor.LastCall!.Value.Pipeline.ToJsonString());
        var log = Assert.Single(h.Store.Logs);
        Assert.Contains("$regex", log.FinalMql);
    }
}
