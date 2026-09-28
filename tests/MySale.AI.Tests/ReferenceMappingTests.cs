using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.BusinessData;

namespace MySale.AI.Tests;

/// <summary>ID/GUID → record mapping: ids are kept, verified records are named, nothing is guessed.</summary>
public class ReferenceMappingTests
{
    private const string Customer1 = "680abc42566d7d2000000001";
    private const string Customer2 = "680abc42566d7d2000000002";
    private const string Item1 = "680def42566d7d2000000001";
    private const string Branch1 = "680fff42566d7d2000000001";

    private readonly FakeExecutor _executor = new();
    private readonly QueryEngine _engine;
    private readonly IReadOnlyList<CollectionSchema> _schema = SampleSchemaCatalog.Build();

    public ReferenceMappingTests()
    {
        var tenant = new TenantOptions();
        _engine = new QueryEngine(new FakeSchema(), new MqlValidator(), new TenantQueryGuard(tenant), tenant, _executor, new FakeUser());
    }

    private static PreparedQuery Prepared(string collection, string pipeline = "[{\"$match\":{}}]") => new()
    {
        Query = TestData.Parse($$"""{"type":"query","operation":"aggregate","collection":"{{collection}}","pipeline":{{pipeline}}}"""),
        Validation = new MqlValidationResult { Collection = collection, Pipeline = (JsonArray)JsonNode.Parse(pipeline)! }
    };

    private static ExecutedQuery Executed(params JsonObject[] rows)
        => new() { Rows = rows.ToList(), Columns = rows[0].Select(kv => kv.Key).ToList() };

    private void Records(Dictionary<string, List<JsonObject>> byCollection)
        => _executor.Handler = (collection, _) => byCollection.TryGetValue(collection, out var docs) ? docs : new List<JsonObject>();

    [Fact]
    public async Task Every_id_column_is_mapped_to_its_record_and_the_ids_are_kept()
    {
        Records(new()
        {
            ["Customers"] = new() { new JsonObject { ["_id"] = Customer1, ["CustomerName"] = "ABC Trading" } },
            ["Items"] = new() { new JsonObject { ["_id"] = Item1, ["ItemName"] = "Samsung Galaxy" } },
            ["Branches"] = new() { new JsonObject { ["_id"] = Branch1, ["BranchName"] = "Kannur" } }
        });
        var executed = Executed(new JsonObject { ["customerId"] = Customer1, ["itemId"] = Item1, ["branchId"] = Branch1, ["sales"] = 4500.0 });

        var r = await _engine.ResolveReferencesAsync(Prepared("SaleItems"), executed, _schema, new AppSettings(), default);

        Assert.True(r.Changed);
        Assert.Equal(new[] { "customerName", "itemName", "branchName", "sales" }, r.Result.Columns);
        var row = r.Result.Rows.Single();
        Assert.Equal("ABC Trading", row["customerName"]!.GetValue<string>());
        Assert.Equal("Samsung Galaxy", row["itemName"]!.GetValue<string>());
        Assert.Equal("Kannur", row["branchName"]!.GetValue<string>());
        Assert.Equal(Customer1, row["customerId"]!.GetValue<string>()); // id preserved
        Assert.Equal(Item1, row["itemId"]!.GetValue<string>());
        Assert.Equal(3, r.Notes.Count);
    }

    [Fact]
    public async Task Ids_that_cannot_be_verified_keep_the_id_and_get_no_invented_name()
    {
        Records(new() { ["Customers"] = new() { new JsonObject { ["_id"] = Customer1, ["CustomerName"] = "ABC Trading" } } });
        var executed = Executed(
            new JsonObject { ["customerId"] = Customer1, ["sales"] = 10.0 },
            new JsonObject { ["customerId"] = Customer2, ["sales"] = 20.0 });

        var r = await _engine.ResolveReferencesAsync(Prepared("Sales"), executed, _schema, new AppSettings(), default);

        Assert.Equal("ABC Trading", r.Result.Rows[0]["customerName"]!.GetValue<string>());
        Assert.Equal(Customer2, r.Result.Rows[1]["customerName"]!.GetValue<string>()); // shows the id, not a guess
        Assert.Equal(Customer2, r.Result.Rows[1]["customerId"]!.GetValue<string>());
        Assert.Contains("1/2 resolved", r.Notes.Single());
    }

    [Fact]
    public async Task Guid_references_are_mapped_by_exact_match()
    {
        const string guid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        Records(new() { ["Customers"] = new() { new JsonObject { ["_id"] = guid.ToUpperInvariant(), ["CustomerName"] = "Gulf Stores" } } });
        var executed = Executed(new JsonObject { ["customerGuid"] = guid, ["sales"] = 5.0 });

        var r = await _engine.ResolveReferencesAsync(Prepared("Sales"), executed, _schema, new AppSettings(), default);

        Assert.Equal("Gulf Stores", r.Result.Rows.Single()["customerName"]!.GetValue<string>());
        Assert.Equal(guid, r.Result.Rows.Single()["customerGuid"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unrelated_fields_are_not_guessed_from_a_partial_match()
    {
        // "refCode" has no relationship and no matching collection: a single coincidental hit in a master
        // collection is not enough to name the column.
        var ids = new[] { "680aaa42566d7d2000000001", "680aaa42566d7d2000000002", "680aaa42566d7d2000000003" };
        Records(new() { ["Customers"] = new() { new JsonObject { ["_id"] = ids[0], ["CustomerName"] = "Someone" } } });
        var executed = Executed(ids.Select(id => new JsonObject { ["refCode"] = id, ["amount"] = 1.0 }).ToArray());

        var r = await _engine.ResolveReferencesAsync(Prepared("Sales"), executed, _schema, new AppSettings(), default);

        Assert.False(r.Changed);
        Assert.Equal(new[] { "refCode", "amount" }, r.Result.Columns);
    }

    [Fact]
    public async Task Invoice_references_show_the_document_number()
    {
        Records(new() { ["Sales"] = new() { new JsonObject { ["_id"] = Customer1, ["InvoiceNo"] = "INV-A-000123", ["CustomerName"] = "ABC" } } });
        var executed = Executed(new JsonObject { ["saleId"] = Customer1, ["lineTotal"] = 5.0 });

        var r = await _engine.ResolveReferencesAsync(Prepared("SaleItems"), executed, _schema, new AppSettings(), default);

        Assert.Equal("INV-A-000123", r.Result.Rows.Single()["saleName"]!.GetValue<string>());
    }

    [Fact]
    public void Answer_prompt_asks_for_names_and_forbids_invented_names()
    {
        var messages = new PromptBuilder().BuildAnswerMessages(
            new PromptContext("A", "AED", "Asia/Dubai", DateAnchors.Compute(DateTime.UtcNow, "Asia/Dubai"), 200, "CompanyId"), "top customers", null,
            new List<JsonObject> { new() { ["customerName"] = "ABC Trading", ["customerId"] = Customer1 } }, false, 10);
        var system = messages[0].Content;
        Assert.Contains("never invent or guess a name", system);
        Assert.Contains("customerId + customerName", system);
    }
}
