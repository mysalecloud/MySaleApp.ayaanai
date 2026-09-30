using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;

namespace MySale.AI.Application.Agent;

/// <summary>
/// A business entity that users know by a name or number (supplier, customer, product, warehouse …) and the master
/// record that holds that name. <see cref="Collection"/> null = no verified master exists in the business database
/// (the id can never be named — it is hidden from users instead).
/// </summary>
public sealed record EntityType(
    string Key,
    string Singular,
    string Plural,
    string? Collection,
    string? NameField,
    IReadOnlyList<string> CodeFields,
    string KeyField = "_id",
    bool IntegerKey = false,
    string? Reason = null)
{
    /// <summary>Display column added next to the id: "supplierName", "invoiceNo", "groupName" …</summary>
    public string DisplayColumn => Key switch
    {
        "invoice" => "invoiceNo",
        "purchaseBill" => "billNo",
        "voucher" => "voucherNo",
        "document" => "documentNo",
        "currency" => "currencyCode",
        _ => Key + "Name"
    };

    /// <summary>"No supplier", "Shared (all branches)" — the label of an empty / "0" reference.</summary>
    public string NoneLabel => Key == "branch" ? "Shared (all branches)" : "No " + Singular;

    /// <summary>"Unknown supplier" — an id that has no master record (orphan) or could not be read.</summary>
    public string UnknownLabel => "Unknown " + Singular;
}

/// <summary>A transaction/master field that stores the id of an entity (Purchase.ledgerId → supplier).</summary>
public sealed record EntityReference(string Collection, string Field, string Entity, string Evidence);

/// <summary>
/// ONE place for every ID → name mapping of AYAAN. The MySaleBooks part is verified from the MySaleApp API source
/// (the [BsonElement] names of the models under Services/User/Inventory/.../Model and the report code) — see
/// docs/ai/ENTITY_RESOLUTION.md. Used by
/// <list type="bullet">
/// <item>the result resolver (<see cref="QueryEngine.ResolveReferencesAsync"/>): grouped ids → names before the answer;</item>
/// <item>the server reports (stock engine, item stock, stock movement): <see cref="QueryEngine.ResolveEntityNamesAsync"/>;</item>
/// <item>the reverse lookup name/code → id of the reports (ledger, category, warehouse; several matches → the user chooses);</item>
/// <item>the data-integrity diagnostics (orphan ids per reference).</item>
/// </list>
/// Nothing here is guessed: an entity without a verified master (user, cost centre) is never named.
/// </summary>
public static class EntityCatalog
{
    private static EntityType T(string key, string singular, string plural, string? collection, string? name, params string[] codes)
        => new(key, singular, plural, collection, name, codes);

    /// <summary>Entity types by key.</summary>
    public static readonly IReadOnlyDictionary<string, EntityType> Types = new[]
    {
        T("item", "product", "products", "Item", "itemName", "itemCode", "barcode"),
        T("service", "service", "services", "Item", "itemName", "itemCode"),
        T("category", "category", "categories", "Category", "categoryName"),
        T("brand", "brand", "brands", "Manufacture", "manufactureName"),
        T("warehouse", "warehouse", "warehouses", "StockLocation", "stockLocationName"),
        T("branch", "branch", "branches", "Branch", "branchName"),
        T("unit", "unit", "units", "Unit", "unitName", "unitShortName"),
        T("currency", "currency", "currencies", "Currency", "currencyCode", "currencyName"),
        T("ledger", "ledger", "ledgers", "Ledger", "ledgerName", "ledgerCode"),
        T("customer", "customer", "customers", "Ledger", "ledgerName", "ledgerCode"),
        T("supplier", "supplier", "suppliers", "Ledger", "ledgerName", "ledgerCode"),
        T("salesman", "salesman", "salesmen", "Employee", "employeeName", "employeeCode"),
        T("employee", "employee", "employees", "Employee", "employeeName", "employeeCode"),
        T("department", "department", "departments", "Department", "departmentName"),
        new EntityType("group", "account group", "account groups", "AccountGroup", "groupName", Array.Empty<string>(), KeyField: "groupId", IntegerKey: true),
        T("invoice", "invoice", "invoices", "Sale", "saleNo", "invoiceNo"),
        T("purchaseBill", "purchase bill", "purchase bills", "Purchase", "purchaseNo", "invoiceNo"),
        new EntityType("voucher", "voucher", "vouchers", "AccountVoucher", "voucherNo", Array.Empty<string>(), KeyField: "voucherGuId"),
        // transactionId of StockMaster / AccountVoucher points to the source document (sale, purchase …): tried in order.
        T("document", "document", "documents", "Sale", "saleNo", "invoiceNo"),
        new EntityType("user", "user", "users", null, null, Array.Empty<string>(),
            Reason: "MySaleBooks users are stored in the identity (admin) database, not in the company database AYAAN reads."),
        new EntityType("costCenter", "cost centre", "cost centres", null, null, Array.Empty<string>(),
            Reason: "MySaleBooks has no cost-centre master or cost-centre id on its transactions (only Ledger.costCenterApplicable)."),
    }.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Other masters a polymorphic document id may point to (tried after <see cref="Types"/>["document"]).</summary>
    public static readonly IReadOnlyList<(string Collection, string NameField)> DocumentMasters = new[]
    {
        ("Sale", "saleNo"), ("Purchase", "purchaseNo")
    };

    private const string Model = "MySaleApp Inventory API Model";

    /// <summary>Verified MySaleBooks references (collection.field → entity).</summary>
    public static readonly IReadOnlyList<EntityReference> References = new[]
    {
        R("StockMaster", "itemId", "item"), R("StockMaster", "stockLocationId", "warehouse"), R("StockMaster", "ledgerId", "ledger"),
        R("StockMaster", "unitId", "unit"), R("StockMaster", "baseUnitId", "unit"), R("StockMaster", "branchId", "branch"),
        R("StockMaster", "transactionId", "document"),
        R("AccountVoucher", "ledgerId", "ledger"), R("AccountVoucher", "groupId", "group"), R("AccountVoucher", "parentGroupId", "group"),
        R("AccountVoucher", "currencyFromId", "currency"), R("AccountVoucher", "currencyToId", "currency"), R("AccountVoucher", "branchId", "branch"),
        R("AccountVoucher", "employeeId", "employee"), R("AccountVoucher", "departmentId", "department"), R("AccountVoucher", "voucherGuId", "voucher"),
        R("AccountVoucher", "transactionId", "document"), R("AccountVoucher", "userId", "user"),
        R("Sale", "ledgerId", "customer"), R("Sale", "employeeId", "salesman"), R("Sale", "stockLocationId", "warehouse"),
        R("Sale", "currencyFromId", "currency"), R("Sale", "branchId", "branch"), R("Sale", "userId", "user"),
        R("Purchase", "ledgerId", "supplier"), R("Purchase", "employeeId", "employee"), R("Purchase", "stockLocationId", "warehouse"),
        R("Purchase", "currencyFromId", "currency"), R("Purchase", "branchId", "branch"), R("Purchase", "userId", "user"),
        R("BillWiseDetails", "ledgerId", "ledger"), R("BillWiseDetails", "branchId", "branch"),
        R("Item", "categoryId", "category"), R("Item", "unitId", "unit"), R("Item", "manufactureId", "brand"), R("Item", "branchId", "branch"),
        R("Ledger", "groupId", "group"), R("Ledger", "parentGroupId", "group"), R("Ledger", "branchId", "branch"),
        R("Category", "parentId", "category"), R("Unit", "parentId", "unit"), R("StockLocation", "branchId", "branch"),
        R("Employee", "departmentId", "department"), R("Employee", "branchId", "branch"),
    };

    private static EntityReference R(string collection, string field, string entity) => new(collection, field, entity, $"{Model}/{collection}.cs [BsonElement(\"{field}\")]");

    /// <summary>
    /// Entity of an id column by its (alias) name when the pipeline does not show where it came from
    /// ("supplierId": "$_id" after a group the tracer could not follow). Only entities with a verified master.
    /// </summary>
    private static readonly (Regex Pattern, string Entity)[] NameRoles =
    {
        (new(@"^(customer|client|debtor)s?(id|guid|_id)?$", RegexOptions.IgnoreCase), "customer"),
        (new(@"^(supplier|vendor|creditor)s?(id|guid|_id)?$", RegexOptions.IgnoreCase), "supplier"),
        (new(@"^(sales?man|sales?person|salesmen|sales?rep|salesexecutive)s?(id|guid|_id)?$", RegexOptions.IgnoreCase), "salesman"),
        (new(@"^employee(id|guid|_id)?$", RegexOptions.IgnoreCase), "employee"),
        (new(@"^(item|product)s?(id|guid|_id)?$", RegexOptions.IgnoreCase), "item"),
        (new(@"^(service|serviceitem)s?(id|guid|_id)$", RegexOptions.IgnoreCase), "service"),
        (new(@"^categor(y|ies)(id|guid|_id)?$", RegexOptions.IgnoreCase), "category"),
        (new(@"^(brand|manufacture|manufacturer)s?(id|guid|_id)?$", RegexOptions.IgnoreCase), "brand"),
        (new(@"^(warehouse|stocklocation|location|godown)s?(id|guid|_id)?$", RegexOptions.IgnoreCase), "warehouse"),
        (new(@"^(branch|store|branche)s?(id|guid|_id)?$", RegexOptions.IgnoreCase), "branch"),
        (new(@"^(ledger|account|party)s?(id|guid|_id)?$", RegexOptions.IgnoreCase), "ledger"),
        (new(@"^(unit|baseunit|uom)s?(id|guid|_id)$", RegexOptions.IgnoreCase), "unit"),
        (new(@"^(currency|currencyfrom|currencyto)(id|guid|_id)$", RegexOptions.IgnoreCase), "currency"),
        (new(@"^department(id|guid|_id)?$", RegexOptions.IgnoreCase), "department"),
        (new(@"^(invoice|sale)(id|guid|_id)$", RegexOptions.IgnoreCase), "invoice"),
        (new(@"^(purchase|bill|purchasebill)(id|guid|_id)$", RegexOptions.IgnoreCase), "purchaseBill"),
        (new(@"^voucherguid$", RegexOptions.IgnoreCase), "voucher"),
        (new(@"^(transaction|document)(id|guid|_id)$", RegexOptions.IgnoreCase), "document"),
        (new(@"^(user|createdby|updatedby|cancelledby)(id|guid|_id)?$", RegexOptions.IgnoreCase), "user"),
        (new(@"^cost(center|centre)(id|guid|_id)?$", RegexOptions.IgnoreCase), "costCenter"),
    };

    /// <summary>The verified reference of collection.field, if any.</summary>
    public static EntityReference? Reference(string? collection, string? field)
    {
        if (collection is null || field is null) return null;
        return References.FirstOrDefault(r => string.Equals(r.Collection, collection, StringComparison.Ordinal)
                                              && string.Equals(r.Field, field, StringComparison.Ordinal));
    }

    /// <summary>Entity named by an output column / alias ("supplierId", "salesmanGuid", "brand").</summary>
    public static EntityType? ByName(string? column)
    {
        if (string.IsNullOrEmpty(column)) return null;
        var last = column.Split('.').Last().Replace("_", string.Empty);
        if (last.Length == 0 || last.Equals("id", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var (pattern, entity) in NameRoles)
            if (pattern.IsMatch(last)) return Types[entity];
        return null;
    }

    /// <summary>True when the business database is MySaleBooks (the verified references apply).</summary>
    public static bool Applies(IEnumerable<CollectionSchema> schema) => MySaleBooksDomain.IsActive(schema.ToList());

    /// <summary>
    /// Role of a Ledger reference from the query: Sale → customer, Purchase → supplier, a filter on account group 15
    /// (Sundry Debtors) → customer, 16 (Sundry Creditors) → supplier. Otherwise the plain ledger / account.
    /// </summary>
    public static EntityType LedgerRole(string? rootCollection, string? pipelineJson)
    {
        if (rootCollection == "Sale") return Types["customer"];
        if (rootCollection == "Purchase") return Types["supplier"];
        if (pipelineJson is not null)
        {
            var debtors = GroupFilter(pipelineJson, MySaleBooksDomain.DebtorsGroup);
            var creditors = GroupFilter(pipelineJson, MySaleBooksDomain.CreditorsGroup);
            if (debtors && !creditors) return Types["customer"];
            if (creditors && !debtors) return Types["supplier"];
        }
        return Types["ledger"];
    }

    private static bool GroupFilter(string json, int group)
        => Regex.IsMatch(json, $@"""(groupId|parentGroupId)""\s*:\s*(\{{[^}}]*?(\$eq|\$in)[^}}]*?\b{group}\b[^}}]*\}}|{group}\b)");

    /// <summary>Text of an id that means "none" in MySaleBooks ("0" is written for an unset reference).</summary>
    public static bool IsNoneValue(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim() == "0";
}
