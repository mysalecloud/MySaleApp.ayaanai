using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.BusinessData;

namespace MySale.AI.Tests;

// Entity resolution (ids → business names) on the verified MySaleBooks schema: every grouped / aliased id of a result
// is shown by name, the id stays in the row, nothing is guessed, orphans are reported. One test per required scenario.

internal static class Ent
{
    public const string SupA = "6a8b1a66ef29e89c73d20001", SupB = "6a8b1a66ef29e89c73d20002", SupGone = "6a8b1a66ef29e89c73d2dead";
    public const string CusA = "6a8b1a66ef29e89c73d30001", CusB = "6a8b1a66ef29e89c73d30002";
    public const string ItemA = "6a8b1a66ef29e89c73d40001", ItemB = "6a8b1a66ef29e89c73d40002";
    public const string WhMain = "6a8b1a66ef29e89c73d50001", WhGone = "6a8b1a66ef29e89c73d5dead";
    public const string EmpA = "6a8b1a66ef29e89c73d60001";
    public const string CatA = "6a8b1a66ef29e89c73d70001";
    public const string BrandA = "6a8b1a66ef29e89c73d80001";
    public const string BranchA = "6a8b1a66ef29e89c73d90001";
    public const string LedRent = "6a8b1a66ef29e89c73da0001", LedPower = "6a8b1a66ef29e89c73da0002";

    public static readonly Dictionary<string, List<JsonObject>> Masters = new()
    {
        ["Ledger"] = new()
        {
            new() { ["_id"] = SupA, ["ledgerName"] = "ABC Trading", ["ledgerCode"] = "SUP-01" },
            new() { ["_id"] = SupB, ["ledgerName"] = "Gulf Supplies", ["ledgerCode"] = "SUP-02" },
            new() { ["_id"] = CusA, ["ledgerName"] = "Al Noor Stores", ["ledgerCode"] = "CUS-01" },
            new() { ["_id"] = CusB, ["ledgerName"] = "Muscat Mart", ["ledgerCode"] = "CUS-02" },
            new() { ["_id"] = LedRent, ["ledgerName"] = "Rent", ["ledgerCode"] = "EXP-01" },
            new() { ["_id"] = LedPower, ["ledgerName"] = "Electricity", ["ledgerCode"] = "EXP-02" },
        },
        ["Item"] = new() { new() { ["_id"] = ItemA, ["itemName"] = "Rice 5kg", ["itemCode"] = "R5" }, new() { ["_id"] = ItemB, ["itemName"] = "Sugar 1kg", ["itemCode"] = "S1" } },
        ["StockLocation"] = new() { new() { ["_id"] = WhMain, ["stockLocationName"] = "Main Store" } },
        ["Employee"] = new() { new() { ["_id"] = EmpA, ["employeeName"] = "Rahul", ["employeeCode"] = "E1" } },
        ["Category"] = new() { new() { ["_id"] = CatA, ["categoryName"] = "Groceries" } },
        ["Manufacture"] = new() { new() { ["_id"] = BrandA, ["manufactureName"] = "Tilda" } },
        ["Branch"] = new() { new() { ["_id"] = BranchA, ["branchName"] = "Muscat" } },
        // As the name lookup returns them: grouped by groupId ({"$group":{"_id":"$groupId","groupName":{"$first":…}}}).
        ["AccountGroup"] = new() { new() { ["_id"] = 11, ["groupName"] = "Indirect Expenses" }, new() { ["_id"] = 9, ["groupName"] = "Direct Expenses" } },
    };

    public static (QueryEngine Engine, FakeExecutor Executor, List<string> Calls) Engine(Dictionary<string, List<JsonObject>>? masters = null)
    {
        var (engine, executor) = Msb.Engine();
        var calls = new List<string>();
        var data = masters ?? Masters;
        executor.Handler = (collection, pipeline) =>
        {
            calls.Add(collection + " " + pipeline.ToJsonString());
            return data.TryGetValue(collection, out var docs) ? docs : new List<JsonObject>();
        };
        return (engine, executor, calls);
    }

    public static PreparedQuery Prepared(string collection, string pipeline) => new()
    {
        Query = TestData.Parse($$"""{"type":"query","operation":"aggregate","collection":"{{collection}}","pipeline":{{pipeline}}}"""),
        Validation = new MqlValidationResult { Collection = collection, Pipeline = (JsonArray)JsonNode.Parse(pipeline)! }
    };

    public static ExecutedQuery Rows(params JsonObject[] rows) => new() { Rows = rows.ToList(), Columns = rows[0].Select(kv => kv.Key).ToList() };

    public static string S(JsonObject row, string key) => row[key]!.GetValue<string>();
}

public class EntityResolutionTests
{
    private static readonly IReadOnlyList<CollectionSchema> Schema = Msb.Schema();

    private static async Task<ReferenceResolution> Resolve(string collection, string pipeline, ExecutedQuery rows, Dictionary<string, List<JsonObject>>? masters = null)
    {
        var (engine, _, _) = Ent.Engine(masters);
        return await engine.ResolveReferencesAsync(Ent.Prepared(collection, pipeline), rows, Schema, new AppSettings(), default);
    }

    [Fact]
    public async Task Top_suppliers_by_purchase_amount_show_supplier_names_in_amount_order()
    {
        var r = await Resolve("Purchase",
            """[{"$group":{"_id":"$ledgerId","purchaseAmount":{"$sum":"$netAmount"}}},{"$sort":{"purchaseAmount":-1}},{"$limit":10},{"$project":{"_id":0,"supplierId":"$_id","purchaseAmount":1}}]""",
            Ent.Rows(new() { ["supplierId"] = Ent.SupA, ["purchaseAmount"] = 18500.0 }, new() { ["supplierId"] = Ent.SupB, ["purchaseAmount"] = 9200.0 }));

        Assert.Equal(new[] { "supplierName", "purchaseAmount" }, r.Result.Columns);         // chart: Purchase Amount by Supplier
        Assert.Equal("ABC Trading", Ent.S(r.Result.Rows[0], "supplierName"));
        Assert.Equal("Gulf Supplies", Ent.S(r.Result.Rows[1], "supplierName"));
        Assert.Equal(Ent.SupA, Ent.S(r.Result.Rows[0], "supplierId"));                       // internal grouping id kept
        Assert.Equal(18500.0, r.Result.Rows[0]["purchaseAmount"]!.GetValue<double>());        // metric and order untouched
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("supplier", d.Entity);
        Assert.Equal("Purchase.ledgerId", d.Source);
        Assert.Equal("verified reference", d.Method);
        Assert.Equal("Ledger.ledgerName", d.Master);
        Assert.Equal(2, d.Resolved);
    }

    [Fact]
    public async Task Top_customers_by_sales_show_customer_names()
    {
        var r = await Resolve("Sale", """[{"$group":{"_id":"$ledgerId","sales":{"$sum":"$netAmount"}}},{"$sort":{"sales":-1}}]""",
            Ent.Rows(new() { ["_id"] = Ent.CusB, ["sales"] = 900.0 }, new() { ["_id"] = Ent.CusA, ["sales"] = 400.0 }));
        Assert.Equal(new[] { "customerName", "sales" }, r.Result.Columns);
        Assert.Equal("Muscat Mart", Ent.S(r.Result.Rows[0], "customerName"));
        Assert.Equal(Ent.CusB, Ent.S(r.Result.Rows[0], "_id"));
    }

    [Fact]
    public async Task Sales_by_customer_from_vouchers_uses_the_debtors_group_to_name_customers()
    {
        var r = await Resolve("AccountVoucher",
            """[{"$match":{"$or":[{"groupId":15},{"parentGroupId":15}]}},{"$group":{"_id":"$ledgerId","debit":{"$sum":"$debit"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.CusA, ["debit"] = 50.0 }));
        Assert.Equal("customerName", r.Result.Columns[0]);
        Assert.Equal("Al Noor Stores", Ent.S(r.Result.Rows[0], "customerName"));
    }

    [Fact]
    public async Task Purchases_by_supplier_with_a_compound_group_key_are_traced_to_the_supplier()
    {
        var r = await Resolve("Purchase", """[{"$group":{"_id":{"supplier":"$ledgerId","month":{"$month":"$purchaseDate"}},"amount":{"$sum":"$netAmount"}}}]""",
            Ent.Rows(new() { ["supplier"] = Ent.SupB, ["month"] = 9, ["amount"] = 10.0 }));
        Assert.Equal(new[] { "supplierName", "month", "amount" }, r.Result.Columns);
        Assert.Equal("Gulf Supplies", Ent.S(r.Result.Rows[0], "supplierName"));
    }

    [Fact]
    public async Task Stock_by_product_and_item_wise_stock_show_item_names()
    {
        var r = await Resolve("StockMaster", """[{"$group":{"_id":"$itemId","quantity":{"$sum":"$stockIn"}}},{"$project":{"_id":0,"itemId":"$_id","quantity":1}}]""",
            Ent.Rows(new() { ["itemId"] = Ent.ItemA, ["quantity"] = 12.0 }, new() { ["itemId"] = Ent.ItemB, ["quantity"] = 3.0 }));
        Assert.Equal(new[] { "itemName", "quantity" }, r.Result.Columns);
        Assert.Equal("Rice 5kg", Ent.S(r.Result.Rows[0], "itemName"));
        Assert.Equal("Sugar 1kg", Ent.S(r.Result.Rows[1], "itemName"));
    }

    [Fact]
    public async Task Stock_by_warehouse_names_warehouses_and_reports_orphans_and_empty_ids_safely()
    {
        var r = await Resolve("StockMaster", """[{"$group":{"_id":"$stockLocationId","quantity":{"$sum":"$stockIn"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.WhMain, ["quantity"] = 10.0 }, new() { ["_id"] = "0", ["quantity"] = 2.0 }, new() { ["_id"] = Ent.WhGone, ["quantity"] = 1.0 }));
        Assert.Equal("warehouseName", r.Result.Columns[0]);
        Assert.Equal("Main Store", Ent.S(r.Result.Rows[0], "warehouseName"));
        Assert.Equal("No warehouse", Ent.S(r.Result.Rows[1], "warehouseName"));
        Assert.Equal("Unknown warehouse", Ent.S(r.Result.Rows[2], "warehouseName"));     // never the raw id, never a guess
        Assert.Equal(Ent.WhGone, Ent.S(r.Result.Rows[2], "_id"));                         // id kept internally
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(1, d.Orphans);
        Assert.Equal(Ent.WhGone, Assert.Single(d.OrphanIds));
        Assert.Equal(1, d.Empty);
        Assert.False(d.LookupFailed);
    }

    [Fact]
    public async Task Outstanding_by_customer_and_by_supplier_use_the_invoice_party()
    {
        var customers = await Resolve("Sale", """[{"$match":{"balanceAmount":{"$ne":0}}},{"$group":{"_id":"$ledgerId","outstanding":{"$sum":"$balanceAmount"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.CusA, ["outstanding"] = 75.5 }));
        Assert.Equal("Al Noor Stores", Ent.S(customers.Result.Rows[0], "customerName"));

        var suppliers = await Resolve("Purchase", """[{"$match":{"balanceAmount":{"$ne":0}}},{"$group":{"_id":"$ledgerId","outstanding":{"$sum":"$balanceAmount"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.SupA, ["outstanding"] = 120.0 }));
        Assert.Equal("ABC Trading", Ent.S(suppliers.Result.Rows[0], "supplierName"));
    }

    [Fact]
    public async Task Sales_by_salesman_show_the_employee_name()
    {
        var r = await Resolve("Sale", """[{"$group":{"_id":"$employeeId","sales":{"$sum":"$netAmount"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.EmpA, ["sales"] = 100.0 }));
        Assert.Equal(new[] { "salesmanName", "sales" }, r.Result.Columns);
        Assert.Equal("Rahul", Ent.S(r.Result.Rows[0], "salesmanName"));
        Assert.Equal("Employee.employeeName", r.Diagnostics.Single().Master);
    }

    [Fact]
    public async Task Expenses_by_ledger_show_ledger_names()
    {
        var r = await Resolve("AccountVoucher", """[{"$match":{"groupId":{"$in":[9,11]}}},{"$group":{"_id":"$ledgerId","expense":{"$sum":"$debit"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.LedRent, ["expense"] = 500.0 }, new() { ["_id"] = Ent.LedPower, ["expense"] = 80.0 }));
        Assert.Equal(new[] { "ledgerName", "expense" }, r.Result.Columns);
        Assert.Equal("Rent", Ent.S(r.Result.Rows[0], "ledgerName"));
    }

    [Fact]
    public async Task Category_wise_and_brand_wise_sales_are_traced_through_the_item_lookup()
    {
        const string head = """[{"$match":{"transactionType":"SALE"}},{"$addFields":{"itemObj":{"$convert":{"input":"$itemId","to":"objectId","onError":null,"onNull":null}}}},{"$lookup":{"from":"Item","localField":"itemObj","foreignField":"_id","as":"item"}},{"$unwind":"$item"},""";
        var byCategory = await Resolve("StockMaster", head + """{"$group":{"_id":"$item.categoryId","sold":{"$sum":"$stockOut"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.CatA, ["sold"] = 7.0 }));
        Assert.Equal("Groceries", Ent.S(byCategory.Result.Rows[0], "categoryName"));
        Assert.Equal("Item.categoryId", byCategory.Diagnostics.Single().Source);

        var byBrand = await Resolve("StockMaster", head + """{"$group":{"_id":"$item.manufactureId","sold":{"$sum":"$stockOut"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.BrandA, ["sold"] = 4.0 }, new() { ["_id"] = "0", ["sold"] = 1.0 }));
        Assert.Equal(new[] { "brandName", "sold" }, byBrand.Result.Columns);
        Assert.Equal("Tilda", Ent.S(byBrand.Result.Rows[0], "brandName"));
        Assert.Equal("No brand", Ent.S(byBrand.Result.Rows[1], "brandName"));
    }

    [Fact]
    public async Task Branch_wise_sales_show_branch_names()
    {
        var r = await Resolve("Sale", """[{"$group":{"_id":"$branchId","sales":{"$sum":"$netAmount"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.BranchA, ["sales"] = 10.0 }));
        Assert.Equal("Muscat", Ent.S(r.Result.Rows[0], "branchName"));
    }

    [Fact]
    public async Task Account_group_numbers_are_shown_as_group_names()
    {
        var r = await Resolve("AccountVoucher", """[{"$group":{"_id":"$groupId","debit":{"$sum":"$debit"}}}]""",
            Ent.Rows(new() { ["_id"] = 11, ["debit"] = 5.0 }, new() { ["_id"] = 9, ["debit"] = 3.0 }));
        Assert.Equal(new[] { "groupName", "debit" }, r.Result.Columns);
        Assert.Equal("Indirect Expenses", Ent.S(r.Result.Rows[0], "groupName"));
        Assert.Equal("Direct Expenses", Ent.S(r.Result.Rows[1], "groupName"));
    }

    [Fact]
    public async Task Duplicate_names_are_told_apart_by_code_not_merged()
    {
        var masters = new Dictionary<string, List<JsonObject>>(Ent.Masters)
        {
            ["Ledger"] = new()
            {
                new() { ["_id"] = Ent.SupA, ["ledgerName"] = "ABC Trading", ["ledgerCode"] = "SUP-01" },
                new() { ["_id"] = Ent.SupB, ["ledgerName"] = "ABC Trading", ["ledgerCode"] = "SUP-02" }
            }
        };
        var r = await Resolve("Purchase", """[{"$group":{"_id":"$ledgerId","amount":{"$sum":"$netAmount"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.SupA, ["amount"] = 1.0 }, new() { ["_id"] = Ent.SupB, ["amount"] = 2.0 }), masters);
        Assert.Equal("ABC Trading (SUP-01)", Ent.S(r.Result.Rows[0], "supplierName"));
        Assert.Equal("ABC Trading (SUP-02)", Ent.S(r.Result.Rows[1], "supplierName"));
        Assert.Equal(2, r.Diagnostics.Single().DuplicateNames);
    }

    [Fact]
    public async Task A_failed_master_read_keeps_the_id_hidden_and_is_not_reported_as_orphan()
    {
        var (engine, executor) = Msb.Engine();
        executor.Throw = new QueryTimeoutException("slow");
        var r = await engine.ResolveReferencesAsync(Ent.Prepared("Purchase", """[{"$group":{"_id":"$ledgerId","amount":{"$sum":"$netAmount"}}}]"""),
            Ent.Rows(new() { ["_id"] = Ent.SupA, ["amount"] = 1.0 }), Schema, new AppSettings(), default);
        Assert.Equal("Unknown supplier", Ent.S(r.Result.Rows[0], "supplierName"));
        var d = r.Diagnostics.Single();
        Assert.True(d.LookupFailed);
        Assert.Equal(0, d.Orphans);
    }

    [Fact]
    public async Task User_ids_have_no_verified_master_and_are_never_shown()
    {
        var (engine, _, calls) = Ent.Engine();
        var r = await engine.ResolveReferencesAsync(Ent.Prepared("Sale", """[{"$group":{"_id":"$userId","count":{"$sum":1}}}]"""),
            Ent.Rows(new() { ["_id"] = Ent.SupA, ["count"] = 4 }), Schema, new AppSettings(), default);
        Assert.Equal("Unknown user", Ent.S(r.Result.Rows[0], "userName"));
        Assert.Empty(calls);                                                                 // nothing to look up
        Assert.Contains("identity", r.Diagnostics.Single().Note);
    }

    [Fact]
    public async Task A_name_already_in_the_result_only_hides_the_id()
    {
        var r = await Resolve("Purchase",
            """[{"$group":{"_id":"$ledgerId","supplier":{"$first":"$ledgerName"},"amount":{"$sum":"$netAmount"}}}]""",
            Ent.Rows(new() { ["_id"] = Ent.SupA, ["supplier"] = "ABC Trading", ["amount"] = 1.0 }));
        Assert.Equal(new[] { "supplier", "amount" }, r.Result.Columns);
        Assert.True(r.Diagnostics.Single().Hidden);
    }

    [Fact]
    public async Task Aliased_ids_without_lineage_are_resolved_by_their_entity_name()
    {
        // $facet hides the lineage: "salesmanId" still names the entity, verified by the id existing in Employee.
        var r = await Resolve("Sale", """[{"$facet":{"x":[{"$group":{"_id":"$employeeId"}}]}}]""",
            Ent.Rows(new() { ["salesmanId"] = Ent.EmpA, ["sales"] = 1.0 }));
        Assert.Equal("Rahul", Ent.S(r.Result.Rows[0], "salesmanName"));
        Assert.Equal("column name", r.Diagnostics.Single().Method);
    }

    [Fact]
    public async Task Report_names_use_the_same_resolver_with_orphans_and_empty_ids()
    {
        var (engine, _, _) = Ent.Engine();
        var names = await engine.ResolveEntityNamesAsync("warehouse", new[] { Ent.WhMain, Ent.WhGone, "0", null }, Schema, new AppSettings(), default);
        Assert.Equal("Main Store", names.Label(Ent.WhMain));
        Assert.Equal("Unknown warehouse", names.Label(Ent.WhGone));
        Assert.Equal("No warehouse", names.Label("0"));
        Assert.Contains(Ent.WhGone, names.Orphans);
    }

    [Fact]
    public async Task Integrity_check_counts_resolved_unresolved_and_orphan_ids_per_reference()
    {
        var (engine, executor) = Msb.Engine();
        executor.Handler = (collection, pipeline) =>
        {
            var json = pipeline.ToJsonString();
            if (collection == "Purchase" && json.Contains("\"$ledgerId\""))
                return new List<JsonObject> { new() { ["_id"] = Ent.SupA }, new() { ["_id"] = Ent.SupGone } };
            return Ent.Masters.TryGetValue(collection, out var docs) && !json.Contains("\"$group\":{\"_id\":\"$") ? docs : new List<JsonObject>();
        };
        var checks = await engine.CheckEntityReferencesAsync(Schema, new AppSettings(), default);
        var supplier = checks.Single(c => c.Reference == "Purchase.ledgerId");
        Assert.Equal("orphans", supplier.Status);
        Assert.Equal(2, supplier.Checked);
        Assert.Equal(1, supplier.Resolved);
        Assert.Equal(1, supplier.Orphans);
        Assert.Equal(Ent.SupGone, Assert.Single(supplier.SampleOrphanIds));
        Assert.Equal("unresolvable", checks.Single(c => c.Reference == "Sale.userId").Status);
    }
}

public class FieldLineageTests
{
    [Fact]
    public void Group_keys_aliases_and_lookups_are_traced_to_the_stored_field()
    {
        var p = (JsonArray)JsonNode.Parse("""
            [{"$addFields":{"itemObj":{"$convert":{"input":"$itemId","to":"objectId","onError":null}}}},
             {"$lookup":{"from":"Item","localField":"itemObj","foreignField":"_id","as":"item"}},{"$unwind":"$item"},
             {"$group":{"_id":{"cat":"$item.categoryId","wh":"$stockLocationId"},"party":{"$first":"$ledgerId"},"q":{"$sum":"$stockIn"}}},
             {"$project":{"_id":0,"categoryId":"$_id.cat","warehouse":"$_id.wh","party":1,"q":1}}]
            """)!;
        var map = FieldLineage.Trace("StockMaster", p);
        Assert.Equal(new FieldSource("Item", "categoryId"), map["categoryId"]);
        Assert.Equal(new FieldSource("StockMaster", "stockLocationId"), map["warehouse"]);
        Assert.Equal(new FieldSource("StockMaster", "ledgerId"), map["party"]);
        Assert.False(map.ContainsKey("q"));                                                    // computed: no source
    }

    [Fact]
    public void Plain_lists_keep_the_root_fields()
        => Assert.Equal(new FieldSource("Sale", "ledgerId"), FieldLineage.Of("Sale", (JsonArray)JsonNode.Parse("""[{"$match":{}}]""")!, "ledgerId"));
}

public class DisplayIdGuardTests
{
    [Fact]
    public void Raw_id_columns_are_hidden_and_id_labels_replaced()
    {
        var rows = new List<JsonObject>
        {
            new() { ["refId"] = Ent.SupA, ["name"] = "ABC Trading", ["amount"] = 1.0 },
            new() { ["refId"] = Ent.SupB, ["name"] = Ent.CusA, ["amount"] = 2.0 }
        };
        var check = DisplayIdGuard.Check(rows, new[] { "refId", "name", "amount" }, new Dictionary<string, string> { [Ent.CusA] = "Al Noor Stores" });
        Assert.Equal(new[] { "name", "amount" }, check.Columns);
        Assert.Equal("Al Noor Stores", check.Rows[1]["name"]!.GetValue<string>());
        Assert.Equal(Ent.SupA, check.Rows[0]["refId"]!.GetValue<string>());              // kept internally
        Assert.Equal(2, check.Issues.Count);
    }

    [Theory]
    [InlineData("Top supplier is 6a8b1a66ef29e89c73d20001 with AED 5.", "Top supplier is ABC Trading with AED 5.", 1)]
    [InlineData("Customer 3f2504e0-4f89-11d3-9a0c-0305e82c3301 owes AED 5.", "Customer (not available) owes AED 5.", 1)]
    [InlineData("Barcode 123456789012345678901234 is fine.", "Barcode 123456789012345678901234 is fine.", 0)]
    public void Answer_text_never_contains_raw_ids(string answer, string expected, int replaced)
    {
        var (text, n) = DisplayIdGuard.CleanText(answer, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Ent.SupA] = "ABC Trading" });
        Assert.Equal(expected, text);
        Assert.Equal(replaced, n);
    }

    [Fact]
    public void Answer_rows_contain_only_displayed_columns()
    {
        var rows = new List<JsonObject> { new() { ["supplierName"] = "ABC", ["supplierId"] = Ent.SupA, ["amount"] = 1.0 } };
        var forAnswer = DisplayIdGuard.ForAnswer(rows, new[] { "supplierName", "amount" });
        Assert.DoesNotContain(Ent.SupA, forAnswer[0].ToJsonString());
    }
}
