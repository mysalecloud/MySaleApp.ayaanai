using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Application.Stores;
using MySale.AI.Domain;

namespace MySale.AI.Tests;

public sealed class FakeStoreSelection : IStoreSelection
{
    public string? SelectedStoreId { get; set; }
}

public sealed class FakeStoreAccess : IStoreAccessProvider
{
    public StoreAccess Access { get; set; } = StoreAccess.Unknown;
    public Task<StoreAccess> GetAsync(CancellationToken ct) => Task.FromResult(Access);
}

/// <summary>Selected-store filtering of operational reports; accounting statements stay company-level.</summary>
public class StoreFilterTests
{
    private const string StoreA = "650aaaaaaaaaaaaaaaaaaaa1";
    private const string StoreB = "650bbbbbbbbbbbbbbbbbbbb2";

    private static FieldSchema F(string name, string type) => new() { Name = name, Type = type };

    // Schema shaped like MySaleBooks (camelCase, store = branchId). Customers/Suppliers/Ledger are company masters.
    private static readonly List<CollectionSchema> Schema = new()
    {
        new() { Name = "Sales", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("branchId", "objectId"), F("customerId", "objectId"),
            F("salesmanName", "string"), F("invoiceDate", "date"), F("netAmount", "double"), F("status", "string") } },
        new() { Name = "SaleItems", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("branchId", "objectId"), F("itemId", "objectId"),
            F("itemName", "string"), F("quantity", "double"), F("lineTotal", "double"), F("invoiceDate", "date") } },
        new() { Name = "SalesReturns", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("branchId", "objectId"), F("returnDate", "date"), F("amount", "double") } },
        new() { Name = "Purchases", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("branchId", "objectId"), F("supplierId", "objectId"),
            F("purchaseDate", "date"), F("netAmount", "double"), F("balanceDue", "double") } },
        new() { Name = "Payments", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("branchId", "objectId"), F("customerId", "objectId"), F("amount", "double") } },
        new() { Name = "Stock", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("branchId", "objectId"), F("itemName", "string"),
            F("quantity", "double"), F("reorderLevel", "double"), F("costPrice", "double") } },
        new() { Name = "Customers", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("customerName", "string"), F("balance", "double") } },
        new() { Name = "Suppliers", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("supplierName", "string"), F("balance", "double") } },
        new() { Name = "Ledger", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("ledgerName", "string"), F("groupName", "string"), F("ledgerBalance", "double") } },
        new() { Name = "AccountTransactions", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("branchId", "objectId"), F("ledgerId", "objectId"),
            F("debit", "double"), F("credit", "double"), F("date", "date") } },
        new() { Name = "Branches", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("branchName", "string") } }
    };

    private static readonly StoreFilterOptions Options = new();

    private static StoreScope Selected(string id, string name = "Store") => new() { Mode = StoreMode.Selected, StoreId = id, StoreName = name, Reason = "selected", Options = Options };

    private static (QueryEngine Engine, FakeExecutor Executor) Engine(FakeUser? user = null)
    {
        var tenant = new TenantOptions();
        var executor = new FakeExecutor();
        return (new QueryEngine(new FakeSchema(), new MqlValidator(), new TenantQueryGuard(tenant), tenant, executor, user ?? new FakeUser()), executor);
    }

    private static PreparedQuery Prepare(QueryEngine engine, string query, StoreScope? store)
    {
        var context = new MqlValidationContext { Collections = Schema, TenantField = "CompanyId", MaxRecords = 200, MaxStages = 12, Store = store };
        return engine.Prepare(TestData.Parse(query), context, new AppSettings());
    }

    private static string? StoreFilterValue(JsonArray scoped)
        => scoped.Take(2).OfType<JsonObject>()
            .Select(s => s["$match"]?["branchId"]?["$oid"]?.GetValue<string>())
            .FirstOrDefault(v => v is not null);

    // Today's / monthly sales, top products, top customers, low stock, purchases, sales returns,
    // customer balances, supplier balances, inventory value, sales by salesperson.
    public static TheoryData<string, string> OperationalReports => new()
    {
        { "today's sales", """{"type":"query","operation":"aggregate","collection":"Sales","pipeline":[{"$match":{"invoiceDate":{"$gte":{"$date":"2026-09-28T00:00:00Z"}}}},{"$group":{"_id":null,"total":{"$sum":"$netAmount"}}}]}""" },
        { "monthly sales", """{"type":"query","operation":"aggregate","collection":"Sales","pipeline":[{"$group":{"_id":{"$month":"$invoiceDate"},"total":{"$sum":"$netAmount"}}}]}""" },
        { "top products", """{"type":"query","operation":"aggregate","collection":"SaleItems","pipeline":[{"$group":{"_id":"$itemName","sales":{"$sum":"$lineTotal"}}},{"$sort":{"sales":-1}},{"$limit":10}]}""" },
        { "top customers", """{"type":"query","operation":"aggregate","collection":"Sales","pipeline":[{"$group":{"_id":"$customerId","sales":{"$sum":"$netAmount"}}},{"$sort":{"sales":-1}},{"$limit":10}]}""" },
        { "low stock", """{"type":"query","operation":"find","collection":"Stock","filter":{"quantity":{"$lte":5}},"sort":{"quantity":1},"limit":20}""" },
        { "purchases", """{"type":"query","operation":"aggregate","collection":"Purchases","pipeline":[{"$group":{"_id":null,"total":{"$sum":"$netAmount"}}}]}""" },
        { "sales returns", """{"type":"query","operation":"aggregate","collection":"SalesReturns","pipeline":[{"$group":{"_id":null,"total":{"$sum":"$amount"}}}]}""" },
        { "customer balances", """{"type":"query","operation":"aggregate","collection":"Payments","pipeline":[{"$group":{"_id":"$customerId","paid":{"$sum":"$amount"}}}]}""" },
        { "supplier balances", """{"type":"query","operation":"aggregate","collection":"Purchases","pipeline":[{"$group":{"_id":"$supplierId","due":{"$sum":"$balanceDue"}}}]}""" },
        { "inventory value", """{"type":"query","operation":"aggregate","collection":"Stock","pipeline":[{"$group":{"_id":null,"value":{"$sum":{"$multiply":["$quantity","$costPrice"]}}}}]}""" },
        { "sales by salesperson", """{"type":"query","operation":"aggregate","collection":"Sales","pipeline":[{"$group":{"_id":"$salesmanName","sales":{"$sum":"$netAmount"}}}]}""" }
    };

    [Theory]
    [MemberData(nameof(OperationalReports))]
    public void Operational_reports_are_limited_to_the_selected_store_and_follow_a_store_switch(string report, string query)
    {
        var (engine, _) = Engine();

        var forA = Prepare(engine, query, Selected(StoreA));
        Assert.True(forA.IsExecutable, report + ": " + string.Join("; ", forA.Validation.Errors));
        Assert.Equal(StoreA, StoreFilterValue(forA.ScopedPipeline!));
        Assert.Equal("branchId", forA.StoreFilterField);

        var forB = Prepare(engine, query, Selected(StoreB));
        Assert.Equal(StoreB, StoreFilterValue(forB.ScopedPipeline!));
        Assert.DoesNotContain(StoreA, forB.Mql);
    }

    [Fact]
    public void Store_filter_comes_right_after_the_tenant_filter_before_any_ai_stage()
    {
        var (engine, _) = Engine();
        var p = Prepare(engine, OperationalReports.First()[1].ToString()!, Selected(StoreA));
        var stages = p.ScopedPipeline!.OfType<JsonObject>().ToList();
        Assert.True(stages[0]["$match"]!.AsObject().ContainsKey("CompanyId"));
        Assert.Equal(StoreA, stages[1]["$match"]!["branchId"]!["$oid"]!.GetValue<string>());
    }

    [Fact]
    public void Company_masters_without_a_store_field_are_not_filtered()
    {
        var (engine, _) = Engine();
        var p = Prepare(engine, """{"type":"query","operation":"find","collection":"Customers","filter":{},"limit":20}""", Selected(StoreA));
        Assert.True(p.IsExecutable);
        Assert.Null(p.StoreFilterField);
        Assert.DoesNotContain(StoreA, p.Mql);
    }

    [Fact]
    public void Lookups_into_store_level_collections_get_the_store_filter()
    {
        var (engine, _) = Engine();
        var p = Prepare(engine, """
            {"type":"query","operation":"aggregate","collection":"Customers","pipeline":[
              {"$lookup":{"from":"Sales","localField":"_id","foreignField":"customerId","as":"sales"}},
              {"$project":{"customerName":1,"invoices":{"$size":"$sales"}}}]}
            """, Selected(StoreA));
        Assert.True(p.IsExecutable, string.Join("; ", p.Validation.Errors));
        var lookup = p.ScopedPipeline!.OfType<JsonObject>().First(s => s.ContainsKey("$lookup"))["$lookup"]!;
        Assert.Contains(StoreA, lookup["pipeline"]!.ToJsonString());
    }

    [Fact]
    public void A_filter_on_another_store_is_rejected_by_validation()
    {
        var (engine, _) = Engine();
        var p = Prepare(engine, $$$$"""{"type":"query","operation":"aggregate","collection":"Sales","pipeline":[{"$match":{"branchId":{"$oid":"{{{{StoreB}}}}"}}},{"$group":{"_id":null,"t":{"$sum":"$netAmount"}}}]}""",
            Selected(StoreA));
        Assert.False(p.IsExecutable);
        Assert.Contains(p.Validation.Errors, e => e.Contains("selected store"));
    }

    [Fact]
    public void Without_a_store_operational_queries_are_not_run_but_company_masters_are()
    {
        var (engine, _) = Engine();
        var missing = new StoreScope { Mode = StoreMode.Missing, Reason = "no-store-selected", Options = Options };
        var sales = Prepare(engine, """{"type":"query","operation":"aggregate","collection":"Sales","pipeline":[{"$group":{"_id":null,"t":{"$sum":"$netAmount"}}}]}""", missing);
        Assert.True(sales.StoreContextMissing);
        Assert.False(sales.IsExecutable);
        Assert.Contains(StoreScope.MissingMessage, sales.Validation.Errors);

        var customers = Prepare(engine, """{"type":"query","operation":"find","collection":"Customers","filter":{},"limit":5}""", missing);
        Assert.True(customers.IsExecutable);
    }

    // ------------------------------------------------------------------ accounting exceptions

    [Theory]
    [InlineData("Show the trial balance")]
    [InlineData("Balance sheet as of today")]
    [InlineData("What is my profit and loss this year?")]
    [InlineData("P&L for last month")]
    public async Task Accounting_statements_are_company_level_and_not_store_filtered(string question)
    {
        var (engine, _) = Engine(new FakeUser { IsMySaleBooksUser = true });
        var resolver = new StoreContextResolver(new FakeUser { IsMySaleBooksUser = true }, new FakeStoreSelection { SelectedStoreId = StoreA },
            new FakeStoreAccess(), Options, engine);

        var scope = await resolver.ResolveAsync(question, new AppSettings(), default);
        Assert.Equal(StoreMode.None, scope.Mode);
        Assert.True(scope.AccountingStatement);

        // Even on a store-level journal collection no store filter is added, and validation does not reject it.
        var p = Prepare(engine, """{"type":"query","operation":"aggregate","collection":"AccountTransactions","pipeline":[{"$group":{"_id":"$ledgerId","dr":{"$sum":"$debit"},"cr":{"$sum":"$credit"}}}]}""", scope);
        Assert.True(p.IsExecutable, string.Join("; ", p.Validation.Errors));
        Assert.Null(p.StoreFilterField);
        Assert.DoesNotContain(StoreA, p.Mql);
    }

    [Fact]
    public void Profitable_products_is_an_operational_question_not_an_accounting_statement()
    {
        Assert.False(StoreQuestionPolicy.IsAccountingStatement("What are my most profitable products?"));
        Assert.False(StoreQuestionPolicy.IsAccountingStatement("Show sales for this month"));
        Assert.True(StoreQuestionPolicy.IsAccountingStatement("Trial Balance"));
    }

    // ------------------------------------------------------------------ resolver: selection, verification, all stores

    private static (StoreContextResolver Resolver, FakeStoreSelection Selection, FakeStoreAccess Access) Resolver(StoreFilterOptions? options = null)
    {
        var user = new FakeUser { IsMySaleBooksUser = true };
        var (engine, executor) = Engine(user);
        // Store master of the tenant (sample catalog "Branches" with BranchName): A = Kannur, B = Calicut.
        executor.Handler = (collection, _) => collection == "Branches"
            ? new List<JsonObject> { new() { ["_id"] = StoreA, ["BranchName"] = "Kannur" }, new() { ["_id"] = StoreB, ["BranchName"] = "Calicut" } }
            : new List<JsonObject>();
        var selection = new FakeStoreSelection();
        var access = new FakeStoreAccess();
        return (new StoreContextResolver(user, selection, access, options ?? Options, engine), selection, access);
    }

    [Fact]
    public async Task The_selected_store_is_verified_and_named_and_switching_changes_it()
    {
        var (resolver, selection, _) = Resolver();
        selection.SelectedStoreId = StoreA;
        var a = await resolver.ResolveAsync("What are my sales today?", new AppSettings(), default);
        Assert.Equal(StoreMode.Selected, a.Mode);
        Assert.Equal("Kannur", a.StoreName);

        selection.SelectedStoreId = StoreB;
        var b = await resolver.ResolveAsync("What are my sales today?", new AppSettings(), default);
        Assert.Equal(StoreB, b.StoreId);
        Assert.Equal("Calicut", b.StoreName);
    }

    [Theory]
    [InlineData(null, "no-store-selected")]
    [InlineData("650cccccccccccccccccccc3", "store-not-found")]
    [InlineData("bad id!", "invalid-store-id")]
    public async Task Missing_or_unknown_stores_never_fall_back_to_all_stores(string? selected, string reason)
    {
        var (resolver, selection, _) = Resolver();
        selection.SelectedStoreId = selected;
        var scope = await resolver.ResolveAsync("What are my sales this month?", new AppSettings(), default);
        Assert.Equal(StoreMode.Missing, scope.Mode);
        Assert.Equal(reason, scope.Reason);
    }

    [Fact]
    public async Task Stores_outside_the_users_branch_mapping_are_refused()
    {
        var (resolver, selection, access) = Resolver();
        access.Access = new StoreAccess { Verified = true, AllowedStoreIds = new HashSet<string> { StoreA } };
        selection.SelectedStoreId = StoreB;
        var scope = await resolver.ResolveAsync("Sales today", new AppSettings(), default);
        Assert.Equal(StoreMode.Missing, scope.Mode);
        Assert.Equal("store-not-allowed", scope.Reason);
    }

    [Fact]
    public async Task All_stores_needs_verified_unrestricted_access()
    {
        var (resolver, selection, access) = Resolver();
        selection.SelectedStoreId = StoreA;

        // Not verified: selected store only, with a note.
        var limited = await resolver.ResolveAsync("Compare sales of all branches this month", new AppSettings(), default);
        Assert.Equal(StoreMode.Selected, limited.Mode);
        Assert.Contains("Kannur", limited.Note);

        // "All" in the selector without verification: ask for a store.
        selection.SelectedStoreId = "0";
        Assert.Equal(StoreMode.Missing, (await resolver.ResolveAsync("Sales today", new AppSettings(), default)).Mode);

        // Verified unrestricted user: company-wide.
        access.Access = new StoreAccess { Verified = true, Unrestricted = true };
        Assert.Equal(StoreMode.AllStores, (await resolver.ResolveAsync("Sales today", new AppSettings(), default)).Mode);
        selection.SelectedStoreId = StoreA;
        Assert.Equal(StoreMode.AllStores, (await resolver.ResolveAsync("Sales of all branches", new AppSettings(), default)).Mode);
    }

    [Fact]
    public async Task Dashboard_users_are_not_affected_unless_configured()
    {
        var (engine, _) = Engine();
        var resolver = new StoreContextResolver(new FakeUser(), new FakeStoreSelection(), new FakeStoreAccess(), Options, engine);
        Assert.Equal(StoreMode.None, (await resolver.ResolveAsync("Sales today", new AppSettings(), default)).Mode);
    }

    // ------------------------------------------------------------------ end to end: two stores

    [Fact]
    public async Task Multi_store_chat_returns_only_the_selected_stores_data_and_changes_with_the_selection()
    {
        const string salesQuery = """
            {"type":"query","operation":"aggregate","collection":"Sales","pipeline":[
              {"$group":{"_id":null,"totalSales":{"$sum":"$NetAmount"}}},{"$project":{"_id":0,"totalSales":1}}],
             "explanation":"Total sales","visualization":"kpi"}
            """;
        var selection = new FakeStoreSelection { SelectedStoreId = StoreA };
        var h = new Harness(storeSelection: selection, storeAccess: new FakeStoreAccess());
        h.User.IsMySaleBooksUser = true;
        // Fake database: Store A sold 100, Store B sold 900. The handler applies the injected store filter.
        h.Executor.Handler = (collection, pipeline) =>
        {
            if (collection == "Branches")
                return new List<JsonObject> { new() { ["_id"] = StoreA, ["BranchName"] = "Kannur" }, new() { ["_id"] = StoreB, ["BranchName"] = "Calicut" } };
            var text = pipeline.ToJsonString();
            var total = text.Contains(StoreA) ? 100.0 : text.Contains(StoreB) ? 900.0 : 1000.0;
            return new List<JsonObject> { new() { ["totalSales"] = total } };
        };
        h.Provider.Answer = req => req.Messages.Last().Content.Contains("900") ? "Your sales are AED 900.00." : "Your sales are AED 100.00.";

        h.Provider.QueryResponses.Enqueue(_ => salesQuery);
        var a = await h.Orchestrator.RunAsync(new ChatRequest { Message = "What are my sales this month?" }, NullChatEventSink.Instance, default);
        Assert.Equal(ChatStatus.Success, a.Status);
        Assert.Contains("100", a.Answer);
        Assert.Contains(StoreA, h.Executor.LastCall!.Value.Pipeline.ToJsonString());

        selection.SelectedStoreId = StoreB;
        h.Provider.QueryResponses.Enqueue(_ => salesQuery);
        var b = await h.Orchestrator.RunAsync(new ChatRequest { Message = "What are my sales this month?" }, NullChatEventSink.Instance, default);
        Assert.Contains("900", b.Answer);
        Assert.DoesNotContain(StoreA, h.Executor.LastCall!.Value.Pipeline.ToJsonString());
    }

    [Fact]
    public async Task Chat_without_a_selected_store_asks_for_one_and_runs_nothing()
    {
        var h = new Harness(storeSelection: new FakeStoreSelection(), storeAccess: new FakeStoreAccess());
        h.User.IsMySaleBooksUser = true;
        h.Provider.QueryResponses.Enqueue(_ => """{"type":"query","operation":"aggregate","collection":"Sales","pipeline":[{"$group":{"_id":null,"t":{"$sum":"$NetAmount"}}}]}""");
        var calls = 0;
        h.Executor.Handler = (collection, _) => { if (collection == "Sales") calls++; return new List<JsonObject>(); };

        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Sales today?" }, NullChatEventSink.Instance, default);

        Assert.Equal(StoreScope.MissingMessage, r.Answer);
        Assert.Equal(0, calls);
    }
}
