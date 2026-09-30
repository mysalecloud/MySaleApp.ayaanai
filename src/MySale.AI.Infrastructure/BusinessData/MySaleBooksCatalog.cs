using MySale.AI.Application.Abstractions;

namespace MySale.AI.Infrastructure.BusinessData;

/// <summary>
/// Verified metadata of the real MySaleBooks (MySaleApp) database, taken from the MySaleApp API source
/// (Services/User/Reports + Inventory models and the code that writes StockMaster / AccountVoucher).
/// It is merged with the live schema discovery: only collections that exist in the customer database are used, and
/// fields found in the data but not listed here are still added (undocumented). See docs/ai/MYSALEBOOKS_DOMAIN.md.
/// Collection name = MySaleApp C# class name. Foreign keys are stored as STRINGS holding the hex ObjectId of the
/// referenced master (masters' _id are ObjectIds) — joins need {"$convert":{"input":"$field","to":"objectId","onError":null}} first.
/// </summary>
public static class MySaleBooksCatalog
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "StockMaster", "AccountVoucher", "Item", "Ledger", "AccountGroup", "AccountType", "StockLocation", "Branch",
        "Unit", "Category", "Currency", "Company", "BillWiseDetails", "Sale", "Purchase", "Employee", "Manufacture", "Department"
    };

    private static FieldSchema F(string name, string type, string description, string? relationship = null, string? example = null) => new()
    {
        Name = name,
        Type = type,
        Description = description,
        Relationship = relationship,
        Example = example,
        Documented = true,
        Operations = type switch
        {
            "decimal" or "double" or "int" => new() { "match", "sum", "avg", "min", "max", "sort" },
            "date" => new() { "match", "range", "group-by-period", "sort" },
            "objectId" => new() { "match", "group", "lookup" },
            "bool" => new() { "match" },
            _ => new() { "match", "group", "sort", "regex" }
        }
    };

    private static FieldSchema Id(string what) => F("_id", "objectId", what);
    private static FieldSchema Branch(string note = "Branch (= the MySaleBooks store) as a string id") => F("branchId", "string", note, "Branch._id");
    private static FieldSchema Company() => F("companyId", "string", "Company id (string). The customer database is already the tenant boundary.", "Company._id");

    public static List<CollectionSchema> Build() => new()
    {
        new CollectionSchema
        {
            Name = "StockMaster",
            Description = "INVENTORY TRANSACTIONS — one document per stock movement line (the source of truth of every MySaleBooks stock report). " +
                          "Quantity on hand = Σ stockIn of rows with transactionPipe \"IN\" − Σ stockOut of rows with transactionPipe \"OUT\" " +
                          "(IN rows also carry consumed quantity in stockOut — never add stockIn−stockOut per row). Exclude isCanceled:true. " +
                          "Quantities are in the item's stock unit (Item.unitId), free quantity included.",
            Fields = new()
            {
                Id("Stock row id"),
                F("transactionDate", "date", "Business date of the movement (invoice / purchase / adjustment date). Use for periods and 'as of' stock."),
                F("transactionType", "string", "OPSTOCK (opening stock) | PURCHASE | SALE | SALE_RETURN | PURCHASE_RETURN | DELIVERY_NOTE | DELIVERY_NOTE_RECEIPT | STOCK_ADJUSTMENT | STOCK_TRANSFER_INTERNAL | STOCK_TRANSFER_EXTERNAL", null, "\"SALE\""),
                F("transactionPipe", "string", "\"IN\" (stock received) or \"OUT\" (stock issued). Transfers have an OUT row at the source and an IN row at the destination."),
                F("itemId", "string", "Product (string of Item._id — convert to objectId with $convert (onError null) to join)", "Item._id"),
                F("stockLocationId", "string", "Warehouse / stock location (string of StockLocation._id)", "StockLocation._id"),
                F("stockIn", "decimal", "Quantity received, in the item's stock unit, free quantity included. Sum ONLY where transactionPipe = \"IN\"."),
                F("stockOut", "decimal", "Quantity issued, in the item's stock unit, free quantity included. Sum ONLY where transactionPipe = \"OUT\" (on IN rows it is the consumed part)."),
                F("balanceStock", "decimal", "Unconsumed remainder of an IN row (FIFO layer). Only meaningful on IN rows; used for FIFO valuation of CURRENT stock."),
                F("landedCost", "decimal", "Unit cost in the company base currency (IN rows: purchase landed cost; OUT rows: cost of goods issued)."),
                F("qty", "decimal", "Quantity in the unit entered on the document (unitId) — not used for stock totals."),
                F("unitId", "string", "Unit entered on the document", "Unit._id"),
                F("baseUnitId", "string", "Item stock unit at posting time", "Unit._id"),
                F("Freeqty", "decimal", "Free quantity in the entered free unit (element name has a capital F)."),
                F("convertedStock", "decimal", "Document quantity converted to the item stock unit (without free quantity)."),
                F("convertedFreeStock", "decimal", "Free quantity converted to the item stock unit."),
                F("batchNo", "string", "Batch number"),
                F("isBatchWise", "bool", "Batch-wise stock row"),
                F("transactionId", "string", "Source document id (sale / purchase / adjustment …)"),
                F("transactionDetailsId", "string", "Source document line id"),
                F("ledgerId", "string", "Customer / supplier ledger of the source document", "Ledger._id"),
                F("ledgerName", "string", "Customer / supplier name (snapshot)"),
                F("costingType", "string", "Costing type recorded on the row (reports use Company.costingType / Item.costingType instead)"),
                F("isCanceled", "bool", "Cancelled rows — always exclude (the only status the stock reports filter on)."),
                Branch("Branch of the movement (string). Rows with branchId \"0\" exist (shared); the Stock Register excludes them for a single branch."),
                Company(),
                F("createdDate", "date", "When the row was created (NOT the business date; only a fallback when transactionDate is empty)."),
                F("platForm", "string", "APP | APP_POS | WEB | WEB_POS")
            }
        },
        new CollectionSchema
        {
            Name = "AccountVoucher",
            Description = "ACCOUNTING TRANSACTIONS — one document per LEDGER LINE of a voucher (sale, purchase, receipt, payment, journal …). " +
                          "Lines of one voucher share voucherGuId. debit/credit are in the company BASE currency; balance = Σdebit − Σcredit (positive = Dr). " +
                          "Exclude isCanceled:true. Count vouchers with distinct voucherGuId, never lines.",
            Fields = new()
            {
                Id("Voucher line id"),
                F("voucherGuId", "string", "Voucher key shared by all lines of one voucher (use to count vouchers / get the other side)"),
                F("voucherId", "int", "Numeric voucher key shared by the lines"),
                F("voucherNo", "string", "Voucher / document number shown to users", null, "\"INV-1023\""),
                F("voucherDate", "date", "Business (posting) date of the voucher. Use for periods, statements and balances."),
                F("voucherType", "string", "SALE | SALE_RETURN | PURCHASE | PURCHASE_RETURN | SERVICE | RECEIPT | PAYMENT | JOURNAL | CONTRA | DEBIT_NOTE | CREDIT_NOTE"),
                F("ledgerId", "string", "Account (string of Ledger._id)", "Ledger._id"),
                F("ledgerName", "string", "Account name (snapshot)"),
                F("groupId", "int", "Account group of the ledger (snapshot): 13 Cash, 14 Bank, 15 Sundry Debtors (customers), 16 Sundry Creditors (suppliers), 7 Sales, 8 Purchase, 9 Direct Expense, 10 Direct Income, 11 Indirect Expense, 12 Indirect Income"),
                F("parentGroupId", "int", "Parent group when the ledger is in a sub-group (match groupId OR parentGroupId for a group incl. sub-groups)"),
                F("groupName", "string", "Account group name (snapshot)"),
                F("debit", "decimal", "Debit amount in the company BASE currency"),
                F("credit", "decimal", "Credit amount in the company BASE currency"),
                F("convertedDebit", "decimal", "Debit in the TRANSACTION currency (currencyFromId). Never add across currencies."),
                F("convertedCredit", "decimal", "Credit in the TRANSACTION currency (currencyFromId). Never add across currencies."),
                F("currencyFromId", "string", "Transaction currency", "Currency._id"),
                F("currencyToId", "string", "Base currency of the conversion", "Currency._id"),
                F("exchangeRate", "decimal", "Rate recorded on the voucher: base amount = transaction amount × exchangeRate ÷ rate of the base currency (normally 1)"),
                F("narration", "string", "Narration"),
                F("referenceNo", "string", "Reference / source document number"),
                F("transactionId", "string", "Source document id (sale / purchase …)"),
                F("isPDC", "bool", "Post-dated cheque"),
                F("isPosted", "bool", "false = PDC not yet posted (the Ledger Book excludes isPosted:false). Missing = posted."),
                F("chequeNo", "string", "Cheque number"),
                F("chequeDate", "date", "Cheque date (not the voucher date)"),
                F("isCanceled", "bool", "Cancelled lines — always exclude (the only status the accounting reports filter on)."),
                F("billWiseAdjustmentCategory", "string", "Advance | ExReference | NewReference | OnAccount"),
                F("employeeId", "string", "Employee / salesman of the voucher (string of Employee._id)", "Employee._id"),
                F("departmentId", "string", "Department of the voucher (string of Department._id)", "Department._id"),
                F("isReconciled", "bool", "Bank reconciliation status"),
                F("reconciledDate", "date", "Bank reconciliation date"),
                Branch(),
                Company(),
                F("createdDate", "date", "When the line was created (NOT the business date)")
            }
        },
        new CollectionSchema
        {
            Name = "Item",
            Description = "Item master: products, raw materials and services (itemType; branchId \"0\" = shared by all branches). Use to include products without movements.",
            Fields = new()
            {
                Id("Product id (referenced as a string by StockMaster.itemId)"),
                F("itemName", "string", "Product name"),
                F("itemLocalName", "string", "Product name in the local language"),
                F("itemCode", "string", "Product code"),
                F("barcode", "string", "Barcode"),
                F("eancode", "string", "EAN code (lower-case c)"),
                F("partNumber", "string", "Part number"),
                F("partNumberDetails.partNumber", "string", "Additional part numbers"),
                F("aliasDetails.aliasName", "string", "Alias (alternative) names of the item"),
                F("alternateUnits.unitId", "string", "Alternate selling unit", "Unit._id"),
                F("alternateUnits.unitName", "string", "Alternate unit name"),
                F("alternateUnits.barcode", "string", "Barcode of the alternate unit"),
                F("alternateUnits.alternateItemCode", "string", "Item code of the alternate unit"),
                F("alternateUnits.alternateItemName", "string", "Item name of the alternate unit"),
                F("alternateUnits.taxExcAmount", "decimal", "Selling price of the alternate unit excluding tax"),
                F("alternateUnits.taxIncAmount", "decimal", "Selling price of the alternate unit including tax"),
                F("itemType", "string", "product | rawMaterial | service (stock reports use product and rawMaterial; services are not stock-tracked)"),
                F("categoryId", "string", "Category (string of Category._id)", "Category._id"),
                F("manufactureId", "string", "Brand / manufacturer (string of Manufacture._id; \"0\" = none)", "Manufacture._id"),
                F("unitId", "string", "Stock unit of the product (StockMaster quantities are in this unit)", "Unit._id"),
                F("costingType", "string", "Item costing type (default FIFO)"),
                F("landingCost", "decimal", "Current landed cost per unit (base currency) — fallback cost when no FIFO layer exists"),
                F("purchaseRate", "decimal", "Purchase rate"),
                F("taxExcAmount", "decimal", "Selling price excluding tax"),
                F("taxIncAmount", "decimal", "Selling price including tax"),
                F("saleTax", "decimal", "Sales tax percentage"),
                F("minimumSellingRate", "decimal", "Minimum selling rate (0 = not set)"),
                F("maximumSellingRate", "decimal", "Maximum selling rate (0 = not set)"),
                F("isBatchWise", "bool", "Batch-wise stock"),
                F("reOrderLevel", "decimal", "Reorder level (0 = not configured)"),
                F("minimumStockQty", "decimal", "Minimum stock quantity (0 = not configured)"),
                F("maximumStockQty", "decimal", "Maximum stock quantity (0 = not configured)"),
                F("stock", "decimal", "Denormalised stock kept on the item (used only by the Stock Level report; StockMaster is the source of truth)"),
                F("openingStock", "decimal", "Opening stock entered on the item (StockMaster OPSTOCK rows are the source of truth)"),
                F("openingStockRate", "decimal", "Opening stock rate"),
                F("isTrackInventory", "bool", "false = not stock-tracked (missing = tracked)"),
                F("isCombo", "bool", "Combo product (excluded from stock value totals)"),
                F("isKotItem", "bool", "Kitchen (KOT) item — excluded from stock reports"),
                F("status", "bool", "Active flag"),
                F("isCanceled", "bool", "Cancelled product"),
                F("isDeleted", "bool", "Deleted product"),
                Branch("\"0\" = shared by all branches"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Ledger",
            Description = "Chart of accounts: customers, suppliers, cash, bank, income, expense … (branchId \"0\" = shared). Opening balance = opBalanceDebit − opBalanceCredit.",
            Fields = new()
            {
                Id("Ledger id (referenced as a string by AccountVoucher.ledgerId)"),
                F("ledgerName", "string", "Account / party name"),
                F("ledgerCode", "string", "Account code"),
                F("groupId", "int", "Account group: 13 Cash, 14 Bank, 15 Sundry Debtors, 16 Sundry Creditors, 7 Sales, 8 Purchase, 9/11 Expenses, 10/12 Incomes"),
                F("parentGroupId", "int", "Parent group when in a sub-group"),
                F("groupName", "string", "Account group name (snapshot)"),
                F("parentId", "string", "Parent ledger (sub-ledgers)"),
                F("opBalanceDebit", "decimal", "Opening balance debit (base currency)"),
                F("opBalanceCredit", "decimal", "Opening balance credit (base currency)"),
                F("taxNumber", "string", "Tax registration number"),
                F("isCustomerVendor", "bool", "Customer / vendor ledger"),
                F("status", "bool", "Active flag"),
                F("isCanceled", "bool", "Cancelled ledger"),
                Branch("\"0\" = shared by all branches"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "AccountGroup",
            Description = "Account groups (hierarchy by integer groupId / parentId; parentId 0 = primary group). Nature via accountTypeId.",
            Fields = new()
            {
                Id("Group document id"),
                F("groupId", "int", "Group number used by Ledger.groupId / AccountVoucher.groupId"),
                F("parentId", "int", "Parent groupId (0 = primary group)"),
                F("groupName", "string", "Group name"),
                F("accountTypeId", "string", "Nature: 6359387c950e586da6919130 Asset, 63593888950e586da6919132 Liability, 6359386f950e586da691912c Expense, 63593875950e586da691912e Income, 63593860950e586da691912a Equity", "AccountType._id")
            }
        },
        new CollectionSchema
        {
            Name = "AccountType",
            Description = "Account natures (Asset, Liability, Expense, Income, Equity).",
            Fields = new() { Id("Account type id"), F("accountTypeName", "string", "Nature name") }
        },
        new CollectionSchema
        {
            Name = "StockLocation",
            Description = "Warehouses / stock locations of a branch.",
            Fields = new()
            {
                Id("Stock location id (referenced as a string by StockMaster.stockLocationId)"),
                F("stockLocationName", "string", "Warehouse / location name"),
                F("location", "string", "Address / location text"),
                F("status", "bool", "Active flag"),
                Branch("\"0\" = shared"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Branch",
            Description = "Branches — the stores selected in MySaleBooks.",
            Fields = new()
            {
                Id("Branch id (referenced as a string by branchId everywhere)"),
                F("branchName", "string", "Branch / store name"),
                F("currencyId", "string", "Branch currency (not used by the reports; amounts are in the company currency)", "Currency._id"),
                F("status", "bool", "Active flag"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Unit",
            Description = "Units of measure. conversion = number of parent (sub) units in one unit.",
            Fields = new()
            {
                Id("Unit id"),
                F("unitName", "string", "Unit name"),
                F("unitShortName", "string", "Unit short name"),
                F("parentId", "string", "Sub unit (parent) of this unit"),
                F("conversion", "decimal", "Number of parent (sub) units in one of this unit"),
                Branch("\"0\" = shared"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Category",
            Description = "Product categories.",
            Fields = new()
            {
                Id("Category id (referenced as a string by Item.categoryId)"),
                F("categoryName", "string", "Category name"),
                F("parentId", "string", "Parent category"),
                Branch("\"0\" = shared"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Currency",
            Description = "Currencies. The company base currency is Company.currencyId (not isDefault).",
            Fields = new()
            {
                Id("Currency id"),
                F("currencyName", "string", "Currency name"),
                F("currencyCode", "string", "Currency code (e.g. AED, OMR)"),
                F("symbol", "string", "Currency symbol"),
                F("decimals", "int", "Decimal places for amounts in this currency (e.g. 3 for OMR)"),
                F("rate", "decimal", "Rate of the currency used by the web app conversions"),
                Branch("\"0\" = shared"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Company",
            Description = "Company settings: base currency, financial year, costing method.",
            Fields = new()
            {
                Id("Company id"),
                F("currencyId", "string", "Base currency of the company", "Currency._id"),
                F("financialYearFrom", "date", "Financial year start"),
                F("financialYearTo", "date", "Financial year end"),
                F("costingType", "string", "Stock costing method: FIFO | AVG | WAC | LP")
            }
        },
        new CollectionSchema
        {
            Name = "Sale",
            Description = "Sales invoice headers (one per invoice). Outstanding invoices = balanceAmount ≠ 0 and isBillWise false (MySaleBooks bill-wise pending report).",
            Fields = new()
            {
                Id("Sale id (StockMaster.transactionId / AccountVoucher.transactionId of the invoice)"),
                F("saleNo", "string", "Invoice number"),
                F("invoiceNo", "string", "Invoice number (tax invoice)"),
                F("saleDate", "date", "Invoice (business) date"),
                F("dueDate", "date", "Due date"),
                F("creditPeriod", "decimal", "Credit period in days"),
                F("ledgerId", "string", "Customer ledger", "Ledger._id"),
                F("employeeId", "string", "Salesman / employee of the invoice (string of Employee._id)", "Employee._id"),
                F("netAmount", "decimal", "Invoice total (base currency)"),
                F("receivedAmount", "decimal", "Amount received against the invoice"),
                F("balanceAmount", "decimal", "Outstanding amount of the invoice"),
                F("convertedNetAmount", "decimal", "Invoice total in the transaction currency (currencyFromId)"),
                F("currencyFromId", "string", "Transaction currency", "Currency._id"),
                F("exchangeRate", "decimal", "Exchange rate recorded on the invoice"),
                F("paymentType", "string", "Cash / Credit / Card / Multi"),
                F("stockLocationId", "string", "Warehouse", "StockLocation._id"),
                F("isBillWise", "bool", "Bill-wise flag (pending invoices have isBillWise false)"),
                F("isCanceled", "bool", "Cancelled invoice — always excluded"),
                Branch(),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Purchase",
            Description = "Purchase invoice headers (one per bill). Outstanding bills = balanceAmount ≠ 0 and isBillWise false.",
            Fields = new()
            {
                Id("Purchase id"),
                F("purchaseNo", "string", "Purchase / bill number"),
                F("purchaseDate", "date", "Purchase (business) date"),
                F("dueDate", "date", "Due date"),
                F("invoiceNo", "string", "Supplier's bill / invoice number"),
                F("ledgerId", "string", "Supplier ledger", "Ledger._id"),
                F("employeeId", "string", "Employee who entered the purchase (string of Employee._id)", "Employee._id"),
                F("stockLocationId", "string", "Warehouse", "StockLocation._id"),
                F("netValue", "decimal", "Purchase value posted to the purchase account"),
                F("netAmount", "decimal", "Bill total"),
                F("balanceAmount", "decimal", "Outstanding amount of the bill"),
                F("isBillWise", "bool", "Bill-wise flag (pending bills have isBillWise false)"),
                F("isCanceled", "bool", "Cancelled bill — always excluded"),
                Branch(),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Employee",
            Description = "Employees — the salesmen of sales invoices (Sale.employeeId) and the employees of purchases / vouchers.",
            Fields = new()
            {
                Id("Employee id (referenced as a string by Sale.employeeId, Purchase.employeeId, AccountVoucher.employeeId)"),
                F("employeeName", "string", "Employee / salesman name"),
                F("employeeLocalName", "string", "Name in the local language"),
                F("employeeCode", "string", "Employee code"),
                F("departmentId", "string", "Department", "Department._id"),
                F("status", "bool", "Active flag"),
                F("isCanceled", "bool", "Cancelled employee"),
                Branch("\"0\" = shared"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Manufacture",
            Description = "Brands / manufacturers of products (Item.manufactureId).",
            Fields = new()
            {
                Id("Brand id (referenced as a string by Item.manufactureId)"),
                F("manufactureName", "string", "Brand / manufacturer name"),
                F("manufactureLocalName", "string", "Name in the local language"),
                F("status", "bool", "Active flag"),
                F("isCanceled", "bool", "Cancelled brand"),
                Branch("\"0\" = shared"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "Department",
            Description = "Departments (Employee.departmentId, AccountVoucher.departmentId).",
            Fields = new()
            {
                Id("Department id"),
                F("departmentName", "string", "Department name"),
                Branch("\"0\" = shared"),
                Company()
            }
        },
        new CollectionSchema
        {
            Name = "BillWiseDetails",
            Description = "Bill-by-bill settlement records. Not used by the existing MySaleBooks outstanding/aging reports (they use the invoice balance and FIFO knock-off).",
            Fields = new()
            {
                Id("Bill-wise row id"),
                F("ledgerId", "string", "Party ledger", "Ledger._id"),
                F("voucherGuId", "string", "Voucher key"),
                F("transactionType", "string", "Source document type"),
                F("transactionNo", "string", "Source document number"),
                F("transactionDate", "date", "Source document date"),
                F("amount", "decimal", "Amount"),
                F("netAmount", "decimal", "Net amount"),
                F("totalReceivedAmount", "decimal", "Received / paid so far"),
                F("balanceAmount", "decimal", "Balance recorded on the row"),
                F("billWiseCategory", "string", "Advance | ExReference | NewReference | OnAccount"),
                F("isCanceled", "bool", "Cancelled"),
                Branch(),
                Company()
            }
        }
    };
}
