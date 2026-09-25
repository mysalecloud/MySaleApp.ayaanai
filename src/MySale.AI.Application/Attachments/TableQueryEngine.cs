using System.Globalization;
using System.Text.Json.Nodes;
using MySale.AI.Application.Common;
using MySale.AI.Domain;

namespace MySale.AI.Application.Attachments;

public sealed class TableQueryResult
{
    public List<JsonObject> Rows { get; } = new();
    public List<string> Errors { get; } = new();
    public bool IsValid => Errors.Count == 0;
    public int MatchedRows { get; set; }
}

/// <summary>
/// Deterministic analysis of CSV / Excel data. The AI only describes WHAT to compute
/// (filter / group / aggregate / sort / limit / distinct); the backend does the arithmetic,
/// so totals over thousands of rows are exact and no rows need to go through the model.
/// <code>
/// {"filter":[{"column":"Region","op":"eq","value":"Dubai"}],
///  "groupBy":["Product"],
///  "aggregates":[{"column":"Amount","fn":"sum","as":"totalAmount"}],
///  "sort":{"by":"totalAmount","dir":"desc"}, "limit":10}
/// </code>
/// </summary>
public static class TableQueryEngine
{
    private static readonly HashSet<string> Ops = new(StringComparer.OrdinalIgnoreCase) { "eq", "ne", "gt", "gte", "lt", "lte", "contains", "in" };
    private static readonly HashSet<string> Fns = new(StringComparer.OrdinalIgnoreCase) { "sum", "avg", "min", "max", "count", "countDistinct" };

    public static TableQueryResult Execute(AttachmentTable table, JsonObject spec, int maxRows = 200)
    {
        var result = new TableQueryResult();
        var colIndex = table.Columns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i, StringComparer.OrdinalIgnoreCase);

        int Col(string? name)
        {
            if (name is not null && colIndex.TryGetValue(name.Trim(), out var i)) return i;
            result.Errors.Add($"Unknown column '{name}'. Columns: {string.Join(", ", table.Columns)}");
            return -1;
        }

        string? Cell(List<string?> row, int i) => i >= 0 && i < row.Count ? row[i] : null;

        // ---- filter
        IEnumerable<List<string?>> rows = table.Rows;
        if (spec["filter"] is JsonArray filters)
        {
            foreach (var f in filters.OfType<JsonObject>())
            {
                JsonHelpers.TryGetString(f["column"], out var column);
                JsonHelpers.TryGetString(f["op"], out var op);
                op = string.IsNullOrEmpty(op) ? "eq" : op;
                var idx = Col(column);
                if (!Ops.Contains(op)) { result.Errors.Add($"Unsupported filter op '{op}'."); continue; }
                if (idx < 0) continue;
                var value = f["value"];
                var values = value is JsonArray arr ? arr.Select(v => v?.ToString() ?? "").ToList() : new List<string> { value?.ToString() ?? "" };
                rows = rows.Where(r => Matches(Cell(r, idx), op, values)).ToList();
            }
        }
        var filtered = rows.ToList();
        result.MatchedRows = filtered.Count;
        if (!result.IsValid) return result;

        // ---- distinct values of one column (used to feed MongoDB lookups)
        if (JsonHelpers.TryGetString(spec["distinct"], out var distinctCol))
        {
            var idx = Col(distinctCol);
            if (idx < 0) return result;
            foreach (var v in filtered.Select(r => Cell(r, idx)).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Take(maxRows))
                result.Rows.Add(new JsonObject { [table.Columns[idx]] = v });
            return result;
        }

        var groupBy = (spec["groupBy"] as JsonArray)?.Select(g => g?.ToString() ?? "").Where(g => g.Length > 0).ToList() ?? new();
        var aggregates = (spec["aggregates"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var groupIdx = groupBy.Select(Col).ToList();
        var aggDefs = aggregates.Select(a =>
        {
            JsonHelpers.TryGetString(a["fn"], out var fn);
            JsonHelpers.TryGetString(a["column"], out var column);
            JsonHelpers.TryGetString(a["as"], out var alias);
            fn = string.IsNullOrEmpty(fn) ? "sum" : fn;
            if (!Fns.Contains(fn)) result.Errors.Add($"Unsupported aggregate '{fn}'.");
            var idx = fn.Equals("count", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(column) ? -2 : Col(column);
            return (Fn: fn.ToLowerInvariant(), Idx: idx, As: string.IsNullOrWhiteSpace(alias) ? $"{fn}{column}" : alias!);
        }).ToList();
        if (!result.IsValid) return result;

        List<JsonObject> output;
        if (groupIdx.Count > 0 || aggDefs.Count > 0)
        {
            output = filtered
                .GroupBy(r => string.Join("\u001f", groupIdx.Select(i => Cell(r, i) ?? "")))
                .Select(g =>
                {
                    var o = new JsonObject();
                    var first = g.First();
                    for (int k = 0; k < groupIdx.Count; k++) o[table.Columns[groupIdx[k]]] = Cell(first, groupIdx[k]);
                    foreach (var a in aggDefs) o[a.As] = Aggregate(a.Fn, a.Idx, g.ToList());
                    return o;
                }).ToList();
        }
        else
        {
            var select = (spec["select"] as JsonArray)?.Select(s => s?.ToString() ?? "").Where(s => s.Length > 0).Select(Col).ToList();
            if (!result.IsValid) return result;
            var cols = select is { Count: > 0 } ? select : Enumerable.Range(0, table.Columns.Count).ToList();
            output = filtered.Select(r =>
            {
                var o = new JsonObject();
                foreach (var i in cols) o[table.Columns[i]] = TypedValue(Cell(r, i));
                return o;
            }).ToList();
        }

        // ---- sort / limit
        if (spec["sort"] is JsonObject sort && JsonHelpers.TryGetString(sort["by"], out var by))
        {
            JsonHelpers.TryGetString(sort["dir"], out var dir);
            var desc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
            var key = output.FirstOrDefault()?.Select(k => k.Key).FirstOrDefault(k => string.Equals(k, by, StringComparison.OrdinalIgnoreCase));
            if (key is null && output.Count > 0) result.Errors.Add($"Cannot sort by '{by}'.");
            else if (key is not null)
            {
                Func<JsonObject, (int, double, string)> sortKey = o =>
                {
                    var v = o[key];
                    if (JsonHelpers.TryGetNumber(v, out var n)) return (0, n, "");
                    var s = v?.ToString() ?? "";
                    return TableTypes.TryNumber(s, out n) ? (0, n, "") : (1, 0, s);
                };
                output = (desc ? output.OrderByDescending(sortKey) : output.OrderBy(sortKey)).ToList();
            }
        }
        var limit = spec["limit"] is JsonNode ln && JsonHelpers.TryGetNumber(ln, out var l) ? (int)Math.Clamp(l, 1, maxRows) : maxRows;
        result.Rows.AddRange(output.Take(limit));
        return result;
    }

    private static JsonNode? Aggregate(string fn, int idx, List<List<string?>> rows)
    {
        if (fn == "count") return rows.Count(r => idx == -2 || (idx < r.Count && !string.IsNullOrWhiteSpace(r[idx])));
        var raw = rows.Select(r => idx < r.Count ? r[idx] : null).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (fn == "countdistinct") return raw.Distinct().Count();
        var nums = raw.Select(v => TableTypes.TryNumber(v, out var n) ? (double?)n : null).Where(n => n.HasValue).Select(n => n!.Value).ToList();
        if (nums.Count == 0) return null;
        return fn switch
        {
            "sum" => Math.Round(nums.Sum(), 4),
            "avg" => Math.Round(nums.Average(), 4),
            "min" => nums.Min(),
            "max" => nums.Max(),
            _ => null
        };
    }

    private static bool Matches(string? cell, string op, List<string> values)
    {
        var c = cell ?? "";
        var v = values.FirstOrDefault() ?? "";
        switch (op.ToLowerInvariant())
        {
            case "contains": return c.Contains(v, StringComparison.OrdinalIgnoreCase);
            case "in": return values.Any(x => string.Equals(x.Trim(), c.Trim(), StringComparison.OrdinalIgnoreCase));
            case "eq": return Compare(c, v) == 0;
            case "ne": return Compare(c, v) != 0;
            case "gt": return Compare(c, v) > 0;
            case "gte": return Compare(c, v) >= 0;
            case "lt": return Compare(c, v) < 0;
            case "lte": return Compare(c, v) <= 0;
            default: return false;
        }
    }

    private static int Compare(string a, string b)
    {
        if (TableTypes.TryNumber(a, out var x) && TableTypes.TryNumber(b, out var y)) return x.CompareTo(y);
        if (DateTime.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.None, out var da) &&
            DateTime.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.None, out var db)) return da.CompareTo(db);
        return string.Compare(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? TypedValue(string? s)
    {
        if (s is null) return null;
        var t = s.Trim();
        // Keep codes with leading zeros ("00123") as text.
        var leadingZeroCode = t.Length > 1 && t[0] == '0' && t[1] != '.';
        return !leadingZeroCode && TableTypes.TryNumber(t, out var n) ? JsonValue.Create(n) : JsonValue.Create(s);
    }
}
