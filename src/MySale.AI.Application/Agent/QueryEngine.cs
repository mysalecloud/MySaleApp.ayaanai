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
public sealed class QueryEngine
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
        var names = await LookupNamesAsync(master.Name, nameField, KeyFields(master, "_id", ids).Concat(new[] { "id", "Id" })
                .Where(k => k == "_id" || master.Fields.Any(f => string.Equals(f.Name, k, StringComparison.Ordinal))).Distinct().ToList(),
            ids, bySchema, settings, ct, null);
        return names.TryGetValue(storeId.ToLowerInvariant(), out var name) ? (true, name) : (false, null);
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

    // ------------------------------------------------------------------ ID → record mapping

    private static readonly Regex ObjectIdText = new("^[0-9a-fA-F]{24}$", RegexOptions.Compiled);
    private const int MaxLookups = 24;
    private const int MaxIdColumns = 12;

    /// <summary>Fields that are always a record's own display name.</summary>
    private static readonly string[] GenericNameFields = { "Name", "DisplayName", "FullName", "Title" };
    /// <summary>Document numbers — the display value of transaction records (invoices, orders, vouchers …).</summary>
    private static readonly string[] DocumentNumberFields =
    {
        "InvoiceNo", "InvoiceNumber", "BillNo", "BillNumber", "VoucherNo", "VoucherNumber", "DocumentNo", "DocumentNumber",
        "OrderNo", "OrderNumber", "PurchaseNo", "ReceiptNo", "ReferenceNo", "RefNo", "Number"
    };
    private static readonly string[] PreferredNameFields =
    {
        "SupplierName", "CustomerName", "PartyName", "LedgerName", "AccountName", "VendorName", "ItemName", "ProductName",
        "EmployeeName", "SalesmanName", "CompanyName", "BranchName", "WarehouseName", "CategoryName", "UserName", "Code", "Description"
    };
    /// <summary>Alternative key fields a master record may be referenced by (GUIDs), besides _id.</summary>
    private static readonly string[] AlternateKeyFields = { "Guid", "GUID", "Id", "ID", "Uuid", "UUID" };

    public static bool IsObjectIdText(string value) => ObjectIdText.IsMatch(value);
    public static bool IsGuidText(string value) => value.Length is 32 or 36 or 38 && Guid.TryParse(value, out _);
    private static bool IsIdText(string value) => IsObjectIdText(value) || IsGuidText(value);

    /// <summary>
    /// Maps every ID/GUID column of the result (CustomerId, ProductId, BranchId, SalesmanId, grouped "_id" …) to the
    /// referenced record's display value (name or document number), so users see "ABC Trading" instead of "680abc…".
    /// <list type="bullet">
    /// <item>IDs are preserved: the row keeps the id field; a "…Name" field is added next to it and shown instead of the
    /// id in the result columns (tables, charts, exports). The AI answer receives both and names the record.</item>
    /// <item>No guessing: a mapping is used only when the id is found in the referenced collection — by the schema
    /// relationship (Field → Collection._id / Collection.Guid), by the collection named after the field (CustomerId →
    /// Customers) or, as a last resort, in a master-data collection where (almost) all ids of the column exist. Ids that
    /// cannot be verified stay as they are; no name is invented.</item>
    /// <item>Every lookup is read-only, id-only, tenant-scoped and time-limited; at most 24 lookups per answer. Any
    /// failure leaves the result unchanged.</item>
    /// </list>
    /// </summary>
    public async Task<ReferenceResolution> ResolveReferencesAsync(PreparedQuery prepared, ExecutedQuery executed,
        IReadOnlyList<CollectionSchema> schema, AppSettings settings, CancellationToken ct, string? comment = null)
    {
        var sw = Stopwatch.StartNew();
        var unchanged = new ReferenceResolution { Result = executed };
        if (executed.Rows.Count == 0 || executed.Columns.Count == 0) return unchanged;

        var bySchema = schema.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var root = prepared.Validation.Collection ?? prepared.Query.Collection;
        bySchema.TryGetValue(root, out var rootSchema);
        var groupField = GroupKeyField(prepared.Validation.Pipeline);
        var lookupAliases = LookupAliases(prepared.Validation.Pipeline);

        var rows = executed.Rows.Select(r => (JsonObject)r.DeepClone()).ToList();
        var columns = executed.Columns.ToList();
        var notes = new List<string>();
        var lookups = 0;
        var mappedColumns = 0;

        foreach (var column in executed.Columns)
        {
            if (lookups >= MaxLookups || mappedColumns >= MaxIdColumns) break;
            var values = rows.Select(r => r.TryGetPropertyValue(column, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var t) ? t : null).ToList();
            var present = values.Where(i => !string.IsNullOrEmpty(i)).Select(i => i!).ToList();
            var nonNull = rows.Count(r => r.TryGetPropertyValue(column, out var v) && v is not null && v.GetValueKind() != System.Text.Json.JsonValueKind.Null);
            if (present.Count == 0 || present.Count < nonNull || !present.All(IsIdText)) continue;
            var distinct = present.Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();

            var sourceField = column == "_id" && groupField is not null ? groupField : column;
            var baseName = BaseName(sourceField);
            var target = DisplayColumn(column, baseName);
            if (columns.Contains(target, StringComparer.Ordinal) || rows.Any(r => r.ContainsKey(target))) continue; // already named
            // The record's own _id in a plain list (e.g. customers) already has its name column next to it.
            if (column == "_id" && groupField is null && rootSchema is not null && NameField(rootSchema) is { } ownName
                && rows.Any(r => r.ContainsKey(ownName))) continue;

            Dictionary<string, string>? names = null;
            string? found = null;
            foreach (var candidate in CandidateCollections(column, sourceField, baseName, rootSchema, bySchema.Values, root, lookupAliases).Take(6))
            {
                if (lookups >= MaxLookups) break;
                lookups++;
                var keyFields = KeyFields(candidate.Schema, candidate.ForeignField, distinct);
                var result = await LookupNamesAsync(candidate.Schema.Name, candidate.NameField, keyFields, distinct, bySchema, settings, ct, comment);
                // Verified = the ids exist in that collection. For the last-resort master-data search require (almost) all.
                var enough = candidate.Verified ? result.Count > 0 : result.Count >= Math.Max(1, (int)Math.Ceiling(distinct.Count * 0.8));
                if (enough) { names = result; found = $"{candidate.Schema.Name}.{candidate.NameField}"; break; }
            }
            if (names is null || names.Count == 0) continue;

            mappedColumns++;
            foreach (var row in rows)
            {
                if (!row.TryGetPropertyValue(column, out var v) || v is not JsonValue jv || !jv.TryGetValue<string>(out var id)) continue;
                if (!names.TryGetValue(id.ToLowerInvariant(), out var label)) continue; // unverified id: keep the id, no name
                // Name first, the id right after it (kept for relationships, shown as secondary information).
                var ordered = new List<KeyValuePair<string, JsonNode?>>();
                foreach (var (k, value) in row.ToList())
                {
                    if (k == column) ordered.Add(new KeyValuePair<string, JsonNode?>(target, label));
                    ordered.Add(new KeyValuePair<string, JsonNode?>(k, value?.DeepClone()));
                }
                row.Clear();
                foreach (var (k, value) in ordered) row[k] = value;
            }
            // Tables, charts and exports show the name instead of the id; unresolved rows fall back to the id.
            foreach (var row in rows)
                if (!row.ContainsKey(target) && row.TryGetPropertyValue(column, out var idNode))
                {
                    var ordered = row.ToList().Select(kv => kv.Key == column
                        ? new[] { new KeyValuePair<string, JsonNode?>(target, idNode?.DeepClone()), new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value?.DeepClone()) }
                        : new[] { new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value?.DeepClone()) }).SelectMany(x => x).ToList();
                    row.Clear();
                    foreach (var (k, value) in ordered) row[k] = value;
                }
            var index = columns.IndexOf(column);
            if (index >= 0) columns[index] = target;
            notes.Add($"{column} → {found} ({names.Count}/{distinct.Count} resolved; id kept)");
        }

        sw.Stop();
        return notes.Count == 0
            ? new ReferenceResolution { Result = executed, ElapsedMs = sw.ElapsedMilliseconds }
            : new ReferenceResolution
            {
                Changed = true,
                Notes = notes,
                ElapsedMs = sw.ElapsedMilliseconds,
                Result = new ExecutedQuery { Rows = rows, Columns = columns, Truncated = executed.Truncated, ElapsedMs = executed.ElapsedMs }
            };
    }

    /// <summary>"customerId" → "customerName", "BranchID" → "BranchName", grouped "_id" of SupplierId → "supplierName".</summary>
    private static string DisplayColumn(string column, string baseName)
    {
        if (column == "_id") return baseName.Length > 0 ? Camel(baseName) + "Name" : "name";
        var last = column.Split('.').Last();
        var prefix = column[..^last.Length];
        foreach (var suffix in new[] { "_id", "Guid", "GUID", "Ids", "Id", "ID" })
            if (last.Length > suffix.Length && last.EndsWith(suffix, StringComparison.Ordinal))
                return prefix + last[..^suffix.Length].TrimEnd('_') + "Name";
        return column + "Name";
    }

    /// <summary>Key fields to match: _id, the relationship's target field, and GUID-style key fields the collection has.</summary>
    private static List<string> KeyFields(CollectionSchema target, string? foreignField, List<string> ids)
    {
        var keys = new List<string>();
        if (!string.IsNullOrEmpty(foreignField)) keys.Add(foreignField);
        if (!keys.Contains("_id")) keys.Add("_id");
        if (ids.Any(IsGuidText))
        {
            var singular = Singular(target.Name);
            foreach (var f in target.Fields.Where(f => !f.Hidden && !f.Name.Contains('.')))
                if (AlternateKeyFields.Contains(f.Name, StringComparer.Ordinal)
                    || string.Equals(f.Name, singular + "guid", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name, singular + "id", StringComparison.OrdinalIgnoreCase))
                    if (!keys.Contains(f.Name)) keys.Add(f.Name);
        }
        return keys.Take(4).ToList();
    }

    private async Task<Dictionary<string, string>> LookupNamesAsync(string collection, string nameField, List<string> keyFields, List<string> ids,
        IReadOnlyDictionary<string, CollectionSchema> bySchema, AppSettings settings, CancellationToken ct, string? comment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var values = new JsonArray();
            foreach (var id in ids)
            {
                if (IsObjectIdText(id)) values.Add(new JsonObject { ["$oid"] = id.ToLowerInvariant() });
                values.Add(JsonValue.Create(id)); // ids stored as strings (GUIDs, string ObjectIds)
                if (IsGuidText(id) && Guid.TryParse(id, out var g))
                {
                    var canonical = g.ToString("D");
                    if (!string.Equals(canonical, id, StringComparison.OrdinalIgnoreCase)) values.Add(JsonValue.Create(canonical));
                }
            }
            var or = new JsonArray();
            foreach (var key in keyFields)
                or.Add(new JsonObject { [key] = new JsonObject { ["$in"] = values.DeepClone() } });
            var project = new JsonObject { ["_id"] = 1, [nameField] = 1 };
            foreach (var key in keyFields.Where(k => k != "_id")) project[key] = 1;
            var pipeline = new JsonArray
            {
                new JsonObject { ["$match"] = or.Count == 1 ? (JsonObject)or[0]!.DeepClone() : new JsonObject { ["$or"] = or } },
                new JsonObject { ["$project"] = project }
            };
            TenantBinding? Resolve(string c)
            {
                if (!bySchema.TryGetValue(c, out var cs)) return _guard.Default;
                if (string.IsNullOrEmpty(cs.TenantField)) return null;
                var isObjectId = cs.TenantValueType switch { "objectId" => true, "string" => false, _ => _tenant.ValueIsObjectId };
                return new TenantBinding(cs.TenantField, isObjectId);
            }
            var scoped = _user.TenantIsDatabase
                ? _guard.Apply(pipeline, _user.CompanyId, ids.Count * 2, collection, _ => null)
                : _guard.Apply(pipeline, _user.CompanyId, ids.Count * 2, collection, Resolve);
            var found = await _executor.ExecuteAsync(collection, scoped,
                new QueryExecutionOptions { MaxDocuments = ids.Count * 2 + 1, TimeoutMs = Math.Min(settings.Query.QueryTimeoutMs, 3000), Comment = comment }, ct);

            var wanted = new HashSet<string>(ids.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            foreach (var doc in found.Rows)
            {
                var name = doc[nameField] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : doc[nameField]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                foreach (var key in keyFields)
                {
                    var raw = doc[key] is JsonValue kv && kv.TryGetValue<string>(out var s) ? s : doc[key]?.ToString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var normalized = Normalize(raw);
                    if (!wanted.Contains(normalized)) continue; // only exact id matches are accepted
                    foreach (var id in ids.Where(i => Normalize(i) == normalized)) result[id.ToLowerInvariant()] = name.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result.Clear(); // mapping is best-effort
        }
        return result;

        static string Normalize(string id) => IsGuidText(id) && Guid.TryParse(id, out var g) ? g.ToString("N") : id.Trim().ToLowerInvariant();
    }

    private sealed record Candidate(CollectionSchema Schema, string NameField, string? ForeignField, bool Verified);

    private IEnumerable<Candidate> CandidateCollections(string column, string sourceField, string baseName,
        CollectionSchema? rootSchema, IEnumerable<CollectionSchema> all, string root, IReadOnlyDictionary<string, (string From, string ForeignField)> lookupAliases)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = all.Where(cs => !string.Equals(cs.Name, root, StringComparison.OrdinalIgnoreCase) || (column == "_id" && sourceField == "_id")).ToList();
        CollectionSchema? Find(string? name) => string.IsNullOrEmpty(name) ? null
            : all.FirstOrDefault(cs => string.Equals(cs.Name, name, StringComparison.OrdinalIgnoreCase));

        // 1. Schema relationship of the source field ("CustomerId" → "Customers._id" / "Customers.Guid")
        foreach (var name in new[] { sourceField, column }.Where(n => !string.IsNullOrEmpty(n)).Distinct())
        {
            var rel = rootSchema?.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Relationship;
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var parts = rel.Split('.', 2);
            if (Find(parts[0].Trim()) is { } rc && NameField(rc) is { } nf && seen.Add(rc.Name))
                yield return new Candidate(rc, nf, parts.Length > 1 ? parts[1].Trim() : "_id", true);
        }

        // 2. A $lookup in the query itself (localField → from.foreignField)
        if (lookupAliases.TryGetValue(sourceField, out var lk) && Find(lk.From) is { } lc && NameField(lc) is { } lnf && seen.Add(lc.Name))
            yield return new Candidate(lc, lnf, lk.ForeignField, true);

        // 3. Collection named after the field ("CustomerId" → Customers / CustomerMaster; "SalesmanId" → Salesmen)
        if (baseName.Length >= 3)
        {
            var key = Singular(baseName);
            var matches = list
                .Select(cs => (Schema: cs, Key: Singular(cs.Name.ToLowerInvariant())))
                .Where(x => x.Key == key || x.Key.StartsWith(key, StringComparison.Ordinal) || x.Key.EndsWith(key, StringComparison.Ordinal)
                            || (key.EndsWith(x.Key, StringComparison.Ordinal) && x.Key.Length >= 4))
                .OrderBy(x => x.Key == key ? 0 : 1).ThenBy(x => x.Key.Length)
                .Select(x => x.Schema)
                .ToList();
            foreach (var c in matches)
                if (NameField(c) is { } nf && seen.Add(c.Name)) yield return new Candidate(c, nf, "_id", true);
        }

        // 4. Last resort: master-data collections (accepted only when ~all ids of the column exist there)
        var masters = list.Where(cs => IsMasterData(cs.Name) && NameField(cs) is not null)
            .OrderBy(cs => cs.DocumentCount ?? long.MaxValue).ToList();
        foreach (var c in masters)
            if (seen.Add(c.Name)) yield return new Candidate(c, NameField(c)!, "_id", false);
    }

    /// <summary>Display field of a record: its own name, then "{Entity}Name", then a document number, then other name fields.</summary>
    private string? NameField(CollectionSchema c)
    {
        var fields = c.Fields.Where(f => !f.Hidden && f.Type is "string" or "String" && !f.Name.Contains('.')
                                         && !string.Equals(f.Name, _tenant.FieldName, StringComparison.OrdinalIgnoreCase)
                                         && !ActivityTracking.ActivityRedactor.IsSensitiveField(f.Name)).ToList();
        FieldSchema? Pick(IEnumerable<string> names)
        {
            foreach (var p in names)
                if (fields.FirstOrDefault(f => string.Equals(f.Name, p, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
            return null;
        }
        var singular = Singular(c.Name);
        var own = fields.FirstOrDefault(f => string.Equals(f.Name, singular + "name", StringComparison.OrdinalIgnoreCase));
        return (Pick(GenericNameFields) ?? own ?? Pick(DocumentNumberFields) ?? Pick(PreferredNameFields)
                ?? fields.FirstOrDefault(f => f.Name.EndsWith("Name", StringComparison.OrdinalIgnoreCase)))?.Name;
    }

    private static bool IsMasterData(string name)
    {
        var n = name.ToLowerInvariant();
        return new[]
        {
            "supplier", "customer", "vendor", "part", "ledger", "account", "item", "product", "employee", "contact", "branch",
            "warehouse", "categor", "salesm", "salesperson", "user", "unit", "brand", "paymentterm", "tax", "store", "location", "department"
        }.Any(n.Contains);
    }

    /// <summary>Field used as the $group key ("$SupplierId" → "SupplierId"), if the pipeline groups by a single field.</summary>
    private static string? GroupKeyField(JsonArray? pipeline)
    {
        if (pipeline is null) return null;
        foreach (var stage in pipeline)
            if (stage is JsonObject o && o["$group"] is JsonObject g && g["_id"] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('$') && !s.StartsWith("$$"))
                return s[1..];
        foreach (var stage in pipeline)
            if (stage is JsonObject o && o["$sortByCount"] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('$'))
                return s[1..];
        return null;
    }

    /// <summary>$lookup stages of the query: localField → (from, foreignField). A verified relationship for mapping.</summary>
    private static Dictionary<string, (string From, string ForeignField)> LookupAliases(JsonArray? pipeline)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (pipeline is null) return map;
        foreach (var stage in pipeline)
            if (stage is JsonObject o && o["$lookup"] is JsonObject l
                && l["from"] is JsonValue f && f.TryGetValue<string>(out var from)
                && l["localField"] is JsonValue lf && lf.TryGetValue<string>(out var local)
                && l["foreignField"] is JsonValue ff && ff.TryGetValue<string>(out var foreign))
                map[local] = (from, foreign);
        return map;
    }

    private static string BaseName(string field)
    {
        var last = field.Split('.').Last();
        if (last.Equals("_id", StringComparison.Ordinal)) return string.Empty;
        foreach (var suffix in new[] { "_id", "Guid", "GUID", "Ids", "Id", "ID" })
            if (last.Length > suffix.Length && last.EndsWith(suffix, StringComparison.Ordinal)) return last[..^suffix.Length].TrimEnd('_');
        return last;
    }

    private static string Singular(string word)
    {
        var w = word.ToLowerInvariant().Replace("_", string.Empty);
        if (w.EndsWith("men") && w.Length > 4) return w[..^3] + "man";
        if (w.EndsWith("ies") && w.Length > 4) return w[..^3] + "y";
        if (w.EndsWith("ches") || w.EndsWith("shes")) return w[..^2];   // branches → branch
        if (w.EndsWith("ses") || w.EndsWith("xes")) return w[..^2];
        if (w.EndsWith('s') && !w.EndsWith("ss") && w.Length > 3) return w[..^1];
        return w;
    }

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
