using System.Text.Json.Nodes;

namespace MySale.AI.Application.Agent;

/// <summary>How a StockMaster movement changes stock.</summary>
public enum StockDirection
{
    /// <summary>Adds stock: stockIn of a transactionPipe "IN" row.</summary>
    In,
    /// <summary>Removes stock: stockOut of a transactionPipe "OUT" row.</summary>
    Out,
    /// <summary>Either way — the row's transactionPipe decides (adjustments, transfers).</summary>
    Adjustment
}

/// <summary>One stored StockMaster transactionType and its effect on stock.</summary>
public sealed record StockTransactionType(string Type, string Label, StockDirection Direction, string Group, bool IsTransfer = false);

/// <summary>
/// THE stock rules of AYAAN, verified from the MySaleBooks Reports API (docs/ai/mysalebooks-audit/STOCK_RULES.md: the
/// Stock screen StockMasterController and the Stock Register). Every stock report, every stock prompt rule and every
/// predefined stock question uses this one definition — the model never decides whether a movement adds or removes stock.
/// <list type="bullet">
/// <item>Quantity = Σ stockIn of transactionPipe "IN" rows − Σ stockOut of transactionPipe "OUT" rows (IN rows also carry the
/// consumed part in stockOut, so a per-row stockIn − stockOut would double count).</item>
/// <item>Current stock: every non-cancelled row, no date filter (Stock screen SMC:419-431). Opening stock (OPSTOCK) and
/// transfers are included; company-wide transfers net to zero.</item>
/// <item>Stock as of a day: rows before the next day plus every OPSTOCK row whatever its date (Stock Register REG:570).</item>
/// <item>Cancelled rows (isCanceled) are removed by the server; isDeleted / isApproved are not filtered (the reports do not).</item>
/// <item>Stock-tracked items: itemType product / rawMaterial, isTrackInventory not false, isKotItem not true (REG:510, SMC:505).</item>
/// <item>Value: FIFO items → Σ(balanceStock × landedCost) ÷ Σ balanceStock of IN rows with balanceStock &gt; 0, else
/// Item.landingCost; other costing types → Item.landingCost; combo items are left out of value totals (SMC:394-417, 123).</item>
/// </list>
/// </summary>
public static class StockSemantics
{
    public const string Collection = "StockMaster";
    public const string Pipe = "transactionPipe";

    /// <summary>Every transactionType the MySaleBooks reports read (REG:361-465). Values are stored exactly like this.</summary>
    public static readonly IReadOnlyList<StockTransactionType> Types = new List<StockTransactionType>
    {
        new("OPSTOCK", "Opening stock", StockDirection.In, "opening"),
        new("PURCHASE", "Purchase", StockDirection.In, "purchase"),
        new("SALE_RETURN", "Sales return", StockDirection.In, "salesReturn"),
        new("DELIVERY_NOTE_RECEIPT", "Delivery note receipt", StockDirection.In, "deliveryReceipt"),
        new("SALE", "Sale", StockDirection.Out, "sale"),
        new("PURCHASE_RETURN", "Purchase return", StockDirection.Out, "purchaseReturn"),
        new("DELIVERY_NOTE", "Delivery note", StockDirection.Out, "deliveryNote"),
        new("STOCK_ADJUSTMENT", "Stock adjustment", StockDirection.Adjustment, "adjustment"),
        new("STOCK_TRANSFER_INTERNAL", "Stock transfer (warehouse)", StockDirection.Adjustment, "transfer", IsTransfer: true),
        new("STOCK_TRANSFER_EXTERNAL", "Stock transfer (branch)", StockDirection.Adjustment, "transfer", IsTransfer: true)
    };

    private static readonly Dictionary<string, StockTransactionType> ByType =
        Types.ToDictionary(t => t.Type, StringComparer.OrdinalIgnoreCase);

    public static StockTransactionType? Find(string? type) => type is not null && ByType.TryGetValue(type, out var t) ? t : null;

    public static string Label(string? type) => Find(type)?.Label ?? type ?? string.Empty;

    /// <summary>Quantity field and sign of a row: the pipe decides (IN → +stockIn, OUT → −stockOut).</summary>
    public static (string Field, int Sign) QuantityOf(string pipe)
        => string.Equals(pipe, "OUT", StringComparison.OrdinalIgnoreCase) ? ("stockOut", -1) : ("stockIn", 1);

    /// <summary>Types that can be stored with the given pipe (for validation / explanations).</summary>
    public static IEnumerable<string> TypesFor(StockDirection direction)
        => Types.Where(t => t.Direction == direction || t.Direction == StockDirection.Adjustment).Select(t => t.Type);

    /// <summary>Σ of <paramref name="field"/> over rows with transactionPipe = <paramref name="pipe"/> (optionally only some types).</summary>
    public static JsonObject PipeSum(string pipe, string field, params string[] onlyTypes)
    {
        JsonNode condition = new JsonObject { ["$eq"] = new JsonArray("$" + Pipe, pipe) };
        if (onlyTypes.Length > 0)
            condition = new JsonObject
            {
                ["$and"] = new JsonArray(condition, new JsonObject { ["$in"] = new JsonArray("$transactionType", new JsonArray(onlyTypes.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray())) })
            };
        return new JsonObject { ["$sum"] = new JsonObject { ["$cond"] = new JsonArray(condition, "$" + field, 0) } };
    }

    /// <summary>Received (Σ stockIn of IN rows) and issued (Σ stockOut of OUT rows) accumulators — quantity = received − issued.</summary>
    public static (JsonObject Received, JsonObject Issued) Balance() => (PipeSum("IN", "stockIn"), PipeSum("OUT", "stockOut"));

    /// <summary>Quantity sold in a period = SALE (OUT) − SALE_RETURN (IN), both inside the date condition.</summary>
    public static JsonObject NetSold(JsonNode inPeriod) => new()
    {
        ["$sum"] = new JsonObject
        {
            ["$cond"] = new JsonArray(
                new JsonObject { ["$and"] = new JsonArray(inPeriod.DeepClone(), new JsonObject { ["$eq"] = new JsonArray("$transactionType", "SALE") }, new JsonObject { ["$eq"] = new JsonArray("$" + Pipe, "OUT") }) },
                "$stockOut",
                new JsonObject
                {
                    ["$cond"] = new JsonArray(
                        new JsonObject { ["$and"] = new JsonArray(inPeriod.DeepClone(), new JsonObject { ["$eq"] = new JsonArray("$transactionType", "SALE_RETURN") }, new JsonObject { ["$eq"] = new JsonArray("$" + Pipe, "IN") }) },
                        new JsonObject { ["$multiply"] = new JsonArray("$stockIn", -1) },
                        0)
                })
        }
    };

    /// <summary>
    /// $match on an Item document (or on "prefix.field" after a $lookup) for stock-tracked items, exactly as the Stock
    /// screen: itemType product / rawMaterial, isTrackInventory not false, isKotItem not true.
    /// </summary>
    public static JsonObject TrackedItem(string prefix = "")
    {
        var p = string.IsNullOrEmpty(prefix) ? string.Empty : prefix + ".";
        return new JsonObject
        {
            [p + "itemType"] = new JsonObject { ["$in"] = new JsonArray("product", "rawMaterial") },
            [p + "isTrackInventory"] = new JsonObject { ["$ne"] = false },
            [p + "isKotItem"] = new JsonObject { ["$ne"] = true }
        };
    }

    /// <summary>
    /// Unit cost expression as the Stock screen, from grouped fields fifoAmount / fifoQty and the joined item
    /// ("item.costingType", "item.landingCost"): FIFO → fifoAmount ÷ fifoQty when that is &gt; 0, else landingCost.
    /// </summary>
    public static JsonObject UnitCostExpression(string itemPrefix = "item")
    {
        var landing = new JsonObject { ["$ifNull"] = new JsonArray($"${itemPrefix}.landingCost", 0) };
        var fifoCost = new JsonObject
        {
            ["$cond"] = new JsonArray(
                new JsonObject { ["$gt"] = new JsonArray("$fifoQty", 0) },
                new JsonObject { ["$divide"] = new JsonArray("$fifoAmount", "$fifoQty") },
                0)
        };
        return new JsonObject
        {
            ["$cond"] = new JsonArray(
                new JsonObject { ["$eq"] = new JsonArray(new JsonObject { ["$ifNull"] = new JsonArray($"${itemPrefix}.costingType", "FIFO") }, "FIFO") },
                new JsonObject { ["$cond"] = new JsonArray(new JsonObject { ["$gt"] = new JsonArray(fifoCost, 0) }, fifoCost.DeepClone(), landing) },
                landing.DeepClone())
        };
    }

    /// <summary>FIFO layer accumulators: Σ balanceStock × landedCost and Σ balanceStock over IN rows with balanceStock &gt; 0.</summary>
    public static (JsonObject Amount, JsonObject Qty) FifoLayers()
    {
        JsonNode openLayer = new JsonObject
        {
            ["$and"] = new JsonArray(
                new JsonObject { ["$eq"] = new JsonArray("$" + Pipe, "IN") },
                new JsonObject { ["$gt"] = new JsonArray("$balanceStock", 0) })
        };
        return (
            new JsonObject { ["$sum"] = new JsonObject { ["$cond"] = new JsonArray(openLayer, new JsonObject { ["$multiply"] = new JsonArray("$balanceStock", "$landedCost") }, 0) } },
            new JsonObject { ["$sum"] = new JsonObject { ["$cond"] = new JsonArray(openLayer.DeepClone(), "$balanceStock", 0) } });
    }

    /// <summary>Prompt text of the movement types (generated from <see cref="Types"/> so prompt and reports never disagree).</summary>
    public static string PromptTypes()
    {
        string List(StockDirection d) => string.Join(", ", Types.Where(t => t.Direction == d).Select(t => t.Type));
        var either = string.Join(", ", Types.Where(t => t.Direction == StockDirection.Adjustment).Select(t => t.Type));
        return $"IN types: {List(StockDirection.In)}. OUT types: {List(StockDirection.Out)}. Either direction (the row's transactionPipe decides): {either} " +
               "(transfers are IN at the destination and OUT at the source).";
    }
}
