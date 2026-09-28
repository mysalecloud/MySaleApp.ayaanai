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
/// <summary>How date literals in a query are read: business time zone and the days the user typed.</summary>
public sealed class DateCoercionContext
{
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    /// <summary>Last days of the ranges the user asked for (inclusive): "$lte that day" means the whole day.</summary>
    public IReadOnlySet<DateOnly> InclusiveEndDays { get; init; } = new HashSet<DateOnly>();
    /// <summary>All days the user typed; a UTC-midnight literal on such a day is read as local midnight.</summary>
    public IReadOnlySet<DateOnly> MentionedDays { get; init; } = new HashSet<DateOnly>();

    public static readonly DateCoercionContext Utc = new();
}

/// <summary>
/// Normalises date literals written by the model so that date filters match what the user meant:
/// <list type="bullet">
/// <item>"2026-09-01" and {"$date":"2026-09-01"} (date only) → start of that day in the business time zone.</item>
/// <item>$lte on a date-only value, or on a day the user named as the end of a range → $lt the start of the next day,
/// so "up to 10 September" includes all of 10 September (never "≤ 10 Sep 00:00").</item>
/// <item>$lte on any other day boundary → $lt (it is an exclusive boundary such as the end of "this month").</item>
/// <item>$gt on a date-only day → $gte the next day ("after 10 Sep" = from 11 Sep).</item>
/// <item>equality with a date-only value → the whole local day [start, next day).</item>
/// <item>fields stored as text keep text values; "≤ 2026-09-10" becomes "&lt; 2026-09-11" (ISO text sorts correctly).</item>
/// </list>
/// </summary>
public static class MqlDateCoercer
{
    private static readonly Regex IsoDate = new(
        @"^\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:?\d{2})?)?$",
        RegexOptions.Compiled);
    private static readonly Regex DateOnlyText = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private static readonly HashSet<string> SkipKeys = new(StringComparer.Ordinal)
    {
        "format", "dateString", "timezone", "unit", "startOfWeek", "$regex", "$options", "as"
    };

    private static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal) { "$gte", "$gt", "$lte", "$lt", "$eq" };

    /// <param name="stringFields">
    /// Fields whose stored values are TEXT (e.g. "2026-09-24" kept as a string). Comparisons on these keep
    /// the model's string values — converting them to real dates would never match.
    /// </param>
    public static void Coerce(JsonNode? node, ISet<string>? stringFields = null, DateCoercionContext? dates = null)
        => Coerce(node, stringFields, dates ?? DateCoercionContext.Utc, inFilter: true);

    // Stages whose field keys are output expressions, not filters: a date-only value there must stay a value.
    private static readonly HashSet<string> ExpressionStages = new(StringComparer.Ordinal)
    {
        "$project", "$addFields", "$set", "$group", "$replaceRoot", "$replaceWith", "$bucket", "$bucketAuto", "$sort", "$unset", "$densify", "$fill", "$setWindowFields"
    };

    private static void Coerce(JsonNode? node, ISet<string>? stringFields, DateCoercionContext dates, bool inFilter)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(k => k.Key).ToList())
                {
                    var child = obj[key];
                    if (key == "$date")
                    {
                        if (JsonHelpers.TryGetString(child, out var ds) && TryNormalize(ds, dates.TimeZone, out var norm))
                            obj[key] = norm;
                        continue;
                    }
                    if (SkipKeys.Contains(key)) continue;
                    var isField = !key.StartsWith('$') && inFilter;
                    var isText = stringFields is not null && stringFields.Contains(key);
                    var childInFilter = key == "$match" || (!ExpressionStages.Contains(key) && inFilter);

                    // Field compared with operators: {"InvoiceDate":{"$gte":…,"$lte":…}}
                    if (isField && child is JsonObject cmp && cmp.Count > 0 && cmp.All(kv => Comparisons.Contains(kv.Key)) && cmp.Any(kv => IsDateLiteral(kv.Value)))
                    {
                        obj[key] = isText ? TextComparison(cmp) : DateComparison(cmp, dates);
                        continue;
                    }
                    // Field equal to a date-only value: {"InvoiceDate":"2026-09-10"} → that whole local day.
                    if (isField && !isText && TryDateOnly(child, out var day))
                    {
                        obj[key] = new JsonObject { ["$gte"] = DateNode(LocalStart(day, dates.TimeZone)), ["$lt"] = DateNode(LocalStart(day.AddDays(1), dates.TimeZone)) };
                        continue;
                    }
                    if (isText) continue; // text dates stay text
                    if (JsonHelpers.TryGetString(child, out var s) && !s.StartsWith('$') && TryNormalize(s, dates.TimeZone, out var iso))
                        obj[key] = new JsonObject { ["$date"] = iso };
                    else
                        Coerce(child, stringFields, dates, childInFilter);
                }
                break;

            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    if (JsonHelpers.TryGetString(arr[i], out var s) && !s.StartsWith('$') && TryNormalize(s, dates.TimeZone, out var iso))
                        arr[i] = new JsonObject { ["$date"] = iso };
                    else
                        Coerce(arr[i], stringFields, dates, inFilter);
                }
                break;
        }
    }

    private static JsonObject DateComparison(JsonObject cmp, DateCoercionContext dates)
    {
        var tz = dates.TimeZone;
        var result = new JsonObject();
        void Put(string op, DateTime utc)
        {
            // Keep the tightest bound if two operators collapse into the same one ($lt and a converted $lte).
            if (result[op] is JsonObject existing && existing["$date"]?.GetValue<string>() is { } prev
                && DateTime.TryParse(prev, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p))
                utc = op is "$lt" or "$lte" ? (utc < p ? utc : p) : (utc > p ? utc : p);
            result[op] = DateNode(utc);
        }

        foreach (var (op, value) in cmp.ToList())
        {
            if (!TryReadDate(value, tz, out var utc, out var dateOnly))
            {
                result[op] = value?.DeepClone();
                continue;
            }
            var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, tz));
            // The model wrote a day the user typed as UTC midnight ("2026-09-10T00:00:00Z") in a non-UTC zone — or, for an
            // exclusive end, UTC midnight of the day after a typed end day ("to 10 Sep" → $lt "2026-09-11T00:00:00Z").
            var utcDay = DateOnly.FromDateTime(utc);
            if (!dateOnly && utc.TimeOfDay == TimeSpan.Zero && tz.GetUtcOffset(utc) != TimeSpan.Zero
                && (dates.MentionedDays.Contains(utcDay) || (op == "$lt" && dates.InclusiveEndDays.Contains(utcDay.AddDays(-1)))))
            {
                localDay = DateOnly.FromDateTime(utc);
                utc = LocalStart(localDay, tz);
            }
            var isDayStart = utc == LocalStart(localDay, tz);

            switch (op)
            {
                case "$lte" when dateOnly || (isDayStart && dates.InclusiveEndDays.Contains(localDay)):
                    Put("$lt", LocalStart(localDay.AddDays(1), tz));   // whole end day included
                    break;
                case "$lte" when isDayStart:
                    Put("$lt", utc);                                    // an exclusive boundary written with $lte
                    break;
                case "$gt" when dateOnly:
                    Put("$gte", LocalStart(localDay.AddDays(1), tz));  // "after 10 Sep" = from 11 Sep
                    break;
                case "$eq" when dateOnly:
                    Put("$gte", LocalStart(localDay, tz));
                    Put("$lt", LocalStart(localDay.AddDays(1), tz));
                    break;
                default:
                    Put(op, utc);
                    break;
            }
        }
        return result;
    }

    private static JsonObject TextComparison(JsonObject cmp)
    {
        var result = new JsonObject();
        foreach (var (op, value) in cmp.ToList())
        {
            if (JsonHelpers.TryGetString(value, out var s) && DateOnlyText.IsMatch(s)
                && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                var next = d.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                switch (op)
                {
                    case "$lte": result["$lt"] = next; continue;   // "2026-09-10 14:30" ≤ "2026-09-10" would be false
                    case "$gt": result["$gte"] = next; continue;
                }
            }
            result[op] = value?.DeepClone();
        }
        return result;
    }

    private static bool IsDateLiteral(JsonNode? value)
        => value is JsonObject o && o.Count == 1 && o["$date"] is not null
           || JsonHelpers.TryGetString(value, out var s) && IsoDate.IsMatch(s);

    private static bool TryDateOnly(JsonNode? value, out DateOnly day)
    {
        day = default;
        var text = value is JsonObject o && o.Count == 1 && JsonHelpers.TryGetString(o["$date"], out var ds) ? ds
            : JsonHelpers.TryGetString(value, out var s) ? s : null;
        return text is not null && DateOnlyText.IsMatch(text)
               && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    private static bool TryReadDate(JsonNode? value, TimeZoneInfo tz, out DateTime utc, out bool dateOnly)
    {
        utc = default;
        dateOnly = false;
        var text = value is JsonObject o && o.Count == 1 && JsonHelpers.TryGetString(o["$date"], out var ds) ? ds
            : JsonHelpers.TryGetString(value, out var s) ? s : null;
        if (text is null || !IsoDate.IsMatch(text)) return false;
        if (TryDateOnly(JsonValue.Create(text), out var day))
        {
            dateOnly = true;
            utc = LocalStart(day, tz);
            return true;
        }
        if (!TryNormalize(text, tz, out var iso)) return false;
        utc = DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return true;
    }

    private static DateTime LocalStart(DateOnly day, TimeZoneInfo tz)
        => MySale.AI.Application.Agent.DateAnchors.LocalToUtc(day.ToDateTime(TimeOnly.MinValue), tz);

    private static JsonObject DateNode(DateTime utc)
        => new() { ["$date"] = utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) };

    internal static bool TryNormalize(string value, out string iso) => TryNormalize(value, TimeZoneInfo.Utc, out iso);

    /// <summary>ISO text → UTC ISO. Values without an offset (date only, or local time) are in the business time zone.</summary>
    internal static bool TryNormalize(string value, TimeZoneInfo tz, out string iso)
    {
        iso = string.Empty;
        if (!IsoDate.IsMatch(value)) return false;
        var hasOffset = value.EndsWith('Z') || Regex.IsMatch(value, @"[+-]\d{2}:?\d{2}$");
        DateTime utc;
        if (hasOffset)
        {
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc)) return false;
        }
        else
        {
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return false;
            try { utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz); }
            catch (ArgumentException) { return false; } // invalid local time (DST gap)
        }
        iso = utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
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
