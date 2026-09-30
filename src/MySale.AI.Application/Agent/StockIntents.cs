using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Mql;

namespace MySale.AI.Application.Agent;

/// <summary>A canonical stock intent resolved from the question, with the server report plan that answers it.</summary>
public sealed record StockIntentMatch(string Intent, string View, MqlQuery Plan, string Reason);

/// <summary>
/// Canonical intents for whole-inventory stock questions ("Current Stock", "Show Stock", "Stock on Hand", "Available
/// Stock", "Inventory Balance" → STOCK_CURRENT; "Low Stock", "Products running low" → STOCK_LOW; "Out of Stock", "Zero
/// Stock" → STOCK_OUT …). Every wording of the same intent runs the same server stock engine
/// (<see cref="MySaleBooksReports.StockSummaryReport"/>), so "Show stock" and "What is my current stock?" can never be
/// calculated differently. Only questions about the WHOLE inventory are routed: a question that names a product, a
/// customer or anything else keeps its extra words, does not match, and goes on to the item report / the planner.
/// </summary>
public static class StockIntents
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Words that carry no meaning for the intent ("Which", "my", "products", "please" …).</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "which", "what", "whats", "what's", "is", "are", "am", "was", "were", "my", "our", "the", "a", "an", "all", "show", "list", "give",
        "me", "tell", "display", "get", "see", "view", "of", "in", "for", "with", "do", "does", "i", "we", "you", "have", "has", "had",
        "please", "currently", "now", "right", "total", "overall", "entire", "whole", "items", "item", "products", "product", "goods",
        "there", "any", "how", "many", "much", "that", "those", "these", "current", "present", "today", "todays", "today's", "report",
        "details", "detail", "can", "could", "would", "like", "to", "know", "check", "find", "at", "moment", "company", "business",
        "shop", "store", "and", "by", "up", "on", "as", "till", "until", "from", "between", "since", "during", "period", "time",
        "top", "some", "hand", "me.", "pls", "kindly", "wise"
    };

    // The words kept by the filler removal must match one of these completely (anchored).
    private static readonly (string Intent, string View, Regex Pattern)[] Rules =
    {
        ("STOCK_NEGATIVE", "negative", new Regex(@"^(negative stock|stock negative|minus stock|stock minus|below zero stock|stock below zero|negative)$", Opt)),
        ("STOCK_OUT", "out", new Regex(@"^(out stock|zero stock|stock zero|no stock|stock out|stockout|nil stock|without stock|stock finished|finished stock|sold out|not stock|stock over|stock exhausted)$", Opt)),
        ("STOCK_LOW", "low", new Regex(@"^(low stock|stock low|running low|running low stock|low|below reorder level|below reorder|reorder|reorder level|need reorder|needs reorder|need reordering|needs reordering|to reorder|should reorder|low stock alert|stock alert|below minimum stock|below minimum level|minimum level)$", Opt)),
        ("STOCK_VALUE", "value", new Regex(@"^((closing )?(stock|inventory) (value|valuation|worth|amount)|value (stock|inventory)|worth (stock|inventory)|stock worth|inventory worth|valuation (stock|inventory))$", Opt)),
        ("STOCK_BY_CATEGORY", "byCategory", new Regex(@"^(stock category|category stock|categorywise stock|stock categorywise|stock per category|stock each category|categories most stock|category most stock|categories highest stock|categories stock|stock categories|stock value category|stock value categories)$", Opt)),
        ("STOCK_BY_WAREHOUSE", "byWarehouse", new Regex(@"^(stock (warehouse|warehouses|location|locations|godown|godowns)|(warehouse|location|godown)(s)? stock|stock per (warehouse|location|godown)|stock each (warehouse|location|godown)|(warehouse|location|godown)wise stock)$", Opt)),
        ("STOCK_HIGHEST", "highest", new Regex(@"^(highest stock|most stock|maximum stock|max stock|largest stock|stock highest|biggest stock)$", Opt)),
        ("STOCK_LOWEST", "lowest", new Regex(@"^(lowest stock|least stock|smallest stock|stock lowest)$", Opt)),
        ("STOCK_MOVEMENT", "movement", new Regex(@"^(stock movements?|inventory movements?|stock movement summary|stock transactions?|inventory transactions?|stock in out|stock inward outward)$", Opt)),
        ("PRODUCTS_FAST_MOVING", "fastMoving", new Regex(@"^(fast moving|fast movers?|fastest moving|best moving|most moving|sold most quantity|sold most|most sold|most sold quantity|highest quantity sold|most quantity sold|selling most quantity|best selling quantity|top selling quantity|top sold quantity)$", Opt)),
        ("PRODUCTS_SLOW_MOVING", "slowMoving", new Regex(@"^(slow moving|slow movers?|slowest moving|least moving|least sold|lowest sales|lowest selling|least selling|slow selling|poor selling)$", Opt)),
        ("PRODUCTS_NON_MOVING", "nonMoving", new Regex(@"^(non moving|nonmoving|not moving|dead stock|no sales|not sold|unsold|without sales|no movement|not selling|no sale)$", Opt)),
        ("STOCK_CURRENT", "current", new Regex(@"^(stock|inventory|stock available|available stock|inventory balance|stock balance|stock position|stock list|stock summary|stock status|inventory status|stock level|stock levels|closing stock|stock quantity|stock qty|inventory list|inventory summary|physical stock)$", Opt))
    };

    // Malayalam / mixed wording (the MySaleBooks chat quick questions): stock + "less" / "finished" / value.
    private static readonly (string Intent, string View, Regex Pattern)[] LocalRules =
    {
        ("STOCK_LOW", "low", new Regex(@"(stock|സ്റ്റോക്ക്?)\s*(കുറവ|കുറഞ്ഞ)", Opt)),
        ("STOCK_OUT", "out", new Regex(@"(stock|സ്റ്റോക്ക്?)\s*(ഇല്ലാത്ത|തീർന്ന)", Opt)),
        ("STOCK_VALUE", "value", new Regex(@"(stock|സ്റ്റോക്ക്?)\s*(value|വാല്യു|മൂല്യം|വില)\s*(എത്ര)", Opt))
    };

    // A named entity: "stock of Pepsi", "stock for Item A", "item A stock", "product ABC" — never a whole-inventory question.
    private static readonly Regex NamedAfterPreposition = new(
        @"\b(of|for)\s+(?!(all|my|the|our|every|each|this|last|today|products|items|goods|stock|stocks|inventory|now|me|us|sale|sales|each)\b)[\p{L}\p{N}]", Opt);
    private static readonly Regex NamedItem = new(
        @"\b(item|product|sku|code|barcode)\s+(?!(stock|stocks|list|wise|value|values|details?|master|category|categories|level|levels|balance|quantity|is|are|in|with|that|which|has|have|running|out|below)\b)[\p{L}\p{N}]", Opt);

    private static readonly Regex TopN = new(@"\btop\s+(\d{1,2})\b", Opt);
    private static readonly Regex AsOfWord = new(@"\b(as\s+of|as\s+on|on|up\s*to|upto|till|until)\b", Opt);

    /// <summary>Relative periods (the date anchors of the business calendar) — removed from the text before matching.</summary>
    private static readonly Regex Relative = new(
        @"\b(today|yesterday|this\s+week|last\s+week|this\s+month|last\s+month|this\s+year|last\s+year|(?:in\s+)?the\s+(?:last|past)\s+(\d{1,3})\s+(days?|weeks?|months?)|(?:last|past)\s+(\d{1,3})\s+(days?|weeks?|months?))\b", Opt);

    public static StockIntentMatch? Route(string? question, DateAnchors anchors, QuestionDates dates)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 200) return null;
        var text = question.Trim();

        // Malayalam / mixed quick questions.
        foreach (var (intent, view, pattern) in LocalRules)
            if (pattern.IsMatch(text) && !Regex.IsMatch(text, @"\bof\b|ന്റെ\s", Opt))
                return Build(intent, view, null, null, null, DefaultLimit, "Malayalam wording");

        // A product, customer, warehouse … named in the question: not a whole-inventory question.
        var withoutDates = dates.Spans.Aggregate(text, (t, sp) => t.Replace(sp.Text, " ", StringComparison.OrdinalIgnoreCase));
        withoutDates = Relative.Replace(withoutDates, " ");
        if (NamedAfterPreposition.IsMatch(withoutDates) || NamedItem.IsMatch(withoutDates)) return null;

        // Period / as-of date / top N — taken out of the text before matching the intent.
        DateOnly? from = null, to = null, asOf = null;
        var hadAsOfWord = false;
        foreach (var span in dates.Spans.Take(1))
        {
            var idx = text.IndexOf(span.Text, StringComparison.OrdinalIgnoreCase);
            var before = idx > 0 ? text[..idx] : string.Empty;
            hadAsOfWord = AsOfWord.IsMatch(before.Length > 12 ? before[^12..] : before);
            if (span.From == span.To && hadAsOfWord) asOf = span.To;
            else { from = span.From; to = span.To; }
            if (idx >= 0) text = text.Remove(idx, span.Text.Length).Insert(idx, " ");
        }
        var relative = Relative.Match(text);
        if (relative.Success && from is null && asOf is null)
        {
            (from, to) = RelativeRange(relative, anchors);
            text = text.Remove(relative.Index, relative.Length).Insert(relative.Index, " ");
        }
        var limit = DefaultLimit;
        var top = TopN.Match(text);
        if (top.Success && int.TryParse(top.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 50)
        {
            limit = n;
            text = text.Remove(top.Index, top.Length).Insert(top.Index, " ");
        }

        var core = Core(text);
        if (core.Length == 0) return null;
        foreach (var (intent, view, pattern) in Rules)
        {
            if (!pattern.IsMatch(core)) continue;
            // A period changes what "stock" means: stock on a day = as of; stock during a period = its movements.
            var finalIntent = intent;
            var finalView = view;
            if (view == "current" && asOf is not null) { finalIntent = "STOCK_AS_OF"; finalView = "asOf"; }
            else if (view == "current" && from is not null && to is not null)
            {
                if (from == to && to == anchors.LocalToday) { /* "stock today" = current stock */ }
                else if (from == to) { finalIntent = "STOCK_AS_OF"; finalView = "asOf"; asOf = to; from = to = null; }
                else { finalIntent = "STOCK_MOVEMENT"; finalView = "movement"; }
            }
            var valueOnPastDay = finalView == "value" && asOf is not null;
            if (valueOnPastDay) { finalIntent = "STOCK_AS_OF"; finalView = "asOf"; }
            var match = Build(finalIntent, finalView, from, to, asOf, limit, $"“{core}”");
            if (valueOnPastDay) match.Plan.Arguments!["measure"] = "value";          // explained: historical value is not recalculated
            return match;
        }
        return null;
    }

    private const int DefaultLimit = 20;

    private static StockIntentMatch Build(string intent, string view, DateOnly? from, DateOnly? to, DateOnly? asOf, int limit, string reason)
    {
        var args = new JsonObject { ["report"] = MySaleBooksReports.StockSummaryReport, ["view"] = view, ["limit"] = limit };
        var usesPeriod = view is "movement" or "fastMoving" or "slowMoving" or "nonMoving";
        if (usesPeriod && from is { } f) args["from"] = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (usesPeriod && to is { } t) args["to"] = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (asOf is { } a) args["asOf"] = a.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new StockIntentMatch(intent, view, new MqlQuery { Type = "report", Arguments = args }, reason);
    }

    /// <summary>The words that remain after removing filler words and punctuation ("Which products are low in stock?" → "low stock").</summary>
    internal static string Core(string text)
    {
        var cleaned = Regex.Replace(text.ToLowerInvariant(), @"[‐\-_/]", " ");
        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{M}\p{N}\s']", " ");
        var words = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\''))
            .Where(w => w.Length > 0 && !Filler.Contains(w))
            .Select(w => w switch { "stocks" => "stock", "inventories" => "inventory", _ => w })
            .ToList();
        // "Show my stock — stock value" (a request merged with its answer) → "stock value": repeated words once.
        var distinct = new List<string>();
        foreach (var w in words)
            if (!distinct.Contains(w)) distinct.Add(w);
        return string.Join(' ', distinct).Trim();
    }

    private static (DateOnly From, DateOnly To) RelativeRange(Match m, DateAnchors anchors)
    {
        var today = anchors.LocalToday;
        var phrase = Regex.Replace(m.Value.ToLowerInvariant(), @"\s+", " ");
        DateOnly Local(DateTime utc) => anchors.WallClockStorage
            ? DateOnly.FromDateTime(utc)
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), DateAnchors.ResolveTimeZone(anchors.BoundaryTimeZoneId)));
        (DateOnly, DateOnly) FromRange(DateRange r) => (Local(r.StartUtc), Local(r.EndUtc).AddDays(-1));
        if (phrase == "today") return (today, today);
        if (phrase == "yesterday") return (today.AddDays(-1), today.AddDays(-1));
        if (phrase == "this week") return (FromRange(anchors.ThisWeek).Item1, today);
        if (phrase == "last week") return FromRange(anchors.LastWeek);
        if (phrase == "this month") return (new DateOnly(today.Year, today.Month, 1), today);
        if (phrase == "last month")
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
            return (first, first.AddMonths(1).AddDays(-1));
        }
        if (phrase == "this year") return (new DateOnly(today.Year, 1, 1), today);
        if (phrase == "last year") return (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31));
        var count = Regex.Match(phrase, @"(\d{1,3})\s+(day|week|month)");
        if (count.Success)
        {
            var nValue = int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture);
            var start = count.Groups[2].Value switch
            {
                "week" => today.AddDays(-7 * nValue + 1),
                "month" => today.AddMonths(-nValue).AddDays(1),
                _ => today.AddDays(-nValue + 1)
            };
            return (start, today);
        }
        return (today.AddDays(-29), today);
    }
}
