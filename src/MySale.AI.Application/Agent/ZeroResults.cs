using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MySale.AI.Application.Agent;

/// <summary>What to show when a valid query matched nothing.</summary>
public sealed class ZeroResult
{
    /// <summary>Clear, topic- and period-aware sentence ("No sales were recorded today.").</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>
    /// For totals/counts ($group with _id null, $count): the row the query would return over zero documents —
    /// sums and counts are 0, averages/min/max stay null ("not available", never turned into 0).
    /// </summary>
    public JsonObject? ZeroRow { get; init; }
}

/// <summary>
/// Turns an empty result into a meaningful answer. MongoDB returns [] for "$group _id:null" over no documents; for
/// totals that is a real zero ("Today's sales are AED 0.00"), for lists it is "no … matched". The message names what
/// was searched and the period — it never claims the business has no records at all.
/// </summary>
public static class ZeroResultPolicy
{
    private sealed record Topic(string Key, Regex Pattern, string ListMessage, string? Noun);

    // Order matters: the first match wins (e.g. "customers who purchased" is about customers, not purchases).
    private static readonly Topic[] Topics =
    {
        new("lowstock", new(@"\b(low|out|below)\s+(?:(?:in|on|of)\s+)?(stock|reorder|minimum)\b|\breorder\b|\bstock\s+(is\s+)?(low|below)|\brunning\s+low\b", RegexOptions.IgnoreCase),
            "No products are currently below the configured stock level.", null),
        new("receivable", new(@"\b(outstanding|receivables?|overdue|unpaid|dues?|pending\s+payments?)\b.*\b(customers?|clients?|debtors?)\b|\b(customers?|clients?|debtors?)\b.*\b(outstanding|balances?|dues?|owe|unpaid|pending)\b|\breceivables?\b|\bowes?\s+(us|me)\b|\bpay\s+(us|me)\b", RegexOptions.IgnoreCase),
            "There are no customers with outstanding balances matching your criteria.", null),
        new("payable", new(@"\b(payables?|outstanding|unpaid|dues?)\b.*\b(suppliers?|vendors?|creditors?)\b|\b(suppliers?|vendors?|creditors?)\b.*\b(outstanding|balances?|dues?|unpaid|pending)\b|\bpayables?\b|\b(we|i)\s+owe\b|\bowe\s+(money\s+)?to\b|\b(we|i)\s+(need|have|must)\s+to\s+pay\b", RegexOptions.IgnoreCase),
            "There are no suppliers with outstanding balances matching your criteria.", null),
        new("nonbuyers", new(@"\b(customers?|clients?)\b.*\b(not|never|no|zero|without|haven'?t|didn'?t)\b.*\b(purchase[ds]?|bought|buy|sales|invoices?|orders?)\b", RegexOptions.IgnoreCase),
            "No customers matched your criteria{period}.", null),
        new("buyers", new(@"\bwho\s+(purchased|bought|ordered)\b|\b(customers?|clients?)\s+(who|that)\s+(purchased|bought|ordered)\b|\b(customers?|clients?)\s+(with|having)\s+(sales|purchases|invoices)\b", RegexOptions.IgnoreCase),
            "No customers made purchases{period}.", null),
        new("customers", new(@"\bcustomers?\b|\bclients?\b|\bbuyers?\b|\bdebtors?\b|\bwho\s+paid\b", RegexOptions.IgnoreCase),
            "No customers matched your criteria{period}.", null),
        new("suppliers", new(@"\bsuppliers?\b|\bvendors?\b|\bcreditors?\b", RegexOptions.IgnoreCase),
            "No suppliers matched your criteria{period}.", null),
        new("returns", new(@"\breturns?\b|\breturned\b", RegexOptions.IgnoreCase),
            "No returns were recorded{period}.", "returns"),
        new("invoices", new(@"\binvoices?\b|\bbills?\b", RegexOptions.IgnoreCase),
            "No invoices were found{period}.", "invoices"),
        new("payments", new(@"\bpayments?\b|\breceipts?\b|\bcollections?\b|\bpaid\b", RegexOptions.IgnoreCase),
            "No payments were recorded{period}.", "payments"),
        new("purchases", new(@"\bpurchases?\b|\bbought\b|\bgrn\b", RegexOptions.IgnoreCase),
            "No purchase transactions were recorded{period}.", "purchases"),
        new("expenses", new(@"\bexpenses?\b|\bspent\b|\bspending\b", RegexOptions.IgnoreCase),
            "No expenses were recorded{period}.", "expenses"),
        new("orders", new(@"\borders?\b|\bquotations?\b|\bquotes?\b|\bdeliver(y|ies)\b", RegexOptions.IgnoreCase),
            "No orders were found{period}.", "orders"),
        new("soldproducts", new(@"\b(products?|items?)\b.*\b(sold|sell|selling)\b|\b(sold|selling)\b.*\b(products?|items?)\b", RegexOptions.IgnoreCase),
            "No products were sold{period}.", null),
        new("products", new(@"\bproducts?\b|\bitems?\b|\bstock\b|\binventory\b", RegexOptions.IgnoreCase),
            "No products matched your criteria{period}.", null),
        new("sales", new(@"\bsales?\b|\bsell\b|\bsold\b|\brevenue\b|\bturnover\b|\bincome\b", RegexOptions.IgnoreCase),
            "No sales were recorded{period}.", "sales")
    };

    private static readonly (Regex Pattern, string Phrase, string Possessive)[] Periods =
    {
        (new(@"\btoday\b|\btoday'?s\b", RegexOptions.IgnoreCase), " today", "Today's"),
        (new(@"\byesterday\b", RegexOptions.IgnoreCase), " yesterday", "Yesterday's"),
        (new(@"\btomorrow\b", RegexOptions.IgnoreCase), " for tomorrow", "Tomorrow's"),
        (new(@"\bthis\s+week\b", RegexOptions.IgnoreCase), " this week", "This week's"),
        (new(@"\b(last|previous)\s+week\b", RegexOptions.IgnoreCase), " last week", "Last week's"),
        (new(@"\b(this|current)\s+month\b", RegexOptions.IgnoreCase), " this month", "This month's"),
        (new(@"\b(last|previous)\s+month\b", RegexOptions.IgnoreCase), " last month", "Last month's"),
        (new(@"\b(this|current)\s+financial\s+year\b|\bthis\s+fy\b", RegexOptions.IgnoreCase), " in the current financial year", "This financial year's"),
        (new(@"\b(last|previous)\s+financial\s+year\b|\blast\s+fy\b", RegexOptions.IgnoreCase), " in the previous financial year", "Last financial year's"),
        (new(@"\b(this|current)\s+year\b", RegexOptions.IgnoreCase), " this year", "This year's"),
        (new(@"\b(last|previous)\s+year\b", RegexOptions.IgnoreCase), " last year", "Last year's"),
        (new(@"\b(last|past|previous)\s+(\d+)\s+days\b", RegexOptions.IgnoreCase), " in the last {n} days", "Last {n} days'"),
    };

    public static ZeroResult Describe(string question, JsonArray? pipeline, string? collection, QuestionDates? dates, string currency)
    {
        var zeroRow = pipeline is null ? null : ZeroRowFor(pipeline);
        var (period, possessive) = PeriodOf(question, dates);

        // Localised generic messages (the question language decides).
        if (Regex.IsMatch(question, @"[ഀ-ൿ]"))
            return new ZeroResult { ZeroRow = zeroRow, Message = zeroRow is not null
                ? "ഈ കാലയളവിൽ ഇടപാടുകളൊന്നും രേഖപ്പെടുത്തിയിട്ടില്ല — ആകെ 0."
                : "ഈ ചോദ്യത്തിന് പൊരുത്തപ്പെടുന്ന രേഖകളൊന്നും കണ്ടെത്തിയില്ല. മറ്റൊരു തീയതിയോ കാലയളവോ ഉപയോഗിച്ച് ശ്രമിക്കുക." };
        if (Regex.IsMatch(question, @"[؀-ۿ]"))
            return new ZeroResult { ZeroRow = zeroRow, Message = zeroRow is not null
                ? "لا توجد معاملات مسجلة في هذه الفترة — الإجمالي 0."
                : "لم أجد أي سجلات مطابقة. جرّب فترة زمنية أخرى (مثل هذا الشهر أو الشهر الماضي)." };

        var topic = Topics.FirstOrDefault(t => t.Pattern.IsMatch(question))
                    ?? TopicFromCollection(collection);

        // Totals: "Today's sales are AED 0.00. No sales were recorded today."
        if (zeroRow is not null && topic?.Noun is { } noun)
        {
            var hasMoney = zeroRow.Any(kv => kv.Value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && !IsCountName(kv.Key));
            var listSentence = Fill(topic.ListMessage, period);
            if (hasMoney && noun is "sales" or "purchases" or "payments" or "expenses" or "returns")
            {
                var subject = possessive is not null ? $"{possessive} {noun}" : $"Total {noun}{period}";
                return new ZeroResult { ZeroRow = zeroRow, Message = $"{subject} {(noun.EndsWith('s') ? "are" : "is")} {currency} 0.00. {listSentence}" };
            }
            return new ZeroResult { ZeroRow = zeroRow, Message = listSentence };
        }

        return new ZeroResult
        {
            ZeroRow = zeroRow,
            Message = topic is not null ? Fill(topic.ListMessage, period) : $"No records matched your criteria{period}."
        };
    }

    private static string Fill(string template, string period) => template.Replace("{period}", period);

    private static bool IsCountName(string name) => Regex.IsMatch(name, "count|number|qty|quantity|orders|invoices$", RegexOptions.IgnoreCase);

    private static Topic? TopicFromCollection(string? collection)
    {
        if (string.IsNullOrEmpty(collection)) return null;
        var c = collection.ToLowerInvariant();
        return c switch
        {
            _ when c.Contains("return") => Topics.First(t => t.Key == "returns"),
            _ when c.Contains("purchase") => Topics.First(t => t.Key == "purchases"),
            _ when c.Contains("payment") || c.Contains("receipt") => Topics.First(t => t.Key == "payments"),
            _ when c.Contains("expense") => Topics.First(t => t.Key == "expenses"),
            _ when c.Contains("customer") => Topics.First(t => t.Key == "customers"),
            _ when c.Contains("supplier") => Topics.First(t => t.Key == "suppliers"),
            _ when c.Contains("item") || c.Contains("product") || c.Contains("stock") => Topics.First(t => t.Key == "products"),
            _ when c.Contains("order") || c.Contains("quotation") => Topics.First(t => t.Key == "orders"),
            _ when c.Contains("sale") || c.Contains("invoice") => Topics.First(t => t.Key == "sales"),
            _ => null
        };
    }

    /// <summary>" between 1 Sep 2026 and 10 Sep 2026" / " today" / "" and the possessive subject ("Today's").</summary>
    public static (string Phrase, string? Possessive) PeriodOf(string question, QuestionDates? dates)
    {
        if (dates is not null)
        {
            var span = dates.Spans.FirstOrDefault();
            if (span is not null)
                return span.From == span.To
                    ? ($" on {QuestionDates.Describe(span.From)}", null)
                    : ($" between {QuestionDates.Describe(span.From)} and {QuestionDates.Describe(span.To)}", null);
        }
        foreach (var (pattern, phrase, possessive) in Periods)
        {
            var m = pattern.Match(question);
            if (!m.Success) continue;
            var n = m.Groups.Count > 2 && m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            return (phrase.Replace("{n}", n), possessive.Replace("{n}", n));
        }
        return (string.Empty, null);
    }

    /// <summary>
    /// The row a "totals" pipeline returns over zero documents: $group with a constant _id (null) or a $count stage.
    /// $sum/$count → 0; $avg/$min/$max/$first/$last/… → null. A following $project keeps/renames those values.
    /// Lists (group by a field, $match only, find) return null: an empty list is the right answer for them.
    /// </summary>
    public static JsonObject? ZeroRowFor(JsonArray pipeline)
    {
        JsonObject? row = null;
        foreach (var stage in pipeline.OfType<JsonObject>())
        {
            if (stage["$group"] is JsonObject group)
            {
                if (row is not null) return null;                               // grouped twice: not a simple total
                var id = group["_id"];
                var constantId = id is null || id.GetValueKind() is System.Text.Json.JsonValueKind.Null
                                 || (id is JsonValue v && !(v.TryGetValue<string>(out var s) && s.StartsWith('$')));
                if (!constantId) return null;
                row = new JsonObject();
                foreach (var (name, acc) in group)
                {
                    if (name == "_id") continue;
                    row[name] = acc is JsonObject a && (a.ContainsKey("$sum") || a.ContainsKey("$count")) ? JsonValue.Create(0) : null;
                }
            }
            else if (stage["$count"] is JsonValue countName && countName.TryGetValue<string>(out var cn))
            {
                if (row is not null) return null;
                row = new JsonObject { [cn] = 0 };
            }
            else if (row is not null && stage["$project"] is JsonObject project)
            {
                var projected = new JsonObject();
                foreach (var (name, spec) in project)
                {
                    if (name == "_id") continue;
                    if (spec is JsonValue flag && (flag.TryGetValue<int>(out var i) ? i != 0 : flag.TryGetValue<bool>(out var b) && b))
                        projected[name] = row[name]?.DeepClone();
                    else if (FirstFieldRef(spec) is { } source)
                        projected[name] = row.ContainsKey(source) ? row[source]?.DeepClone() : null;
                }
                row = projected;
            }
            else if (row is not null && (stage.ContainsKey("$unwind") || stage.ContainsKey("$lookup") || stage.ContainsKey("$facet")))
            {
                return null;                                                    // shape too complex to predict
            }
        }
        return row is { Count: > 0 } ? row : null;
    }

    private static string? FirstFieldRef(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith('$') && !s.StartsWith("$$") => s[1..],
        JsonObject o => o.Select(kv => FirstFieldRef(kv.Value)).FirstOrDefault(r => r is not null),
        JsonArray a => a.Select(FirstFieldRef).FirstOrDefault(r => r is not null),
        _ => null
    };
}
