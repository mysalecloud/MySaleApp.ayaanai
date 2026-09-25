using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Common;

namespace MySale.AI.Application.Mql;

/// <summary>Converts find / count / distinct into an aggregation pipeline so there is a single execution path.</summary>
public static class MqlNormalizer
{
    public static JsonArray ToPipeline(MqlQuery q, int maxRecords)
    {
        var pipeline = new JsonArray();
        switch (q.Operation)
        {
            case "aggregate":
                foreach (var stage in q.Pipeline ?? new JsonArray())
                    pipeline.Add(stage?.DeepClone());
                break;

            case "find":
                if (q.Filter is { Count: > 0 }) pipeline.Add(new JsonObject { ["$match"] = q.Filter.DeepClone() });
                if (q.Sort is { Count: > 0 }) pipeline.Add(new JsonObject { ["$sort"] = q.Sort.DeepClone() });
                var limit = q.Limit is > 0 ? Math.Min(q.Limit.Value, maxRecords) : maxRecords;
                pipeline.Add(new JsonObject { ["$limit"] = limit });
                if (q.Projection is { Count: > 0 }) pipeline.Add(new JsonObject { ["$project"] = q.Projection.DeepClone() });
                break;

            case "count":
                if (q.Filter is { Count: > 0 }) pipeline.Add(new JsonObject { ["$match"] = q.Filter.DeepClone() });
                pipeline.Add(new JsonObject { ["$count"] = "count" });
                break;

            case "distinct":
                var field = q.Field ?? "_id";
                var outName = field.Contains('.') ? field[(field.LastIndexOf('.') + 1)..] : field;
                if (q.Filter is { Count: > 0 }) pipeline.Add(new JsonObject { ["$match"] = q.Filter.DeepClone() });
                pipeline.Add(new JsonObject { ["$group"] = new JsonObject { ["_id"] = "$" + field } });
                pipeline.Add(new JsonObject { ["$sort"] = new JsonObject { ["_id"] = 1 } });
                pipeline.Add(new JsonObject { ["$limit"] = maxRecords });
                pipeline.Add(new JsonObject { ["$project"] = new JsonObject { ["_id"] = 0, [outName] = "$_id" } });
                break;
        }
        return pipeline;
    }
}

/// <summary>Tenant field of a collection and how its value is stored.</summary>
public sealed record TenantBinding(string Field, bool IsObjectId);

/// <summary>
/// Enforces tenant isolation on a validated pipeline. The company always comes from the authenticated
/// user — never from the AI. Adds the filter as the first stage and inside every $lookup.
/// </summary>
public sealed class TenantQueryGuard
{
    private readonly TenantOptions _options;

    public TenantQueryGuard(TenantOptions options) => _options = options;

    public JsonArray Apply(JsonArray pipeline, string companyId, int maxRecords)
        => Apply(pipeline, companyId, maxRecords, rootCollection: null, resolve: null);

    /// <summary>
    /// Scopes the pipeline. <paramref name="resolve"/> maps a collection to its tenant binding
    /// (field + value type); a null binding means the collection is shared (no company filter).
    /// </summary>
    public JsonArray Apply(JsonArray pipeline, string companyId, int maxRecords, string? rootCollection, Func<string, TenantBinding?>? resolve)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new InvalidOperationException("No tenant is associated with the current user.");

        resolve ??= _ => Default;
        var scoped = new JsonArray();
        var root = rootCollection is null ? Default : resolve(rootCollection);
        if (root is not null) scoped.Add(new JsonObject { ["$match"] = TenantFilter(companyId, root) });
        foreach (var stage in pipeline)
            scoped.Add(ScopeStage(stage?.DeepClone(), companyId, resolve));

        // Always bound the result; +1 lets the executor detect truncation.
        scoped.Add(new JsonObject { ["$limit"] = maxRecords + 1 });
        return scoped;
    }

    public TenantBinding Default => new(_options.FieldName, _options.ValueIsObjectId);

    public JsonObject TenantFilter(string companyId) => TenantFilter(companyId, Default);

    public static JsonObject TenantFilter(string companyId, TenantBinding binding) => new()
    {
        [binding.Field] = binding.IsObjectId
            ? (JsonNode)new JsonObject { ["$oid"] = companyId }
            : JsonValue.Create(companyId)
    };

    private JsonNode? ScopeStage(JsonNode? stage, string companyId, Func<string, TenantBinding?> resolve)
    {
        if (stage is not JsonObject obj || obj.Count != 1) return stage;
        var (name, body) = obj.First();

        if (name == "$lookup" && body is JsonObject lookup)
        {
            // MongoDB 5.0+: localField/foreignField can be combined with a pipeline.
            var from = lookup["from"]?.ToString() ?? string.Empty;
            var binding = resolve(from);
            var sub = lookup["pipeline"] as JsonArray ?? new JsonArray();
            var scopedSub = new JsonArray();
            if (binding is not null) scopedSub.Add(new JsonObject { ["$match"] = TenantFilter(companyId, binding) });
            foreach (var s in sub) scopedSub.Add(ScopeStage(s?.DeepClone(), companyId, resolve));
            if (scopedSub.Count > 0) lookup["pipeline"] = scopedSub;
        }
        else if (name == "$facet" && body is JsonObject facet)
        {
            foreach (var key in facet.Select(k => k.Key).ToList())
            {
                if (facet[key] is not JsonArray sub) continue;
                var scopedSub = new JsonArray();
                foreach (var s in sub) scopedSub.Add(ScopeStage(s?.DeepClone(), companyId, resolve));
                facet[key] = scopedSub;
            }
        }
        return obj;
    }
}

/// <summary>
/// Converts ISO-8601 date strings produced by the model into Extended JSON dates, and normalises
/// {"$date": "..."} values, so comparisons against BSON dates work with small local models too.
/// </summary>
public static class MqlDateCoercer
{
    private static readonly Regex IsoDate = new(
        @"^\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:?\d{2})?)?$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> SkipKeys = new(StringComparer.Ordinal)
    {
        "format", "dateString", "timezone", "unit", "startOfWeek", "$regex", "$options", "as"
    };

    /// <param name="stringFields">
    /// Fields whose stored values are TEXT (e.g. "2026-09-24" kept as a string). Comparisons on these keep
    /// the model's string values — converting them to real dates would never match.
    /// </param>
    public static void Coerce(JsonNode? node, ISet<string>? stringFields = null)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(k => k.Key).ToList())
                {
                    var child = obj[key];
                    if (key == "$date")
                    {
                        if (JsonHelpers.TryGetString(child, out var ds) && TryNormalize(ds, out var norm))
                            obj[key] = norm;
                        continue;
                    }
                    if (SkipKeys.Contains(key)) continue;
                    if (stringFields is not null && stringFields.Contains(key)) continue; // text dates stay text
                    if (JsonHelpers.TryGetString(child, out var s) && !s.StartsWith('$') && TryNormalize(s, out var iso))
                        obj[key] = new JsonObject { ["$date"] = iso };
                    else
                        Coerce(child, stringFields);
                }
                break;

            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    if (JsonHelpers.TryGetString(arr[i], out var s) && !s.StartsWith('$') && TryNormalize(s, out var iso))
                        arr[i] = new JsonObject { ["$date"] = iso };
                    else
                        Coerce(arr[i], stringFields);
                }
                break;
        }
    }

    internal static bool TryNormalize(string value, out string iso)
    {
        iso = string.Empty;
        if (!IsoDate.IsMatch(value)) return false;
        var styles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out var dt)) return false;
        iso = dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return true;
    }
}

/// <summary>Renders a pipeline in mongosh syntax for display (ObjectId(...), ISODate(...)).</summary>
public static class MqlFormatter
{
    private static readonly Regex OidRegex = new(@"\{\s*""\$oid""\s*:\s*""([0-9a-fA-F]{24})""\s*\}", RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"\{\s*""\$date""\s*:\s*""([^""]+)""\s*\}", RegexOptions.Compiled);

    public static string ToShell(string collection, JsonArray pipeline)
    {
        var json = pipeline.ToIndented();
        json = OidRegex.Replace(json, m => $"ObjectId(\"{m.Groups[1].Value}\")");
        json = DateRegex.Replace(json, m => $"ISODate(\"{m.Groups[1].Value}\")");
        return $"db.{collection}.aggregate({json})";
    }
}
