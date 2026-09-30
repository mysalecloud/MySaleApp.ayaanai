using System.Text.Json.Nodes;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.BusinessData;
using Xunit;

namespace MySale.AI.Tests;

/// <summary>
/// The server stock engine (report stockSummary): every view is validated by the real validator, tenant-scoped,
/// store-scoped and cancellation-filtered, and uses the one stock definition of <see cref="StockSemantics"/>.
/// </summary>
public class StockEngineTests
{
    private static readonly DateAnchors Today = DateAnchors.Compute(new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc), "Asia/Dubai", 1, true);
    private const string Rice = "6740000000000000000000a1";
    private const string Soap = "6740000000000000000000a2";
    private const string Salt = "6740000000000000000000a3";

    private static MqlQuery Plan(string json) => MqlParser.Parse(json).Query!;

    /// <summary>Facet result of the per-item balance pipeline: summary + listed rows.</summary>
    private static JsonObject Facet(JsonObject summary, params JsonObject[] rows) => new()
    {
        ["summary"] = new JsonArray(summary),
        ["rows"] = new JsonArray(rows.Select(r => (JsonNode)r).ToArray())
    };

    private static JsonObject ItemRow(string id, string name, decimal qty, decimal value = 0, decimal level = 0, decimal sold = 0) => new()
    {
        ["_id"] = id, ["itemName"] = name, ["itemCode"] = name[..3].ToUpperInvariant(), ["unitId"] = Items.Pcs, ["quantity"] = qty,
        ["value"] = value, ["unitCost"] = qty == 0 ? 0 : value / qty, ["level"] = level, ["sold"] = sold
    };

    private static (MySaleBooksReports Reports, List<string> Seen) Build(Func<string, string, List<JsonObject>> handler)
    {
        var (engine, executor) = Msb.Engine();
        var seen = new List<string>();
        executor.Handler = (collection, pipeline) =>
        {
            var json = pipeline.ToJsonString();
            seen.Add(collection + " " + json);
            return collection switch
            {
                "Unit" => Items.Units(json),
                "StockLocation" => Items.Locations(),
                _ => handler(collection, json)
            };
        };
        return (new MySaleBooksReports(engine), seen);
    }

    private static Task<ReportOutcome> Run(MySaleBooksReports reports, string plan, Application.Stores.StoreScope? store = null)
        => reports.RunAsync(Plan(plan), Msb.Context(store), Today, new AppSettings(), 3, default, null, "OMR");

    private static List<JsonObject> StandardStock(string collection, string json)
    {
        if (collection == "StockMaster" && json.Contains("$facet"))
            return new()
            {
                Facet(new JsonObject { ["products"] = 3, ["inStock"] = 2, ["zero"] = 1, ["negative"] = 0, ["withLevel"] = 2, ["low"] = 1, ["value"] = 1250.5m, ["noCost"] = 0, ["sold"] = 1, ["notSold"] = 1 },
                    ItemRow(Rice, "Rice 5kg", 40, 1000m, level: 10),
                    ItemRow(Soap, "Soap bar", 5, 250.5m, level: 10))
            };
        if (collection == "Item" && json.Contains("\"moves\""))
            return new() { Facet(new JsonObject { ["n"] = 1 }, new JsonObject { ["_id"] = Salt, ["itemName"] = "Salt 1kg", ["itemCode"] = "SAL", ["unitId"] = Items.Pcs, ["quantity"] = 0 }) };
        return new();
    }

    [Theory]
    [InlineData("current")]
    [InlineData("value")]
    [InlineData("low")]
    [InlineData("out")]
    [InlineData("negative")]
    [InlineData("highest")]
    [InlineData("lowest")]
    [InlineData("byCategory")]
    [InlineData("byWarehouse")]
    [InlineData("movement")]
    [InlineData("fastMoving")]
    [InlineData("slowMoving")]
    [InlineData("nonMoving")]
    public async Task Every_view_passes_the_validator_and_the_server_scopes(string view)
    {
        var (reports, seen) = Build(StandardStock);
        var r = await Run(reports, $$"""{"type":"report","report":"stockSummary","view":"{{view}}"}""", Msb.Store());

        Assert.True(r.Kind == "ok", $"{view}: {r.Kind} {r.Message}");                   // "blocked" = the validator rejected the pipeline
        var stock = seen.First(s => s.StartsWith("StockMaster"));
        Assert.Contains("\"isCanceled\":{\"$ne\":true}", stock);                        // cancelled movements excluded (server)
        Assert.Contains(Msb.StoreA, stock);                                             // selected store …
        Assert.Contains("\"0\"", stock);                                                // … plus shared branch "0" rows (Stock screen)
        Assert.Contains(TestData.CompanyA, stock);                                      // tenant filter
        Assert.DoesNotContain("\"$qty\"", stock);                                        // never the entered-unit qty
    }

    [Fact]
    public async Task Current_stock_is_received_minus_issued_over_all_movements_of_tracked_products()
    {
        var (reports, seen) = Build(StandardStock);
        var r = await Run(reports, """{"type":"report","report":"stockSummary","view":"current"}""");

        var stock = seen.First(s => s.StartsWith("StockMaster"));
        Assert.Contains("{\"$cond\":[{\"$eq\":[\"$transactionPipe\",\"IN\"]},\"$stockIn\",0]}", stock);
        Assert.Contains("{\"$cond\":[{\"$eq\":[\"$transactionPipe\",\"OUT\"]},\"$stockOut\",0]}", stock);
        Assert.DoesNotContain("transactionDate\":{\"$lt\"", stock);                     // current stock: no date filter
        Assert.Contains("\"item.itemType\":{\"$in\":[\"product\",\"rawMaterial\"]}", stock); // services / KOT / untracked excluded
        Assert.Contains("\"item.isTrackInventory\":{\"$ne\":false}", stock);
        Assert.Contains("\"item.isKotItem\":{\"$ne\":true}", stock);
        Assert.StartsWith("Current stock: 2 in stock, 1 at zero and 0 negative", r.Message);
        Assert.Contains("OMR 1,250.500", r.Message);                                    // company currency and decimals
        Assert.Equal("40 Pcs", r.Rows[0]["stock"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stock_as_of_a_day_counts_movements_up_to_that_day_plus_all_opening_stock()
    {
        var (reports, seen) = Build(StandardStock);
        var r = await Run(reports, """{"type":"report","report":"stockSummary","view":"current","asOf":"2026-09-01"}""");

        var stock = seen.First(s => s.StartsWith("StockMaster"));
        Assert.Contains("{\"transactionType\":\"OPSTOCK\"}", stock);
        Assert.Contains("\"transactionDate\":{\"$lt\":{\"$date\":\"2026-09-02T00:00:00", stock);    // wall-clock day boundary
        Assert.Contains("not recalculated", r.Message);                                  // no historical value
        Assert.DoesNotContain("balanceStock", stock);
    }

    [Fact]
    public async Task Low_stock_without_any_configured_level_is_not_invented()
    {
        var (reports, _) = Build((c, json) => c == "StockMaster" && json.Contains("$facet")
            ? new() { Facet(new JsonObject { ["products"] = 3, ["inStock"] = 3, ["zero"] = 0, ["negative"] = 0, ["withLevel"] = 0, ["low"] = 0, ["value"] = 10m }) }
            : new());
        var r = await Run(reports, """{"type":"report","report":"stockSummary","view":"low"}""");

        Assert.Equal("ok", r.Kind);
        Assert.StartsWith("No reorder level or minimum stock level is set for any product", r.Message);
        Assert.Empty(r.Rows);
    }

    [Fact]
    public async Task Low_stock_uses_the_reorder_level_strictly_below_and_above_zero()
    {
        var (reports, seen) = Build(StandardStock);
        var r = await Run(reports, """{"type":"report","report":"stockSummary","view":"low"}""");

        var stock = seen.First(s => s.StartsWith("StockMaster"));
        Assert.Contains("\"$item.reOrderLevel\"", stock);
        Assert.Contains("\"$item.minimumStockQty\"", stock);
        Assert.Contains("{\"$match\":{\"isLow\":true}}", stock);
        Assert.StartsWith("1 product is low in stock", r.Message);
        Assert.Contains("reorderLevel", r.Columns);
    }

    [Fact]
    public async Task Out_of_stock_includes_tracked_products_that_never_moved()
    {
        var (reports, seen) = Build(StandardStock);
        var r = await Run(reports, """{"type":"report","report":"stockSummary","view":"out"}""");

        var items = seen.Single(s => s.StartsWith("Item") && s.Contains("\"moves\""));
        Assert.Contains("\"foreignField\":\"itemId\"", items);
        Assert.Contains("{\"moves\":{\"$size\":0}}", items);
        Assert.Contains("\"isCanceled\":{\"$ne\":true}", items);
        Assert.StartsWith("2 products are out of stock (stock exactly 0)", r.Message);
        Assert.Contains("1 of them never had a stock movement", r.Message);
    }

    [Fact]
    public async Task Stock_value_follows_the_stock_screen_costing()
    {
        var (reports, seen) = Build(StandardStock);
        var r = await Run(reports, """{"type":"report","report":"stockSummary","view":"value"}""");

        var stock = seen.First(s => s.StartsWith("StockMaster"));
        Assert.Contains("\"$balanceStock\",\"$landedCost\"", stock);                     // FIFO layers
        Assert.Contains("\"$item.landingCost\"", stock);                                 // fallback cost
        Assert.Contains("\"isCombo\"", stock);                                           // combos out of the total
        Assert.DoesNotContain("taxIncAmount", stock);                                    // never the selling price
        Assert.StartsWith("Total stock value: OMR 1,250.500 for 2 products in stock", r.Message);
    }

    [Fact]
    public async Task Moving_analysis_uses_net_sales_in_the_period_and_says_when_the_period_was_defaulted()
    {
        var (reports, seen) = Build(StandardStock);
        var r = await Run(reports, """{"type":"report","report":"stockSummary","view":"slowMoving"}""");

        var stock = seen.First(s => s.StartsWith("StockMaster"));
        Assert.Contains("\"SALE\"", stock);
        Assert.Contains("\"SALE_RETURN\"", stock);
        Assert.Contains("the last 30 days; no period was given", r.Message);

        var (reports2, seen2) = Build(StandardStock);
        await Run(reports2, """{"type":"report","report":"stockSummary","view":"fastMoving","from":"2026-09-01","to":"2026-09-28"}""");
        Assert.Contains("2026-09-01T00:00:00", seen2.First(s => s.StartsWith("StockMaster")));
    }

    [Fact]
    public async Task An_unknown_category_is_not_found_not_zero()
    {
        var (reports, _) = Build((c, json) => new());
        var r = await Run(reports, """{"type":"report","report":"stockSummary","view":"current","category":"Toys"}""");
        Assert.Equal("notfound", r.Kind);
        Assert.Contains("Toys", r.Message);
    }

    [Fact]
    public async Task No_movements_at_all_is_said_plainly()
    {
        var (reports, _) = Build((c, json) => c == "StockMaster" && json.Contains("$facet")
            ? new() { Facet(new JsonObject { ["products"] = 0, ["inStock"] = 0, ["zero"] = 0, ["negative"] = 0 }) } : new());
        var r = await Run(reports, """{"type":"report","report":"stockSummary","view":"current"}""");
        Assert.StartsWith("There are no stock movements for stock-tracked products", r.Message);
    }

    [Fact]
    public void Every_stored_transaction_type_has_one_direction()
    {
        foreach (var type in new[] { "OPSTOCK", "PURCHASE", "SALE_RETURN", "DELIVERY_NOTE_RECEIPT" })
            Assert.Equal(StockDirection.In, StockSemantics.Find(type)!.Direction);
        foreach (var type in new[] { "SALE", "PURCHASE_RETURN", "DELIVERY_NOTE" })
            Assert.Equal(StockDirection.Out, StockSemantics.Find(type)!.Direction);
        foreach (var type in new[] { "STOCK_ADJUSTMENT", "STOCK_TRANSFER_INTERNAL", "STOCK_TRANSFER_EXTERNAL" })
            Assert.Equal(StockDirection.Adjustment, StockSemantics.Find(type)!.Direction);
        Assert.Equal(("stockIn", 1), StockSemantics.QuantityOf("IN"));
        Assert.Equal(("stockOut", -1), StockSemantics.QuantityOf("OUT"));
        var prompt = MySaleBooksDomain.PromptRules(MySaleBooksCatalog.Build());
        foreach (var t in StockSemantics.Types) Assert.Contains(t.Type, prompt);          // prompt generated from the same table
        Assert.Contains("\"report\":\"stockSummary\"", prompt);
    }
}

/// <summary>Canonical stock intents: many wordings, one calculation; item questions are never captured.</summary>
public class StockIntentTests
{
    private static readonly DateAnchors Today = DateAnchors.Compute(new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc), "Asia/Dubai", 1, true);

    private static StockIntentMatch? Route(string q) => StockIntents.Route(q, Today, QuestionDates.Parse(q, Today.LocalToday, "DMY"));

    [Theory]
    [InlineData("Current Stock", "STOCK_CURRENT")]
    [InlineData("Show stock", "STOCK_CURRENT")]
    [InlineData("Show current stock.", "STOCK_CURRENT")]
    [InlineData("What is my stock?", "STOCK_CURRENT")]
    [InlineData("What is my item stock?", "STOCK_CURRENT")]
    [InlineData("Stock on Hand", "STOCK_CURRENT")]
    [InlineData("Available Stock", "STOCK_CURRENT")]
    [InlineData("My Stock", "STOCK_CURRENT")]
    [InlineData("Inventory Balance", "STOCK_CURRENT")]
    [InlineData("Stock today", "STOCK_CURRENT")]
    [InlineData("Low Stock", "STOCK_LOW")]
    [InlineData("Low Stock Items", "STOCK_LOW")]
    [InlineData("Products Running Low", "STOCK_LOW")]
    [InlineData("Which products are low in stock?", "STOCK_LOW")]
    [InlineData("Which items are low in stock?", "STOCK_LOW")]
    [InlineData("Stock കുറവുള്ള products ഏതൊക്കെയാണ്?", "STOCK_LOW")]
    [InlineData("Out of Stock", "STOCK_OUT")]
    [InlineData("Zero Stock", "STOCK_OUT")]
    [InlineData("Items Out of Stock", "STOCK_OUT")]
    [InlineData("Which products have negative stock?", "STOCK_NEGATIVE")]
    [InlineData("What is the total stock value?", "STOCK_VALUE")]
    [InlineData("What is my current inventory value?", "STOCK_VALUE")]
    [InlineData("Which product categories have the most stock?", "STOCK_BY_CATEGORY")]
    [InlineData("Warehouse wise stock", "STOCK_BY_WAREHOUSE")]
    [InlineData("Which products have the highest stock?", "STOCK_HIGHEST")]
    [InlineData("Which products sold the most quantity this month?", "PRODUCTS_FAST_MOVING")]
    [InlineData("Which products had the lowest sales this month?", "PRODUCTS_SLOW_MOVING")]
    [InlineData("Show products with no sales in the last 30 days.", "PRODUCTS_NON_MOVING")]
    [InlineData("Dead stock", "PRODUCTS_NON_MOVING")]
    [InlineData("Stock as of 1 September 2026", "STOCK_AS_OF")]
    [InlineData("Stock movement between 1 September 2026 and 20 September 2026", "STOCK_MOVEMENT")]
    [InlineData("Show stock movement this month.", "STOCK_MOVEMENT")]
    public void Wordings_map_to_one_canonical_intent(string question, string intent)
        => Assert.Equal(intent, Route(question)?.Intent);

    [Theory]
    [InlineData("What is the stock of iPhone 15?")]
    [InlineData("Show stock for Item A.")]
    [InlineData("How many units of ABC do I have?")]
    [InlineData("Pepsi stock")]
    [InlineData("What is the stock value of Pepsi?")]
    [InlineData("Stock of Installation Service")]
    [InlineData("Show the top 10 selling products this month.")]
    [InlineData("What are today's total sales?")]
    [InlineData("Which customers have outstanding balances?")]
    [InlineData("Show categories")]
    public void Questions_about_one_item_or_another_subject_are_not_captured(string question)
        => Assert.Null(Route(question));

    [Fact]
    public void Same_intent_means_same_plan()
    {
        var a = Route("Show stock")!.Plan.Arguments!.ToJsonString();
        var b = Route("What is my current stock?")!.Plan.Arguments!.ToJsonString();
        var c = Route("Stock on hand")!.Plan.Arguments!.ToJsonString();
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Dates_change_the_stock_semantics()
    {
        Assert.Equal("2026-09-01", Route("Stock as of 1 September 2026")!.Plan.Arguments!["asOf"]!.GetValue<string>());
        var movement = Route("Stock movement between 1 September 2026 and 20 September 2026")!.Plan.Arguments!;
        Assert.Equal("2026-09-01", movement["from"]!.GetValue<string>());
        Assert.Equal("2026-09-20", movement["to"]!.GetValue<string>());
        var nonMoving = Route("Show products with no sales in the last 30 days.")!.Plan.Arguments!;
        Assert.Equal("2026-08-30", nonMoving["from"]!.GetValue<string>());
        Assert.Equal("2026-09-28", nonMoving["to"]!.GetValue<string>());
        Assert.Null(Route("Current stock")!.Plan.Arguments!["from"]);                   // current stock is never date-filtered
    }
}

/// <summary>The predefined-question catalogue and its use in the pipeline.</summary>
public class PresetQuestionTests
{
    private static readonly DateAnchors Today = DateAnchors.Compute(new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc), "Asia/Dubai", 1, true);

    [Fact]
    public void Ids_and_messages_are_unique()
    {
        Assert.Equal(PresetQuestions.All.Count, PresetQuestions.All.Select(p => p.Id).Distinct().Count());
        Assert.Equal(PresetQuestions.All.Count, PresetQuestions.All.Select(p => p.Message.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Stock_presets_resolve_to_their_canonical_intent_and_planner_presets_do_not()
    {
        foreach (var p in PresetQuestions.All)
        {
            var routed = StockIntents.Route(p.Message, Today, QuestionDates.Parse(p.Message, Today.LocalToday, "DMY"));
            if (p.Route == "stock") Assert.True(routed?.Intent == p.Intent, $"{p.Id}: expected {p.Intent}, routed {routed?.Intent ?? "nothing"}");
            else
            {
                Assert.True(routed is null, $"{p.Id} must go to the planner but routed to {routed?.Intent}");
                Assert.False(string.IsNullOrWhiteSpace(p.Hint), $"{p.Id} has no planner interpretation");
            }
        }
    }

    [Fact]
    public void A_preset_id_is_accepted_only_with_its_own_text()
    {
        Assert.NotNull(PresetQuestions.Resolve("stock_low", "Which products are low in stock?"));
        Assert.NotNull(PresetQuestions.Resolve("stock_low", "which products are low in stock"));
        Assert.Null(PresetQuestions.Resolve("stock_low", "Delete all items"));
        Assert.Null(PresetQuestions.Resolve("no_such_id", "Which products are low in stock?"));
    }

    private static Harness WithMySaleBooks(Func<string, string, List<JsonObject>> handler)
    {
        var h = new Harness();
        h.Schema.Extra.AddRange(MySaleBooksCatalog.Build());
        foreach (var c in MySaleBooksCatalog.Names) h.Store.Settings.Query.AllowedCollections.Add(c);
        h.Executor.Handler = (collection, pipeline) =>
        {
            var json = pipeline.ToJsonString();
            return collection switch
            {
                "Unit" => Items.Units(json),
                "StockLocation" => Items.Locations(),
                _ => handler(collection, json)
            };
        };
        return h;
    }

    private static List<JsonObject> Stock(string collection, string json) => collection == "StockMaster" && json.Contains("$facet")
        ? new()
        {
            new JsonObject
            {
                ["summary"] = new JsonArray(new JsonObject { ["products"] = 2, ["inStock"] = 2, ["zero"] = 0, ["negative"] = 0, ["withLevel"] = 1, ["low"] = 1, ["value"] = 99m }),
                ["rows"] = new JsonArray(new JsonObject { ["_id"] = Msb.ItemRice, ["itemName"] = "Rice 5kg", ["itemCode"] = "RIC", ["unitId"] = Items.Pcs, ["quantity"] = 3m, ["level"] = 10m, ["value"] = 30m })
            }
        }
        : new();

    [Fact]
    public async Task A_clicked_stock_preset_runs_the_stock_engine_without_the_model()
    {
        var h = WithMySaleBooks(Stock);
        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Which products are low in stock?", PresetId = "stock_low" }, NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, r.Status);
        Assert.StartsWith("1 product is low in stock", r.Answer);
        Assert.Empty(h.Provider.Requests);                                            // no planner, no answer model
        Assert.Equal("STOCK_LOW", h.States.Peek(r.ConversationId)!.Intent);
    }

    [Fact]
    public async Task Typed_wordings_of_current_stock_give_the_same_answer()
    {
        var answers = new List<string>();
        foreach (var q in new[] { "Show stock", "What is my current stock?", "Stock on hand" })
        {
            var h = WithMySaleBooks(Stock);
            var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = q }, NullChatEventSink.Instance, default);
            Assert.Empty(h.Provider.Requests);
            answers.Add(r.Answer);
        }
        Assert.Single(answers.Distinct());
    }

    [Fact]
    public async Task A_planner_preset_gives_the_model_its_fixed_interpretation()
    {
        var h = WithMySaleBooks((_, _) => new() { new JsonObject { ["total"] = 10m } });
        h.Provider.QueryResponses.Enqueue(req =>
        {
            Assert.Contains("Predefined question RECEIVABLES_BY_CUSTOMER", req.Messages[^1].Content);
            Assert.Contains("Ledger balance (not unpaid invoices)", req.Messages[^1].Content);
            return """{"type":"unsupported","reason":"test"}""";
        });
        await h.Orchestrator.RunAsync(new ChatRequest { Message = "Which customers have outstanding balances?", PresetId = "customers_outstanding" }, NullChatEventSink.Instance, default);
        Assert.Single(h.Provider.Requests);
    }

    [Fact]
    public async Task A_preset_click_is_a_new_request_even_while_a_question_is_open()
    {
        var h = WithMySaleBooks(Stock);
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"clarify","question":"Which customer?"}""");
        var first = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Show customer balance" }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.Clarification, first.Status);

        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Which products are out of stock?", PresetId = "stock_out", ConversationId = first.ConversationId },
            NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.Success, r.Status);
        Assert.Contains("out of stock", r.Answer);
        Assert.NotNull(h.States.Peek(first.ConversationId)!.Suspended);             // the open question is kept for "continue"
    }
}
