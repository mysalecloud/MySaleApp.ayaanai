using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.BusinessData;
using Xunit;

namespace MySale.AI.Tests;

/// <summary>Fixtures of an item master and its stock movements (shapes as stored by MySaleBooks).</summary>
internal static class Items
{
    public const string Pepsi = "6740000000000000000000d1";
    public const string Pepsi15 = "6740000000000000000000d2";
    public const string Wash = "6740000000000000000000d3";
    public const string WashProduct = "6740000000000000000000d4";
    public const string Box = "6750000000000000000000e1";
    public const string Pcs = "6750000000000000000000e2";
    public const string Main = "6760000000000000000000f1";
    public const string Back = "6760000000000000000000f2";

    public static JsonObject Item(string id, string name, string code, string type = "product", decimal inc = 1.25m, decimal exc = 1.19m,
        string? unit = Pcs, bool? tracked = null) => new()
    {
        ["_id"] = id, ["itemName"] = name, ["itemCode"] = code, ["itemType"] = type, ["unitId"] = unit,
        ["taxIncAmount"] = inc, ["taxExcAmount"] = exc, ["landingCost"] = 0.9m, ["costingType"] = "FIFO",
        ["isTrackInventory"] = tracked is null ? null : JsonValue.Create(tracked.Value)
    };

    public static bool IsIdentifierQuery(string json) => json.Contains("\"itemCode\":{\"$regex\"");
    public static bool IsNameQuery(string json) => json.Contains("\"itemName\":{\"$regex\"") && !json.Contains("\"$and\"");
    public static bool IsPartialQuery(string json) => json.Contains("\"$and\"");

    /// <summary>Unit master: Pcs (no sub unit) and Box = 12 Pcs.</summary>
    public static List<JsonObject> Units(string json)
    {
        if (json.Contains(Box)) return new() { new() { ["_id"] = Box, ["unitName"] = "Box", ["unitShortName"] = "Box", ["parentId"] = Pcs, ["conversion"] = 12m } };
        if (json.Contains(Pcs)) return new() { new() { ["_id"] = Pcs, ["unitName"] = "Pieces", ["unitShortName"] = "Pcs", ["parentId"] = "0", ["conversion"] = 1m } };
        return new();
    }

    public static List<JsonObject> Locations() => new()
    {
        new() { ["_id"] = Main, ["stockLocationName"] = "Main Store" },
        new() { ["_id"] = Back, ["stockLocationName"] = "Back Store" }
    };
}

public class ItemStockReportTests
{
    private static readonly DateAnchors Today = DateAnchors.Compute(new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc), "Asia/Dubai", 1, true);
    private static MqlQuery Plan(string json) => MqlParser.Parse(json).Query!;

    private static (MySaleBooksReports Reports, FakeExecutor Executor, List<string> Seen) Build(Func<string, string, List<JsonObject>> handler)
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
        return (new MySaleBooksReports(engine), executor, seen);
    }

    private static Task<ReportOutcome> Run(MySaleBooksReports reports, string plan, Application.Stores.StoreScope? store = null)
        => reports.RunAsync(Plan(plan), Msb.Context(store), Today, new AppSettings(), 2, default, null, "AED");

    private static List<JsonObject> StockByLocation() => new()
    {
        new() { ["_id"] = Items.Main, ["received"] = 100m, ["issued"] = 40m, ["movements"] = 6 },
        new() { ["_id"] = Items.Back, ["received"] = 20m, ["issued"] = 5m, ["movements"] = 2 }
    };

    [Fact]
    public async Task Stock_of_an_item_code_is_the_current_quantity_by_warehouse()
    {
        var (reports, _, seen) = Build((c, json) =>
            c == "Item" && Items.IsIdentifierQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi 500 ML", "P-500") }
            : c == "StockMaster" ? StockByLocation() : new());

        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"P-500"}""");

        Assert.Equal("ok", r.Kind);
        Assert.Equal("Current stock of “Pepsi 500 ML” (P-500): 75 Pcs across 2 warehouses.", r.Message);
        Assert.Equal(new[] { "Main Store", "Back Store", "Total" }, r.Rows.Select(x => x["warehouse"]!.GetValue<string>()));
        Assert.Equal(new[] { 60m, 15m, 75m }, r.Rows.Select(x => x["quantity"]!.GetValue<decimal>()));
        var stock = seen.Single(s => s.StartsWith("StockMaster"));
        Assert.Contains("\"itemId\":\"" + Items.Pepsi + "\"", stock);                 // the verified id, not a name search
        Assert.Contains("$transactionPipe", stock);                                     // Σ IN stockIn − Σ OUT stockOut
        Assert.Contains("\"isCanceled\":{\"$ne\":true}", stock);                         // cancelled movements excluded
        Assert.DoesNotContain(seen, s => s.StartsWith("Item") && Items.IsNameQuery(s)); // exact code: no name search needed
    }

    [Fact]
    public async Task Barcodes_of_alternate_units_and_ean_codes_are_identifiers()
    {
        var (reports, _, seen) = Build((c, json) => c == "Item" ? new() : new());
        await Run(reports, """{"type":"report","report":"itemStock","item":"8901234567890"}""");
        var identifiers = seen.First(s => s.StartsWith("Item"));
        foreach (var field in new[] { "itemCode", "barcode", "eancode", "partNumber", "partNumberDetails.partNumber", "alternateUnits.barcode", "alternateUnits.alternateItemCode" })
            Assert.Contains("\"" + field + "\":{\"$regex\":\"^\\\\s*8901234567890\\\\s*$\"", identifiers);
    }

    [Theory]
    [InlineData("pepsi 500ml", "Pepsi 500 ML", true)]
    [InlineData("Pepsi500ml", "Pepsi 500 ML", true)]
    [InlineData("PEPSI 500 ML", "Pepsi-500-ML", true)]
    [InlineData("pepsi 500ml", "Pepsi 1.5 L", false)]                // a different variant never matches
    [InlineData("pepsi 500ml", "Pepsi 5000 ML", false)]
    [InlineData("pepsi 500ml", "Pepsi 500 ML Can", false)]
    [InlineData("rice 1,5 kg", "Rice 1.5 KG", true)]
    [InlineData("പാൽ 1 ലിറ്റർ", "പാൽ 1 ലിറ്റർ", true)]
    public void Name_matching_ignores_spacing_but_keeps_variants(string typed, string stored, bool expected)
        => Assert.Equal(expected, Regex.IsMatch(stored, MySaleBooksReports.NamePattern(typed), RegexOptions.IgnoreCase));

    [Fact]
    public async Task Several_matches_are_listed_with_codes_and_ids()
    {
        var (reports, _, _) = Build((c, json) => c == "Item" && Items.IsNameQuery(json)
            ? new() { Items.Item(Items.Pepsi, "Pepsi", "P-500"), Items.Item(Items.Pepsi15, "Pepsi", "P-1500") }
            : new());

        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"Pepsi"}""");

        Assert.Equal("clarify", r.Kind);
        Assert.Equal("Several items match “Pepsi”. Which one do you mean?", r.Message);
        Assert.Equal(new[] { "Pepsi (code P-500)", "Pepsi (code P-1500)" }, r.Options);
        Assert.Equal(new[] { Items.Pepsi, Items.Pepsi15 }, r.OptionIds);
        Assert.Equal("item", r.AnswerArgument);
    }

    [Fact]
    public async Task A_product_and_a_service_with_the_same_name_are_offered_with_their_type()
    {
        var (reports, _, seen) = Build((c, json) => c == "Item" && Items.IsNameQuery(json)
            ? new() { Items.Item(Items.WashProduct, "Car Wash", "CW-1"), Items.Item(Items.Wash, "Car Wash", "SRV-1", "service") }
            : new());

        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"car wash"}""");

        Assert.Equal("clarify", r.Kind);
        Assert.Contains("matches both a product and a service", r.Message);
        Assert.Equal(new[] { "Car Wash (code CW-1, product)", "Car Wash (code SRV-1, service)" }, r.Options);
        Assert.DoesNotContain(seen, s => s.StartsWith("StockMaster"));
    }

    [Fact]
    public async Task An_unknown_item_is_not_found_never_zero_stock()
    {
        var (reports, _, seen) = Build((_, _) => new());
        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"Unicorn Soap"}""");
        Assert.Equal("notfound", r.Kind);
        Assert.Equal("I couldn't find an item or service matching “Unicorn Soap”. Please check the name, item code or barcode.", r.Message);
        Assert.DoesNotContain(seen, s => s.StartsWith("StockMaster"));
        Assert.Contains(seen, s => Items.IsPartialQuery(s));                            // the fallback was tried
    }

    [Fact]
    public async Task A_single_partial_match_is_confirmed_not_chosen()
    {
        var (reports, _, seen) = Build((c, json) => c == "Item" && Items.IsPartialQuery(json)
            ? new() { Items.Item(Items.Pepsi, "Pepsi Max 500 ML", "PM-500") }
            : new());
        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"pepsi 500"}""");
        Assert.Equal("clarify", r.Kind);
        Assert.Equal("I couldn't find an item named exactly “pepsi 500”. Did you mean Pepsi Max 500 ML (code PM-500)?", r.Message);
        Assert.Equal(new[] { Items.Pepsi }, r.OptionIds);
        Assert.DoesNotContain(seen, s => s.StartsWith("StockMaster"));
    }

    [Fact]
    public async Task An_item_without_movements_exists_with_zero_stock()
    {
        var (reports, _, _) = Build((c, json) => c == "Item" && Items.IsNameQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi 500 ML", "P-500") } : new());
        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"Pepsi 500 ML"}""", Msb.Store());
        Assert.Equal("ok", r.Kind);
        Assert.Equal("“Pepsi 500 ML” (P-500) exists, but it has no stock movements in Muscat yet, so its stock is 0 Pcs.", r.Message);
    }

    [Fact]
    public async Task A_service_has_no_stock()
    {
        var (reports, _, seen) = Build((c, json) => c == "Item" && Items.IsNameQuery(json)
            ? new() { Items.Item(Items.Wash, "Car Wash", "SRV-1", "service", inc: 5m, exc: 5m) } : new());
        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"Car Wash"}""");
        Assert.Equal("ok", r.Kind);
        Assert.Equal("“Car Wash” (SRV-1) is a service, so it has no stock. Its rate is AED 5.00.", r.Message);
        Assert.DoesNotContain(seen, s => s.StartsWith("StockMaster"));
    }

    [Fact]
    public async Task Stock_follows_the_selected_store_like_the_stock_screen()
    {
        var (reports, _, seen) = Build((c, json) =>
            c == "Item" && Items.IsIdentifierQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi 500 ML", "P-500") }
            : c == "StockMaster" ? new() { new() { ["_id"] = Items.Main, ["received"] = 30m, ["issued"] = 12m, ["movements"] = 3 } } : new());
        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"P-500"}""", Msb.Store());
        Assert.Equal("Current stock of “Pepsi 500 ML” (P-500) in Muscat: 18 Pcs.", r.Message);
        // Store rows plus branch "0" rows (opening stock for all stores), as the MySaleBooks Stock screen.
        Assert.Contains("\"branchId\":{\"$in\":[\"" + Msb.StoreA + "\",\"0\",null]}", seen.Single(s => s.StartsWith("StockMaster")));
    }

    [Fact]
    public async Task Stock_in_boxes_is_shown_with_the_sub_unit()
    {
        var (reports, _, _) = Build((c, json) =>
            c == "Item" && Items.IsIdentifierQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi 500 ML", "P-500", unit: Items.Box) }
            : c == "StockMaster" ? new() { new() { ["_id"] = Items.Main, ["received"] = 3m, ["issued"] = 0.5m, ["movements"] = 2 } } : new());
        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"P-500"}""");
        Assert.Equal("Current stock of “Pepsi 500 ML” (P-500): 2 Box 6 Pcs.", r.Message);
        Assert.Equal(2.5m, r.Rows[0]["quantity"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData(2.5, "2 Box 6 Pcs")]
    [InlineData(3, "3 Box")]
    [InlineData(0.25, "3 Pcs")]
    [InlineData(-1.5, "-1 Box 6 Pcs")]
    public void Quantities_use_main_and_sub_units(double quantity, string expected)
        => Assert.Equal(expected, MySaleBooksReports.FormatQuantity((decimal)quantity, new MySaleBooksReports.UnitInfo("Box", 12m, "Pcs")));

    [Fact]
    public async Task Stock_value_uses_the_fifo_cost_of_the_stock_screen()
    {
        var (reports, _, seen) = Build((c, json) =>
            c == "Item" && Items.IsIdentifierQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi 500 ML", "P-500") }
            : c == "StockMaster" && json.Contains("balanceStock") ? new() { new() { ["amount"] = 150m, ["layers"] = 100m } }
            : c == "StockMaster" ? StockByLocation() : new());
        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"P-500","measure":"value"}""");
        Assert.Equal("ok", r.Kind);
        Assert.Contains("Stock value: AED 112.50 at AED 1.50 per Pcs (FIFO cost", r.Message);
        Assert.Contains(seen, s => s.StartsWith("StockMaster") && s.Contains("\"transactionPipe\":\"IN\"") && s.Contains("balanceStock"));
    }

    [Fact]
    public async Task Historical_stock_counts_opening_entries_and_movements_up_to_the_day()
    {
        var (reports, _, seen) = Build((c, json) =>
            c == "Item" && Items.IsIdentifierQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi 500 ML", "P-500") }
            : c == "StockMaster" ? StockByLocation() : new());
        var r = await Run(reports, """{"type":"report","report":"itemStock","item":"P-500","asOf":"2026-09-10"}""");
        Assert.StartsWith("Stock of “Pepsi 500 ML” (P-500) on 10 Sep 2026: 75 Pcs", r.Message);
        var stock = seen.Single(s => s.StartsWith("StockMaster"));
        Assert.Contains("\"transactionType\":\"OPSTOCK\"", stock);
        Assert.Contains("2026-09-11T00:00:00", stock);                                   // up to the end of 10 Sep (wall clock)
    }

    [Fact]
    public async Task Item_details_show_the_selling_price()
    {
        var (reports, _, _) = Build((c, json) => c == "Item" && Items.IsIdentifierQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi 500 ML", "P-500") } : new());
        var r = await Run(reports, """{"type":"report","report":"itemDetails","item":"P-500"}""");
        Assert.Equal("“Pepsi 500 ML” (P-500) is a product. Selling price: AED 1.25 incl. tax (AED 1.19 excl. tax) per Pcs.", r.Message);
        Assert.Contains(r.Rows, x => x["detail"]!.GetValue<string>() == "Item code" && x["value"]!.GetValue<string>() == "P-500");
    }

    [Fact]
    public async Task A_chosen_item_id_is_used_directly()
    {
        var (reports, _, seen) = Build((c, json) =>
            c == "Item" && json.Contains(Items.Pepsi15) ? new() { Items.Item(Items.Pepsi15, "Pepsi", "P-1500") }
            : c == "StockMaster" ? StockByLocation() : new());
        var r = await Run(reports, $$"""{"type":"report","report":"itemStock","item":"Pepsi (code P-1500)","itemId":"{{Items.Pepsi15}}"}""");
        Assert.Equal("ok", r.Kind);
        Assert.Single(seen, s => s.StartsWith("Item"));                                  // one lookup by id, no name search
    }
}

public class ItemStockConversationTests
{
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

    [Fact]
    public async Task Stock_of_an_item_is_answered_without_asking_quantity_or_value()
    {
        var h = WithMySaleBooks((c, json) =>
            c == "Item" && Items.IsNameQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi 500 ML", "P-500") }
            : c == "StockMaster" ? new() { new() { ["_id"] = Items.Main, ["received"] = 30m, ["issued"] = 12m, ["movements"] = 3 } } : new());
        h.Provider.QueryResponses.Enqueue(req =>
        {
            Assert.Contains("\"report\":\"itemStock\"", req.Messages[0].Content);              // rule + example in the prompt
            return """{"type":"report","report":"itemStock","item":"Pepsi 500 ML"}""";
        });

        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "What is the stock of Pepsi 500 ML?" }, NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, r.Status);
        Assert.Equal("Current stock of “Pepsi 500 ML” (P-500): 18 Pcs.", r.Answer);
        Assert.Single(h.Provider.Requests);                                                   // no answer-model call: verified sentence
        Assert.Equal(1, r.Data!.Count);
    }

    [Fact]
    public async Task Choosing_from_the_list_reruns_the_report_with_the_verified_id()
    {
        var h = WithMySaleBooks((c, json) =>
            c == "Item" && json.Contains(Items.Pepsi15) ? new() { Items.Item(Items.Pepsi15, "Pepsi", "P-1500") }
            : c == "Item" && Items.IsNameQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi", "P-500"), Items.Item(Items.Pepsi15, "Pepsi", "P-1500") }
            : c == "StockMaster" ? new() { new() { ["_id"] = Items.Main, ["received"] = 10m, ["issued"] = 4m, ["movements"] = 2 } } : new());
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"report","report":"itemStock","item":"Pepsi"}""");

        var first = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Pepsi stock" }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.Clarification, first.Status);
        Assert.Equal(new[] { "Pepsi (code P-500)", "Pepsi (code P-1500)" }, first.Clarification!.Options);
        var calls = h.Provider.Requests.Count;

        var second = await h.Orchestrator.RunAsync(new ChatRequest { Message = "the second one", ConversationId = first.ConversationId, ReplyToMessageId = first.MessageId },
            NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, second.Status);
        Assert.Equal("Current stock of “Pepsi” (P-1500): 6 Pcs.", second.Answer);
        Assert.Equal(calls, h.Provider.Requests.Count);                                       // no model call: the original request is kept
        Assert.Contains(Items.Pepsi15, h.Store.Logs.Last().FinalMql);
    }

    [Fact]
    public async Task A_typed_code_answers_the_item_question()
    {
        var h = WithMySaleBooks((c, json) =>
            c == "Item" && Items.IsIdentifierQuery(json) && json.Contains("P-1500") ? new() { Items.Item(Items.Pepsi15, "Pepsi", "P-1500") }
            : c == "Item" && Items.IsNameQuery(json) ? new() { Items.Item(Items.Pepsi, "Pepsi", "P-500"), Items.Item(Items.Pepsi15, "Pepsi", "P-1500") }
            : c == "StockMaster" ? new() { new() { ["_id"] = Items.Main, ["received"] = 10m, ["issued"] = 4m, ["movements"] = 2 } } : new());
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"report","report":"itemStock","item":"Pepsi"}""");
        var first = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Pepsi stock" }, NullChatEventSink.Instance, default);

        var second = await h.Orchestrator.RunAsync(new ChatRequest { Message = "P-1500", ConversationId = first.ConversationId }, NullChatEventSink.Instance, default);

        Assert.Equal("Current stock of “Pepsi” (P-1500): 6 Pcs.", second.Answer);
    }

    [Fact]
    public async Task Not_found_is_a_normal_reply_not_zero_stock()
    {
        var h = WithMySaleBooks((_, _) => new());
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"report","report":"itemStock","item":"Unicorn Soap"}""");
        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Stock of Unicorn Soap" }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.NoResults, r.Status);
        Assert.Equal("answer", r.ResponseType);
        Assert.Contains("couldn't find", r.Answer);
        Assert.DoesNotContain("0 ", r.Answer);
    }

    [Fact]
    public void Generated_stock_queries_without_rows_do_not_claim_zero_stock()
    {
        var pipeline = JsonNode.Parse("""[{"$group":{"_id":null,"stock":{"$sum":"$stockIn"}}}]""")!.AsArray();
        var z = ZeroResultPolicy.Describe("What is the stock of Pepsi?", pipeline, "StockMaster", null, "AED");
        Assert.Null(z.ZeroRow);
        Assert.Contains("not the same as zero stock", z.Message);
    }
}
