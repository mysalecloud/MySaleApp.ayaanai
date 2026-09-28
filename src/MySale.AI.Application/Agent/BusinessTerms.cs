using System.Text;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;

namespace MySale.AI.Application.Agent;

/// <summary>
/// Configuration section "BusinessTerms": the semantic dictionary that maps the words users say ("customer", "who owes us",
/// "vendor ledger", "payables") to the accounting concepts MySaleBooks stores ("Sundry Debtors", "Sundry Creditors" …).
/// Entries here extend / override the built-in dictionary by key. The mapping is conceptual only — the real collections,
/// fields and stored group values are always taken from the schema and the company's data.
/// </summary>
public sealed class BusinessTermOptions
{
    public const string Section = "BusinessTerms";

    /// <summary>Extra or replacement concepts, e.g. { "customer": { "AccountGroups": ["Sundry Debtors", "Trade Receivables"] } }.</summary>
    public Dictionary<string, BusinessConceptOptions> Concepts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Field names (last path segment, case-insensitive) that hold an account group / party classification.</summary>
    public List<string> GroupFields { get; set; } = new()
    {
        "groupName", "group", "accountGroup", "accountGroupName", "ledgerGroup", "ledgerGroupName", "underGroup", "underGroupName",
        "parentGroup", "parentGroupName", "primaryGroup", "primaryGroupName", "groupType", "partyType", "ledgerType", "accountType", "under"
    };

    /// <summary>Read the stored group values (e.g. "SUNDRY DEBTORS") from the company's data before generating the query.</summary>
    public bool VerifyGroupValues { get; set; } = true;

    /// <summary>After an empty result, ask the model once to re-check the business mapping (entity, collection, group, store, dates).</summary>
    public bool RetryOnZeroResults { get; set; } = true;
}

public sealed class BusinessConceptOptions
{
    /// <summary>How the concept is called in answers when the user's own word is not suitable ("customers").</summary>
    public string? DisplayName { get; set; }
    /// <summary>Words / phrases (case-insensitive, whole words) that mean this concept.</summary>
    public List<string>? Synonyms { get; set; }
    /// <summary>Regular expressions for phrasings such as "who owes us".</summary>
    public List<string>? Patterns { get; set; }
    /// <summary>Accounting groups / classifications that represent the concept ("Sundry Debtors").</summary>
    public List<string>? AccountGroups { get; set; }
    /// <summary>Collection name hints (matched against the schema; only collections that exist are used).</summary>
    public List<string>? Collections { get; set; }
    /// <summary>"receivable" | "payable" | null.</summary>
    public string? Side { get; set; }
}

/// <summary>One business concept of the dictionary (built-in + configured).</summary>
public sealed record BusinessConcept(
    string Key,
    string DisplayName,
    IReadOnlyList<string> Synonyms,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> AccountGroups,
    IReadOnlyList<string> Collections,
    string? Side);

/// <summary>A place in the schema where an accounting group is stored (e.g. Ledgers.groupName).</summary>
public sealed record GroupFieldRef(string Collection, string Field);

/// <summary>How one business term of the question maps to the application's structure.</summary>
public sealed class ConceptMapping
{
    public required BusinessConcept Concept { get; init; }
    /// <summary>The words the user used ("customer list", "who owes us").</summary>
    public required string UserTerm { get; init; }
    /// <summary>list | ledger | outstanding | transactions | balance</summary>
    public required string Aspect { get; init; }
    /// <summary>Collections of the schema that represent the concept (masters / transactions).</summary>
    public List<string> Collections { get; init; } = new();
    /// <summary>Schema fields that hold account groups (candidates for the concept's AccountGroups).</summary>
    public List<GroupFieldRef> GroupFields { get; init; } = new();
    /// <summary>Stored group values found in the company's data, per group field ("SUNDRY DEBTORS").</summary>
    public Dictionary<GroupFieldRef, List<string>> StoredGroupValues { get; } = new();

    public bool HasTarget => Collections.Count > 0 || GroupFields.Count > 0;
    public IEnumerable<string> AllStoredValues => StoredGroupValues.Values.SelectMany(v => v);
}

/// <summary>Result of the semantic step: business intent of the question, before the query is generated.</summary>
public sealed class SemanticInterpretation
{
    public List<ConceptMapping> Mappings { get; } = new();
    /// <summary>Set when the business entity cannot be determined (e.g. "party list": customers or suppliers?).</summary>
    public string? Clarification { get; set; }
    public bool IsEmpty => Mappings.Count == 0 && Clarification is null;

    /// <summary>Plain-text summary for the trace / activity log (no data values beyond group names).</summary>
    public string Describe()
    {
        if (Clarification is not null) return "Clarification needed: " + Clarification;
        var sb = new StringBuilder();
        foreach (var m in Mappings)
        {
            sb.Append($"\"{m.UserTerm}\" → {m.Concept.Key} ({m.Aspect})");
            if (m.Concept.AccountGroups.Count > 0) sb.Append($" · group {string.Join(" / ", m.Concept.AccountGroups)}");
            if (m.Collections.Count > 0) sb.Append($" · collections {string.Join(", ", m.Collections)}");
            foreach (var (field, values) in m.StoredGroupValues)
                sb.Append($" · {field.Collection}.{field.Field} = {string.Join(", ", values.Select(v => "\"" + v + "\""))}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Semantic step before query generation: business intent → business entity → accounting concept → the collections,
/// group fields and stored values of THIS database. Pure and deterministic (the stored group values are read separately by
/// <see cref="QueryEngine.StoredValuesAsync"/>), so it can be unit-tested and it never guesses database content.
/// </summary>
public static class BusinessTerms
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Built-in dictionary. Examples only — schema and configuration decide the final meaning.</summary>
    public static readonly IReadOnlyList<BusinessConcept> Defaults = new List<BusinessConcept>
    {
        new("customer", "customers",
            new[] { "customer", "customers", "client", "clients", "buyer", "buyers", "debtor", "debtors", "sundry debtor", "sundry debtors",
                    "receivable", "receivables", "receivable party", "receivable parties", "customer dues", "dues from customers" },
            new[] { @"\bowes?\s+(?:us|me)\b", @"\bowing\s+(?:us|me)\b", @"\bowe\s+(?:us|me)\b", @"\b(?:need|needs|have|has|yet)\s+to\s+pay\s+(?:us|me)\b",
                    @"\bpay\s+(?:us|me)\b", @"\bto\s+(?:be\s+)?receive[d]?\b", @"\bamount\s+pending\s+from\b", @"\bpending\s+from\s+customers?\b" },
            new[] { "Sundry Debtors" },
            new[] { "Customers", "Customer", "CustomerMaster", "Parties", "Party", "Ledgers", "Ledger", "AccountLedgers", "Accounts", "LedgerMaster" },
            "receivable"),
        new("vendor", "suppliers",
            new[] { "vendor", "vendors", "supplier", "suppliers", "creditor", "creditors", "sundry creditor", "sundry creditors",
                    "payable", "payables", "payable party", "payable parties", "purchase party", "purchase parties" },
            new[] { @"\b(?:we|i)\s+owe\b", @"\bdo\s+(?:we|i)\s+owe\b", @"\bowe\s+(?:money\s+)?to\b", @"\b(?:we|i)\s+(?:need|have|must)\s+to\s+pay\b",
                    @"\bamount\s+(?:we|i)\s+(?:need|have)\s+to\s+pay\b", @"\bparty\s+we\s+owe\b", @"\bto\s+be\s+paid\b" },
            new[] { "Sundry Creditors" },
            new[] { "Suppliers", "Supplier", "Vendors", "Vendor", "Parties", "Party", "Ledgers", "Ledger", "AccountLedgers", "Accounts", "LedgerMaster" },
            "payable"),
        new("sales", "sales",
            new[] { "sales", "sale", "sales invoice", "sales invoices", "invoice", "invoices", "sold", "billing", "turnover" },
            Array.Empty<string>(), Array.Empty<string>(),
            new[] { "Sales", "SalesInvoices", "SaleInvoices", "Invoices", "SalesVouchers", "SaleItems", "SalesItems" }, null),
        new("purchase", "purchases",
            new[] { "purchase", "purchases", "purchase invoice", "purchase invoices", "purchase bill", "purchase bills", "bought" },
            Array.Empty<string>(), Array.Empty<string>(),
            new[] { "Purchases", "PurchaseInvoices", "PurchaseVouchers", "PurchaseItems", "Bills" }, null),
        new("stock", "stock",
            new[] { "stock", "inventory", "stock balance", "stock level", "stock levels", "closing stock", "item stock" },
            Array.Empty<string>(), Array.Empty<string>(),
            new[] { "Stock", "Stocks", "StockBalances", "Inventory", "ItemStock", "Items", "Products" }, null),
        new("item", "items",
            new[] { "item", "items", "product", "products", "goods", "sku", "skus", "article", "articles" },
            Array.Empty<string>(), Array.Empty<string>(),
            new[] { "Items", "Products", "ItemMaster", "ProductMaster", "Inventory" }, null),
        new("cash", "cash",
            new[] { "cash", "cash in hand", "cash-in-hand", "cash account", "cash ledger", "petty cash" },
            Array.Empty<string>(), new[] { "Cash-in-Hand", "Cash in Hand" },
            new[] { "Ledgers", "Ledger", "AccountLedgers", "Accounts" }, null),
        new("bank", "bank",
            new[] { "bank", "banks", "bank balance", "bank account", "bank accounts", "bank ledger" },
            Array.Empty<string>(), new[] { "Bank Accounts", "Bank OD A/c", "Bank OCC A/c" },
            new[] { "Ledgers", "Ledger", "AccountLedgers", "Accounts", "Banks" }, null),
        new("expense", "expenses",
            new[] { "expense", "expenses", "expenditure", "expenditures", "expense account", "expense ledger", "spending" },
            Array.Empty<string>(), new[] { "Direct Expenses", "Indirect Expenses" },
            new[] { "Expenses", "Ledgers", "Ledger", "AccountLedgers", "Accounts" }, null),
        new("income", "income",
            new[] { "income", "incomes", "revenue", "revenues", "other income", "income account", "revenue account" },
            Array.Empty<string>(), new[] { "Direct Incomes", "Indirect Incomes", "Sales Accounts" },
            new[] { "Ledgers", "Ledger", "AccountLedgers", "Accounts", "Incomes" }, null),
        new("profit", "profit",
            new[] { "profit", "loss", "net profit", "gross profit", "profit and loss", "profit & loss", "p&l", "p & l", "pnl" },
            Array.Empty<string>(), Array.Empty<string>(),
            new[] { "Ledgers", "Ledger", "AccountLedgers", "Accounts", "Vouchers", "Transactions" }, null)
    };

    /// <summary>The dictionary with configured entries applied (same key = override, new key = added).</summary>
    public static IReadOnlyList<BusinessConcept> Dictionary(BusinessTermOptions? options)
    {
        var list = Defaults.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        if (options?.Concepts is { Count: > 0 } extra)
            foreach (var (key, o) in extra)
            {
                list.TryGetValue(key, out var baseConcept);
                list[key] = new BusinessConcept(key,
                    o.DisplayName ?? baseConcept?.DisplayName ?? key,
                    o.Synonyms ?? baseConcept?.Synonyms.ToList() ?? new List<string>(),
                    o.Patterns ?? baseConcept?.Patterns.ToList() ?? new List<string>(),
                    o.AccountGroups ?? baseConcept?.AccountGroups.ToList() ?? new List<string>(),
                    o.Collections ?? baseConcept?.Collections.ToList() ?? new List<string>(),
                    o.Side ?? baseConcept?.Side);
            }
        return list.Values.ToList();
    }

    // Aspect of an entity: "customer ledger" ≠ "customer sales" ≠ "customer outstanding" ≠ "customer list".
    private static readonly Regex LedgerWords = new(@"\b(ledger|statement|account\s+history|transactions?\s+(of|for)|day\s*book)\b", Opt);
    private static readonly Regex OutstandingWords = new(@"\b(outstanding|dues?|pending|unpaid|owes?|owing|owe|receivables?|payables?|to\s+be\s+(paid|received)|need\s+to\s+pay|have\s+to\s+pay|overdue)\b", Opt);
    private static readonly Regex BalanceWords = new(@"\bbalances?\b", Opt);
    private static readonly Regex SalesWords = new(@"\b(sales?|sold|invoices?|bought|purchased|purchases?|orders?|bills?)\b", Opt);
    private static readonly Regex PartyWord = new(@"\bpart(?:y|ies)\b|\bledger\s+list\b|\baccount\s+list\b", Opt);

    /// <summary>
    /// Maps the business terms of a question to concepts and to the collections / group fields that exist in the schema.
    /// Returns a clarification when the entity is ambiguous ("show party list").
    /// </summary>
    public static SemanticInterpretation Interpret(string? question, IReadOnlyList<CollectionSchema> schema, BusinessTermOptions? options = null)
    {
        var result = new SemanticInterpretation();
        if (string.IsNullOrWhiteSpace(question)) return result;
        var text = Regex.Replace(question, @"\s+", " ");
        var dictionary = Dictionary(options);
        var groupFieldNames = new HashSet<string>(options?.GroupFields ?? new BusinessTermOptions().GroupFields, StringComparer.OrdinalIgnoreCase);

        var found = new List<(BusinessConcept Concept, string Term, int Index)>();
        foreach (var concept in dictionary)
        {
            (string Term, int Index)? best = null;
            foreach (var p in concept.Patterns)
            {
                Match m;
                try { m = Regex.Match(text, p, Opt, TimeSpan.FromMilliseconds(50)); }
                catch (ArgumentException) { continue; }             // a bad configured pattern is ignored
                catch (RegexMatchTimeoutException) { continue; }
                if (m.Success && (best is null || m.Index < best.Value.Index)) best = (m.Value, m.Index);
            }
            // Longest synonym first ("sundry debtors" before "debtors"); whole words only.
            foreach (var s in concept.Synonyms.OrderByDescending(s => s.Length))
            {
                var m = Regex.Match(text, @"(?<![\w&])" + Regex.Escape(s) + @"(?![\w&])", Opt);
                if (m.Success && (best is null || m.Index < best.Value.Index || m.Value.Length > best.Value.Term.Length && m.Index == best.Value.Index))
                    best = (m.Value, m.Index);
            }
            if (best is { } b) found.Add((concept, b.Term, b.Index));
        }

        // "Purchases" by a customer is the customer's buying (sales to them), not our purchase transactions — and the
        // generic "sales"/"purchase" concepts are only aspects when a party is the entity.
        var hasCustomer = found.Any(f => f.Concept.Key == "customer");
        var hasVendor = found.Any(f => f.Concept.Key == "vendor");
        if (!hasCustomer && !hasVendor && PartyWord.IsMatch(text) && !found.Any(f => f.Concept.Key is "cash" or "bank" or "expense" or "income"))
        {
            result.Clarification = "Do you mean customers (parties who owe you money) or suppliers (parties you owe money to)?";
            return result;
        }

        foreach (var (concept, term, _) in found.OrderBy(f => f.Index))
        {
            // Skip transaction concepts that only describe what a party did ("customer sales", "supplier purchases").
            if ((hasCustomer || hasVendor) && concept.Key is "sales" or "purchase") continue;
            var aspect = AspectOf(text, concept);
            result.Mappings.Add(new ConceptMapping
            {
                Concept = concept,
                UserTerm = term.Trim(),
                Aspect = aspect,
                Collections = MatchCollections(concept, schema),
                GroupFields = concept.AccountGroups.Count == 0 ? new List<GroupFieldRef>() : FindGroupFields(schema, groupFieldNames)
            });
        }
        return result;
    }

    private static string AspectOf(string text, BusinessConcept concept)
    {
        if (concept.Key is not ("customer" or "vendor")) return BalanceWords.IsMatch(text) ? "balance" : "list";
        if (LedgerWords.IsMatch(text)) return "ledger";
        if (OutstandingWords.IsMatch(text) || BalanceWords.IsMatch(text)) return "outstanding";
        if (SalesWords.IsMatch(text)) return "transactions";
        return "list";
    }

    private static List<string> MatchCollections(BusinessConcept concept, IReadOnlyList<CollectionSchema> schema)
    {
        var names = schema.Select(c => c.Name).ToList();
        var result = new List<string>();
        foreach (var hint in concept.Collections)
            foreach (var n in names)
                if (string.Equals(Normalize(n), Normalize(hint), StringComparison.Ordinal) && !result.Contains(n, StringComparer.Ordinal))
                    result.Add(n);
        return result;
    }

    private static string Normalize(string name) => Regex.Replace(name, "[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();

    /// <summary>String fields named like an account group (groupName, accountGroup, underGroup …), in allowed collections.</summary>
    public static List<GroupFieldRef> FindGroupFields(IReadOnlyList<CollectionSchema> schema, ISet<string> groupFieldNames)
    {
        var result = new List<GroupFieldRef>();
        foreach (var c in schema)
            foreach (var f in c.Fields)
            {
                if (f.Hidden || f.Type is not ("string" or "String" or "mixed")) continue;
                var last = f.Name.Split('.').Last();
                if (groupFieldNames.Contains(last)) result.Add(new GroupFieldRef(c.Name, f.Name));
            }
        return result.Take(6).ToList();
    }

    /// <summary>
    /// Anchored, case- and spacing-insensitive pattern for a group name that also accepts the singular / plural form:
    /// "Sundry Debtors" → ^\s*sundry\s+debtors?\s*$ (matches "SUNDRY DEBTORS", "Sundry Debtor ").
    /// </summary>
    public static string GroupPattern(string group)
    {
        var words = Regex.Split(group.Trim(), @"[\s\-]+").Where(w => w.Length > 0)
            .Select(w => w.Length > 3 && w.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? Regex.Escape(w[..^1]) + "s?" : Regex.Escape(w));
        return "^\\s*" + string.Join("[\\s-]+", words) + "\\s*$";
    }
}
