using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;

namespace MySale.AI.Application.Agent;

/// <summary>
/// Verified business rules of the MySaleBooks (MySaleApp) database — inventory (StockMaster), accounting (AccountVoucher),
/// invoices (Sale / Purchase) and currency — reproduced from the existing MySaleBooks report code, so AYAAN answers match
/// the MySaleBooks reports. Evidence (file:line of the MySaleApp API) is in docs/ai/MYSALEBOOKS_DOMAIN.md.
/// Active only when the connected database has these collections.
/// </summary>
public static class MySaleBooksDomain
{
    public const string Stock = "StockMaster";
    public const string Vouchers = "AccountVoucher";

    // Account group ids (Ledger.groupId / AccountVoucher.groupId or parentGroupId) used by the MySaleBooks reports.
    public const int CashGroup = 13, BankGroup = 14, DebtorsGroup = 15, CreditorsGroup = 16;
    public const int SalesGroup = 7, PurchaseGroup = 8, DirectExpenseGroup = 9, DirectIncomeGroup = 10, IndirectExpenseGroup = 11, IndirectIncomeGroup = 12;

    public static readonly IReadOnlyList<string> InTypes = new[] { "OPSTOCK", "PURCHASE", "SALE_RETURN", "DELIVERY_NOTE_RECEIPT", "STOCK_ADJUSTMENT", "STOCK_TRANSFER_INTERNAL", "STOCK_TRANSFER_EXTERNAL" };
    public static readonly IReadOnlyList<string> OutTypes = new[] { "SALE", "PURCHASE_RETURN", "DELIVERY_NOTE", "STOCK_ADJUSTMENT", "STOCK_TRANSFER_INTERNAL", "STOCK_TRANSFER_EXTERNAL" };

    /// <summary>Collections whose cancelled documents the server always excludes (the MySaleBooks reports filter only isCanceled).</summary>
    private static readonly HashSet<string> CancellableTransactions = new(StringComparer.Ordinal) { Stock, Vouchers, "Sale", "Purchase", "BillWiseDetails" };

    /// <summary>True when the connected database is a MySaleBooks database (its transaction collections are allowed).</summary>
    public static bool IsActive(IReadOnlyList<CollectionSchema> schema)
        => schema.Any(c => c.Name == Stock) || schema.Any(c => c.Name == Vouchers);

    /// <summary>
    /// Server-enforced status filter of a collection: {"isCanceled": {"$ne": true}} for MySaleBooks transactions that have
    /// the field (null otherwise). Applied to the root collection and to every $lookup into it.
    /// </summary>
    public static JsonObject? RequiredFilter(CollectionSchema? collection)
    {
        if (collection is null || !CancellableTransactions.Contains(collection.Name)) return null;
        if (!collection.Fields.Any(f => f.Name == "isCanceled")) return null;
        return new JsonObject { ["isCanceled"] = new JsonObject { ["$ne"] = true } };
    }

    // ------------------------------------------------------------------ validation (prevents double counting)

    private static readonly Regex ConvertedAmount = new(@"""\$(convertedDebit|convertedCredit|convertedNetAmount|convertedBalanceAmount|convertedReceivedAmount|convertedAmount|convertedAdvanceAmount)""", RegexOptions.Compiled);

    /// <summary>
    /// Business-rule errors of a generated pipeline (sent back to the model for repair):
    /// StockMaster quantities must be pipe-conditional; original-currency amounts must be grouped by currency.
    /// </summary>
    public static IEnumerable<string> Check(string collection, JsonArray pipeline)
    {
        var json = pipeline.ToJsonString();
        if (collection == Stock)
        {
            var usesIn = json.Contains("\"$stockIn\"", StringComparison.Ordinal);
            var usesOut = json.Contains("\"$stockOut\"", StringComparison.Ordinal);
            var usesBalance = json.Contains("\"$balanceStock\"", StringComparison.Ordinal);
            var pipes = PipeFilter(pipeline);                      // values of a root $match on transactionPipe, if any
            var conditional = json.Contains("\"$transactionPipe\"", StringComparison.Ordinal);
            if ((usesIn || usesOut) && pipes is null && !conditional)
                yield return "StockMaster quantity = Σ stockIn of rows with transactionPipe \"IN\" − Σ stockOut of rows with transactionPipe \"OUT\" " +
                             "(IN rows also hold consumed quantity in stockOut). Use {\"$sum\":{\"$cond\":[{\"$eq\":[\"$transactionPipe\",\"IN\"]},\"$stockIn\",0]}} and the same for OUT/stockOut.";
            if (pipes is not null && !conditional)
            {
                if (usesOut && !pipes.Contains("OUT")) yield return "stockOut must only be summed for transactionPipe \"OUT\" rows (on IN rows it is the consumed part of the receipt).";
                if (usesIn && !pipes.Contains("IN")) yield return "stockIn must only be summed for transactionPipe \"IN\" rows.";
            }
            if (usesBalance && (pipes is null || !pipes.SequenceEqual(new[] { "IN" })) && !conditional)
                yield return "balanceStock is only meaningful on transactionPipe \"IN\" rows (unconsumed FIFO layers): filter transactionPipe \"IN\" first.";
            if (Regex.IsMatch(json, @"""\$sum""\s*:\s*""\$qty"""))
                yield return "qty is in the unit entered on each document; stock quantities must use stockIn / stockOut (item stock unit).";
        }

        // Original (transaction-currency) amounts can only be added up per currency.
        foreach (var stage in pipeline.OfType<JsonObject>())
        {
            if (stage["$group"] is not JsonObject group) continue;
            var accumulators = new JsonObject(group.Where(kv => kv.Key != "_id").Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
            if (!ConvertedAmount.IsMatch(accumulators.ToJsonString())) continue;
            var id = group["_id"]?.ToJsonString() ?? "null";
            if (!id.Contains("currencyFromId", StringComparison.Ordinal))
                yield return "converted* amounts are in each document's own transaction currency (currencyFromId): group by currencyFromId or use debit/credit (company base currency). Never add different currencies together.";
        }
    }

    private static string[]? PipeFilter(JsonArray pipeline)
    {
        foreach (var stage in pipeline.OfType<JsonObject>())
        {
            if (stage["$match"] is not JsonObject match) { if (stage.ContainsKey("$group")) break; continue; }
            if (match["transactionPipe"] is JsonValue v && v.TryGetValue<string>(out var single)) return new[] { single.ToUpperInvariant() };
            if (match["transactionPipe"] is JsonObject o)
            {
                if (o["$eq"] is JsonValue ev && ev.TryGetValue<string>(out var eq)) return new[] { eq.ToUpperInvariant() };
                if (o["$in"] is JsonArray arr) return arr.Select(x => x?.ToString().ToUpperInvariant() ?? "").Where(x => x.Length > 0).ToArray();
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ prompt rules

    /// <summary>Rules added to the query prompt when the database is MySaleBooks. Only rules proven from the MySaleBooks code.</summary>
    public static string PromptRules(IReadOnlyList<CollectionSchema> schema)
    {
        bool Has(string name) => schema.Any(c => c.Name == name);
        var sb = new StringBuilder();
        sb.AppendLine("## MySaleBooks rules (verified from the MySaleBooks reports — follow them exactly)");
        sb.AppendLine("General: every *Id field (itemId, ledgerId, branchId, stockLocationId, categoryId, unitId, currencyFromId …) is a STRING holding the hex ObjectId of the master; to $lookup a master first add {\"$addFields\":{\"xObj\":{\"$convert\":{\"input\":\"$xId\",\"to\":\"objectId\",\"onError\":null,\"onNull\":null}}}} (ids such as \"0\" are not ObjectIds — never use a bare $toObjectId) and use localField \"xObj\", foreignField \"_id\"; from a master to transactions use {\"$toString\":\"$_id\"}. Keep the id in the output — the server shows the names. Who is who: Sale.ledgerId = customer, Purchase.ledgerId = supplier, AccountVoucher.ledgerId = ledger / account, Sale.employeeId = salesman (Employee), StockMaster.itemId = product (Item), Item.categoryId = category, Item.manufactureId = brand (Manufacture), stockLocationId = warehouse (StockLocation), branchId = branch; \"by customer / supplier / salesman / product / category / brand / warehouse / branch\" → $group on that id field (for category / brand via the $lookup of Item) and name the output after the entity (\"supplierId\":\"$_id\"). Users, cost centres and item groups have no verified master: do not group by them. Cancelled documents (isCanceled) are removed by the server; do not filter isDeleted / isApproved (the reports do not). The selected store (branchId) is applied by the server. Branch/master records with branchId \"0\" are shared by all stores.");
        if (Has(Stock))
        {
            sb.AppendLine("Inventory (StockMaster, one document per movement line, business date transactionDate):");
            sb.AppendLine("- Quantity = Σ stockIn of transactionPipe \"IN\" rows − Σ stockOut of transactionPipe \"OUT\" rows, i.e. {\"$sum\":{\"$cond\":[{\"$eq\":[\"$transactionPipe\",\"IN\"]},\"$stockIn\",0]}} minus {\"$sum\":{\"$cond\":[{\"$eq\":[\"$transactionPipe\",\"OUT\"]},\"$stockOut\",0]}}. IN rows also carry consumption in stockOut — never compute stockIn − stockOut per row. Never use qty, balanceStock or Item.stock for quantities. Quantities are in the item's stock unit (Item.unitId), free quantity included.");
            sb.AppendLine("- Current stock: all rows. Stock as of a date D: rows with transactionDate < start of the day after D, plus every OPSTOCK row (opening stock counts whatever its date). Opening stock of a period = the same up to the period start; received / issued = IN / OUT rows inside the period; closing = opening + received − issued.");
            sb.AppendLine("- " + StockSemantics.PromptTypes());
            sb.AppendLine("- WHOLE-inventory stock questions (no product named) are answered by the server stock engine — do not write a query: {\"type\":\"report\",\"report\":\"stockSummary\",\"view\":\"<view>\"} with view current (stock / stock on hand / available / inventory balance), value (total stock value), low (low stock / reorder), out (out of stock / zero stock), negative, highest, lowest, byCategory, byWarehouse, movement (stock movements in a period: add \"from\"/\"to\" YYYY-MM-DD), fastMoving / slowMoving / nonMoving (sales of products in a period: add \"from\"/\"to\"); stock on a past date: view current + \"asOf\":\"YYYY-MM-DD\"; optional \"category\" / \"warehouse\" (name as written) and \"limit\" (≤ 50).");
            sb.AppendLine("- Quantity sold = Σ stockOut of SALE (OUT) − Σ stockIn of SALE_RETURN (IN); quantity purchased = PURCHASE (IN) − PURCHASE_RETURN (OUT). Transfers are internal movements: never count them as purchases or sales (company-wide they net to zero; per warehouse/store they move stock in or out).");
            sb.AppendLine("- By warehouse: group/filter on stockLocationId; by product: itemId; by category: $lookup Item (itemId converted to objectId as above) and use Item.categoryId; batches: batchNo.");
            sb.AppendLine("- Products with no movement or no sales, zero or negative stock must start from Item (itemType \"product\" or \"rawMaterial\", isTrackInventory not false, isKotItem not true) so products without transactions are included: $addFields {\"itemKey\":{\"$toString\":\"$_id\"}}, $lookup from StockMaster localField \"itemKey\" foreignField \"itemId\", then compute with $filter on the joined array and $sum over the filtered values (e.g. {\"$sum\":{\"$map\":{\"input\":{\"$filter\":{…}},\"as\":\"r\",\"in\":\"$$r.stockIn\"}}}).");
            sb.AppendLine("- Low stock / reorder: compare the quantity with Item.reOrderLevel (reorder) or Item.minimumStockQty (minimum) only where that level is > 0; if the question needs a level that is not set, say it is not configured.");
            sb.AppendLine("- Fast / slow / non-moving products need a period (ask for one if missing): rank by quantity sold in the period; non-moving = products with no SALE movement in the period (start from Item).");
            sb.AppendLine("- ONE named item, product or service (by name, item code, SKU, barcode or part number, in any language): the server resolves the item and calculates — do not write a query. Current stock / how many left / in hand / available → {\"type\":\"report\",\"report\":\"itemStock\",\"item\":\"<the name, code or barcode exactly as the user wrote it>\"} (current quantity in the selected store — do NOT ask quantity or value); its stock value / cost / worth → add \"measure\":\"value\"; its stock on a past date → add \"asOf\":\"YYYY-MM-DD\"; price, selling price, service rate, code, barcode or details → {\"type\":\"report\",\"report\":\"itemDetails\",\"item\":\"…\"}; its movement history over a period → stockMovement; its sales, purchases, returns or service billing → a normal query. \"Item\" and \"product\" mean the same; services are items with itemType \"service\" and have no stock.");
            sb.AppendLine("- MySaleBooks does not reserve stock (sale orders do not move stock): available stock = physical stock; if asked for reserved stock, say it is not tracked.");
            sb.AppendLine("- Current stock value (as the MySaleBooks stock report): per item, value = current quantity × unit cost; for items with costingType \"FIFO\" (the default) unit cost = Σ(balanceStock × landedCost) ÷ Σ balanceStock over IN rows with balanceStock > 0, falling back to Item.landingCost when that is 0; other items use Item.landingCost. Amounts are in the base currency; combo items (Item.isCombo) are left out of value totals. Historical stock value, AVG/WAC/LP valuation, cost of goods sold and gross profit follow the company costing method of the MySaleBooks reports and cannot be recalculated here — return {\"type\":\"unsupported\",\"reason\":\"valuation\"} for them.");
        }
        if (Has(Vouchers))
        {
            sb.AppendLine("Accounting (AccountVoucher, one document per LEDGER LINE, business date voucherDate):");
            sb.AppendLine("- A voucher is all lines with the same voucherGuId: count vouchers with distinct voucherGuId (never lines); a voucher amount = Σ debit of its lines (never debit + credit).");
            sb.AppendLine("- Amounts: debit / credit are in the company base currency. Balance = Σ debit − Σ credit (positive = Dr, negative = Cr) plus the ledger opening balance Ledger.opBalanceDebit − Ledger.opBalanceCredit. Closing at a date = opening balance + all lines up to that date; opening of a period = opening balance + lines before the start.");
            sb.AppendLine($"- Account groups (match groupId OR parentGroupId to include sub-groups): {CashGroup} Cash, {BankGroup} Bank, {DebtorsGroup} Sundry Debtors (customers / receivables), {CreditorsGroup} Sundry Creditors (suppliers / payables), {SalesGroup} Sales, {PurchaseGroup} Purchase, {DirectExpenseGroup} Direct Expenses, {DirectIncomeGroup} Direct Incomes, {IndirectExpenseGroup} Indirect Expenses, {IndirectIncomeGroup} Indirect Incomes.");
            sb.AppendLine("- Receivable (customer balance) = Dr balance of a Sundry Debtors ledger; payable (supplier balance) = Cr balance of a Sundry Creditors ledger (show credit − debit). To include ledgers without vouchers start from Ledger and add the opening balance.");
            sb.AppendLine("- Income for a period = Σ credit − Σ debit of groups 7, 10, 12; expenses = Σ debit − Σ credit of groups 8, 9, 11 — say that these totals exclude the opening/closing stock adjustment of the Profit & Loss report.");
            sb.AppendLine("- voucherType values: SALE, SALE_RETURN, PURCHASE, PURCHASE_RETURN, SERVICE, RECEIPT, PAYMENT, JOURNAL, CONTRA, DEBIT_NOTE, CREDIT_NOTE. Unposted post-dated cheques have isPosted false (exclude them from ledger statements).");
            sb.AppendLine("- Running balances / ledger statements and item movement histories are produced by the server: return {\"type\":\"report\",\"report\":\"ledgerStatement\",\"ledger\":\"<ledger name as the user wrote it>\",\"from\":\"YYYY-MM-DD\",\"to\":\"YYYY-MM-DD\"} or {\"type\":\"report\",\"report\":\"stockMovement\",\"item\":\"<product name or code>\",\"from\":\"YYYY-MM-DD\",\"to\":\"YYYY-MM-DD\"} (local business dates).");
            sb.AppendLine("- Trial Balance, Profit & Loss and Balance Sheet are not recalculated by AYAAN (they depend on the MySaleBooks stock valuation and closing rules).");
        }
        if (Has("Sale") || Has("Purchase"))
            sb.AppendLine("Invoices (Sale / Purchase): outstanding invoices = documents with balanceAmount ≠ 0 and isBillWise false (as the MySaleBooks bill-wise pending report); overdue = dueDate before today and balanceAmount > 0; age in days is counted from the invoice date (saleDate / purchaseDate). A party's ledger balance is not the same as its unpaid invoices — say which one you show.");
        sb.AppendLine("Currency: debit, credit, netAmount, balanceAmount, landedCost are in the company base currency. converted* fields are in each document's transaction currency (currencyFromId): show them only grouped by currencyFromId, never add different currencies, never convert with a current rate.");
        sb.AppendLine("Ambiguity: return {\"type\":\"clarify\",\"question\":\"…\",\"options\":[…]} (e.g. {\"type\":\"clarify\",\"question\":\"Do you want stock quantity or stock value?\",\"options\":[\"Stock quantity\",\"Stock value\"]}) when the answer depends on it and neither the question nor its clarification answers say: \"how many products did I sell\" (different products or total quantity?), \"customer balance\" (ledger balance or unpaid invoices?), \"top products\" (by quantity or by sales value?).");
        return sb.ToString();
    }
}
