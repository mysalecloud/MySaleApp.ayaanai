using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Common;
using MySale.AI.Application.Contracts;

namespace MySale.AI.Application.Agent;

/// <summary>Flattens group keys and computes the column list so the UI and the answer model see tidy rows.</summary>
public static class ResultShaper
{
    public static (List<JsonObject> Rows, List<string> Columns) Shape(IEnumerable<JsonObject> rows)
    {
        var shaped = new List<JsonObject>();
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var output = new JsonObject();
            if (row.TryGetPropertyValue("_id", out var id))
            {
                bool hasOther = row.Count > 1;
                if (id is JsonObject idObj)
                {
                    foreach (var (k, v) in idObj)
                    {
                        var name = row.ContainsKey(k) ? "_id_" + k : k;
                        output[name] = v?.DeepClone();
                    }
                }
                else if (id is null || (id is JsonValue && id.GetValueKind() == JsonValueKind.Null))
                {
                    if (!hasOther) output["_id"] = null;
                }
                else
                {
                    output["_id"] = id.DeepClone();
                }
            }
            foreach (var (k, v) in row)
            {
                if (k == "_id") continue;
                output[k] = v?.DeepClone();
            }
            foreach (var (k, _) in output)
                if (seen.Add(k)) columns.Add(k);
            shaped.Add(output);
        }
        return (shaped, columns);
    }
}

/// <summary>Chooses how the UI should render a result (KPI cards, chart or table).</summary>
public static class VisualizationAdvisor
{
    private static readonly string[] TimeHints = { "date", "day", "month", "year", "week", "period", "quarter", "hour" };

    public static VisualizationDto Decide(IReadOnlyList<JsonObject> rows, IReadOnlyList<string> columns, string? hint)
    {
        if (rows.Count == 0 || columns.Count == 0) return new VisualizationDto { Type = "none" };

        var numeric = columns.Where(c => rows.All(r => !r.TryGetPropertyValue(c, out var v) || v is null || JsonHelpers.TryGetNumber(v, out _))
                                         && rows.Any(r => r.TryGetPropertyValue(c, out var v) && JsonHelpers.TryGetNumber(v, out _)))
                             .ToList();
        var labels = columns.Except(numeric).ToList();

        if (rows.Count == 1 && numeric.Count >= 1 && numeric.Count <= 6 && labels.Count <= 1)
            return new VisualizationDto { Type = "kpi", YFields = numeric };

        if (rows.Count >= 2 && rows.Count <= 60 && numeric.Count >= 1)
        {
            string? x = labels.Count == 1 ? labels[0] : null;

            // Numeric-looking time keys (year / month numbers from $group) can act as the x axis.
            if (x is null && labels.Count == 0 && numeric.Count >= 2)
            {
                var timeKey = numeric.FirstOrDefault(IsTimeName);
                if (timeKey is not null) x = timeKey;
            }
            // Several label columns (e.g. code + name): use the most descriptive one.
            if (x is null && labels.Count > 1)
                x = labels.FirstOrDefault(l => l.Contains("name", StringComparison.OrdinalIgnoreCase)) ?? labels[0];

            if (x is not null)
            {
                var y = numeric.Where(n => n != x).Take(3).ToList();
                if (y.Count == 0) return Table();
                var isTime = IsTimeName(x) || rows.All(r => r.TryGetPropertyValue(x, out var v) && JsonHelpers.TryGetString(v, out var s) && LooksLikeDate(s));
                var type = (hint ?? string.Empty).ToLowerInvariant() switch
                {
                    "line" => "line",
                    "bar" => "bar",
                    "pie" when rows.Count <= 8 && y.Count == 1 => "pie",
                    "table" when labels.Count > 2 => "table",
                    _ => isTime ? "line" : "bar"
                };
                if (labels.Count > 3) type = "table";
                return new VisualizationDto { Type = type, XField = x, YFields = y };
            }
        }
        return Table();

        static VisualizationDto Table() => new() { Type = "table" };
    }

    private static bool IsTimeName(string name) => TimeHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeDate(string s)
        => s.Length >= 7 && char.IsDigit(s[0]) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

/// <summary>
/// Checks that numbers quoted in the AI answer exist in the query result (with rounding tolerance).
/// Ungrounded numbers are reported as warnings — this makes hallucinations measurable per model.
/// </summary>
public static class GroundingChecker
{
    private static readonly Regex NumberRegex = new(@"(?<![\w.])-?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?(?![\w])", RegexOptions.Compiled);
    private static readonly Regex DigitsRegex = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);

    public static GroundingDto Check(string answer, IReadOnlyList<JsonObject> rows, string question)
    {
        var result = new GroundingDto { Checked = true };
        if (string.IsNullOrWhiteSpace(answer)) return result;

        var known = new List<double> { rows.Count };
        foreach (var row in rows) Collect(row, known);
        foreach (Match m in DigitsRegex.Matches(question))
            if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var q)) known.Add(q);

        foreach (Match m in NumberRegex.Matches(answer))
        {
            var text = m.Value;
            if (!double.TryParse(text.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            var abs = Math.Abs(value);
            if (abs <= 31 && value == Math.Floor(value)) continue;                 // small counts, days, "top 10"
            if (value is >= 1990 and <= 2100 && value == Math.Floor(value)) continue; // years
            if (IsGrounded(abs, known)) continue;
            var warning = $"'{text}' does not appear in the query result.";
            if (!result.Warnings.Contains(warning)) result.Warnings.Add(warning);
        }
        return result;
    }

    private static bool IsGrounded(double value, List<double> known)
    {
        foreach (var k in known)
        {
            var a = Math.Abs(k);
            var tolerance = Math.Max(0.011, a * 0.005);
            if (Math.Abs(a - value) <= tolerance) return true;
            // "AED 184K" / "1.2 million" style rounding
            if (a >= 1000 && Math.Abs(a / 1000 - value) <= Math.Max(0.06, a / 1000 * 0.01)) return true;
            if (a >= 1_000_000 && Math.Abs(a / 1_000_000 - value) <= Math.Max(0.06, a / 1_000_000 * 0.01)) return true;
        }
        return false;
    }

    private static void Collect(JsonNode? node, List<double> known)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, v) in o) Collect(v, known);
                break;
            case JsonArray a:
                known.Add(a.Count);
                foreach (var v in a) Collect(v, known);
                break;
            case JsonValue:
                if (JsonHelpers.TryGetNumber(node, out var d)) known.Add(d);
                else if (JsonHelpers.TryGetString(node, out var s))
                    foreach (Match m in DigitsRegex.Matches(s))
                        if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) known.Add(n);
                break;
        }
    }
}
