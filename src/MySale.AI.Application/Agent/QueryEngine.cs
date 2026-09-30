using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Stores;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

public sealed class PreparedQuery
{
    public required MqlQuery Query { get; init; }
    public required MqlValidationResult Validation { get; init; }
    public JsonArray? ScopedPipeline { get; init; }
    public string? Mql { get; init; }
    /// <summary>The query needs the selected store, but no verified store is available (answer: "please select a store").</summary>
    public bool StoreContextMissing { get; init; }
    /// <summary>Field the selected-store filter was applied on (null = not store-filtered).</summary>
    public string? StoreFilterField { get; init; }
    public bool IsExecutable => Validation.IsValid && ScopedPipeline is not null && !StoreContextMissing;
}

/// <summary>Outcome of replacing ObjectId reference columns with the referenced document's name.</summary>
public sealed class ReferenceResolution
{
    public bool Changed { get; init; }
    public ExecutedQuery Result { get; init; } = new();
    /// <summary>One line per column, e.g. "supplierId → Suppliers.SupplierName (7/7 resolved)".</summary>
    public List<string> Notes { get; init; } = new();
    public long ElapsedMs { get; init; }
    /// <summary>Per id column: entity, ids checked, resolved, unresolved, orphans (for the trace, activity log and diagnostics).</summary>
    public List<ReferenceDiagnostic> Diagnostics { get; init; } = new();
    /// <summary>Every id of the result (lower case) → the label shown instead (name, "Unknown supplier", "No warehouse").</summary>
    public Dictionary<string, string> Labels { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExecutedQuery
{
    public List<JsonObject> Rows { get; init; } = new();
    public List<string> Columns { get; init; } = new();
    public bool Truncated { get; init; }
    public long ElapsedMs { get; init; }
}

/// <summary>
/// The MQL pipeline shared by the chat orchestrator and the query sandbox:
/// schema → validation → date coercion → tenant scoping → execution → shaping.
/// </summary>
public sealed partial class QueryEngine
{
    private readonly ISchemaService _schema;
    private readonly MqlValidator _validator;
    private readonly TenantQueryGuard _guard;
    private readonly TenantOptions _tenant;
    private readonly IQueryExecutor _executor;
    private readonly IUserContext _user;

    public QueryEngine(ISchemaService schema, MqlValidator validator, TenantQueryGuard guard, TenantOptions tenant,
        IQueryExecutor executor, IUserContext user)
    {
        _schema = schema;
        _validator = validator;
        _guard = guard;
        _tenant = tenant;
        _executor = executor;
        _user = user;
    }

    public string TenantField => _tenant.FieldName;

    public async Task<IReadOnlyList<CollectionSchema>> GetAllowedSchemaAsync(AppSettings settings, CancellationToken ct)
    {
        var all = await _schema.GetSchemaAsync(includeDiscovery: true, ct);
        var allowed = settings.Query.AllowedCollections;
        var result = all
            .Where(c => allowed.Count == 0 || allowed.Contains(c.Name, StringComparer.Ordinal))
            .ToList();
        // Hidden fields are neither shown to the AI nor accepted by the validator.
        foreach (var c in result) c.Fields = c.Fields.Where(f => !f.Hidden).ToList();
        return result;
    }

    public MqlValidationContext CreateContext(IReadOnlyList<CollectionSchema> schema, AppSettings settings, StoreScope? store = null,
        DateCoercionContext? dates = null, string? question = null, IReadOnlyDictionary<string, string>? businessDateFields = null) => new()
    {
        Collections = schema,
        TenantField = _tenant.FieldName,
        MaxStages = settings.Query.MaxPipelineStages,
        MaxRecords = settings.Query.MaxRecords,
        Store = store,
        Dates = dates,
        Question = question,
        BusinessDateFields = businessDateFields
    };

    public PreparedQuery Prepare(MqlQuery query, MqlValidationContext context, AppSettings settings)
    {
        var validation = _validator.Validate(query, context);
        if (!validation.IsValid || validation.Pipeline is null || validation.Collection is null)
            return new PreparedQuery { Query = query, Validation = validation };

        var pipeline = (JsonArray)validation.Pipeline.DeepClone();
        var bySchema = context.Collections.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Only convert "2026-09-24"-style values to real dates for fields that are NOT stored as text.
        var textFields = bySchema.TryGetValue(validation.Collection, out var rootSchema)
            ? rootSchema.Fields.Where(f => f.Type is "string").Select(f => f.Name).ToHashSet(StringComparer.Ordinal)
            : null;
        MqlDateCoercer.Coerce(pipeline, textFields, context.Dates);

        // Periods must use the business date of the transaction (invoiceDate …), not record timestamps (createdAt …).
        if (BusinessDateField(rootSchema, context.BusinessDateFields) is { } businessDate && !AsksAboutRecordTimestamps(context.Question))
        {
            foreach (var used in MatchedFields(pipeline).Where(IsRecordTimestamp).Distinct(StringComparer.OrdinalIgnoreCase))
                validation.Errors.Add($"For periods use the business date field \"{businessDate}\" of {validation.Collection}, not the record timestamp \"{used}\".");
            if (!validation.IsValid) return new PreparedQuery { Query = query, Validation = validation };
        }

        // MySaleBooks business rules that prevent double counting (pipe-conditional stock sums, one currency per total).
        foreach (var error in MySaleBooksDomain.Check(validation.Collection, pipeline)) validation.Errors.Add(error);
        if (!validation.IsValid) return new PreparedQuery { Query = query, Validation = validation };

        // Status filter the MySaleBooks reports always apply (cancelled documents excluded), enforced by the server.
        // (named statusFilter: an if-condition pattern variable is in scope for the whole method, and "required" is used below)
        if (MySaleBooksDomain.RequiredFilter(rootSchema) is { } statusFilter)
            pipeline.Insert(0, new JsonObject { ["$match"] = statusFilter });

        TenantBinding? Resolve(string collection)
        {
            if (!bySchema.TryGetValue(collection, out var c)) return _guard.Default;
            if (string.IsNullOrEmpty(c.TenantField)) return null; // shared collection
            var isObjectId = c.TenantValueType switch { "objectId" => true, "string" => false, _ => _tenant.ValueIsObjectId };
            return new TenantBinding(c.TenantField, isObjectId);
        }
        // MySaleBooks JWT users without a company claim: the customer database itself is the tenant boundary
        // (resolved from the JWT "dbName" claim), so no company filter is added — limits still apply.
        // Selected store (MySaleBooks "Store Location"): checked before scoping, injected after the tenant filter.
        var store = context.Store;
        string? storeField = null;
        if (store is { Mode: StoreMode.Selected or StoreMode.Missing })
        {
            var usesStoreData = store.FieldFor(rootSchema) is not null || LookupCollections(pipeline).Any(c => bySchema.TryGetValue(c, out var ls) && store.FieldFor(ls) is not null);
            if (store.Mode == StoreMode.Missing)
            {
                if (usesStoreData)
                {
                    validation.Errors.Add(StoreScope.MissingMessage);
                    return new PreparedQuery { Query = query, Validation = validation, StoreContextMissing = true };
                }
            }
            else
            {
                foreach (var conflict in StoreConflicts(pipeline, store, rootSchema))
                    validation.Errors.Add($"Queries are limited to the selected store. Remove the filter on \"{conflict}\" — the server adds the store filter itself.");
                if (!validation.IsValid) return new PreparedQuery { Query = query, Validation = validation };
            }
        }

        var scoped = _user.TenantIsDatabase
            ? _guard.Apply(pipeline, _user.CompanyId, settings.Query.MaxRecords, validation.Collection, _ => null)
            : _guard.Apply(pipeline, _user.CompanyId, settings.Query.MaxRecords, validation.Collection, Resolve);

        if (store is { Mode: StoreMode.Selected })
        {
            storeField = ApplyStoreFilter(scoped, validation.Collection, store, bySchema);
            // Validation of the store rule: a store-scoped root must start with the selected-store filter.
            if (store.FieldFor(rootSchema) is { } required && !HasStoreFilter(scoped, required, store))
            {
                validation.Errors.Add("The selected-store filter is missing from the query.");
                return new PreparedQuery { Query = query, Validation = validation };
            }
        }

        foreach (var stage in scoped) AddRequiredFiltersToLookups(stage, bySchema);

        return new PreparedQuery
        {
            Query = query,
            Validation = validation,
            ScopedPipeline = scoped,
            StoreFilterField = storeField,
            Mql = MqlFormatter.ToShell(validation.Collection, scoped)
        };
    }

    // ------------------------------------------------------------------ business date field

    private static readonly string[] RecordTimestampNames =
    {
        "createdat", "createdon", "createddate", "createdtime", "created", "creationdate", "insertedat", "updatedat", "updatedon",
        "updateddate", "modifiedat", "modifiedon", "modifieddate", "lastmodified", "lastupdated", "timestamp"
    };
    private static readonly string[] BusinessDateNames =
    {
        "invoicedate", "billdate", "saledate", "salesdate", "purchasedate", "paymentdate", "receiptdate", "returndate", "voucherdate",
        "transactiondate", "txndate", "orderdate", "quotationdate", "deliverydate", "documentdate", "docdate", "entrydate", "postingdate",
        "expensedate", "movementdate", "date"
    };

    private static string Plain(string name) => name.Split('.').Last().Replace("_", string.Empty).ToLowerInvariant();

    public static bool IsRecordTimestamp(string field) => RecordTimestampNames.Contains(Plain(field));

    /// <summary>
    /// The business date of a collection: configured (Business:BusinessDateFields), else "{entity}Date" (Sales → saleDate),
    /// else the first known transaction-date name (invoiceDate, voucherDate, transactionDate, date …). Never createdAt/updatedAt.
    /// </summary>
    public static string? BusinessDateField(CollectionSchema? collection, IReadOnlyDictionary<string, string>? configured = null)
    {
        if (collection is null) return null;
        if (configured is not null && configured.TryGetValue(collection.Name, out var explicitField) && !string.IsNullOrWhiteSpace(explicitField))
            return explicitField;
        var dates = collection.Fields
            .Where(f => !f.Hidden && !f.Name.Contains('.') && !IsRecordTimestamp(f.Name)
                        && (f.Type is "date" or "Date" || (f.Type is "string" && Plain(f.Name).EndsWith("date"))))
            .ToList();
        var own = Singular(collection.Name) + "date";
        return dates.FirstOrDefault(f => Plain(f.Name) == own)?.Name
               ?? BusinessDateNames.Select(n => dates.FirstOrDefault(f => Plain(f.Name) == n)).FirstOrDefault(f => f is not null)?.Name;
    }

    private static readonly Regex CreationQuestion = new(
        @"\b(created|creation|added|registered|joined|signed\s+up|onboarded|new\s+(customers?|suppliers?|items?|products?|users?|accounts?)|updated|modified|edited|changed)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool AsksAboutRecordTimestamps(string? question) => !string.IsNullOrWhiteSpace(question) && CreationQuestion.IsMatch(question);

    /// <summary>Field names filtered in $match stages before the first $group/$project (top level and inside $and/$or).</summary>
    private static IEnumerable<string> MatchedFields(JsonArray pipeline)
    {
        foreach (var stage in pipeline.OfType<JsonObject>())
        {
            if (stage["$match"] is JsonObject match)
            {
                foreach (var f in Fields(match)) yield return f;
                continue;
            }
            if (stage.ContainsKey("$group") || stage.ContainsKey("$project")) yield break;
        }

        static IEnumerable<string> Fields(JsonObject node)
        {
            foreach (var (key, value) in node)
            {
                if (key is "$and" or "$or" or "$nor" && value is JsonArray list)
                {
                    foreach (var item in list.OfType<JsonObject>())
                        foreach (var f in Fields(item)) yield return f;
                }
                else if (!key.StartsWith('$')) yield return key;
            }
        }
    }

    // ------------------------------------------------------------------ store filter

    /// <summary>Adds the selected-store $match to the root (after the tenant filter) and to $lookup sub-pipelines of store-scoped collections.</summary>
    private static string? ApplyStoreFilter(JsonArray scoped, string root, StoreScope store, IReadOnlyDictionary<string, CollectionSchema> bySchema)
    {
        bySchema.TryGetValue(root, out var rootSchema);
        var rootField = store.FieldFor(rootSchema);
        if (rootField is not null)
        {
            // Tenant $match (if any) is first; the store filter goes right after it, before every AI stage.
            var index = scoped.Count > 0 && scoped[0] is JsonObject first && first.ContainsKey("$match") && IsTenantMatch(first) ? 1 : 0;
            scoped.Insert(index, new JsonObject { ["$match"] = store.Filter(rootField, rootSchema) });
        }
        foreach (var stage in scoped) AddStoreToLookups(stage, store, bySchema);
        return rootField;

        static bool IsTenantMatch(JsonObject stage) => stage["$match"] is JsonObject m && m.Count == 1;
    }

    private static void AddStoreToLookups(JsonNode? stage, StoreScope store, IReadOnlyDictionary<string, CollectionSchema> bySchema)
    {
        if (stage is not JsonObject o) return;
        if (o["$lookup"] is JsonObject lookup && lookup["from"]?.ToString() is { } from
            && bySchema.TryGetValue(from, out var target) && store.FieldFor(target) is { } field)
        {
            if (lookup["pipeline"] is not JsonArray sub)
            {
                sub = new JsonArray();
                lookup["pipeline"] = sub;
            }
            var index = sub.Count > 0 && sub[0] is JsonObject s0 && s0["$match"] is JsonObject m0 && m0.Count == 1 ? 1 : 0;
            sub.Insert(index, new JsonObject { ["$match"] = store.Filter(field, target) });
            foreach (var s in sub.ToList()) AddStoreToLookups(s, store, bySchema);
        }
        if (o["$facet"] is JsonObject facet)
            foreach (var (_, branch) in facet)
                if (branch is JsonArray stages) foreach (var s in stages) AddStoreToLookups(s, store, bySchema);
    }

    /// <summary>Adds the server status filter (e.g. isCanceled ≠ true) inside every $lookup into a MySaleBooks transaction collection.</summary>
    private static void AddRequiredFiltersToLookups(JsonNode? stage, IReadOnlyDictionary<string, CollectionSchema> bySchema)
    {
        if (stage is not JsonObject o) return;
        if (o["$lookup"] is JsonObject lookup && lookup["from"]?.ToString() is { } from
            && bySchema.TryGetValue(from, out var target) && MySaleBooksDomain.RequiredFilter(target) is { } filter)
        {
            if (lookup["pipeline"] is not JsonArray sub)
            {
                sub = new JsonArray();
                lookup["pipeline"] = sub;
            }
            sub.Insert(0, new JsonObject { ["$match"] = filter });
        }
        if (o["$lookup"] is JsonObject l2 && l2["pipeline"] is JsonArray nested)
            foreach (var s in nested.ToList()) AddRequiredFiltersToLookups(s, bySchema);
        if (o["$facet"] is JsonObject facet)
            foreach (var (_, branch) in facet)
                if (branch is JsonArray stages) foreach (var s in stages) AddRequiredFiltersToLookups(s, bySchema);
    }

    private static bool HasStoreFilter(JsonArray scoped, string field, StoreScope store)
        => scoped.Take(2).OfType<JsonObject>().Any(s => s["$match"] is JsonObject m && m.ContainsKey(field));

    private static IEnumerable<string> LookupCollections(JsonArray pipeline)
    {
        foreach (var stage in pipeline.OfType<JsonObject>())
        {
            if (stage["$lookup"] is JsonObject l && l["from"]?.ToString() is { Length: > 0 } from) yield return from;
            if (stage["$facet"] is JsonObject facet)
                foreach (var (_, branch) in facet)
                    if (branch is JsonArray sub) foreach (var c in LookupCollections(sub)) yield return c;
        }
    }

    /// <summary>Filters the AI wrote on the store field with a different store (the server filter would silently empty or widen them).</summary>
    private static IEnumerable<string> StoreConflicts(JsonArray pipeline, StoreScope store, CollectionSchema? rootSchema)
    {
        var field = store.FieldFor(rootSchema);
        if (field is null) yield break;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in pipeline.OfType<JsonObject>())
            if (stage["$match"] is JsonObject match) Scan(match, found);
        foreach (var f in found) yield return f;

        void Scan(JsonObject node, HashSet<string> hits)
        {
            foreach (var (key, value) in node)
            {
                if (key is "$and" or "$or" or "$nor" && value is JsonArray list) { foreach (var item in list.OfType<JsonObject>()) Scan(item, hits); continue; }
                if (!string.Equals(key, field, StringComparison.OrdinalIgnoreCase)) continue;
                var literal = value switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonObject { Count: 1 } o when o["$oid"] is JsonValue ov && ov.TryGetValue<string>(out var s2) => s2,
                    JsonObject { Count: 1 } o when o["$eq"] is JsonValue ev && ev.TryGetValue<string>(out var s3) => s3,
                    _ => null
                };
                if (literal is null || !string.Equals(literal, store.StoreId, StringComparison.OrdinalIgnoreCase)) hits.Add(key);
            }
        }
    }

    /// <summary>
    /// Verifies a store id against the tenant's store master (Branches by default) and returns its name.
    /// true = found, false = the store master exists but has no such store, null = no store master to verify against.
    /// </summary>
    public async Task<(bool? Verified, string? Name)> VerifyStoreAsync(string storeId, string? storeCollection, AppSettings settings, CancellationToken ct)
    {
        IReadOnlyList<CollectionSchema> all;
        try { all = await _schema.GetSchemaAsync(includeDiscovery: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, null); }
        var master = !string.IsNullOrWhiteSpace(storeCollection)
            ? all.FirstOrDefault(c => string.Equals(c.Name, storeCollection, StringComparison.OrdinalIgnoreCase))
            : all.FirstOrDefault(c => Singular(c.Name) == "branch") ?? all.FirstOrDefault(c => Singular(c.Name) is "branchmaster" or "storelocation");
        if (master is null) return (null, null);
        var bySchema = all.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var nameField = NameField(master) ?? "_id";
        var ids = new List<string> { storeId };
        var keys = KeyFields(master, "_id", ids).Concat(new[] { "id", "Id" })
            .Where(k => k == "_id" || master.Fields.Any(f => string.Equals(f.Name, k, StringComparison.Ordinal))).Distinct().ToList();
        var (names, _) = await LookupAsync(master.Name, nameField, keys, Array.Empty<string>(), ids, false, bySchema, settings, ct, null);
        return names.TryGetValue(storeId, out var found) ? (true, found.Name) : (false, null);
    }

    /// <param name="comment">Attached to the MongoDB command (e.g. "ayaan:{correlationId}") so the request can be found in the profiler/logs.</param>
    public async Task<ExecutedQuery> ExecuteAsync(PreparedQuery prepared, AppSettings settings, CancellationToken ct, string? comment = null)
    {
        if (!prepared.IsExecutable)
            throw new InvalidOperationException("The query has not passed validation.");

        var max = settings.Query.MaxRecords;
        var result = await _executor.ExecuteAsync(
            prepared.Validation.Collection!,
            prepared.ScopedPipeline!,
            new QueryExecutionOptions { MaxDocuments = max + 1, TimeoutMs = settings.Query.QueryTimeoutMs, Comment = comment },
            ct);

        var truncated = result.Rows.Count > max;
        var (rows, columns) = ResultShaper.Shape(result.Rows.Take(max));
        return new ExecutedQuery { Rows = rows, Columns = columns, Truncated = truncated, ElapsedMs = result.ElapsedMs };
    }

    // ------------------------------------------------------------------ zero-result retry (text matching)

    /// <summary>
    /// When a query returned no rows, text equality filters are the usual cause: the user (or the model) wrote
    /// "SUNDRY DEBTORS" but the data holds "Sundry Debtors" or "SUNDRY DEBTORS ". This builds the same query with
    /// every exact text match in $match / filter replaced by a case-insensitive, whitespace-tolerant, anchored match
    /// (still the same value — never a partial match). Ids, dates, numbers and the tenant field are left untouched.
    /// The result goes through the normal validation and tenant scoping. Returns null when nothing can be relaxed.
    /// </summary>
    public PreparedQuery? RelaxTextMatches(PreparedQuery prepared, MqlValidationContext context, AppSettings settings, out List<string> relaxedFields)
    {
        relaxedFields = new List<string>();
        var q = prepared.Query;
        var root = prepared.Validation.Collection ?? q.Collection;
        var schema = context.Collections.FirstOrDefault(c => string.Equals(c.Name, root, StringComparison.OrdinalIgnoreCase));
        var fields = relaxedFields;

        JsonArray? pipeline = null;
        if (q.Pipeline is not null)
        {
            pipeline = (JsonArray)q.Pipeline.DeepClone();
            foreach (var stage in pipeline)
            {
                if (stage is not JsonObject o) continue;
                if (o["$match"] is JsonObject match) { RelaxConditions(match, schema, fields); continue; }
                if (o.ContainsKey("$sort") || o.ContainsKey("$limit") || o.ContainsKey("$skip")) continue;
                break; // after $group / $project / $lookup / $unwind … fields are computed: leave later filters alone
            }
        }
        JsonObject? filter = null;
        if (q.Filter is not null)
        {
            filter = (JsonObject)q.Filter.DeepClone();
            RelaxConditions(filter, schema, fields);
        }
        if (fields.Count == 0) return null;

        var relaxed = new MqlQuery
        {
            Type = q.Type, Operation = q.Operation, Collection = q.Collection, Pipeline = pipeline ?? q.Pipeline?.DeepClone() as JsonArray,
            Filter = filter ?? q.Filter?.DeepClone() as JsonObject, Projection = q.Projection?.DeepClone() as JsonObject,
            Sort = q.Sort?.DeepClone() as JsonObject, Limit = q.Limit, Field = q.Field, Explanation = q.Explanation,
            Visualization = q.Visualization, Reason = q.Reason
        };
        var result = Prepare(relaxed, context, settings);
        return result.IsExecutable ? result : null;
    }

    private void RelaxConditions(JsonObject match, CollectionSchema? schema, List<string> relaxed)
    {
        foreach (var (key, value) in match.ToList())
        {
            if (key is "$and" or "$or" or "$nor")
            {
                if (value is JsonArray list)
                    foreach (var item in list.OfType<JsonObject>()) RelaxConditions(item, schema, relaxed);
                continue;
            }
            if (key.StartsWith('$') || !IsRelaxableField(key, schema)) continue;

            string? text = value switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonObject { Count: 1 } eq when eq["$eq"] is JsonValue ev && ev.TryGetValue<string>(out var s2) => s2,
                _ => null
            };
            if (text is null || !IsRelaxableText(text)) continue;

            var words = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
            match[key] = new JsonObject
            {
                ["$regex"] = "^\\s*" + string.Join("\\s+", words) + "\\s*$",
                ["$options"] = "i"
            };
            relaxed.Add(key);
        }
    }

    private bool IsRelaxableField(string field, CollectionSchema? schema)
    {
        if (string.Equals(field, _tenant.FieldName, StringComparison.OrdinalIgnoreCase) || field == "_id") return false;
        var last = field.Split('.').Last();
        if (last.EndsWith("Id", StringComparison.Ordinal) || last.EndsWith("ID", StringComparison.Ordinal)
            || last.EndsWith("Guid", StringComparison.OrdinalIgnoreCase)) return false;
        var type = schema?.Fields.FirstOrDefault(f => string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase))?.Type;
        return type is null or "string" or "String" or "mixed";
    }

    private static bool IsRelaxableText(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 120 || !t.Any(char.IsLetter)) return false;           // numbers, codes like "1001"
        if (IsIdText(t) || DateTime.TryParse(t, out _)) return false;
        return true;
    }

    // ------------------------------------------------------------------ server-side master reads

    /// <summary>
    /// Reads a few documents of a master collection with a server-built pipeline (company settings, currency, branch).
    /// Read-only, tenant-scoped, at most <paramref name="max"/> documents, 3 s. Never used with AI-generated pipelines.
    /// </summary>
    public async Task<List<JsonObject>> ReadMasterAsync(string collection, JsonArray pipeline, IReadOnlyList<CollectionSchema> schema,
        AppSettings settings, int max, CancellationToken ct, string? comment = null)
    {
        var bySchema = schema.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        TenantBinding? Resolve(string c)
        {
            if (!bySchema.TryGetValue(c, out var cs)) return _guard.Default;
            if (string.IsNullOrEmpty(cs.TenantField)) return null;
            var isObjectId = cs.TenantValueType switch { "objectId" => true, "string" => false, _ => _tenant.ValueIsObjectId };
            return new TenantBinding(cs.TenantField, isObjectId);
        }
        var scoped = _user.TenantIsDatabase
            ? _guard.Apply(pipeline, _user.CompanyId, max, collection, _ => null)
            : _guard.Apply(pipeline, _user.CompanyId, max, collection, Resolve);
        var found = await _executor.ExecuteAsync(collection, scoped,
            new QueryExecutionOptions { MaxDocuments = max, TimeoutMs = Math.Min(settings.Query.QueryTimeoutMs, 3000), Comment = comment }, ct);
        return found.Rows;
    }

    // ------------------------------------------------------------------ stored business values (semantic step)

    /// <summary>
    /// Distinct stored values of a classification field that match the given patterns — e.g. how "Sundry Debtors" is
    /// actually written in Ledgers.groupName ("SUNDRY DEBTORS"). Read-only, tenant-scoped, at most 20 values, 3 s.
    /// Any failure returns an empty list (the query is then generated without the stored spelling).
    /// </summary>
    public async Task<List<string>> StoredValuesAsync(string collection, string field, IReadOnlyList<string> patterns,
        IReadOnlyList<CollectionSchema> schema, AppSettings settings, CancellationToken ct, string? comment = null)
    {
        var values = new List<string>();
        if (patterns.Count == 0) return values;
        try
        {
            var or = new JsonArray();
            foreach (var p in patterns)
                or.Add(new JsonObject { [field] = new JsonObject { ["$regex"] = p, ["$options"] = "i" } });
            var pipeline = new JsonArray
            {
                new JsonObject { ["$match"] = or.Count == 1 ? (JsonObject)or[0]!.DeepClone() : new JsonObject { ["$or"] = or } },
                new JsonObject { ["$group"] = new JsonObject { ["_id"] = "$" + field } },
                new JsonObject { ["$limit"] = 20 }
            };
            var bySchema = schema.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            TenantBinding? Resolve(string c)
            {
                if (!bySchema.TryGetValue(c, out var cs)) return _guard.Default;
                if (string.IsNullOrEmpty(cs.TenantField)) return null;
                var isObjectId = cs.TenantValueType switch { "objectId" => true, "string" => false, _ => _tenant.ValueIsObjectId };
                return new TenantBinding(cs.TenantField, isObjectId);
            }
            var scoped = _user.TenantIsDatabase
                ? _guard.Apply(pipeline, _user.CompanyId, 20, collection, _ => null)
                : _guard.Apply(pipeline, _user.CompanyId, 20, collection, Resolve);
            var found = await _executor.ExecuteAsync(collection, scoped,
                new QueryExecutionOptions { MaxDocuments = 21, TimeoutMs = Math.Min(settings.Query.QueryTimeoutMs, 3000), Comment = comment }, ct);
            // Keep only values that really match one of the group patterns (defence in depth: never put unrelated data
            // into the prompt, whatever the executor returned).
            var checks = patterns.Select(p =>
            {
                try { return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
                catch (ArgumentException) { return null; }
            }).Where(r => r is not null).Select(r => r!).ToList();
            foreach (var row in found.Rows)
                if (row["_id"] is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                    && checks.Any(r => { try { return r.IsMatch(text); } catch (RegexMatchTimeoutException) { return false; } })
                    && !values.Contains(text, StringComparer.Ordinal))
                    values.Add(text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // best effort: fall back to the configured group names
            values.Clear();
        }
        return values;
    }

}
