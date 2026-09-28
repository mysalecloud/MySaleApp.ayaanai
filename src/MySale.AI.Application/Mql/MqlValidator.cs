using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Common;

namespace MySale.AI.Application.Mql;

public sealed class MqlValidationContext
{
    /// <summary>Collections the AI may query (already intersected with the allowed list).</summary>
    public required IReadOnlyList<CollectionSchema> Collections { get; init; }
    public string TenantField { get; init; } = "CompanyId";
    public int MaxStages { get; init; } = 12;
    public int MaxRecords { get; init; } = 200;
    public int MaxDepth { get; init; } = 16;
    public int MaxLookups { get; init; } = 3;
    public int MaxNodes { get; init; } = 4000;
    public int MaxRegexLength { get; init; } = 200;
    /// <summary>Store context of the request (selected MySaleBooks store). Null = no store filtering.</summary>
    public Stores.StoreScope? Store { get; init; }
    /// <summary>Business time zone and the days typed in the question (date literals are read with these).</summary>
    public DateCoercionContext? Dates { get; init; }
    /// <summary>The user's question (used to allow createdAt-style fields when the user asks about creation).</summary>
    public string? Question { get; init; }
    /// <summary>Configured business date field per collection (Business:BusinessDateFields).</summary>
    public IReadOnlyDictionary<string, string>? BusinessDateFields { get; init; }
}

public sealed class MqlValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
    /// <summary>True when the query was rejected for a security reason (write, JS, admin, tenant, collection).</summary>
    public bool Blocked { get; set; }
    public string? Collection { get; set; }
    public string? Operation { get; set; }
    /// <summary>Normalised (and clamped) aggregation pipeline. Not yet tenant-scoped.</summary>
    public JsonArray? Pipeline { get; set; }
}

/// <summary>
/// Validates AI-generated queries. Every query is treated as untrusted input: only read operations,
/// whitelisted stages/operators, known collections and fields, bounded size, and no tenant field access.
/// </summary>
public sealed class MqlValidator
{
    private static readonly Regex VariableRegex = new(@"^\$\$([A-Za-z_][A-Za-z0-9_]*)(\.[A-Za-z0-9_]+)*$", RegexOptions.Compiled);
    private static readonly Regex RegexOptionsRegex = new(@"^[imxs]*$", RegexOptions.Compiled);

    public MqlValidationResult Validate(MqlQuery query, MqlValidationContext context)
    {
        var result = new MqlValidationResult();
        var run = new Run(context, result);
        run.Validate(query);
        return result;
    }

    private sealed class Run
    {
        private readonly MqlValidationContext _ctx;
        private readonly MqlValidationResult _r;
        private readonly Dictionary<string, CollectionSchema> _collections;
        private int _nodes;
        private int _lookups;
        private readonly HashSet<string> _tenantFields;

        public Run(MqlValidationContext ctx, MqlValidationResult r)
        {
            _ctx = ctx;
            _r = r;
            _collections = ctx.Collections
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            // Every tenant field used by any allowed collection is server-managed.
            _tenantFields = new HashSet<string>(
                ctx.Collections.Select(c => c.TenantField).Append(ctx.TenantField).Where(f => !string.IsNullOrEmpty(f)),
                StringComparer.OrdinalIgnoreCase);
        }

        private void Error(string message) { if (!_r.Errors.Contains(message)) _r.Errors.Add(message); }

        private void Block(string message) { _r.Blocked = true; Error(message); }

        public void Validate(MqlQuery q)
        {
            if (q.IsUnsupported || q.IsClarification)
            {
                Error(q.IsClarification ? "The model asked for a clarification instead of a query." : "The model marked the question as unsupported.");
                return;
            }

            var op = (q.Operation ?? string.Empty).Trim();
            _r.Operation = op;
            if (MqlOperators.WriteOperations.Contains(op))
            {
                Block($"Operation '{op}' is not allowed. Only read operations (find, aggregate, count, distinct) are permitted.");
                return;
            }
            if (!MqlOperators.ReadOperations.Contains(op))
            {
                Error($"Unknown operation '{op}'. Use one of: find, aggregate, count, distinct.");
                return;
            }

            if (!_collections.TryGetValue(q.Collection ?? string.Empty, out var schema))
            {
                Block($"Collection '{q.Collection}' is not allowed. Allowed collections: {string.Join(", ", _collections.Values.Select(c => c.Name))}.");
                return;
            }
            _r.Collection = schema.Name;

            if (op == "distinct")
            {
                if (string.IsNullOrWhiteSpace(q.Field) || q.Field.StartsWith('$'))
                {
                    Error("A distinct query requires a 'field' (a field name without '$').");
                    return;
                }
            }
            if (op == "aggregate" && (q.Pipeline is null || q.Pipeline.Count == 0))
            {
                Error("An aggregate query requires a non-empty 'pipeline' array.");
                return;
            }

            var pipeline = MqlNormalizer.ToPipeline(q, _ctx.MaxRecords);

            if (pipeline.Count > _ctx.MaxStages)
                Error($"The pipeline has {pipeline.Count} stages; the maximum is {_ctx.MaxStages}.");

            var scope = FieldScope.FromSchema(schema, schema.TenantField);
            ValidatePipeline(pipeline, scope, schema.Name, depth: 0, insideFacet: false);

            if (_nodes > _ctx.MaxNodes)
                Error($"The query is too large ({_nodes} nodes; maximum {_ctx.MaxNodes}).");

            _r.Pipeline = pipeline;
        }

        // ------------------------------------------------------------------ pipeline

        private FieldScope ValidatePipeline(JsonArray pipeline, FieldScope scope, string collection, int depth, bool insideFacet)
        {
            for (int i = 0; i < pipeline.Count; i++)
            {
                if (pipeline[i] is not JsonObject stage || stage.Count != 1)
                {
                    Error($"Stage {i + 1} must be an object with exactly one stage operator.");
                    continue;
                }
                var (name, body) = stage.First();
                _nodes++;

                if (MqlOperators.Denied.TryGetValue(name, out var reason))
                {
                    Block($"Stage '{name}' is blocked: {reason}.");
                    continue;
                }
                if (!MqlOperators.Stages.Contains(name))
                {
                    Error($"Stage '{name}' is not allowed. Allowed stages: {string.Join(", ", MqlOperators.Stages)}.");
                    continue;
                }

                switch (name)
                {
                    case "$match":
                        if (body is JsonObject filter) ValidateFilter(filter, scope, depth + 1);
                        else Error("$match must be an object.");
                        break;

                    case "$group":
                        scope = ValidateGroup(body, scope, depth + 1);
                        break;

                    case "$sort":
                        ValidateSort(body, scope);
                        break;

                    case "$limit":
                        if (!JsonHelpers.TryGetNumber(body, out var limit) || limit < 1 || limit != Math.Floor(limit))
                            Error("$limit must be a positive integer.");
                        else if (limit > _ctx.MaxRecords)
                        {
                            stage["$limit"] = _ctx.MaxRecords;
                            _r.Warnings.Add($"$limit {limit} reduced to the maximum of {_ctx.MaxRecords} records.");
                        }
                        break;

                    case "$skip":
                        if (!JsonHelpers.TryGetNumber(body, out var skip) || skip < 0 || skip > 100_000)
                            Error("$skip must be an integer between 0 and 100000.");
                        break;

                    case "$project":
                        scope = ValidateProject(body, scope, depth + 1);
                        break;

                    case "$addFields":
                    case "$set":
                        scope = ValidateAddFields(name, body, scope, depth + 1);
                        break;

                    case "$unset":
                        scope = ValidateUnset(body, scope);
                        break;

                    case "$unwind":
                        scope = ValidateUnwind(body, scope);
                        break;

                    case "$count":
                        if (!JsonHelpers.TryGetString(body, out var countName) || !IsValidNewFieldName(countName))
                            Error("$count must be a field name string, e.g. {\"$count\": \"total\"}.");
                        else
                        {
                            CheckNotTenant(countName);
                            scope = FieldScope.Empty();
                            scope.Add(countName);
                        }
                        break;

                    case "$sortByCount":
                        ValidateExpression(body, scope, depth + 1);
                        scope = FieldScope.Empty();
                        scope.Add("_id");
                        scope.Add("count");
                        break;

                    case "$lookup":
                        scope = ValidateLookup(stage, body, scope);
                        break;

                    case "$facet":
                        if (insideFacet) { Error("$facet cannot be nested inside $facet."); break; }
                        scope = ValidateFacet(body, scope, collection, depth + 1);
                        break;
                }
            }
            return scope;
        }

        // ------------------------------------------------------------------ stages

        private FieldScope ValidateGroup(JsonNode? body, FieldScope scope, int depth)
        {
            if (body is not JsonObject group || !group.ContainsKey("_id"))
            {
                Error("$group must be an object with an '_id' key (use null to group everything).");
                return scope;
            }

            var next = FieldScope.Empty();
            foreach (var (key, value) in group.ToList())
            {
                _nodes++;
                if (key == "_id")
                {
                    ValidateExpression(value, scope, depth);
                    next.Add("_id", value is JsonObject idObj && !idObj.Any(k => k.Key.StartsWith('$')) ? SubScopeOf(idObj) : null);
                    continue;
                }
                if (!IsValidNewFieldName(key)) { Error($"Invalid output field name '{key}' in $group."); continue; }
                CheckNotTenant(key);

                if (value is not JsonObject acc || acc.Count != 1)
                {
                    Error($"$group field '{key}' must use one accumulator, e.g. {{\"$sum\": \"$NetAmount\"}}.");
                    continue;
                }
                var (accName, accArg) = acc.First();
                if (MqlOperators.Denied.TryGetValue(accName, out var reason)) { Block($"Accumulator '{accName}' is blocked: {reason}."); continue; }
                if (!MqlOperators.Accumulators.Contains(accName)) { Error($"Accumulator '{accName}' is not allowed in $group."); continue; }
                ValidateExpression(accArg, scope, depth + 1);
                next.Add(key);
            }
            return next;
        }

        private static FieldScope SubScopeOf(JsonObject obj)
        {
            var s = FieldScope.Empty();
            foreach (var kv in obj) s.Add(kv.Key);
            return s;
        }

        private void ValidateSort(JsonNode? body, FieldScope scope)
        {
            if (body is not JsonObject sort || sort.Count == 0)
            {
                Error("$sort must be a non-empty object like {\"totalSales\": -1}.");
                return;
            }
            foreach (var (field, dir) in sort)
            {
                _nodes++;
                CheckField(field, scope);
                if (!JsonHelpers.TryGetNumber(dir, out var d) || (d != 1 && d != -1))
                    Error($"$sort direction for '{field}' must be 1 or -1.");
            }
        }

        private FieldScope ValidateProject(JsonNode? body, FieldScope scope, int depth)
        {
            if (body is not JsonObject project || project.Count == 0)
            {
                Error("$project must be a non-empty object.");
                return scope;
            }

            bool hasInclusion = false, hasExclusion = false, excludeId = false;
            var included = new List<string>();
            var computed = new List<string>();
            var excluded = new List<string>();

            foreach (var (key, value) in project)
            {
                _nodes++;
                if (key.StartsWith('$')) { Error($"Invalid $project key '{key}'."); continue; }
                CheckNotTenant(key);

                bool isFlag = JsonHelpers.TryGetNumber(value, out var num) || JsonHelpers.IsBoolean(value, out _);
                if (isFlag)
                {
                    bool include = JsonHelpers.IsBoolean(value, out var b) ? b : num != 0;
                    if (key == "_id")
                    {
                        excludeId = !include;
                        continue;
                    }
                    if (include)
                    {
                        CheckField(key, scope);
                        hasInclusion = true;
                        included.Add(key);
                    }
                    else
                    {
                        hasExclusion = true;
                        excluded.Add(key);
                    }
                }
                else
                {
                    ValidateExpression(value, scope, depth);
                    hasInclusion = true;
                    computed.Add(key);
                }
            }

            if (hasInclusion && hasExclusion)
            {
                Error("$project cannot mix inclusion and exclusion (except for _id).");
                return scope;
            }

            if (hasInclusion)
            {
                var next = FieldScope.Empty();
                if (!excludeId) next.Add("_id");
                foreach (var path in included)
                {
                    var root = path.Split('.')[0];
                    next.Add(root, scope.TryGetSub(root, out var sub) ? sub : null);
                }
                foreach (var c in computed) next.Add(c.Split('.')[0]);
                return next;
            }

            var rest = scope.Clone();
            foreach (var e in excluded) rest.Remove(e.Split('.')[0]);
            if (excludeId) rest.Remove("_id");
            return rest;
        }

        private FieldScope ValidateAddFields(string stageName, JsonNode? body, FieldScope scope, int depth)
        {
            if (body is not JsonObject fields || fields.Count == 0)
            {
                Error($"{stageName} must be a non-empty object.");
                return scope;
            }
            var next = scope.Clone();
            foreach (var (key, value) in fields)
            {
                _nodes++;
                if (!IsValidNewFieldName(key)) { Error($"Invalid field name '{key}' in {stageName}."); continue; }
                CheckNotTenant(key);
                ValidateExpression(value, scope, depth);
                next.Add(key);
            }
            return next;
        }

        private FieldScope ValidateUnset(JsonNode? body, FieldScope scope)
        {
            var next = scope.Clone();
            IEnumerable<JsonNode?> items = body is JsonArray arr ? arr : new[] { body };
            foreach (var item in items)
            {
                if (!JsonHelpers.TryGetString(item, out var f) || f.StartsWith('$'))
                {
                    Error("$unset expects a field name or an array of field names.");
                    continue;
                }
                next.Remove(f.Split('.')[0]);
            }
            return next;
        }

        private FieldScope ValidateUnwind(JsonNode? body, FieldScope scope)
        {
            string? path = null;
            var next = scope.Clone();
            if (JsonHelpers.TryGetString(body, out var p)) path = p;
            else if (body is JsonObject o)
            {
                foreach (var (key, value) in o)
                {
                    switch (key)
                    {
                        case "path":
                            if (JsonHelpers.TryGetString(value, out var pp)) path = pp;
                            break;
                        case "preserveNullAndEmptyArrays":
                            if (!JsonHelpers.IsBoolean(value, out _)) Error("$unwind.preserveNullAndEmptyArrays must be true or false.");
                            break;
                        case "includeArrayIndex":
                            if (!JsonHelpers.TryGetString(value, out var idx) || !IsValidNewFieldName(idx)) Error("$unwind.includeArrayIndex must be a field name.");
                            else next.Add(idx);
                            break;
                        default:
                            Error($"Unknown $unwind option '{key}'.");
                            break;
                    }
                }
            }

            if (path is null || !path.StartsWith('$') || path.StartsWith("$$"))
            {
                Error("$unwind requires a field path such as \"$items\".");
                return scope;
            }
            CheckField(path[1..], scope);
            return next;
        }

        private FieldScope ValidateLookup(JsonObject stage, JsonNode? body, FieldScope scope)
        {
            if (++_lookups > _ctx.MaxLookups)
            {
                Error($"Too many $lookup stages (maximum {_ctx.MaxLookups}).");
                return scope;
            }
            if (body is not JsonObject lookup)
            {
                Error("$lookup must be an object.");
                return scope;
            }

            var allowedKeys = new HashSet<string> { "from", "localField", "foreignField", "as" };
            foreach (var key in lookup.Select(k => k.Key))
            {
                if (!allowedKeys.Contains(key))
                {
                    if (key is "pipeline" or "let") Block("$lookup with 'pipeline'/'let' is not allowed; use localField/foreignField.");
                    else Error($"Unknown $lookup option '{key}'.");
                }
            }

            if (!JsonHelpers.TryGetString(lookup["from"], out var from) ||
                !JsonHelpers.TryGetString(lookup["localField"], out var localField) ||
                !JsonHelpers.TryGetString(lookup["foreignField"], out var foreignField) ||
                !JsonHelpers.TryGetString(lookup["as"], out var asName))
            {
                Error("$lookup requires string 'from', 'localField', 'foreignField' and 'as'.");
                return scope;
            }

            if (!_collections.TryGetValue(from, out var foreign))
            {
                Block($"$lookup into collection '{from}' is not allowed.");
                return scope;
            }
            lookup["from"] = foreign.Name; // canonical casing

            CheckField(localField, scope);
            var foreignScope = FieldScope.FromSchema(foreign, foreign.TenantField);
            CheckNotTenant(foreignField);
            if (!foreignScope.Contains(foreignField))
                Error($"Unknown field '{foreignField}' in collection '{foreign.Name}'.");

            if (!IsValidNewFieldName(asName)) Error($"Invalid $lookup 'as' name '{asName}'.");
            CheckNotTenant(asName);

            var next = scope.Clone();
            next.Add(asName, foreignScope);
            _ = stage;
            return next;
        }

        private FieldScope ValidateFacet(JsonNode? body, FieldScope scope, string collection, int depth)
        {
            if (body is not JsonObject facet || facet.Count == 0)
            {
                Error("$facet must be a non-empty object of named pipelines.");
                return scope;
            }
            var next = FieldScope.Empty();
            foreach (var (name, sub) in facet)
            {
                if (!IsValidNewFieldName(name)) { Error($"Invalid $facet name '{name}'."); continue; }
                if (sub is not JsonArray subPipeline || subPipeline.Count == 0)
                {
                    Error($"$facet '{name}' must be a non-empty pipeline array.");
                    continue;
                }
                if (subPipeline.Count > _ctx.MaxStages)
                    Error($"$facet '{name}' has too many stages (maximum {_ctx.MaxStages}).");
                ValidatePipeline(subPipeline, scope.Clone(), collection, depth, insideFacet: true);
                next.Add(name);
            }
            return next;
        }

        // ------------------------------------------------------------------ filters

        private void ValidateFilter(JsonObject filter, FieldScope scope, int depth)
        {
            if (depth > _ctx.MaxDepth) { Error("The query is nested too deeply."); return; }

            foreach (var (key, value) in filter)
            {
                _nodes++;
                if (key.StartsWith('$'))
                {
                    if (MqlOperators.Denied.TryGetValue(key, out var reason)) { Block($"Operator '{key}' is blocked: {reason}."); continue; }
                    switch (key)
                    {
                        case "$and":
                        case "$or":
                        case "$nor":
                            if (value is not JsonArray arr || arr.Count == 0) { Error($"{key} must be a non-empty array."); break; }
                            foreach (var item in arr)
                            {
                                if (item is JsonObject sub) ValidateFilter(sub, scope, depth + 1);
                                else Error($"Every element of {key} must be an object.");
                            }
                            break;
                        case "$expr":
                            ValidateExpression(value, scope, depth + 1);
                            break;
                        case "$comment":
                            break;
                        default:
                            Error($"Operator '{key}' is not allowed at the top level of a filter.");
                            break;
                    }
                    continue;
                }

                CheckField(key, scope);
                ValidateFilterValue(value, scope, depth + 1);
            }
        }

        private void ValidateFilterValue(JsonNode? value, FieldScope scope, int depth)
        {
            if (depth > _ctx.MaxDepth) { Error("The query is nested too deeply."); return; }
            _nodes++;

            if (value is JsonObject obj && obj.Count > 0 && obj.Any(k => k.Key.StartsWith('$')))
            {
                if (IsExtendedJsonLiteral(obj)) return;
                if (!obj.All(k => k.Key.StartsWith('$')))
                {
                    Error("A filter value cannot mix operators and field names.");
                    return;
                }
                foreach (var (op, arg) in obj)
                {
                    if (MqlOperators.Denied.TryGetValue(op, out var reason)) { Block($"Operator '{op}' is blocked: {reason}."); continue; }
                    if (!MqlOperators.QueryOperators.Contains(op))
                    {
                        Error($"Query operator '{op}' is not allowed.");
                        continue;
                    }
                    switch (op)
                    {
                        case "$in":
                        case "$nin":
                        case "$all":
                            if (arg is not JsonArray list) Error($"{op} requires an array.");
                            else if (list.Count > 1000) Error($"{op} list is too long (maximum 1000 values).");
                            else foreach (var item in list) ValidateLiteral(item, depth + 1);
                            break;
                        case "$not":
                            if (arg is JsonObject) ValidateFilterValue(arg, scope, depth + 1);
                            else if (!JsonHelpers.TryGetString(arg, out _)) Error("$not requires an operator object or a regex.");
                            break;
                        case "$elemMatch":
                            if (arg is not JsonObject em) { Error("$elemMatch requires an object."); break; }
                            if (em.All(k => k.Key.StartsWith('$'))) ValidateFilterValue(em, scope, depth + 1);
                            else ValidateFilter(em, OpenScope(), depth + 1);
                            break;
                        case "$regex":
                            if (!JsonHelpers.TryGetString(arg, out var pattern)) Error("$regex requires a string pattern.");
                            else if (pattern.Length > _ctx.MaxRegexLength) Error($"$regex pattern is too long (maximum {_ctx.MaxRegexLength} characters).");
                            break;
                        case "$options":
                            if (!JsonHelpers.TryGetString(arg, out var opts) || !RegexOptionsRegex.IsMatch(opts)) Error("$options may only contain i, m, x, s.");
                            break;
                        case "$exists":
                            if (!JsonHelpers.IsBoolean(arg, out _) && !JsonHelpers.TryGetNumber(arg, out _)) Error("$exists requires true or false.");
                            break;
                        case "$size":
                            if (!JsonHelpers.TryGetNumber(arg, out _)) Error("$size requires a number.");
                            break;
                        default:
                            ValidateLiteral(arg, depth + 1);
                            break;
                    }
                }
                return;
            }

            ValidateLiteral(value, depth);
        }

        private static FieldScope OpenScope()
        {
            var s = FieldScope.FromSchema(null, string.Empty);
            return s;
        }

        /// <summary>A literal value may not contain operators, except Extended JSON wrappers.</summary>
        private void ValidateLiteral(JsonNode? node, int depth)
        {
            if (depth > _ctx.MaxDepth) { Error("The query is nested too deeply."); return; }
            _nodes++;
            switch (node)
            {
                case JsonObject o:
                    if (IsExtendedJsonLiteral(o)) return;
                    foreach (var (k, v) in o)
                    {
                        if (k.StartsWith('$'))
                        {
                            if (MqlOperators.Denied.TryGetValue(k, out var reason)) Block($"Operator '{k}' is blocked: {reason}.");
                            else Error($"Operator '{k}' is not allowed inside a literal value.");
                            continue;
                        }
                        ValidateLiteral(v, depth + 1);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a) ValidateLiteral(item, depth + 1);
                    break;
            }
        }

        // ------------------------------------------------------------------ expressions

        private void ValidateExpression(JsonNode? node, FieldScope scope, int depth)
        {
            if (depth > _ctx.MaxDepth) { Error("The query is nested too deeply."); return; }
            _nodes++;

            switch (node)
            {
                case null:
                    return;

                case JsonValue:
                    if (JsonHelpers.TryGetString(node, out var s))
                    {
                        if (s.StartsWith("$$"))
                        {
                            var m = VariableRegex.Match(s);
                            if (!m.Success) Error($"Invalid variable reference '{s}'.");
                            else if (MqlOperators.DeniedVariables.Contains(m.Groups[1].Value)) Block($"Variable '{s}' is not allowed.");
                        }
                        else if (s.StartsWith('$'))
                        {
                            CheckField(s[1..], scope);
                        }
                    }
                    return;

                case JsonArray arr:
                    foreach (var item in arr) ValidateExpression(item, scope, depth + 1);
                    return;

                case JsonObject obj:
                    if (obj.Count == 0 || IsExtendedJsonLiteral(obj)) return;
                    var operatorKeys = obj.Count(k => k.Key.StartsWith('$'));
                    if (operatorKeys == 0)
                    {
                        foreach (var (_, v) in obj) ValidateExpression(v, scope, depth + 1);
                        return;
                    }
                    if (operatorKeys != obj.Count || obj.Count != 1)
                    {
                        Error("An expression object must contain exactly one operator.");
                        return;
                    }
                    var (op, arg) = obj.First();
                    if (MqlOperators.Denied.TryGetValue(op, out var reason)) { Block($"Operator '{op}' is blocked: {reason}."); return; }
                    if (!MqlOperators.ExpressionOperators.Contains(op)) { Error($"Expression operator '{op}' is not allowed."); return; }
                    if (op == "$literal") return;
                    ValidateExpression(arg, scope, depth + 1);
                    return;
            }
        }

        // ------------------------------------------------------------------ helpers

        private void CheckField(string path, FieldScope scope)
        {
            if (string.IsNullOrWhiteSpace(path)) { Error("Empty field name."); return; }
            if (path.StartsWith('$')) { Error($"Invalid field name '{path}'."); return; }
            if (!CheckNotTenant(path)) return;
            if (!scope.Contains(path))
            {
                var suggestion = scope.Suggest(path);
                Error(suggestion is null
                    ? $"Unknown field '{path}'."
                    : $"Unknown field '{path}'. Did you mean '{suggestion}'?");
            }
        }

        private bool CheckNotTenant(string path)
        {
            var root = path.Split('.')[0];
            if (_tenantFields.Contains(root))
            {
                Block($"Field '{root}' is managed by the server. Do not filter or reference the company.");
                return false;
            }
            return true;
        }

        private static bool IsValidNewFieldName(string name)
            => !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && !name.StartsWith('$') && !name.Contains('.');

        internal static bool IsExtendedJsonLiteral(JsonObject o)
            => o.Count == 1 && MqlOperators.ExtendedJsonLiterals.Contains(o.First().Key);
    }
}
