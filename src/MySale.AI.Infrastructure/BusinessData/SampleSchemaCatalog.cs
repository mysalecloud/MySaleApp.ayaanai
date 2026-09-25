using MySale.AI.Application.Abstractions;

namespace MySale.AI.Infrastructure.BusinessData;

/// <summary>
/// Curated metadata for the sample ERP database. This is the piece that will be replaced by the
/// MySaleBooks schema service; descriptions matter a lot for NL → MQL accuracy.
/// </summary>
public static class SampleSchemaCatalog
{
    private static FieldSchema F(string name, string type, string description, string? example = null, string? relationship = null, params string[] ops)
        => new()
        {
            Name = name,
            Type = type,
            Description = description,
            Example = example,
            Relationship = relationship,
            Operations = ops.Length > 0 ? ops.ToList() : DefaultOps(type),
            Documented = true
        };

    private static List<string> DefaultOps(string type) => type switch
    {
        "double" or "int" => new() { "match", "sum", "avg", "min", "max", "sort" },
        "date" => new() { "match", "range", "group-by-period", "sort" },
        "objectId" => new() { "match", "group", "lookup" },
        "bool" => new() { "match" },
        _ => new() { "match", "group", "sort", "regex" }
    };

    private static FieldSchema Tenant() => new()
    {
        Name = "CompanyId",
        Type = "objectId",
        Description = "Tenant key. Injected by the server — never used by the AI.",
        Relationship = "Companies._id",
        Operations = new() { "server-only" }
    };

    public static List<CollectionSchema> Build() => new()
    {
        new CollectionSchema
        {
            Name = "Sales",
            Description = "Sales invoice headers — one document per invoice. Use for sales totals, invoice counts, customer and branch sales.",
            Fields = new()
            {
                F("_id", "objectId", "Invoice id"),
                Tenant(),
                F("InvoiceNo", "string", "Invoice number", "\"INV-A-000123\""),
                F("InvoiceDate", "date", "Invoice date/time (UTC)"),
                F("CustomerId", "objectId", "Customer reference", null, "Customers._id"),
                F("CustomerName", "string", "Customer name (\"Cash Customer\" for walk-in sales)", "\"Ahmed Al Mansoori\""),
                F("BranchId", "objectId", "Branch reference", null, "Branches._id"),
                F("BranchName", "string", "Branch name", "\"Dubai - Deira\""),
                F("SalesPerson", "string", "Sales person name"),
                F("PaymentMode", "string", "Cash | Card | Credit | BankTransfer"),
                F("ItemCount", "int", "Number of invoice lines"),
                F("GrossAmount", "double", "Sum of quantity × rate before discount and VAT"),
                F("Discount", "double", "Discount amount"),
                F("Tax", "double", "VAT amount"),
                F("NetAmount", "double", "Invoice total = GrossAmount − Discount + Tax. Use this for 'sales'."),
                F("PaidAmount", "double", "Amount received against the invoice"),
                F("BalanceAmount", "double", "Outstanding amount = NetAmount − PaidAmount"),
                F("Status", "string", "Paid | Partial | Unpaid | Cancelled"),
                F("Currency", "string", "Currency code", "\"AED\"")
            }
        },
        new CollectionSchema
        {
            Name = "SaleItems",
            Description = "Sales invoice lines — one document per product per invoice. Use for product/category sales, quantities and profit. Has InvoiceDate so no $lookup is needed.",
            Fields = new()
            {
                F("_id", "objectId", "Line id"),
                Tenant(),
                F("SaleId", "objectId", "Invoice reference", null, "Sales._id"),
                F("InvoiceNo", "string", "Invoice number"),
                F("InvoiceDate", "date", "Invoice date/time (UTC)"),
                F("BranchId", "objectId", "Branch reference", null, "Branches._id"),
                F("CustomerId", "objectId", "Customer reference", null, "Customers._id"),
                F("ItemId", "objectId", "Product reference", null, "Items._id"),
                F("ItemCode", "string", "Product code", "\"ITM-A-0042\""),
                F("ItemName", "string", "Product name"),
                F("Category", "string", "Product category"),
                F("Quantity", "double", "Quantity sold"),
                F("Rate", "double", "Unit selling price"),
                F("Discount", "double", "Line discount"),
                F("Tax", "double", "Line VAT"),
                F("LineTotal", "double", "Line total incl. VAT = Quantity × Rate − Discount + Tax"),
                F("CostAmount", "double", "Quantity × purchase price"),
                F("Profit", "double", "Line profit = Quantity × Rate − Discount − CostAmount")
            }
        },
        new CollectionSchema
        {
            Name = "Customers",
            Description = "Customer master. Balance is the current outstanding receivable.",
            Fields = new()
            {
                F("_id", "objectId", "Customer id"),
                Tenant(),
                F("CustomerCode", "string", "Customer code", "\"CUS-A-0007\""),
                F("CustomerName", "string", "Customer name"),
                F("Phone", "string", "Phone number"),
                F("Email", "string", "Email address"),
                F("City", "string", "City", "\"Dubai\""),
                F("CustomerType", "string", "Retail | Wholesale | Corporate"),
                F("CreditLimit", "double", "Credit limit"),
                F("Balance", "double", "Outstanding balance (amount the customer owes)"),
                F("CreatedAt", "date", "Customer creation date"),
                F("LastPurchaseDate", "date", "Date of the most recent non-cancelled invoice (null if never purchased)"),
                F("IsActive", "bool", "Active flag")
            }
        },
        new CollectionSchema
        {
            Name = "Items",
            Description = "Product / inventory master with current stock. Low stock: Stock <= ReorderLevel. Inventory value: Stock × PurchasePrice.",
            Fields = new()
            {
                F("_id", "objectId", "Product id"),
                Tenant(),
                F("ItemCode", "string", "Product code", "\"ITM-A-0042\""),
                F("ItemName", "string", "Product name"),
                F("Category", "string", "Product category"),
                F("Brand", "string", "Brand"),
                F("Unit", "string", "Unit of measure", "\"PCS\""),
                F("SellingPrice", "double", "Selling price per unit"),
                F("PurchasePrice", "double", "Purchase (cost) price per unit"),
                F("Stock", "int", "Current quantity in stock"),
                F("ReorderLevel", "int", "Minimum stock before reordering"),
                F("Barcode", "string", "Barcode"),
                F("IsActive", "bool", "Active flag"),
                F("CreatedAt", "date", "Creation date")
            }
        },
        new CollectionSchema
        {
            Name = "Branches",
            Description = "Company branches / outlets.",
            Fields = new()
            {
                F("_id", "objectId", "Branch id"),
                Tenant(),
                F("BranchCode", "string", "Branch code"),
                F("BranchName", "string", "Branch name"),
                F("City", "string", "City"),
                F("IsActive", "bool", "Active flag"),
                F("OpenedOn", "date", "Opening date")
            }
        },
        new CollectionSchema
        {
            Name = "Purchases",
            Description = "Purchase invoice headers from suppliers.",
            Fields = new()
            {
                F("_id", "objectId", "Purchase id"),
                Tenant(),
                F("PurchaseNo", "string", "Purchase invoice number"),
                F("PurchaseDate", "date", "Purchase date (UTC)"),
                F("SupplierName", "string", "Supplier name"),
                F("BranchId", "objectId", "Branch reference", null, "Branches._id"),
                F("BranchName", "string", "Branch name"),
                F("ItemCount", "int", "Number of lines"),
                F("GrossAmount", "double", "Amount before VAT"),
                F("Tax", "double", "VAT amount"),
                F("NetAmount", "double", "Purchase total incl. VAT"),
                F("PaidAmount", "double", "Amount paid to supplier"),
                F("BalanceAmount", "double", "Amount still payable"),
                F("Status", "string", "Paid | Partial | Unpaid")
            }
        },
        new CollectionSchema
        {
            Name = "PurchaseItems",
            Description = "Purchase invoice lines.",
            Fields = new()
            {
                F("_id", "objectId", "Line id"),
                Tenant(),
                F("PurchaseId", "objectId", "Purchase reference", null, "Purchases._id"),
                F("PurchaseNo", "string", "Purchase number"),
                F("PurchaseDate", "date", "Purchase date (UTC)"),
                F("ItemId", "objectId", "Product reference", null, "Items._id"),
                F("ItemCode", "string", "Product code"),
                F("ItemName", "string", "Product name"),
                F("Quantity", "double", "Quantity purchased"),
                F("Rate", "double", "Unit cost"),
                F("Tax", "double", "Line VAT"),
                F("LineTotal", "double", "Line total incl. VAT")
            }
        },
        new CollectionSchema
        {
            Name = "Payments",
            Description = "Money received from customers (Receipt) and paid to suppliers (Payment).",
            Fields = new()
            {
                F("_id", "objectId", "Payment id"),
                Tenant(),
                F("PaymentNo", "string", "Payment number"),
                F("PaymentDate", "date", "Payment date (UTC)"),
                F("PaymentType", "string", "Receipt (from customer) | Payment (to supplier)"),
                F("PartyType", "string", "Customer | Supplier"),
                F("PartyId", "objectId", "Customer id for receipts", null, "Customers._id"),
                F("PartyName", "string", "Customer or supplier name"),
                F("InvoiceNo", "string", "Related sales or purchase invoice number"),
                F("Amount", "double", "Amount"),
                F("Mode", "string", "Cash | Card | BankTransfer | Cheque"),
                F("BranchId", "objectId", "Branch reference", null, "Branches._id")
            }
        },
        new CollectionSchema
        {
            Name = "Companies",
            Description = "Company master (not exposed to the AI by default).",
            Fields = new()
            {
                F("_id", "objectId", "Company id"),
                Tenant(),
                F("CompanyCode", "string", "Code"),
                F("CompanyName", "string", "Name"),
                F("Currency", "string", "Currency code"),
                F("TimeZone", "string", "IANA time zone"),
                F("Country", "string", "Country"),
                F("TRN", "string", "Tax registration number"),
                F("CreatedAt", "date", "Creation date")
            }
        },
        new CollectionSchema
        {
            Name = "Users",
            Description = "ERP users (not exposed to the AI by default).",
            Fields = new()
            {
                F("_id", "objectId", "User id"),
                Tenant(),
                F("UserName", "string", "Login name"),
                F("FullName", "string", "Full name"),
                F("Role", "string", "Role"),
                F("BranchId", "objectId", "Branch reference", null, "Branches._id"),
                F("IsActive", "bool", "Active flag"),
                F("LastLoginAt", "date", "Last login")
            }
        }
    };
}
