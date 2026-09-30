using System.Text.Json.Nodes;

namespace MySale.AI.Application.Agent;

/// <summary>Where an output column comes from: the stored field <see cref="Field"/> of <see cref="Collection"/>.</summary>
public sealed record FieldSource(string Collection, string Field);

/// <summary>
/// Follows an aggregation pipeline stage by stage to find the stored field behind every output column — so a grouped
/// "_id", "supplierId": "$_id", "_id": {"customer": "$ledgerId"}, "salesman": {"$first": "$employeeId"} or
/// "item.categoryId" after a $lookup of Item are all traced back to the collection and field that hold the id.
/// Unknown stages or computed values give no source (the resolver then falls back to the column name).
/// </summary>
public static class FieldLineage
{
    public static IReadOnlyDictionary<string, FieldSource> Trace(string root, JsonArray? pipeline)
    {
        var state = new State(root);
        if (pipeline is null) return state.Result();
        foreach (var node in pipeline)
        {
            if (node is not JsonObject stage || stage.Count != 1) { state.Reset(); continue; }
            var (op, body) = stage.First();
            switch (op)
            {
                case "$match": case "$sort": case "$limit": case "$skip": case "$sample": case "$count":
                    break;
                case "$unwind":
                    break; // the array field keeps its source; lookup aliases keep theirs
                case "$lookup":
                    if (body is JsonObject l && Str(l["from"]) is { } from && Str(l["as"]) is { } alias)
                        state.Aliases[alias] = from;
                    break;
                case "$addFields": case "$set":
                    if (body is JsonObject add)
                        foreach (var (k, v) in add) state.Set(k, state.SourceOf(v));
                    break;
                case "$project":
                    if (body is JsonObject project) state.Project(project);
                    break;
                case "$group":
                    if (body is JsonObject group) state.Group(group);
                    else state.Reset();
                    break;
                case "$sortByCount":
                    var key = state.SourceOf(body);
                    state.Reset();
                    if (key is not null) state.Set("_id", key);
                    break;
                default:
                    state.Reset(); // $facet, $replaceRoot, $bucket … — no reliable lineage
                    break;
            }
        }
        return state.Result();
    }

    private sealed class State
    {
        private readonly string _root;
        private Dictionary<string, FieldSource?> _map = new(StringComparer.Ordinal);
        private bool _identity = true;   // unmapped paths still mean root fields (before a $group / inclusion $project)
        public Dictionary<string, string> Aliases { get; private set; } = new(StringComparer.Ordinal);

        public State(string root) => _root = root;

        public void Reset()
        {
            _map = new(StringComparer.Ordinal);
            _identity = false;
            Aliases = new(StringComparer.Ordinal);
        }

        public void Set(string key, FieldSource? source) => _map[key] = source;

        public FieldSource? Resolve(string path)
        {
            if (_map.TryGetValue(path, out var mapped)) return mapped;
            var dot = path.IndexOf('.');
            if (dot > 0)
            {
                var head = path[..dot];
                var rest = path[(dot + 1)..];
                if (Aliases.TryGetValue(head, out var from)) return new FieldSource(from, rest);
                if (_map.TryGetValue(head, out var parent) && parent is not null && Aliases.Count == 0 && _identity)
                    return new FieldSource(parent.Collection, parent.Field + "." + rest);
            }
            return _identity && !Aliases.ContainsKey(path) ? new FieldSource(_root, path) : null;
        }

        /// <summary>Source of an expression that passes a field through unchanged (type conversions included).</summary>
        public FieldSource? SourceOf(JsonNode? expr)
        {
            switch (expr)
            {
                case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith('$') && !s.StartsWith("$$"):
                    return Resolve(s[1..]);
                case JsonObject o when o.Count == 1:
                {
                    var (op, arg) = o.First();
                    switch (op)
                    {
                        case "$toString": case "$toObjectId": case "$first": case "$last": case "$min": case "$max": case "$toLower": case "$toUpper": case "$trim":
                            return SourceOf(arg is JsonObject t && t["input"] is { } input && op == "$trim" ? input : arg);
                        case "$convert":
                            return arg is JsonObject c ? SourceOf(c["input"]) : null;
                        case "$ifNull":
                            return arg is JsonArray a && a.Count > 0 ? SourceOf(a[0]) : null;
                        case "$arrayElemAt":
                            return arg is JsonArray e && e.Count > 0 ? SourceOf(e[0]) : null;
                    }
                    return null;
                }
                default:
                    return null;
            }
        }

        public void Project(JsonObject project)
        {
            var inclusion = project.Any(kv => kv.Key != "_id" && !IsExclusion(kv.Value));
            if (!inclusion)
            {
                foreach (var (k, v) in project) if (IsExclusion(v)) _map[k] = null;
                return;
            }
            var next = new Dictionary<string, FieldSource?>(StringComparer.Ordinal);
            foreach (var (k, v) in project)
            {
                if (IsExclusion(v)) continue;
                next[k] = v is JsonValue jv && (jv.TryGetValue<int>(out _) || jv.TryGetValue<bool>(out _) || jv.TryGetValue<long>(out _) || jv.TryGetValue<double>(out _))
                    ? Resolve(k)
                    : SourceOf(v);
            }
            if (!project.ContainsKey("_id")) next["_id"] = Resolve("_id");
            _map = next;
            _identity = false;
            // a projected lookup alias keeps its fields (e.g. "item": 1 after $lookup of Item)
            Aliases = Aliases.Where(a => project.ContainsKey(a.Key) && !IsExclusion(project[a.Key])).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        }

        public void Group(JsonObject group)
        {
            var next = new Dictionary<string, FieldSource?>(StringComparer.Ordinal);
            var id = group["_id"];
            if (id is JsonObject compound && !compound.Any(kv => kv.Key.StartsWith('$')))
            {
                foreach (var (k, v) in compound)
                {
                    var src = SourceOf(v);
                    next["_id." + k] = src;
                    next[k] = src;          // ResultShaper flattens compound keys ("_id.customer" → "customer")
                    next["_id_" + k] = src; // … or "_id_customer" when an accumulator already uses that name
                }
            }
            else
            {
                next["_id"] = SourceOf(id);
            }
            foreach (var (k, v) in group)
            {
                if (k == "_id") continue;
                next[k] = v is JsonObject acc && acc.Count == 1 && acc.First().Key is "$first" or "$last" or "$min" or "$max"
                    ? SourceOf(acc.First().Value)
                    : null;
            }
            _map = next;
            _identity = false;
            Aliases = new(StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, FieldSource> Result()
            => _map.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);

        /// <summary>Source of a result column (resolved lazily: identity pipelines have no explicit map).</summary>
        public FieldSource? Column(string column) => Resolve(column);

        private static bool IsExclusion(JsonNode? v)
            => v is JsonValue jv && ((jv.TryGetValue<int>(out var i) && i == 0) || (jv.TryGetValue<long>(out var l) && l == 0) || (jv.TryGetValue<bool>(out var b) && !b));
    }

    /// <summary>Source of one result column.</summary>
    public static FieldSource? Of(string root, JsonArray? pipeline, string column)
    {
        var map = Trace(root, pipeline);
        if (map.TryGetValue(column, out var s)) return s;
        // Pipelines without $group / inclusion $project return root documents: the column is the root field itself.
        if (pipeline is null || !pipeline.OfType<JsonObject>().Any(st => st.ContainsKey("$group") || st.ContainsKey("$project")
                                                                         || st.ContainsKey("$sortByCount") || st.ContainsKey("$replaceRoot") || st.ContainsKey("$facet")))
        {
            var lookups = pipeline?.OfType<JsonObject>().Select(st => st["$lookup"] as JsonObject).Where(l => l is not null)
                .Select(l => (As: Str(l!["as"]), From: Str(l!["from"]))).Where(x => x.As is not null && x.From is not null).ToList() ?? new();
            var dot = column.IndexOf('.');
            if (dot > 0 && lookups.FirstOrDefault(x => x.As == column[..dot]) is { From: { } from }) return new FieldSource(from, column[(dot + 1)..]);
            return new FieldSource(root, column);
        }
        return null;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
