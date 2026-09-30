namespace MySale.AI.Application.Agent;

/// <summary>
/// One predefined (quick) question shown by the AYAAN clients. The client sends <see cref="Id"/> with the message; the
/// server validates both and never trusts the id alone (the message must be the catalogue text).
/// </summary>
/// <param name="Id">Stable id sent by the clients as <c>presetId</c>.</param>
/// <param name="Label">Button label shown in the widget / chat app.</param>
/// <param name="Message">Exact text sent to the AI (may differ from the label).</param>
/// <param name="Category">Business area (Sales, Stock, Products, Customers, Suppliers, Accounts, Summary).</param>
/// <param name="Intent">Canonical business intent (STOCK_LOW, SALES_TOTAL …).</param>
/// <param name="Route">"stock" = the server stock engine (deterministic, no model); "planner" = the validated query planner.</param>
/// <param name="Collections">Collections that must answer it (for the audit and the planner hint).</param>
/// <param name="Hint">Canonical interpretation given to the planner, so a predefined question is never re-guessed or re-asked.</param>
public sealed record PresetQuestion(string Id, string Label, string Message, string Category, string Intent, string Route,
    string Collections, string? Hint = null);

/// <summary>
/// Catalogue of every predefined question of the MySaleBooks widget and the standalone chat app
/// (frontend/*/QuickQuestions.tsx — the same ids are used there). Stock / inventory questions resolve to the canonical
/// stock intents of <see cref="StockIntents"/> and run the server stock engine; the others go to the planner with a fixed
/// interpretation. See docs/ai/PRESET_QUESTIONS_AUDIT.md for the full test matrix.
/// </summary>
public static class PresetQuestions
{
    private const string Stock = "stock";
    private const string Planner = "planner";

    public static readonly IReadOnlyList<PresetQuestion> All = new List<PresetQuestion>
    {
        // ------------------------------------------------------------------ Sales
        new("sales_today", "Today's Sales", "What are today's total sales?", "Sales", "SALES_TOTAL", Planner, "Sale",
            "Total sales = Σ Sale.netAmount of the invoices dated today (saleDate), cancelled invoices excluded (server). One KPI."),
        new("sales_yesterday", "Yesterday's Sales", "What were yesterday's total sales?", "Sales", "SALES_TOTAL", Planner, "Sale",
            "Total sales = Σ Sale.netAmount with saleDate yesterday. One KPI."),
        new("sales_this_week", "This Week", "What are the total sales this week?", "Sales", "SALES_TOTAL", Planner, "Sale",
            "Total sales = Σ Sale.netAmount with saleDate in this week (to today). One KPI."),
        new("sales_this_month", "Monthly Sales", "What are the total sales this month?", "Sales", "SALES_TOTAL", Planner, "Sale",
            "Total sales = Σ Sale.netAmount with saleDate in this month (to today). One KPI."),
        new("sales_last_month", "Last Month", "What were the total sales last month?", "Sales", "SALES_TOTAL", Planner, "Sale",
            "Total sales = Σ Sale.netAmount with saleDate in last month. One KPI."),
        new("sales_trend", "Sales Trend", "Show monthly sales for the last 12 months.", "Sales", "SALES_TREND", Planner, "Sale",
            "Group Sale by year and month of saleDate over the last 12 months, Σ netAmount, sorted by month. Line chart."),
        new("sales_daily", "Daily Sales", "Show daily sales for this month.", "Sales", "SALES_TREND", Planner, "Sale",
            "Group Sale by day of saleDate for this month, Σ netAmount and invoice count, sorted by day. Bar chart."),
        new("sales_invoice_count", "Invoice Count", "How many sales invoices were created this month?", "Sales", "SALES_COUNT", Planner, "Sale",
            "Count Sale documents with saleDate in this month. One KPI."),
        new("sales_avg_invoice", "Average Invoice", "What is the average invoice value this month?", "Sales", "SALES_AVERAGE", Planner, "Sale",
            "Average of Sale.netAmount over invoices with saleDate in this month (also show the count). One KPI."),
        new("sales_by_branch", "Sales by Branch", "Show total sales by branch this month.", "Sales", "SALES_BY_BRANCH", Planner, "Sale, Branch",
            "Group Sale by branchId for this month, Σ netAmount; keep branchId (the server shows branch names). All stores of the company unless one store is selected."),
        new("sales_by_payment", "Sales by Payment", "Show sales by payment method this month.", "Sales", "SALES_BY_PAYMENT", Planner, "Sale",
            "Group Sale by paymentType for this month, Σ netAmount and count."),
        new("sales_returns", "Sales Returns", "What is the total sales return amount this month?", "Sales", "SALES_RETURN_TOTAL", Planner, "AccountVoucher",
            "Sales returns this month = AccountVoucher vouchers with voucherType SALE_RETURN and voucherDate in this month: amount = Σ debit of their lines (a voucher amount is the Σ debit of its lines); count = distinct voucherGuId. One KPI."),

        // ------------------------------------------------------------------ Products & stock
        new("products_top", "Top Products", "Show the top 10 selling products this month.", "Products", "PRODUCTS_TOP_SELLING", Planner, "StockMaster, Item",
            "Top 10 products this month by quantity sold (StockMaster: SALE OUT stockOut − SALE_RETURN IN stockIn, grouped by itemId; project \"itemId\":\"$_id\" — the server shows the product names); say that the ranking is by quantity because sale-line amounts are not in the verified schema."),
        new("products_top_qty", "Top by Quantity", "Which products sold the most quantity this month?", "Products", "PRODUCTS_FAST_MOVING", Stock, "StockMaster, Item, Unit"),
        new("products_slow", "Slow Movers", "Which products had the lowest sales this month?", "Products", "PRODUCTS_SLOW_MOVING", Stock, "StockMaster, Item, Unit"),
        new("stock_low", "Low Stock", "Which products are low in stock?", "Stock", "STOCK_LOW", Stock, "StockMaster, Item, Unit"),
        new("stock_out", "Out of Stock", "Which products are out of stock?", "Stock", "STOCK_OUT", Stock, "StockMaster, Item, Unit"),
        new("stock_value", "Stock Value", "What is the total stock value?", "Stock", "STOCK_VALUE", Stock, "StockMaster, Item, Unit"),
        new("stock_current", "Current Stock", "Show current stock.", "Stock", "STOCK_CURRENT", Stock, "StockMaster, Item, Unit"),
        new("stock_negative", "Negative Stock", "Which products have negative stock?", "Stock", "STOCK_NEGATIVE", Stock, "StockMaster, Item, Unit"),
        new("stock_by_category", "Stock by Category", "Show stock by category.", "Stock", "STOCK_BY_CATEGORY", Stock, "StockMaster, Item, Category"),
        new("stock_by_warehouse", "Stock by Warehouse", "Show stock by warehouse.", "Stock", "STOCK_BY_WAREHOUSE", Stock, "StockMaster, Item, StockLocation"),
        new("stock_non_moving", "Non-moving Items", "Show products with no sales in the last 30 days.", "Stock", "PRODUCTS_NON_MOVING", Stock, "StockMaster, Item, Unit"),
        new("stock_movement", "Stock Movement", "Show stock movement this month.", "Stock", "STOCK_MOVEMENT", Stock, "StockMaster"),
        new("sales_by_category", "Sales by Category", "Show sales by product category this month.", "Products", "SALES_BY_CATEGORY", Planner, "StockMaster, Item, Category",
            "Quantity sold per item category this month (StockMaster SALE OUT − SALE_RETURN IN, $lookup Item, group by the item's categoryId and project \"categoryId\":\"$_id\" — the server shows the category names); say it is by quantity because sale-line amounts are not in the verified schema."),

        // ------------------------------------------------------------------ Customers
        new("customers_top", "Top Customers", "Who are the top 10 customers by sales?", "Customers", "CUSTOMERS_TOP", Planner, "Sale, Ledger",
            "Group Sale by ledgerId (customer), Σ netAmount, top 10 descending; project \"customerId\":\"$_id\" (the server shows the customer names, never the ids)."),
        new("customers_new", "New Customers", "How many new customers were added this month?", "Customers", "CUSTOMERS_NEW", Planner, "Ledger",
            "Count Ledger accounts of group 15 (Sundry Debtors: groupId or parentGroupId 15) created this month — only if the Ledger collection has a creation date field in the schema; otherwise say that the customer creation date is not recorded (do not guess)."),
        new("customers_outstanding", "Outstanding", "Which customers have outstanding balances?", "Receivables", "RECEIVABLES_BY_CUSTOMER", Planner, "Ledger, AccountVoucher",
            "Ledger balance (not unpaid invoices): customers = ledgers of group 15; balance = opBalanceDebit − opBalanceCredit + Σ debit − Σ credit of their AccountVoucher lines; list those with a Dr balance > 0, largest first, with ledgerName (never only the id)."),
        new("customers_overdue", "Overdue Invoices", "Show overdue customer invoices.", "Receivables", "RECEIVABLES_OVERDUE", Planner, "Sale",
            "Sale invoices with balanceAmount > 0, isBillWise false and dueDate before today: invoice number, customer (ledgerId), saleDate, dueDate, balanceAmount, days overdue."),
        new("customers_count", "Customer Count", "How many active customers do I have?", "Customers", "CUSTOMERS_COUNT", Planner, "Ledger",
            "Count Ledger accounts of group 15 (Sundry Debtors) with status not false."),

        // ------------------------------------------------------------------ Purchases, payables, accounts
        new("purchases_this_month", "Monthly Purchases", "What are the total purchases this month?", "Purchases", "PURCHASES_TOTAL", Planner, "Purchase",
            "Total purchases = Σ Purchase.netAmount with purchaseDate in this month. One KPI."),
        new("suppliers_top", "Top Suppliers", "Who are the top 10 suppliers by purchase amount?", "Suppliers", "SUPPLIERS_TOP", Planner, "Purchase, Ledger",
            "Group Purchase by ledgerId (supplier), Σ netAmount, top 10 descending; project \"supplierId\":\"$_id\" and purchaseAmount (the server shows the supplier names, never the ids)."),
        new("supplier_dues", "Supplier Dues", "How much do I owe to suppliers?", "Payables", "PAYABLES_TOTAL", Planner, "Ledger, AccountVoucher",
            "Ledger balance: suppliers = ledgers of group 16 (Sundry Creditors); payable = opBalanceCredit − opBalanceDebit + Σ credit − Σ debit; total of the positive balances and the largest ones with ledgerName (never only the id)."),
        new("payments_received", "Payments Received", "How much payment was received this month?", "Accounts", "RECEIPTS_TOTAL", Planner, "AccountVoucher",
            "Receipts this month = AccountVoucher vouchers with voucherType RECEIPT and voucherDate in this month: amount = Σ debit of their lines; count = distinct voucherGuId. One KPI."),
        new("expenses_this_month", "Expenses", "What are the total expenses this month?", "Accounts", "EXPENSES_TOTAL", Planner, "AccountVoucher",
            "Expenses = Σ debit − Σ credit of AccountVoucher lines of groups 9 and 11 (Direct / Indirect Expenses, groupId or parentGroupId) with voucherDate in this month; say that purchases (group 8) are not included."),

        // ------------------------------------------------------------------ Summary
        new("business_summary", "Business Summary", "Give me a business summary for this month.", "Summary", "BUSINESS_SUMMARY", Planner, "Sale, Purchase",
            "This month: total sales (Σ Sale.netAmount, count) and total purchases (Σ Purchase.netAmount, count) in one query with $facet."),
        new("sales_vs_last_month", "This vs Last Month", "Compare this month sales with last month.", "Sales", "SALES_COMPARE", Planner, "Sale",
            "Σ Sale.netAmount for this month (to today) and for last month, with the difference and % change."),
        new("income_vs_expenses", "Income vs Expenses", "What are the total income and expenses this month?", "Accounts", "INCOME_EXPENSES", Planner, "AccountVoucher",
            "Income = Σ credit − Σ debit of groups 7, 10, 12; expenses = Σ debit − Σ credit of groups 8, 9, 11, voucherDate in this month; say that this is not the Profit & Loss (no stock adjustment)."),

        // ------------------------------------------------------------------ Featured (welcome screen)
        new("featured_sales_today", "How much did I sell today?", "How much did I sell today?", "Sales", "SALES_TOTAL", Planner, "Sale",
            "Total sales = Σ Sale.netAmount of the invoices dated today (saleDate). One KPI."),
        new("featured_top_products", "What are my top products this month?", "What are my top products this month?", "Products", "PRODUCTS_TOP_SELLING", Planner, "StockMaster, Item",
            "Top 10 products this month by quantity sold (StockMaster SALE OUT − SALE_RETURN IN by itemId; project \"itemId\":\"$_id\" — the server shows the product names); say the ranking is by quantity."),
        new("featured_low_stock", "Which items are low in stock?", "Which items are low in stock?", "Stock", "STOCK_LOW", Stock, "StockMaster, Item, Unit"),
        new("featured_outstanding", "Which customers have outstanding balances?", "Which customers have outstanding balances?", "Receivables", "RECEIVABLES_BY_CUSTOMER", Planner, "Ledger, AccountVoucher",
            "Ledger balance (not unpaid invoices): ledgers of group 15 with a Dr balance > 0 (opening + Σ debit − Σ credit), largest first, with ledgerName (never only the id)."),
        new("featured_summary", "Give me a business summary for this month.", "Give me a business summary for this month.", "Summary", "BUSINESS_SUMMARY", Planner, "Sale, Purchase",
            "This month: total sales (Σ Sale.netAmount, count) and total purchases (Σ Purchase.netAmount, count) in one query with $facet.")
    };

    private static readonly Dictionary<string, PresetQuestion> ById = All.ToDictionary(p => p.Id, StringComparer.Ordinal);

    /// <summary>The preset with this id when the message is its catalogue text (case / spacing / final punctuation ignored).</summary>
    public static PresetQuestion? Resolve(string? id, string? message)
    {
        if (string.IsNullOrWhiteSpace(id) || !ById.TryGetValue(id.Trim(), out var preset)) return null;
        return Same(preset.Message, message) ? preset : null;
    }

    public static PresetQuestion? Find(string id) => ById.TryGetValue(id, out var p) ? p : null;

    private static string Norm(string? s) => System.Text.RegularExpressions.Regex.Replace((s ?? string.Empty).Trim().TrimEnd('.', '?', '!'), @"\s+", " ").ToLowerInvariant();

    private static bool Same(string a, string? b) => Norm(a) == Norm(b) && Norm(a).Length > 0;
}
