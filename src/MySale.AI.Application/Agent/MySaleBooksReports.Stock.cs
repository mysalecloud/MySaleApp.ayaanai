using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

/// <summary>
/// Inventory-wide stock questions answered by ONE server-side stock engine (no generated query), with the rules of
/// <see cref="StockSemantics"/> — the same definition as the item report and the MySaleBooks Stock screen:
/// <list type="bullet">
/// <item><c>current</c> — current stock of every stock-tracked product (received − issued over all non-cancelled movements).</item>
/// <item><c>value</c> — current stock value (quantity × FIFO / landing cost, combos left out, as the Stock screen).</item>
/// <item><c>low</c> — stock above zero but below the item's reorder level (or minimum level) — only where a level is set.</item>
/// <item><c>out</c> — zero stock, including tracked products that never had a movement; <c>negative</c> — below zero.</item>
/// <item><c>highest</c> / <c>lowest</c> — products with the most / least stock (lowest = the smallest stock above zero).</item>
/// <item><c>byCategory</c> / <c>byWarehouse</c> — stock grouped by item category / warehouse (StockLocation).</item>
/// <item><c>asOf</c> — stock on a past day (movements up to that day plus all opening stock); value not recalculated.</item>
/// <item><c>movement</c> — movements inside a period by transaction type (received / issued).</item>
/// <item><c>fastMoving</c> / <c>slowMoving</c> / <c>nonMoving</c> — quantity sold in a period (SALE − SALE_RETURN) for
/// products in stock: most sold / least sold / none sold.</item>
/// </list>
/// Every pipeline runs through the normal validator, tenant scope, selected-store rule (store + shared "0" rows, as the
/// Stock screen) and the cancelled-document filter. Quantities of different items are never added together (their units
/// differ): totals are item counts and values.
/// </summary>
public sealed partial class MySaleBooksReports
{
    public const string StockSummaryReport = "stockSummary";

    public static readonly IReadOnlyList<string> StockViews = new[]
    {
        "current", "value", "low", "out", "negative", "highest", "lowest", "byCategory", "byWarehouse", "asOf", "movement",
        "fastMoving", "slowMoving", "nonMoving"
    };

    private const int DefaultListSize = 20;

    private static string NormalizeView(string? view)
    {
        var v = (view ?? "current").Trim();
        var match = StockViews.FirstOrDefault(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase));
        return match ?? (v.ToLowerInvariant() switch
        {
            "zero" or "outofstock" or "out_of_stock" => "out",
            "reorder" or "lowstock" or "low_stock" or "minimum" => "low",
            "category" or "bycategory" or "category_wise" => "byCategory",
            "warehouse" or "location" or "bywarehouse" or "warehouse_wise" => "byWarehouse",
            "fast" or "top" or "fastmoving" => "fastMoving",
            "slow" or "slowmoving" => "slowMoving",
            "dead" or "nonmoving" or "non_moving" => "nonMoving",
            "worth" or "valuation" => "value",
            _ => "current"
        });
    }

    private async Task<ReportOutcome> StockSummaryAsync(JsonObject args, DateAnchors anchors, MqlValidationContext context, AppSettings settings,
        int decimals, string? currency, Run run, CancellationToken ct, string? comment)
    {
        if (!context.Collections.Any(c => c.Name == StockSemantics.Collection) || !context.Collections.Any(c => c.Name == "Item"))
            return new ReportOutcome { Kind = "invalid", Message = "Stock data is not available for this company." };

        var view = NormalizeView(Str(args["view"]));
        var requested = (int)Num(args["limit"]);
        var limit = Math.Clamp(requested > 0 ? requested : DefaultListSize, 1, 50);

        // Optional filters: a category or a warehouse named in the question (resolved against the masters, never guessed).
        string? categoryId = null, categoryName = null, locationId = null, locationName = null;
        if (Str(args["category"]) is not null || Str(args["categoryId"]) is not null)
        {
            var pick = await PickEntityAsync("category", "category", args, new JsonObject { ["categoryName"] = 1 }, context, settings, run, ct, comment);
            if (pick.Outcome is not null) return pick.Outcome;
            categoryId = Str(pick.Record!["_id"]);
            categoryName = Str(pick.Record["categoryName"]);
        }
        if (Str(args["warehouse"]) is not null || Str(args["warehouseId"]) is not null)
        {
            var pick = await PickEntityAsync("warehouse", "warehouse", args, new JsonObject { ["stockLocationName"] = 1 }, context, settings, run, ct, comment);
            if (pick.Outcome is not null) return pick.Outcome;
            locationId = Str(pick.Record!["_id"]);
            locationName = Str(pick.Record["stockLocationName"]);
        }

        DateOnly? asOf = DateOnly.TryParseExact(Str(args["asOf"]) ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOfDay) ? asOfDay : null;
        if (asOf is { } future && future >= anchors.LocalToday) asOf = null;               // today / a future day = current stock
        if (view == "asOf" && asOf is null) view = "current";
        if (asOf is not null && view is "value") view = "asOf";                             // historical value is not recalculated

        var scope = StoreText(context.Store)
                    + (locationName is null ? string.Empty : $" (warehouse {locationName})")
                    + (categoryName is null ? string.Empty : $" (category {categoryName})");

        var outcome = view switch
        {
            "movement" => await StockMovementsByTypeAsync(args, anchors, context, settings, locationId, categoryId, scope, run, ct, comment),
            "byWarehouse" => await StockByWarehouseAsync(context, settings, categoryId, scope, run, ct, comment),
            "byCategory" => await StockByCategoryAsync(context, settings, decimals, currency, locationId, scope, run, ct, comment),
            "fastMoving" or "slowMoving" or "nonMoving" => await StockMovingAsync(view, args, anchors, context, settings, decimals, currency, locationId, categoryId, scope, limit, run, ct, comment),
            _ => await StockBalancesAsync(view, asOf, anchors, context, settings, decimals, currency, locationId, categoryId, scope, limit, run, ct, comment)
        };
        // Stock VALUE on a past day: explained (costing method), with the quantities on that day.
        if (asOf is { } valueDay && outcome.Kind == "ok" && string.Equals(Str(args["measure"]), "value", StringComparison.OrdinalIgnoreCase))
            return new ReportOutcome
            {
                Kind = outcome.Kind, Rows = outcome.Rows, Columns = outcome.Columns, Explanation = outcome.Explanation, Queries = outcome.Queries, ElapsedMs = outcome.ElapsedMs,
                Message = $"Stock value on {QuestionDates.Describe(valueDay)} depends on the MySaleBooks costing method (FIFO / average / last purchase) and is not recalculated here — " +
                          "please use the MySaleBooks Stock Register for it. The stock quantities on that day: " + outcome.Message
            };
        return outcome;
    }

    // ------------------------------------------------------------------ the stock engine (per item)

    /// <summary>
    /// Per-item balance pipeline: movements grouped by itemId (received / issued, optional FIFO layers and period sales),
    /// joined to the item master, restricted to stock-tracked items. Output fields: quantity, itemName, itemCode, unitId,
    /// categoryId, isCombo, level (reorder level, else minimum level) and — with cost — unitCost and value.
    /// </summary>
    internal static JsonArray BalancePipeline(DateRange? asOfEnd, DateRange? period, string? locationId, string? categoryId, bool withCost)
    {
        var stages = new JsonArray();
        var rowMatch = new JsonObject();
        if (locationId is not null) rowMatch["stockLocationId"] = locationId;
        if (asOfEnd is { } end)
            rowMatch["$or"] = new JsonArray(
                new JsonObject { ["transactionType"] = "OPSTOCK" },                                   // opening stock counts whatever its date
                new JsonObject { ["transactionDate"] = new JsonObject { ["$lt"] = DateNode(end.EndUtc) } });
        if (rowMatch.Count > 0) stages.Add(new JsonObject { ["$match"] = rowMatch });

        var (received, issued) = StockSemantics.Balance();
        var group = new JsonObject
        {
            ["_id"] = "$itemId",
            ["received"] = received,
            ["issued"] = issued,
            ["movements"] = new JsonObject { ["$sum"] = 1 }
        };
        if (withCost)
        {
            var (amount, qty) = StockSemantics.FifoLayers();
            group["fifoAmount"] = amount;
            group["fifoQty"] = qty;
        }
        if (period is { } p)
        {
            JsonNode inPeriod = new JsonObject
            {
                ["$and"] = new JsonArray(
                    new JsonObject { ["$gte"] = new JsonArray("$transactionDate", DateNode(p.StartUtc)) },
                    new JsonObject { ["$lt"] = new JsonArray("$transactionDate", DateNode(p.EndUtc)) })
            };
            group["sold"] = StockSemantics.NetSold(inPeriod);
            group["lastSale"] = new JsonObject
            {
                ["$max"] = new JsonObject
                {
                    ["$cond"] = new JsonArray(
                        new JsonObject { ["$and"] = new JsonArray(new JsonObject { ["$eq"] = new JsonArray("$transactionType", "SALE") }, new JsonObject { ["$eq"] = new JsonArray("$" + StockSemantics.Pipe, "OUT") }) },
                        "$transactionDate", null)
                }
            };
        }
        stages.Add(new JsonObject { ["$group"] = group });
        stages.Add(new JsonObject
        {
            ["$addFields"] = new JsonObject
            {
                ["quantity"] = new JsonObject { ["$subtract"] = new JsonArray("$received", "$issued") },
                ["itemObj"] = new JsonObject { ["$convert"] = new JsonObject { ["input"] = "$_id", ["to"] = "objectId", ["onError"] = null, ["onNull"] = null } }
            }
        });
        stages.Add(new JsonObject { ["$lookup"] = new JsonObject { ["from"] = "Item", ["localField"] = "itemObj", ["foreignField"] = "_id", ["as"] = "item" } });
        stages.Add(new JsonObject { ["$unwind"] = "$item" });
        var itemMatch = StockSemantics.TrackedItem("item");
        if (categoryId is not null) itemMatch["item.categoryId"] = categoryId;
        stages.Add(new JsonObject { ["$match"] = itemMatch });

        var fields = new JsonObject
        {
            ["itemName"] = "$item.itemName",
            ["itemCode"] = "$item.itemCode",
            ["unitId"] = "$item.unitId",
            ["categoryId"] = "$item.categoryId",
            ["isCombo"] = new JsonObject { ["$ifNull"] = new JsonArray("$item.isCombo", false) },
            ["level"] = LevelExpression(),
            // Low stock (as the MySaleBooks Stock Level report, strict "<"): a level is set, stock is above zero and below it.
            ["isLow"] = new JsonObject
            {
                ["$and"] = new JsonArray(
                    new JsonObject { ["$gt"] = new JsonArray(LevelExpression(), 0) },
                    new JsonObject { ["$gt"] = new JsonArray("$quantity", 0) },
                    new JsonObject { ["$lt"] = new JsonArray("$quantity", LevelExpression()) })
            }
        };
        if (withCost)
        {
            fields["unitCost"] = StockSemantics.UnitCostExpression("item");
            fields["value"] = new JsonObject { ["$multiply"] = new JsonArray("$quantity", StockSemantics.UnitCostExpression("item")) };
        }
        stages.Add(new JsonObject { ["$addFields"] = fields });
        return stages;
    }

    /// <summary>Item.reOrderLevel when set (&gt; 0), else Item.minimumStockQty (0 when neither is set).</summary>
    private static JsonObject LevelExpression() => new()
    {
        ["$cond"] = new JsonArray(
            new JsonObject { ["$gt"] = new JsonArray(new JsonObject { ["$ifNull"] = new JsonArray("$item.reOrderLevel", 0) }, 0) },
            "$item.reOrderLevel",
            new JsonObject { ["$ifNull"] = new JsonArray("$item.minimumStockQty", 0) })
    };

    private static JsonObject Cond(JsonNode test, JsonNode yes, JsonNode no) => new() { ["$cond"] = new JsonArray(test, yes, no) };
    private static JsonObject CountIf(JsonNode test) => new() { ["$sum"] = Cond(test, 1, 0) };
    private static JsonObject Cmp(string op, string field, JsonNode value) => new() { [op] = new JsonArray("$" + field, value) };

    /// <summary>Row projection of the item lists.</summary>
    private static JsonObject ItemRowProjection(bool withCost, bool withPeriod = false) => new JsonObject
    {
        ["itemName"] = 1, ["itemCode"] = 1, ["unitId"] = 1, ["categoryId"] = 1, ["quantity"] = 1, ["level"] = 1, ["isLow"] = 1
    }.Also(p =>
    {
        if (withCost) { p["value"] = 1; p["unitCost"] = 1; }                   // only fields the pipeline computed (validator)
        if (withPeriod) { p["sold"] = 1; p["lastSale"] = 1; }
    });

    // ------------------------------------------------------------------ balances: current / value / low / out / negative / highest / lowest / asOf

    private async Task<ReportOutcome> StockBalancesAsync(string view, DateOnly? asOf, DateAnchors anchors, MqlValidationContext context, AppSettings settings,
        int decimals, string? currency, string? locationId, string? categoryId, string scope, int limit, Run run, CancellationToken ct, string? comment)
    {
        var historical = asOf is not null;
        var withCost = !historical;                                  // value only for current stock (as the Stock screen)
        DateRange? end = asOf is { } day ? DateAnchors.ForLocalDays(day, day, anchors.BoundaryTimeZoneId) : null;
        var pipeline = BalancePipeline(end, null, locationId, categoryId, withCost);

        // Which rows the list shows, and in which order.
        JsonObject? listMatch;
        JsonObject sort;
        switch (view)
        {
            case "low":
                listMatch = new JsonObject { ["isLow"] = true };
                sort = new JsonObject { ["quantity"] = 1, ["itemName"] = 1 };
                break;
            case "out":
                listMatch = new JsonObject { ["quantity"] = 0 };
                sort = new JsonObject { ["itemName"] = 1 };
                break;
            case "negative":
                listMatch = new JsonObject { ["quantity"] = new JsonObject { ["$lt"] = 0 } };
                sort = new JsonObject { ["quantity"] = 1, ["itemName"] = 1 };
                break;
            case "highest":
                listMatch = new JsonObject { ["quantity"] = new JsonObject { ["$gt"] = 0 } };
                sort = new JsonObject { ["quantity"] = -1, ["itemName"] = 1 };
                break;
            case "lowest":
                listMatch = new JsonObject { ["quantity"] = new JsonObject { ["$gt"] = 0 } };
                sort = new JsonObject { ["quantity"] = 1, ["itemName"] = 1 };
                break;
            case "value":
                listMatch = new JsonObject { ["quantity"] = new JsonObject { ["$ne"] = 0 }, ["isCombo"] = new JsonObject { ["$ne"] = true } };
                sort = new JsonObject { ["value"] = -1, ["itemName"] = 1 };
                break;
            default:                                                  // current / asOf: every product with stock ≠ 0
                listMatch = new JsonObject { ["quantity"] = new JsonObject { ["$ne"] = 0 } };
                sort = withCost ? new JsonObject { ["value"] = -1, ["itemName"] = 1 } : new JsonObject { ["itemName"] = 1 };
                break;
        }

        var summary = new JsonObject
        {
            ["_id"] = null,
            ["products"] = new JsonObject { ["$sum"] = 1 },
            ["inStock"] = CountIf(Cmp("$gt", "quantity", 0)),
            ["zero"] = CountIf(Cmp("$eq", "quantity", 0)),
            ["negative"] = CountIf(Cmp("$lt", "quantity", 0)),
            ["withLevel"] = CountIf(Cmp("$gt", "level", 0)),
            ["low"] = CountIf(Cmp("$eq", "isLow", true))
        };
        if (withCost)
        {
            // Stock screen total: Σ quantity × cost of every non-combo item (negative stock reduces it, as there).
            summary["value"] = new JsonObject { ["$sum"] = Cond(Cmp("$ne", "isCombo", true), "$value", 0) };
            summary["valueInStock"] = new JsonObject { ["$sum"] = Cond(new JsonObject { ["$and"] = new JsonArray(Cmp("$ne", "isCombo", true), Cmp("$gt", "quantity", 0)) }, "$value", 0) };
            summary["noCost"] = CountIf(new JsonObject { ["$and"] = new JsonArray(Cmp("$gt", "quantity", 0), Cmp("$lte", "unitCost", 0)) });
        }
        var rowsPipeline = new JsonArray();
        if (listMatch is not null) rowsPipeline.Add(new JsonObject { ["$match"] = listMatch });
        rowsPipeline.Add(new JsonObject { ["$sort"] = sort });
        rowsPipeline.Add(new JsonObject { ["$limit"] = limit });
        rowsPipeline.Add(new JsonObject { ["$project"] = ItemRowProjection(withCost) });
        pipeline.Add(new JsonObject { ["$facet"] = new JsonObject { ["summary"] = new JsonArray(new JsonObject { ["$group"] = summary }), ["rows"] = rowsPipeline } });

        var result = await AggregateAsync(StockSemantics.Collection, pipeline, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();
        var facet = result.Rows.FirstOrDefault();
        var s = (facet?["summary"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? new JsonObject();
        var listed = (facet?["rows"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();

        var products = (int)Num(s["products"]);
        var inStock = (int)Num(s["inStock"]);
        var zero = (int)Num(s["zero"]);
        var negative = (int)Num(s["negative"]);
        var withLevel = (int)Num(s["withLevel"]);
        var low = (int)Num(s["low"]);
        var totalValue = Math.Round(Num(s["value"]), decimals);

        // Tracked products that never had a movement also have zero stock (the Stock screen starts from the item list).
        var neverMoved = view == "out" ? await NeverMovedAsync(categoryId, locationId, limit, context, settings, run, ct, comment) : (Count: 0, Rows: new List<JsonObject>(), Failed: false);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();

        var units = await UnitsAsync(listed.Concat(neverMoved.Rows).Select(r => Str(r["unitId"])).OfType<string>().Distinct().ToList(), context, settings, run, ct, comment);
        var asOfText = asOf is { } a ? $" on {QuestionDates.Describe(a)}" : string.Empty;
        var costNote = "FIFO cost of the unconsumed purchase layers, or the item's landing cost — as the MySaleBooks Stock screen";

        JsonObject Row(JsonObject r, bool showLevel = false)
        {
            var unit = Str(r["unitId"]) is { } uid && units.TryGetValue(uid, out var u) ? u : new UnitInfo(null, 0, null);
            var qty = Num(r["quantity"]);
            var row = new JsonObject
            {
                ["product"] = Str(r["itemName"]) ?? "(no name)",
                ["code"] = Str(r["itemCode"]),
                ["stock"] = FormatQuantity(qty, unit),
                ["quantity"] = qty,
                ["unit"] = unit.Name
            };
            if (showLevel) row["reorderLevel"] = FormatQuantity(Num(r["level"]), unit);
            if (withCost && r["value"] is not null) row["value"] = Math.Round(Num(r["value"]), decimals);
            return row;
        }

        List<string> Columns(bool showLevel) => new List<string> { "product", "code", "stock", "quantity", "unit" }
            .Concat(showLevel ? new[] { "reorderLevel" } : Array.Empty<string>())
            .Concat(withCost && view is not ("out" or "low") ? new[] { "value" } : Array.Empty<string>()).ToList();

        ReportOutcome Done(string message, List<JsonObject> rows, List<string> columns, string explanation) => new()
        {
            Kind = "ok", Message = message, Rows = rows, Columns = columns, Explanation = explanation,
            Truncated = false, Queries = run.Queries, ElapsedMs = run.Ms
        };

        if (products == 0 && neverMoved.Count == 0)
            return Done($"There are no stock movements for stock-tracked products{scope}{(asOf is { } upTo ? " up to " + QuestionDates.Describe(upTo) : string.Empty)}, so there is no stock to show.",
                new List<JsonObject>(), new List<string>(), "No stock-tracked product has a non-cancelled stock movement in this scope.");

        var counts = $"{inStock} in stock, {zero + neverMoved.Count} at zero and {negative} negative";
        switch (view)
        {
            case "low":
            {
                if (withLevel == 0)
                    return Done($"No reorder level or minimum stock level is set for any product{scope}, so I can't tell which products are low in stock. " +
                                $"Set the reorder level in the item master (MySaleBooks → Items). For reference: {counts}.",
                        new List<JsonObject>(), new List<string>(),
                        "Low stock needs Item.reOrderLevel or Item.minimumStockQty (> 0); none is configured, so nothing is invented.");
                var rows = listed.Select(r => Row(r, showLevel: true)).ToList();
                var head = low == 0
                    ? $"No product is below its reorder level{scope}."
                    : $"{low} product{Plural(low)} {(low == 1 ? "is" : "are")} low in stock{scope} (above zero but below the reorder level)" + (low > rows.Count ? $"; the {rows.Count} lowest are shown." : ":");
                var tail = $" {withLevel} of {products} products have a reorder or minimum level set." + (zero + negative + neverMoved.Count > 0 ? $" Out of stock or negative: {zero + negative} product{Plural(zero + negative)} (ask “Which products are out of stock?”)." : string.Empty);
                return Done(head + tail + ListPreview(rows), rows, Columns(true),
                    $"Low stock{scope}: current stock > 0 and below Item.reOrderLevel (or Item.minimumStockQty when no reorder level is set), strict “<” as the MySaleBooks Stock Level report; stock from movements (received − issued).");
            }
            case "out":
            {
                var rows = listed.Select(r => Row(r)).Concat(neverMoved.Rows.Select(r => Row(r)).Take(Math.Max(0, limit - listed.Count))).ToList();
                var total = zero + neverMoved.Count;
                var head = total == 0 ? $"No stock-tracked product is out of stock{scope}." : $"{total} product{Plural(total)} {(total == 1 ? "is" : "are")} out of stock{scope} (stock exactly 0)" + (total > rows.Count ? $"; the first {rows.Count} are shown." : ":");
                var extra = (neverMoved.Count > 0 ? $" {neverMoved.Count} of them never had a stock movement." : string.Empty)
                            + (neverMoved.Failed ? " Products that never had a movement could not be checked this time." : string.Empty)
                            + (negative > 0 ? $" {negative} more product{Plural(negative)} {(negative == 1 ? "has" : "have")} negative stock (more issued than received) — ask “Which products have negative stock?”." : string.Empty);
                return Done(head + extra + ListPreview(rows), rows, Columns(false),
                    $"Out of stock{scope}: stock-tracked products whose current stock (received − issued over all movements) is exactly 0, plus tracked products without any movement.");
            }
            case "negative":
            {
                var rows = listed.Select(r => Row(r)).ToList();
                var head = negative == 0 ? $"No product has negative stock{scope}." : $"{negative} product{Plural(negative)} {(negative == 1 ? "has" : "have")} negative stock{scope} — more has been issued than received" + (negative > rows.Count ? $"; the {rows.Count} most negative are shown." : ":");
                return Done(head + ListPreview(rows), rows, Columns(false), $"Negative stock{scope}: current stock below 0.");
            }
            case "highest" or "lowest":
            {
                var rows = listed.Select(r => Row(r)).ToList();
                var head = inStock == 0 ? $"No product is in stock{scope}." :
                    view == "highest" ? $"Products with the highest stock{scope} (by quantity in each product's own unit):" : $"Products with the lowest stock above zero{scope}:";
                return Done(head + ListPreview(rows) + " Quantities of different products are in their own units, so they are not added together.", rows, Columns(false),
                    $"{(view == "highest" ? "Highest" : "Lowest")} current stock{scope} per product (stock > 0).");
            }
            case "value":
            {
                var rows = listed.Select(r => Row(r)).ToList();
                var noCost = (int)Num(s["noCost"]);
                var message = $"Total stock value{scope}: {Money(totalValue, decimals, currency)} for {inStock} product{Plural(inStock)} in stock ({costNote}; combo items excluded)."
                              + (negative > 0 ? $" {negative} product{Plural(negative)} with negative stock reduce{(negative == 1 ? "s" : string.Empty)} this total, as on the Stock screen." : string.Empty)
                              + (noCost > 0 ? $" {noCost} product{Plural(noCost)} in stock {(noCost == 1 ? "has" : "have")} no cost price, so {(noCost == 1 ? "it adds" : "they add")} 0 to the value." : string.Empty)
                              + (rows.Count > 0 ? $" Highest values:{ListPreview(rows, withValue: true, decimals: decimals, currency: currency)}" : string.Empty);
                return Done(message, rows, Columns(false),
                    $"Current stock value{scope}: Σ over non-combo products of current quantity × unit cost ({costNote}).");
            }
            default:                                                                                  // current / asOf
            {
                var rows = listed.Select(r => Row(r)).ToList();
                var shown = inStock + negative;
                var head = asOf is null
                    ? $"Current stock{scope}: {counts} ({products} stock-tracked product{Plural(products)} with movements)."
                    : $"Stock{scope}{asOfText}: {counts}.";
                var valueText = withCost ? $" Total stock value: {Money(totalValue, decimals, currency)} ({costNote})." :
                    " Stock value on a past date depends on the MySaleBooks costing method and is not recalculated here — please use the MySaleBooks Stock Register for it.";
                var listText = rows.Count == 0 ? string.Empty : shown > rows.Count ? $" The {rows.Count} {(withCost ? "highest-value" : "first")} products are listed." : " All products with stock are listed.";
                return Done(head + valueText + listText, rows, Columns(false),
                    $"{(asOf is null ? "Current stock" : "Stock" + asOfText)}{scope} per product: received − issued over all non-cancelled movements (opening stock, purchases, sales, returns, adjustments and transfers), in each product's stock unit.");
            }
        }
    }

    /// <summary>Stock-tracked products without any stock movement (stock 0). Best effort: a failure is reported, not hidden.</summary>
    private async Task<(int Count, List<JsonObject> Rows, bool Failed)> NeverMovedAsync(string? categoryId, string? locationId, int limit,
        MqlValidationContext context, AppSettings settings, Run run, CancellationToken ct, string? comment)
    {
        if (locationId is not null) return (0, new List<JsonObject>(), false);         // "no movement in this warehouse" is not "never moved"
        var match = StockSemantics.TrackedItem();
        match["isCanceled"] = new JsonObject { ["$ne"] = true };
        match["isDeleted"] = new JsonObject { ["$ne"] = true };
        if (categoryId is not null) match["categoryId"] = categoryId;
        var pipeline = new JsonArray
        {
            new JsonObject { ["$match"] = match },
            new JsonObject { ["$addFields"] = new JsonObject { ["itemKey"] = new JsonObject { ["$toString"] = "$_id" } } },
            new JsonObject { ["$lookup"] = new JsonObject { ["from"] = StockSemantics.Collection, ["localField"] = "itemKey", ["foreignField"] = "itemId", ["as"] = "moves" } },
            new JsonObject { ["$match"] = new JsonObject { ["moves"] = new JsonObject { ["$size"] = 0 } } },
            new JsonObject
            {
                ["$facet"] = new JsonObject
                {
                    ["summary"] = new JsonArray(new JsonObject { ["$count"] = "n" }),
                    ["rows"] = new JsonArray(
                        new JsonObject { ["$sort"] = new JsonObject { ["itemName"] = 1 } },
                        new JsonObject { ["$limit"] = limit },
                        new JsonObject { ["$project"] = new JsonObject { ["itemName"] = 1, ["itemCode"] = 1, ["unitId"] = 1, ["quantity"] = new JsonObject { ["$literal"] = 0 } } })
                }
            }
        };
        try
        {
            var r = await AggregateAsync("Item", pipeline, context, settings, run, ct, comment, optional: true);
            var facet = r.Rows.FirstOrDefault();
            var count = (int)Num((facet?["summary"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["n"]);
            var rows = (facet?["rows"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            return (count, rows, facet is null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, new List<JsonObject>(), true);                                 // e.g. an item with very many movements
        }
    }

    // ------------------------------------------------------------------ moving analysis

    private async Task<ReportOutcome> StockMovingAsync(string view, JsonObject args, DateAnchors anchors, MqlValidationContext context, AppSettings settings,
        int decimals, string? currency, string? locationId, string? categoryId, string scope, int limit, Run run, CancellationToken ct, string? comment)
    {
        var (from, to, defaulted) = MovingPeriod(args, anchors);
        var range = DateAnchors.ForLocalDays(from, to, anchors.BoundaryTimeZoneId);
        var pipeline = BalancePipeline(null, range, locationId, categoryId, withCost: false);
        JsonObject listMatch = view switch
        {
            "fastMoving" => new JsonObject { ["sold"] = new JsonObject { ["$gt"] = 0 } },
            "nonMoving" => new JsonObject { ["quantity"] = new JsonObject { ["$gt"] = 0 }, ["sold"] = new JsonObject { ["$lte"] = 0 } },
            _ => new JsonObject { ["quantity"] = new JsonObject { ["$gt"] = 0 } }                      // slow: in stock, least sold first
        };
        var sort = view == "fastMoving" ? new JsonObject { ["sold"] = -1, ["itemName"] = 1 } : new JsonObject { ["sold"] = 1, ["quantity"] = -1, ["itemName"] = 1 };
        pipeline.Add(new JsonObject
        {
            ["$facet"] = new JsonObject
            {
                ["summary"] = new JsonArray(new JsonObject
                {
                    ["$group"] = new JsonObject
                    {
                        ["_id"] = null,
                        ["inStock"] = CountIf(Cmp("$gt", "quantity", 0)),
                        ["sold"] = CountIf(Cmp("$gt", "sold", 0)),
                        ["notSold"] = CountIf(new JsonObject { ["$and"] = new JsonArray(Cmp("$gt", "quantity", 0), Cmp("$lte", "sold", 0)) })
                    }
                }),
                ["rows"] = new JsonArray(
                    new JsonObject { ["$match"] = listMatch },
                    new JsonObject { ["$sort"] = sort },
                    new JsonObject { ["$limit"] = limit },
                    new JsonObject { ["$project"] = ItemRowProjection(false, withPeriod: true) })
            }
        });
        var result = await AggregateAsync(StockSemantics.Collection, pipeline, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();
        var facet = result.Rows.FirstOrDefault();
        var s = (facet?["summary"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? new JsonObject();
        var listed = (facet?["rows"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var units = await UnitsAsync(listed.Select(r => Str(r["unitId"])).OfType<string>().Distinct().ToList(), context, settings, run, ct, comment);

        var period = $"{QuestionDates.Describe(from)} – {QuestionDates.Describe(to)}" + (defaulted ? " (the last 30 days; no period was given)" : string.Empty);
        var rows = listed.Select(r =>
        {
            var unit = Str(r["unitId"]) is { } uid && units.TryGetValue(uid, out var u) ? u : new UnitInfo(null, 0, null);
            return new JsonObject
            {
                ["product"] = Str(r["itemName"]) ?? "(no name)",
                ["code"] = Str(r["itemCode"]),
                ["sold"] = FormatQuantity(Num(r["sold"]), unit),
                ["soldQuantity"] = Num(r["sold"]),
                ["stock"] = FormatQuantity(Num(r["quantity"]), unit),
                ["lastSale"] = DateText(r["lastSale"], anchors) ?? "—"
            };
        }).ToList();
        var inStock = (int)Num(s["inStock"]);
        var notSold = (int)Num(s["notSold"]);
        var soldCount = (int)Num(s["sold"]);
        var columns = new List<string> { "product", "code", "sold", "soldQuantity", "stock", "lastSale" };
        var basis = "Quantity sold = sales − sales returns in the period (stock movements), in each product's unit.";
        return view switch
        {
            "fastMoving" => new ReportOutcome
            {
                Kind = "ok",
                Message = soldCount == 0 ? $"No product was sold{scope} in {period}." : $"Fastest-moving products{scope} in {period} (by quantity sold):{ListPreview(rows, sold: true)} {basis}",
                Rows = rows, Columns = columns, Explanation = $"Fast-moving products{scope}, {period}. {basis}", Queries = run.Queries, ElapsedMs = run.Ms
            },
            "nonMoving" => new ReportOutcome
            {
                Kind = "ok",
                Message = notSold == 0 ? $"Every product in stock{scope} was sold at least once in {period}." :
                    $"{notSold} of {inStock} product{Plural(inStock)} in stock{scope} had no sales in {period}" + (notSold > rows.Count ? $"; {rows.Count} are shown (largest stock first)." : ":") + ListPreview(rows),
                Rows = rows, Columns = columns, Explanation = $"Non-moving products{scope}: in stock now, no net sale in {period}.", Queries = run.Queries, ElapsedMs = run.Ms
            },
            _ => new ReportOutcome
            {
                Kind = "ok",
                Message = inStock == 0 ? $"No product is in stock{scope}." : $"Slowest-moving products in stock{scope} in {period} (least sold first; {notSold} had no sales at all):{ListPreview(rows, sold: true)} {basis}",
                Rows = rows, Columns = columns, Explanation = $"Slow-moving products{scope}: in stock now, ranked by quantity sold in {period}. {basis}", Queries = run.Queries, ElapsedMs = run.Ms
            }
        };
    }

    private static (DateOnly From, DateOnly To, bool Defaulted) MovingPeriod(JsonObject args, DateAnchors anchors)
    {
        DateOnly? Parse(string? s) => DateOnly.TryParseExact(s ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var today = anchors.LocalToday;
        var from = Parse(Str(args["from"]));
        var to = Parse(Str(args["to"]));
        if (from is null && to is null) return (today.AddDays(-29), today, true);
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        return start <= end ? (start, end, false) : (end, start, false);
    }

    // ------------------------------------------------------------------ movements by type (period)

    private async Task<ReportOutcome> StockMovementsByTypeAsync(JsonObject args, DateAnchors anchors, MqlValidationContext context, AppSettings settings,
        string? locationId, string? categoryId, string scope, Run run, CancellationToken ct, string? comment)
    {
        var (from, to, _) = Period(args, anchors);
        var range = DateAnchors.ForLocalDays(from, to, anchors.BoundaryTimeZoneId);
        var match = new JsonObject { ["transactionDate"] = Between(range) };
        if (locationId is not null) match["stockLocationId"] = locationId;
        var pipeline = new JsonArray { new JsonObject { ["$match"] = match } };
        if (categoryId is not null)
        {
            pipeline.Add(new JsonObject { ["$addFields"] = new JsonObject { ["itemObj"] = new JsonObject { ["$convert"] = new JsonObject { ["input"] = "$itemId", ["to"] = "objectId", ["onError"] = null, ["onNull"] = null } } } });
            pipeline.Add(new JsonObject { ["$lookup"] = new JsonObject { ["from"] = "Item", ["localField"] = "itemObj", ["foreignField"] = "_id", ["as"] = "item" } });
            pipeline.Add(new JsonObject { ["$match"] = new JsonObject { ["item.categoryId"] = categoryId } });
        }
        pipeline.Add(new JsonObject
        {
            ["$group"] = new JsonObject
            {
                ["_id"] = new JsonObject { ["type"] = "$transactionType", ["pipe"] = "$" + StockSemantics.Pipe },
                ["lines"] = new JsonObject { ["$sum"] = 1 },
                ["received"] = StockSemantics.PipeSum("IN", "stockIn"),
                ["issued"] = StockSemantics.PipeSum("OUT", "stockOut"),
                ["products"] = new JsonObject { ["$addToSet"] = "$itemId" }
            }
        });
        pipeline.Add(new JsonObject { ["$addFields"] = new JsonObject { ["productCount"] = new JsonObject { ["$size"] = "$products" } } });
        pipeline.Add(new JsonObject { ["$project"] = new JsonObject { ["products"] = 0 } });
        pipeline.Add(new JsonObject { ["$sort"] = new JsonObject { ["_id.pipe"] = 1, ["_id.type"] = 1 } });
        var result = await AggregateAsync(StockSemantics.Collection, pipeline, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();

        var period = $"{QuestionDates.Describe(from)} – {QuestionDates.Describe(to)}";
        var rows = result.Rows.Select(r =>
        {
            var type = Str(r["type"]) ?? Str(r["_id_type"]) ?? "?";
            var pipe = Str(r["pipe"]) ?? Str(r["_id_pipe"]) ?? "?";
            return new JsonObject
            {
                ["movement"] = StockSemantics.Label(type),
                ["direction"] = pipe == "IN" ? "In" : pipe == "OUT" ? "Out" : pipe,
                ["lines"] = Num(r["lines"]),
                ["products"] = Num(r["productCount"]),
                ["quantity"] = pipe == "IN" ? Num(r["received"]) : Num(r["issued"])
            };
        }).ToList();
        if (rows.Count == 0)
            return new ReportOutcome { Kind = "ok", Message = $"There were no stock movements{scope} in {period}.", Queries = run.Queries, ElapsedMs = run.Ms,
                Explanation = $"Stock movements{scope}, {period}: none." };
        var lines = rows.Sum(r => Num(r["lines"]));
        return new ReportOutcome
        {
            Kind = "ok",
            Message = $"Stock movements{scope} in {period}: {lines:0} movement line{Plural((int)lines)} — " +
                      string.Join("; ", rows.Select(r => $"{Str(r["movement"])} ({(Str(r["direction"]) ?? string.Empty).ToLowerInvariant()}): {Num(r["lines"]):0} line{Plural((int)Num(r["lines"]))}, {Num(r["products"]):0} product{Plural((int)Num(r["products"]))}")) +
                      ". Quantities are shown per type in each product's own unit and are not added across products.",
            Rows = rows,
            Columns = new() { "movement", "direction", "lines", "products", "quantity" },
            Explanation = $"Stock movements{scope}, {period}, by transaction type (IN rows add stockIn, OUT rows remove stockOut; transfers move stock between warehouses).",
            Queries = run.Queries,
            ElapsedMs = run.Ms
        };
    }

    // ------------------------------------------------------------------ grouped views

    private async Task<ReportOutcome> StockByCategoryAsync(MqlValidationContext context, AppSettings settings, int decimals, string? currency,
        string? locationId, string scope, Run run, CancellationToken ct, string? comment)
    {
        var pipeline = BalancePipeline(null, null, locationId, null, withCost: true);
        pipeline.Add(new JsonObject
        {
            ["$group"] = new JsonObject
            {
                ["_id"] = "$categoryId",
                ["products"] = CountIf(Cmp("$gt", "quantity", 0)),
                ["negative"] = CountIf(Cmp("$lt", "quantity", 0)),
                ["value"] = new JsonObject { ["$sum"] = Cond(Cmp("$ne", "isCombo", true), "$value", 0) }
            }
        });
        pipeline.Add(new JsonObject { ["$sort"] = new JsonObject { ["value"] = -1 } });
        var result = await AggregateAsync(StockSemantics.Collection, pipeline, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();
        var names = await EntityNamesAsync("category", result.Rows.Select(r => Str(r["_id"])), context, settings, run, ct, comment);
        var rows = result.Rows.Select(r => new JsonObject
        {
            ["category"] = names.Label(Str(r["_id"])),
            ["categoryId"] = Str(r["_id"]),      // internal (drill-down); not a display column
            ["productsInStock"] = Num(r["products"]),
            ["negativeProducts"] = Num(r["negative"]),
            ["value"] = Math.Round(Num(r["value"]), decimals)
        }).ToList();
        if (rows.Count == 0)
            return new ReportOutcome { Kind = "ok", Message = $"There is no stock{scope} to group by category.", Queries = run.Queries, ElapsedMs = run.Ms, Explanation = "No stock movements." };
        var top = rows.Take(5).Select(r => $"{Str(r["category"])}: {Money(Num(r["value"]), decimals, currency)} ({Num(r["productsInStock"]):0} product{Plural((int)Num(r["productsInStock"]))} in stock)");
        return new ReportOutcome
        {
            Kind = "ok",
            Message = $"Stock by category{scope}, ranked by current stock value (quantities of different products are in different units, so value is used to compare categories): " +
                      string.Join("; ", top) + (rows.Count > 5 ? $"; {rows.Count - 5} more categor{(rows.Count - 5 == 1 ? "y" : "ies")} in the table." : "."),
            Rows = rows,
            Columns = new() { "category", "productsInStock", "negativeProducts", "value" },
            Explanation = $"Current stock value per item category{scope} (FIFO / landing cost as the Stock screen; combo items excluded).",
            Queries = run.Queries,
            ElapsedMs = run.Ms
        };
    }

    private async Task<ReportOutcome> StockByWarehouseAsync(MqlValidationContext context, AppSettings settings, string? categoryId, string scope, Run run,
        CancellationToken ct, string? comment)
    {
        var (received, issued) = StockSemantics.Balance();
        var pipeline = new JsonArray
        {
            new JsonObject { ["$group"] = new JsonObject { ["_id"] = new JsonObject { ["item"] = "$itemId", ["location"] = "$stockLocationId" }, ["received"] = received, ["issued"] = issued } },
            new JsonObject
            {
                ["$addFields"] = new JsonObject
                {
                    ["quantity"] = new JsonObject { ["$subtract"] = new JsonArray("$received", "$issued") },
                    ["itemObj"] = new JsonObject { ["$convert"] = new JsonObject { ["input"] = "$_id.item", ["to"] = "objectId", ["onError"] = null, ["onNull"] = null } }
                }
            },
            new JsonObject { ["$lookup"] = new JsonObject { ["from"] = "Item", ["localField"] = "itemObj", ["foreignField"] = "_id", ["as"] = "item" } },
            new JsonObject { ["$unwind"] = "$item" },
            new JsonObject { ["$match"] = StockSemantics.TrackedItem("item").Also(m => { if (categoryId is not null) m["item.categoryId"] = categoryId; }) },
            new JsonObject
            {
                ["$group"] = new JsonObject
                {
                    ["_id"] = "$_id.location",
                    ["inStock"] = CountIf(Cmp("$gt", "quantity", 0)),
                    ["zero"] = CountIf(Cmp("$eq", "quantity", 0)),
                    ["negative"] = CountIf(Cmp("$lt", "quantity", 0))
                }
            },
            new JsonObject { ["$sort"] = new JsonObject { ["inStock"] = -1 } }
        };
        var result = await AggregateAsync(StockSemantics.Collection, pipeline, context, settings, run, ct, comment);
        if (run.Error is not null || run.StoreMissing) return new ReportOutcome();
        var names = await LocationNamesAsync(result.Rows.Select(r => Str(r["_id"])).OfType<string>().ToList(), context, settings, run, ct, comment);
        var rows = result.Rows.Select(r => new JsonObject
        {
            ["warehouse"] = names.Label(Str(r["_id"])),
            ["stockLocationId"] = Str(r["_id"]), // internal (drill-down); not a display column
            ["productsInStock"] = Num(r["inStock"]),
            ["zeroStock"] = Num(r["zero"]),
            ["negativeStock"] = Num(r["negative"])
        }).ToList();
        if (rows.Count == 0)
            return new ReportOutcome { Kind = "ok", Message = $"There is no stock{scope} to group by warehouse.", Queries = run.Queries, ElapsedMs = run.Ms, Explanation = "No stock movements." };
        return new ReportOutcome
        {
            Kind = "ok",
            Message = $"Stock by warehouse{scope}: " + string.Join("; ", rows.Take(6).Select(r => $"{Str(r["warehouse"])}: {Num(r["productsInStock"]):0} product{Plural((int)Num(r["productsInStock"]))} in stock" + (Num(r["negativeStock"]) > 0 ? $", {Num(r["negativeStock"]):0} negative" : string.Empty))) +
                      (rows.Count > 6 ? $"; {rows.Count - 6} more in the table." : ".") + " Ask “stock of <product>” for one product's quantity per warehouse.",
            Rows = rows,
            Columns = new() { "warehouse", "productsInStock", "zeroStock", "negativeStock" },
            Explanation = $"Current stock per warehouse (StockLocation){scope}: products with stock above zero, at zero and below zero (received − issued per warehouse; transfers move stock between warehouses).",
            Queries = run.Queries,
            ElapsedMs = run.Ms
        };
    }

    // ------------------------------------------------------------------ helpers

    private static string Plural(int n) => n == 1 ? string.Empty : "s";

    /// <summary>" A (12 Pcs), B (3 Box 2 Pcs), …" — the first rows of a list, for the verified sentence.</summary>
    private static string ListPreview(List<JsonObject> rows, bool withValue = false, bool sold = false, int decimals = 2, string? currency = null)
    {
        if (rows.Count == 0) return string.Empty;
        var parts = rows.Take(5).Select(r =>
        {
            var name = Str(r["product"]) ?? "?";
            var detail = withValue ? Money(Num(r["value"]), decimals, currency) : sold ? Str(r["sold"]) + " sold" : Str(r["stock"]);
            return $"{name} ({detail})";
        });
        return " " + string.Join(", ", parts) + (rows.Count > 5 ? $" and {rows.Count - 5} more in the table." : ".");
    }

    /// <summary>Units of many items in two queries: the units, then their sub units (Unit[parentId]).</summary>
    private async Task<Dictionary<string, UnitInfo>> UnitsAsync(List<string> unitIds, MqlValidationContext context, AppSettings settings, Run run,
        CancellationToken ct, string? comment)
    {
        var map = new Dictionary<string, UnitInfo>(StringComparer.OrdinalIgnoreCase);
        var valid = unitIds.Where(i => Regex.IsMatch(i, "^[0-9a-fA-F]{24}$")).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        if (valid.Count == 0 || !context.Collections.Any(c => c.Name == "Unit")) return map;
        async Task<List<JsonObject>> Load(IEnumerable<string> ids) => (await AggregateAsync("Unit", new JsonArray
        {
            new JsonObject { ["$match"] = new JsonObject { ["_id"] = new JsonObject { ["$in"] = new JsonArray(ids.Select(i => (JsonNode)new JsonObject { ["$oid"] = i.ToLowerInvariant() }).ToArray()) } } },
            new JsonObject { ["$project"] = new JsonObject { ["unitName"] = 1, ["unitShortName"] = 1, ["parentId"] = 1, ["conversion"] = 1 } }
        }, context, settings, run, ct, comment, optional: true)).Rows;
        var units = await Load(valid);
        var parents = units.Select(u => Str(u["parentId"])).OfType<string>().Where(p => Regex.IsMatch(p, "^[0-9a-fA-F]{24}$")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parentRows = parents.Count > 0 ? await Load(parents) : new List<JsonObject>();
        var parentNames = parentRows.Where(p => Str(p["_id"]) is not null)
            .ToDictionary(p => Str(p["_id"])!, p => Str(p["unitShortName"]) ?? Str(p["unitName"]), StringComparer.OrdinalIgnoreCase);
        foreach (var u in units)
        {
            if (Str(u["_id"]) is not { } id) continue;
            var parent = Str(u["parentId"]);
            var conversion = Num(u["conversion"]);
            var sub = conversion > 0 && parent is not null && !string.Equals(parent, id, StringComparison.OrdinalIgnoreCase) && parentNames.TryGetValue(parent, out var pn) ? pn : null;
            map[id] = new UnitInfo(Str(u["unitShortName"]) ?? Str(u["unitName"]), conversion, sub);
        }
        return map;
    }
}

internal static class JsonObjectExtensions
{
    /// <summary>Applies <paramref name="change"/> and returns the same object (fluent building).</summary>
    public static JsonObject Also(this JsonObject obj, Action<JsonObject> change)
    {
        change(obj);
        return obj;
    }
}
