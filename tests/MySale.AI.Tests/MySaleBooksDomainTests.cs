using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Stores;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.BusinessData;

namespace MySale.AI.Tests;

// MySaleBooks inventory (StockMaster), accounting (AccountVoucher), currency and date rules verified from the MySaleApp
// report code (see docs/ai/MYSALEBOOKS_DOMAIN.md). The test data mirrors how MySaleBooks writes these documents.

internal static class Msb
{
    public const string StoreA = "6700000000000000000000aa";
    public const string ItemRice = "6710000000000000000000a1";
    public const string LedgerAlNoor = "6720000000000000000000b1";
    public const string CurrencyOmr = "6730000000000000000000c1";

    public static List<CollectionSchema> Schema() => TestData.Schema().Concat(MySaleBooksCatalog.Build()).ToList();

    public static MqlValidationContext Context(StoreScope? store = null, bool wallClock = true) => new()
    {
        Collections = Schema(),
        TenantField = "CompanyId",
        MaxRecords = 200,
        MaxStages = 12,
        Store = store,
        Dates = new DateCoercionContext { TimeZone = wallClock ? TimeZoneInfo.Utc : DateAnchors.ResolveTimeZone("Asia/Dubai") }
    };

    public static (QueryEngine Engine, FakeExecutor Executor) Engine(FakeUser? user = null)
    {
        var tenant = new TenantOptions();
        var executor = new FakeExecutor();
        return (new QueryEngine(new FakeSchema(), new MqlValidator(), new TenantQueryGuard(tenant), tenant, executor, user ?? new FakeUser()), executor);
    }

    public static PreparedQuery Prepare(QueryEngine engine, string json, StoreScope? store = null)
        => engine.Prepare(TestData.Parse(json), Context(store), new AppSettings());

    public static StoreScope Store() => new() { Mode = StoreMode.Selected, StoreId = StoreA, StoreName = "Muscat", Reason = "selected" };
}

public class MySaleBooksSchemaTests
{
    [Fact]
    public void Catalog_describes_the_verified_transaction_collections()
    {
        var schema = MySaleBooksCatalog.Build();
        var stock = schema.Single(c => c.Name == "StockMaster");
        foreach (var f in new[] { "transactionDate", "transactionType", "transactionPipe", "itemId", "stockLocationId", "stockIn", "stockOut", "balanceStock", "landedCost", "isCanceled", "branchId", "Freeqty" })
            Assert.Contains(stock.Fields, x => x.Name == f);
        Assert.Equal("Item._id", stock.Fields.Single(f => f.Name == "itemId").Relationship);
        Assert.Equal("string", stock.Fields.Single(f => f.Name == "itemId").Type);            // ids are stored as strings

        var vouchers = schema.Single(c => c.Name == "AccountVoucher");
        foreach (var f in new[] { "voucherGuId", "voucherDate", "voucherType", "ledgerId", "groupId", "parentGroupId", "debit", "credit", "convertedDebit", "convertedCredit", "currencyFromId", "exchangeRate", "isPosted", "isCanceled" })
            Assert.Contains(vouchers.Fields, x => x.Name == f);
        Assert.DoesNotContain(vouchers.Fields, f => f.Name == "isDebit");                     // not a stored field
    }

    [Theory]
    [InlineData("StockMaster", "transactionDate")]
    [InlineData("AccountVoucher", "voucherDate")]
    [InlineData("Sale", "saleDate")]
    [InlineData("Purchase", "purchaseDate")]
    public void Periods_use_the_business_date_not_createdDate(string collection, string expected)
        => Assert.Equal(expected, QueryEngine.BusinessDateField(MySaleBooksCatalog.Build().Single(c => c.Name == collection)));

    [Fact]
    public void Domain_is_active_only_for_MySaleBooks_databases()
    {
        Assert.True(MySaleBooksDomain.IsActive(Msb.Schema()));
        Assert.False(MySaleBooksDomain.IsActive(TestData.Schema()));
        Assert.NotNull(MySaleBooksDomain.RequiredFilter(Msb.Schema().Single(c => c.Name == "StockMaster")));
        Assert.Null(MySaleBooksDomain.RequiredFilter(TestData.Schema().Single(c => c.Name == "Sales")));
        Assert.Null(MySaleBooksDomain.RequiredFilter(Msb.Schema().Single(c => c.Name == "Item")));  // masters are not status-filtered
    }
}

public class MySaleBooksRuleTests
{
    private static JsonArray P(string json) => (JsonArray)JsonNode.Parse(json)!;

    [Fact]
    public void Naive_stock_sums_are_rejected_because_IN_rows_also_carry_consumption()
    {
        var errors = MySaleBooksDomain.Check("StockMaster", P("""[{"$group":{"_id":"$itemId","qty":{"$sum":{"$subtract":["$stockIn","$stockOut"]}}}}]""")).ToList();
        Assert.Contains(errors, e => e.Contains("transactionPipe"));
    }

    [Fact]
    public void Pipe_conditional_stock_sums_are_accepted()
        => Assert.Empty(MySaleBooksDomain.Check("StockMaster", P("""
            [{"$group":{"_id":"$itemId",
              "received":{"$sum":{"$cond":[{"$eq":["$transactionPipe","IN"]},"$stockIn",0]}},
              "issued":{"$sum":{"$cond":[{"$eq":["$transactionPipe","OUT"]},"$stockOut",0]}}}}]
            """)));

    [Fact]
    public void Issues_summed_on_receipt_rows_are_rejected()
    {
        var errors = MySaleBooksDomain.Check("StockMaster", P("""[{"$match":{"transactionPipe":"IN"}},{"$group":{"_id":null,"out":{"$sum":"$stockOut"}}}]""")).ToList();
        Assert.Contains(errors, e => e.Contains("stockOut must only be summed"));
        Assert.Empty(MySaleBooksDomain.Check("StockMaster", P("""[{"$match":{"transactionPipe":"OUT","transactionType":"SALE"}},{"$group":{"_id":"$itemId","sold":{"$sum":"$stockOut"}}}]""")));
    }

    [Fact]
    public void Balance_stock_and_entered_quantities_are_guarded()
    {
        Assert.NotEmpty(MySaleBooksDomain.Check("StockMaster", P("""[{"$group":{"_id":"$itemId","left":{"$sum":"$balanceStock"}}}]""")));
        Assert.Empty(MySaleBooksDomain.Check("StockMaster", P("""[{"$match":{"transactionPipe":"IN","balanceStock":{"$gt":0}}},{"$group":{"_id":"$itemId","left":{"$sum":"$balanceStock"}}}]""")));
        Assert.Contains(MySaleBooksDomain.Check("StockMaster", P("""[{"$match":{"transactionPipe":"OUT"}},{"$group":{"_id":null,"q":{"$sum":"$qty"}}}]""")), e => e.Contains("qty"));
    }

    [Fact]
    public void Original_currency_amounts_are_only_added_per_currency()
    {
        Assert.NotEmpty(MySaleBooksDomain.Check("AccountVoucher", P("""[{"$group":{"_id":null,"total":{"$sum":"$convertedDebit"}}}]""")));
        Assert.Empty(MySaleBooksDomain.Check("AccountVoucher", P("""[{"$group":{"_id":"$currencyFromId","total":{"$sum":"$convertedDebit"}}}]""")));
        Assert.Empty(MySaleBooksDomain.Check("AccountVoucher", P("""[{"$group":{"_id":null,"total":{"$sum":"$debit"}}}]""")));   // base currency
    }

    [Fact]
    public void Prompt_rules_state_the_verified_formulas()
    {
        var rules = MySaleBooksDomain.PromptRules(Msb.Schema());
        Assert.Contains("\"$transactionPipe\",\"IN\"", rules);
        Assert.Contains("voucherGuId", rules);
        Assert.Contains("15 Sundry Debtors", rules);
        Assert.Contains("16 Sundry Creditors", rules);
        Assert.Contains("OPSTOCK", rules);
        Assert.Contains("never add different currencies", rules);
        Assert.Contains("\"type\":\"clarify\"", rules);
    }
}

public class MySaleBooksScopingTests
{
    [Fact]
    public void Cancelled_documents_are_removed_by_the_server()
    {
        var (engine, _) = Msb.Engine();
        var p = Msb.Prepare(engine, """
            {"type":"query","operation":"aggregate","collection":"StockMaster","pipeline":[
              {"$group":{"_id":"$itemId","received":{"$sum":{"$cond":[{"$eq":["$transactionPipe","IN"]},"$stockIn",0]}}}}]}
            """);
        Assert.True(p.IsExecutable, string.Join("; ", p.Validation.Errors));
        Assert.Contains("\"isCanceled\":{\"$ne\":true}", p.ScopedPipeline!.ToJsonString());
    }

    [Fact]
    public void Joined_transactions_are_status_filtered_too()
    {
        var (engine, _) = Msb.Engine();
        var p = Msb.Prepare(engine, """
            {"type":"query","operation":"aggregate","collection":"Ledger","pipeline":[
              {"$match":{"groupId":15}},{"$addFields":{"ledgerKey":{"$toString":"$_id"}}},
              {"$lookup":{"from":"AccountVoucher","localField":"ledgerKey","foreignField":"ledgerId","as":"lines"}},
              {"$project":{"ledgerName":1,"debit":{"$sum":"$lines.debit"}}}]}
            """);
        Assert.True(p.IsExecutable, string.Join("; ", p.Validation.Errors));
        var lookup = p.ScopedPipeline!.OfType<JsonObject>().Single(s => s.ContainsKey("$lookup"))["$lookup"]!;
        Assert.Contains("isCanceled", lookup["pipeline"]!.ToJsonString());
    }

    [Fact]
    public void Store_filter_is_exact_for_transactions_and_keeps_shared_masters()
    {
        var (engine, _) = Msb.Engine(new FakeUser { IsMySaleBooksUser = true });
        var p = Msb.Prepare(engine, """
            {"type":"query","operation":"aggregate","collection":"StockMaster","pipeline":[
              {"$addFields":{"itemObj":{"$convert":{"input":"$itemId","to":"objectId","onError":null,"onNull":null}}}},
              {"$lookup":{"from":"Item","localField":"itemObj","foreignField":"_id","as":"item"}},
              {"$group":{"_id":"$itemId","received":{"$sum":{"$cond":[{"$eq":["$transactionPipe","IN"]},"$stockIn",0]}}}}]}
            """, Msb.Store());
        Assert.True(p.IsExecutable, string.Join("; ", p.Validation.Errors));
        var json = p.ScopedPipeline!.ToJsonString();
        Assert.Contains("{\"branchId\":\"" + Msb.StoreA + "\"}", json);                         // stock rows of the store only
        var lookup = p.ScopedPipeline!.OfType<JsonObject>().Single(s => s.ContainsKey("$lookup"))["$lookup"]!.ToJsonString();
        Assert.Contains("\"$in\":[\"" + Msb.StoreA + "\",\"0\",null]", lookup);                // shared products (branchId "0") stay visible
    }

    [Fact]
    public void Account_groups_are_never_store_filtered()
    {
        var store = Msb.Store();
        Assert.Null(store.FieldFor(Msb.Schema().Single(c => c.Name == "AccountGroup")));
    }

    [Fact]
    public void Tampered_queries_are_still_blocked()
    {
        var (engine, _) = Msb.Engine();
        Assert.False(Msb.Prepare(engine, """{"type":"query","operation":"aggregate","collection":"AccountVoucher","pipeline":[{"$out":"x"}]}""").IsExecutable);
        Assert.False(Msb.Prepare(engine, """{"type":"query","operation":"aggregate","collection":"StockMaster","pipeline":[{"$lookup":{"from":"AccountVoucher","localField":"a","foreignField":"b","as":"c","pipeline":[]}}]}""").IsExecutable);
    }
}

public class MySaleBooksDateTests
{
    [Fact]
    public void Wall_clock_storage_uses_the_local_date_with_utc_day_boundaries()
    {
        // 29 Sep 2026 02:30 in Dubai = 28 Sep 22:30Z. MySaleBooks stores local wall-clock time as UTC.
        var a = DateAnchors.Compute(new DateTime(2026, 9, 28, 22, 30, 0, DateTimeKind.Utc), "Asia/Dubai", 1, wallClockStorage: true);
        Assert.Equal(new DateOnly(2026, 9, 29), a.LocalToday);
        Assert.Equal(("2026-09-29T00:00:00Z", "2026-09-30T00:00:00Z"), (a.Today.StartIso, a.Today.EndIso));
        Assert.Equal(("2026-09-01T00:00:00Z", "2026-10-01T00:00:00Z"), (a.ThisMonth.StartIso, a.ThisMonth.EndIso));
        Assert.Equal("UTC", a.BoundaryTimeZoneId);
        Assert.True(a.WallClockStorage);

        var instant = DateAnchors.Compute(new DateTime(2026, 9, 28, 22, 30, 0, DateTimeKind.Utc), "Asia/Dubai");
        Assert.Equal("2026-09-28T20:00:00Z", instant.Today.StartIso);                       // real instants: Dubai midnight
    }

    [Fact]
    public void A_late_evening_invoice_belongs_to_its_own_business_day()
    {
        // Sale on 10 Sep 2026 at 21:00 local is stored as 2026-09-10T21:00Z; "10 Sep" must include it.
        var day = DateAnchors.ForLocalDays(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 10), "UTC");
        var stored = new DateTime(2026, 9, 10, 21, 0, 0, DateTimeKind.Utc);
        Assert.True(stored >= day.StartUtc && stored < day.EndUtc);
    }

    [Fact]
    public void Business_calendar_chooses_the_storage_per_user()
    {
        var calendar = new BusinessCalendarOptions();
        Assert.True(calendar.IsWallClock(mySaleBooksUser: true));
        Assert.False(calendar.IsWallClock(mySaleBooksUser: false));
    }
}

public class MySaleBooksReportTests
{
    private static readonly DateAnchors September = DateAnchors.Compute(new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc), "Asia/Dubai", 1, true);

    private static MqlQuery Plan(string json) => MqlParser.Parse(json).Query!;

    private static (MySaleBooksReports Reports, FakeExecutor Executor) Build()
    {
        var (engine, executor) = Msb.Engine();
        return (new MySaleBooksReports(engine), executor);
    }

    private static List<JsonObject> Ledger(decimal opDr, decimal opCr) => new()
    {
        new() { ["_id"] = Msb.LedgerAlNoor, ["ledgerName"] = "Al Noor Trading", ["groupName"] = "Sundry Debtors", ["opBalanceDebit"] = opDr, ["opBalanceCredit"] = opCr }
    };

    private static List<JsonObject> Vouchers(string json)
    {
        if (json.Contains("firstLineId"))
            return new()
            {
                new() { ["_id"] = "g1", ["voucherDate"] = "2026-09-05T10:00:00.000Z", ["voucherNo"] = "S-1", ["voucherType"] = "SALE", ["debit"] = 300m, ["credit"] = 0m },
                new() { ["_id"] = "g2", ["voucherDate"] = "2026-09-10T21:00:00.000Z", ["voucherNo"] = "R-1", ["voucherType"] = "RECEIPT", ["debit"] = 0m, ["credit"] = 100m }
            };
        if (json.Contains("$gte")) return new() { new() { ["debit"] = 300m, ["credit"] = 100m, ["voucherCount"] = 2 } };
        return new() { new() { ["debit"] = 50m, ["credit"] = 20m } };                        // before the period
    }

    [Fact]
    public async Task Ledger_statement_has_opening_running_and_closing_balances()
    {
        var (reports, executor) = Build();
        var seen = new List<string>();
        executor.Handler = (collection, pipeline) =>
        {
            var json = pipeline.ToJsonString();
            seen.Add(collection + " " + json);
            return collection == "Ledger" ? Ledger(100m, 0m) : collection == "AccountVoucher" ? Vouchers(json) : new List<JsonObject>();
        };

        var r = await reports.RunAsync(Plan("""{"type":"report","report":"ledgerStatement","ledger":"al noor trading","from":"2026-09-01","to":"2026-09-30"}"""),
            Msb.Context(), September, new AppSettings(), 3, default);

        Assert.Equal("ok", r.Kind);
        Assert.Equal(4, r.Rows.Count);
        Assert.Equal("Opening balance", r.Rows[0]["type"]!.GetValue<string>());
        Assert.Equal(130m, r.Rows[0]["balance"]!.GetValue<decimal>());                      // 100 opening + 50 − 20 earlier
        Assert.Equal(430m, r.Rows[1]["balance"]!.GetValue<decimal>());
        Assert.Equal("2026-09-10", r.Rows[2]["date"]!.GetValue<string>());                   // wall-clock date shown as stored
        Assert.Equal(330m, r.Rows[2]["balance"]!.GetValue<decimal>());
        Assert.Equal("Dr", r.Rows[3]["drCr"]!.GetValue<string>());
        Assert.Equal(330m, r.Rows[3]["balance"]!.GetValue<decimal>());
        // Same rules as the Ledger Book: cancelled and unposted PDC lines excluded, period boundaries on business days.
        var voucherQueries = seen.Where(s => s.StartsWith("AccountVoucher")).ToList();
        Assert.All(voucherQueries, q => Assert.Contains("\"isCanceled\":{\"$ne\":true}", q));
        Assert.All(voucherQueries, q => Assert.Contains("\"isPosted\":{\"$ne\":false}", q));
        Assert.Contains(voucherQueries, q => q.Contains("2026-09-01T00:00:00") && q.Contains("2026-10-01T00:00:00"));
        Assert.Equal(4, r.Queries.Count);                                                     // ledger + opening + period + lines
    }

    [Fact]
    public async Task Credit_opening_balance_wins_like_the_ledger_book()
    {
        var (reports, executor) = Build();
        executor.Handler = (collection, pipeline) => collection == "Ledger" ? Ledger(100m, 50m)
            : collection == "AccountVoucher" && pipeline.ToJsonString().Contains("firstLineId") ? new List<JsonObject>()
            : new List<JsonObject> { new() { ["debit"] = 0m, ["credit"] = 0m } };
        var r = await reports.RunAsync(Plan("""{"type":"report","report":"ledgerStatement","ledger":"Al Noor Trading"}"""), Msb.Context(), September, new AppSettings(), 2, default);
        Assert.Equal(50m, r.Rows[0]["balance"]!.GetValue<decimal>());
        Assert.Equal("Cr", r.Rows[0]["drCr"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_and_ambiguous_ledgers_are_reported_not_guessed()
    {
        var (reports, executor) = Build();
        executor.Handler = (_, _) => new List<JsonObject>();
        var none = await reports.RunAsync(Plan("""{"type":"report","report":"ledgerStatement","ledger":"Nobody"}"""), Msb.Context(), September, new AppSettings(), 2, default);
        Assert.Equal("notfound", none.Kind);

        executor.Handler = (c, _) => c == "Ledger"
            ? new List<JsonObject> { new() { ["_id"] = Msb.LedgerAlNoor, ["ledgerName"] = "Al Noor Trading" }, new() { ["_id"] = "6720000000000000000000b2", ["ledgerName"] = "Al Noor Stores" } }
            : new List<JsonObject>();
        var two = await reports.RunAsync(Plan("""{"type":"report","report":"ledgerStatement","ledger":"al noor"}"""), Msb.Context(), September, new AppSettings(), 2, default);
        Assert.Equal("clarify", two.Kind);
        Assert.Contains("Al Noor Stores", two.Message);
    }

    [Fact]
    public async Task Stock_movement_counts_opening_entries_once_and_keeps_a_running_balance()
    {
        var (reports, executor) = Build();
        var seen = new List<string>();
        executor.Handler = (collection, pipeline) =>
        {
            var json = pipeline.ToJsonString();
            seen.Add(collection + " " + json);
            if (collection == "Item") return new List<JsonObject> { new() { ["_id"] = Msb.ItemRice, ["itemName"] = "Rice 5kg" } };
            if (collection != "StockMaster") return new List<JsonObject>();
            if (json.Contains("\"$or\"")) return new List<JsonObject> { new() { ["received"] = 100m, ["issued"] = 30m } };   // opening
            if (json.Contains("\"$sort\""))
                return new List<JsonObject>
                {
                    new() { ["transactionDate"] = "2026-09-03T09:00:00.000Z", ["transactionType"] = "PURCHASE", ["received"] = 20m, ["issued"] = 0m },
                    new() { ["transactionDate"] = "2026-09-04T18:00:00.000Z", ["transactionType"] = "SALE", ["received"] = 0m, ["issued"] = 15m }
                };
            return new List<JsonObject> { new() { ["received"] = 20m, ["issued"] = 15m } };                               // period totals
        };

        var r = await reports.RunAsync(Plan("""{"type":"report","report":"stockMovement","item":"Rice 5kg","from":"2026-09-01","to":"2026-09-30"}"""),
            Msb.Context(), September, new AppSettings(), 2, default);

        Assert.Equal("ok", r.Kind);
        Assert.Equal(new[] { 70m, 90m, 75m, 75m }, r.Rows.Select(x => x["balance"]!.GetValue<decimal>()));
        Assert.Equal("Purchase", r.Rows[1]["type"]!.GetValue<string>());
        var stock = seen.Where(s => s.StartsWith("StockMaster")).ToList();
        Assert.Contains(stock, q => q.Contains("\"transactionType\":\"OPSTOCK\""));                      // opening entries: always in the opening
        Assert.Contains(stock, q => q.Contains("\"transactionType\":{\"$ne\":\"OPSTOCK\"}"));           // … and never again in the period
        Assert.All(stock, q => Assert.Contains("$transactionPipe", q));                                // pipe-conditional quantities
    }

    [Fact]
    public async Task Reports_need_a_valid_period()
    {
        var (reports, _) = Build();
        var r = await reports.RunAsync(Plan("""{"type":"report","report":"stockMovement","item":"Rice","from":"2026-09-30","to":"2026-09-01"}"""),
            Msb.Context(), September, new AppSettings(), 2, default);
        Assert.Equal("invalid", r.Kind);
    }
}

public class MySaleBooksCurrencyTests
{
    private static (CompanyContextService Service, FakeExecutor Executor) Build(string company)
    {
        var user = new FakeUser { IsMySaleBooksUser = true, CompanyId = "db:" + company };
        var (engine, executor) = Msb.Engine(user);
        return (new CompanyContextService(engine, user), executor);
    }

    [Fact]
    public async Task Base_currency_comes_from_Company_currencyId_with_its_decimals()
    {
        var (service, executor) = Build("omr-co");
        executor.Handler = (c, _) => c switch
        {
            "Company" => new List<JsonObject> { new() { ["_id"] = "6740000000000000000000d1", ["currencyId"] = Msb.CurrencyOmr, ["financialYearFrom"] = "2026-04-01T00:00:00.000Z", ["costingType"] = "FIFO" } },
            "Currency" => new List<JsonObject> { new() { ["_id"] = Msb.CurrencyOmr, ["currencyCode"] = "omr", ["symbol"] = "ر.ع.", ["decimals"] = 3 } },
            _ => new List<JsonObject>()
        };
        var ctx = await service.ResolveAsync(Msb.Schema(), null, new AppSettings(), default);
        Assert.NotNull(ctx);
        Assert.True(ctx!.CurrencyResolved);
        Assert.Equal("OMR", ctx.CurrencyCode);
        Assert.Equal(3, ctx.Decimals);
        Assert.Equal(4, ctx.FinancialYearFrom!.Value.Month);
    }

    [Fact]
    public async Task Missing_currency_is_stated_never_defaulted()
    {
        var (service, executor) = Build("no-currency-co");
        executor.Handler = (c, _) => c == "Company"
            ? new List<JsonObject> { new() { ["_id"] = "6740000000000000000000d2", ["currencyId"] = "" } }
            : new List<JsonObject>();
        var ctx = await service.ResolveAsync(Msb.Schema(), null, new AppSettings(), default);
        Assert.False(ctx!.CurrencyResolved);
        Assert.Null(ctx.CurrencyCode);
        Assert.Contains("currencyId", ctx.Missing);
    }

    [Fact]
    public async Task Several_companies_without_a_store_are_not_guessed()
    {
        var (service, executor) = Build("multi-co");
        executor.Handler = (c, _) => c == "Company"
            ? new List<JsonObject> { new() { ["_id"] = "6740000000000000000000d3", ["currencyId"] = Msb.CurrencyOmr }, new() { ["_id"] = "6740000000000000000000d4", ["currencyId"] = Msb.CurrencyOmr } }
            : new List<JsonObject>();
        var ctx = await service.ResolveAsync(Msb.Schema(), null, new AppSettings(), default);
        Assert.False(ctx!.CurrencyResolved);
        Assert.Contains("several companies", ctx.Missing);
    }

    [Fact]
    public async Task Demo_users_keep_the_configured_currency()
    {
        var user = new FakeUser();                                      // not a MySaleBooks token
        var (engine, _) = Msb.Engine(user);
        Assert.Null(await new CompanyContextService(engine, user).ResolveAsync(Msb.Schema(), null, new AppSettings(), default));
    }

    [Theory]
    [InlineData("OMR", 3, "Today's sales are OMR 0.000. No sales were recorded today.")]
    [InlineData("AED", 2, "Today's sales are AED 0.00. No sales were recorded today.")]
    [InlineData(null, 2, "Today's sales are 0.00. No sales were recorded today.")]
    public void Zero_totals_use_the_company_currency_and_precision(string? currency, int decimals, string expected)
    {
        var pipeline = (JsonArray)JsonNode.Parse("""[{"$group":{"_id":null,"totalSales":{"$sum":"$netAmount"}}}]""")!;
        Assert.Equal(expected, ZeroResultPolicy.Describe("What are today's sales?", pipeline, "Sale", null, currency, decimals).Message);
    }

    [Fact]
    public void Prompts_label_money_with_the_verified_currency_or_say_it_is_missing()
    {
        var anchors = DateAnchors.Compute(DateTime.UtcNow, "Asia/Muscat", 1, true);
        var ok = new PromptContext("Co", "OMR", "Asia/Muscat", anchors, 200, "CompanyId") { CurrencyDecimals = 3 };
        var system = new PromptBuilder().BuildQuerySystemPrompt(ok, Msb.Schema());
        Assert.Contains("OMR (3 decimals)", system);
        Assert.Contains("stores the local date/time as UTC", system);
        var answer = new PromptBuilder().BuildAnswerMessages(ok, "q", null, new List<JsonObject>(), false, 10)[0].Content;
        Assert.Contains("OMR 184,250.000", answer);
        Assert.Contains("never convert them to another time zone", answer);

        var missing = ok with { Currency = "", CurrencyMissing = "Company.currencyId is empty" };
        var noCurrency = new PromptBuilder().BuildAnswerMessages(missing, "q", null, new List<JsonObject>(), false, 10)[0].Content;
        Assert.Contains("currency is not configured", noCurrency);
        Assert.DoesNotContain("AED", noCurrency);
    }
}

public class MySaleBooksOrchestratorTests
{
    private static Harness WithMySaleBooks()
    {
        var h = new Harness();
        h.Schema.Extra.AddRange(MySaleBooksCatalog.Build());
        foreach (var c in MySaleBooksCatalog.Names) h.Store.Settings.Query.AllowedCollections.Add(c);
        return h;
    }

    [Theory]
    [InlineData("Show the trial balance for this year")]
    [InlineData("Balance sheet as of today")]
    [InlineData("ميزان المراجعة")]
    public async Task Financial_statements_point_to_the_MySaleBooks_reports(string question)
    {
        var h = WithMySaleBooks();
        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = question }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.Unsupported, r.Status);
        Assert.Contains("don't recalculate", r.Answer);
        Assert.Empty(h.Provider.Requests);
    }

    [Fact]
    public async Task Valuation_questions_explain_what_is_missing()
    {
        var h = WithMySaleBooks();
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"unsupported","reason":"valuation"}""");
        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "What was my stock value on 1 January?" }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.Unsupported, r.Status);
        Assert.Contains("costing method", r.Answer);
    }

    [Fact]
    public async Task Ledger_statement_plan_runs_as_a_server_report()
    {
        var h = WithMySaleBooks();
        h.Executor.Handler = (collection, pipeline) =>
        {
            var json = pipeline.ToJsonString();
            if (collection == "Ledger")
                return new List<JsonObject> { new() { ["_id"] = Msb.LedgerAlNoor, ["ledgerName"] = "Al Noor Trading", ["opBalanceDebit"] = 0m, ["opBalanceCredit"] = 0m } };
            if (collection == "AccountVoucher" && json.Contains("firstLineId"))
                return new List<JsonObject> { new() { ["_id"] = "g1", ["voucherDate"] = "2026-09-05T10:00:00.000Z", ["voucherNo"] = "S-1", ["voucherType"] = "SALE", ["debit"] = 250m, ["credit"] = 0m } };
            if (collection == "AccountVoucher") return new List<JsonObject> { new() { ["debit"] = json.Contains("$gte") ? 250m : 0m, ["credit"] = 0m, ["voucherCount"] = 1 } };
            return new List<JsonObject>();
        };
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"report","report":"ledgerStatement","ledger":"Al Noor Trading","from":"2026-09-01","to":"2026-09-30"}""");
        h.Provider.Answer = req =>
        {
            Assert.Contains("Al Noor Trading", req.Messages[^1].Content);
            return "Al Noor Trading owes AED 250.00 at 30 Sep 2026.";
        };

        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Show the ledger of Al Noor Trading for September 2026" }, NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.Success, r.Status);
        Assert.Equal(3, r.Data!.Count);                                         // opening, one voucher, closing
        var log = Assert.Single(h.Store.Logs);
        Assert.Contains("AccountVoucher", log.FinalMql);                         // every executed query is logged
        Assert.Contains("isCanceled", log.FinalMql);
    }
}
