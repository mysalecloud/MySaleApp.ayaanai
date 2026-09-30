using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MySale.AI.Application.Agent;

/// <summary>Result of the final display check: what users will see, and every raw id that had to be removed.</summary>
public sealed class DisplayCheck
{
    public List<JsonObject> Rows { get; init; } = new();
    public List<string> Columns { get; init; } = new();
    /// <summary>"supplierId: raw ids hidden (4 values)" — missed mappings, for the trace / activity log.</summary>
    public List<string> Issues { get; init; } = new();
}

/// <summary>
/// Last safeguard before a result or answer reaches the user: finds values that look like MongoDB ObjectIds or GUIDs
/// where a name is expected (table / chart / KPI / ranking labels and the answer text). Ids that the resolver named are
/// replaced by that name; any other id is removed from the display (the row keeps it for drill-down) and reported as a
/// missed mapping. The entity resolver (<see cref="QueryEngine.ResolveReferencesAsync"/>) is the real fix — this layer
/// only makes a missed mapping visible in the logs instead of on the customer's screen.
/// </summary>
public static class DisplayIdGuard
{
    private static readonly Regex IdToken = new(
        @"(?<![0-9A-Za-z])(?:[0-9a-fA-F]{24}|\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?|[0-9a-fA-F]{32})(?![0-9A-Za-z])",
        RegexOptions.Compiled);

    /// <summary>Shown instead of an id that has no verified name.</summary>
    public const string NotAvailable = "Not available";

    public static bool IsIdLike(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.All(char.IsDigit)) return false; // long numbers (barcodes, invoice numbers) are not ids
        return QueryEngine.IsIdText(v);
    }

    /// <summary>
    /// Columns and rows as users will see them: an all-id column is taken out of the display list (when other columns
    /// remain), an id inside a label column becomes its resolved name or "Not available". Numbers, dates and other text
    /// are untouched. The id stays in the row under its own column name only when that column is not displayed.
    /// </summary>
    public static DisplayCheck Check(IReadOnlyList<JsonObject> rows, IReadOnlyList<string> columns, IReadOnlyDictionary<string, string>? labels = null)
    {
        var issues = new List<string>();
        var shown = columns.ToList();
        var output = rows.Select(r => (JsonObject)r.DeepClone()).ToList();

        foreach (var column in columns)
        {
            var texts = output.Select(r => r.TryGetPropertyValue(column, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null).ToList();
            var ids = texts.Where(IsIdLike).ToList();
            if (ids.Count == 0) continue;
            var nonEmpty = output.Count(r => r.TryGetPropertyValue(column, out var v) && v is { } node && node.GetValueKind() != JsonValueKind.Null
                                             && !(node is JsonValue sv && sv.TryGetValue<string>(out var t) && string.IsNullOrWhiteSpace(t)));
            var allIds = ids.Count == nonEmpty;
            var named = ids.Count(i => labels is not null && labels.ContainsKey(i.Trim()));

            if (allIds && named == 0 && shown.Count > 1 && shown.Any(c => c != column && !AllIds(output, c)))
            {
                shown.Remove(column);
                issues.Add($"{column}: raw id column hidden ({ids.Distinct(StringComparer.OrdinalIgnoreCase).Count()} distinct id(s), no verified name)");
                continue;
            }
            // Keep the column, but never show an id as a label: resolved name, else "Not available".
            foreach (var row in output)
            {
                if (!row.TryGetPropertyValue(column, out var v) || v is not JsonValue jv || !jv.TryGetValue<string>(out var s) || !IsIdLike(s)) continue;
                row[column] = labels is not null && labels.TryGetValue(s.Trim(), out var label) ? label : NotAvailable;
            }
            issues.Add($"{column}: {ids.Count} raw id value(s) replaced ({named} by a resolved name, {ids.Count - named} as \"{NotAvailable}\")");
        }
        return new DisplayCheck { Rows = output, Columns = shown, Issues = issues };
    }

    /// <summary>Rows reduced to the displayed columns — what the answer model receives (never the internal ids).</summary>
    public static List<JsonObject> ForAnswer(IReadOnlyList<JsonObject> rows, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0) return rows.Select(r => (JsonObject)r.DeepClone()).ToList();
        return rows.Select(r =>
        {
            var o = new JsonObject();
            foreach (var c in columns)
                if (r.TryGetPropertyValue(c, out var v)) o[c] = v?.DeepClone();
            return o;
        }).ToList();
    }

    /// <summary>
    /// Answer text without raw ids: an id the resolver named becomes the name; any other id becomes
    /// "(not available)". Returns the number of replacements (0 = the answer was clean).
    /// </summary>
    public static (string Text, int Replaced) CleanText(string text, IReadOnlyDictionary<string, string>? labels = null)
    {
        if (string.IsNullOrEmpty(text)) return (text, 0);
        var replaced = 0;
        var cleaned = IdToken.Replace(text, m =>
        {
            var token = m.Value.Trim('{', '}');
            if (!IsIdLike(token)) return m.Value;
            replaced++;
            return labels is not null && labels.TryGetValue(token, out var label) ? label : "(not available)";
        });
        return (cleaned, replaced);
    }

    private static bool AllIds(List<JsonObject> rows, string column)
    {
        var values = rows.Select(r => r.TryGetPropertyValue(column, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return values.Count > 0 && values.All(IsIdLike);
    }
}
