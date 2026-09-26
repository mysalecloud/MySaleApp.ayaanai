using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

public sealed class PreparedQuery
{
    public required MqlQuery Query { get; init; }
    public required MqlValidationResult Validation { get; init; }
    public JsonArray? ScopedPipeline { get; init; }
    public string? Mql { get; init; }
    public bool IsExecutable => Validation.IsValid && ScopedPipeline is not null;
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

    public MqlValidationContext CreateContext(IReadOnlyList<CollectionSchema> schema, AppSettings settings) => new()
    {
        Collections = schema,
        TenantField = _tenant.FieldName,
        MaxStages = settings.Query.MaxPipelineStages,
        MaxRecords = settings.Query.MaxRecords
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
        MqlDateCoercer.Coerce(pipeline, textFields);
        TenantBinding? Resolve(string collection)
        {
            if (!bySchema.TryGetValue(collection, out var c)) return _guard.Default;
            if (string.IsNullOrEmpty(c.TenantField)) return null; // shared collection
            var isObjectId = c.TenantValueType switch { "objectId" => true, "string" => false, _ => _tenant.ValueIsObjectId };
            return new TenantBinding(c.TenantField, isObjectId);
        }
        // MySaleBooks JWT users without a company claim: the customer database itself is the tenant boundary
        // (resolved from the JWT "dbName" claim), so no company filter is added — limits still apply.
        var scoped = _user.TenantIsDatabase
            ? _guard.Apply(pipeline, _user.CompanyId, settings.Query.MaxRecords, validation.Collection, _ => null)
            : _guard.Apply(pipeline, _user.CompanyId, settings.Query.MaxRecords, validation.Collection, Resolve);
        return new PreparedQuery
        {
            Query = query,
            Validation = validation,
            ScopedPipeline = scoped,
            Mql = MqlFormatter.ToShell(validation.Collection, scoped)
        };
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

    // ------------------------------------------------------------------ ID → name resolution

    private static readonly Regex ObjectIdText = new("^[0-9a-fA-F]{24}$", RegexOptions.Compiled);
    private static readonly string[] PreferredNameFields =
    {
        "Name", "DisplayName", "FullName", "SupplierName", "CustomerName", "PartyName", "LedgerName", "AccountName",
        "VendorName", "ItemName", "ProductName", "EmployeeName", "CompanyName", "BranchName", "WarehouseName",
        "CategoryName", "UserName", "Title", "Description", "Code"
    };

    /// <summary>
    /// When a result column contains only database ids (e.g. "supplierId" or a grouped "_id"), looks the ids up in
    /// the referenced collection and replaces them with that document's name, so users see "Al Noor Trading"
    /// instead of "68b3758…". Uses schema relationships, then collection-name matching; every lookup is read-only,
    /// id-only, tenant-scoped and time-limited. Any failure leaves the result unchanged.
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

        var rows = executed.Rows.Select(r => (JsonObject)r.DeepClone()).ToList();
        var columns = executed.Columns.ToList();
        var notes = new List<string>();
        var lookups = 0;

        foreach (var column in executed.Columns)
        {
            if (lookups >= 8) break;
            var ids = rows.Select(r => r.TryGetPropertyValue(column, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var t) ? t : null).ToList();
            var present = ids.Where(i => i is not null).Select(i => i!).ToList();
            if (present.Count == 0 || present.Count < rows.Count(r => r.TryGetPropertyValue(column, out var v) && v is not null)
                || !present.All(i => ObjectIdText.IsMatch(i))) continue;
            var distinct = present.Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();

            var baseName = BaseName(column == "_id" && groupField is not null ? groupField : column);
            var candidates = CandidateCollections(column, groupField, baseName, rootSchema, bySchema.Values, root);
            Dictionary<string, string>? names = null;
            string? found = null;
            foreach (var (collection, nameField) in candidates.Take(6))
            {
                lookups++;
                names = await LookupNamesAsync(collection, nameField, distinct, bySchema, settings, ct, comment);
                if (names.Count > 0) { found = $"{collection}.{nameField}"; break; }
                if (lookups >= 8) break;
            }
            if (names is null || names.Count == 0) continue;

            var target = column == "_id" ? (baseName.Length > 0 ? Camel(baseName) + "Name" : "name")
                : column.EndsWith("Id", StringComparison.Ordinal) && column.Length > 2 ? column[..^2] + "Name"
                : column.EndsWith("ID", StringComparison.Ordinal) && column.Length > 2 ? column[..^2] + "Name"
                : column;
            if (target != column && columns.Contains(target, StringComparer.Ordinal)) continue; // a name column already exists

            foreach (var row in rows)
            {
                if (!row.TryGetPropertyValue(column, out var v) || v is not JsonValue jv || !jv.TryGetValue<string>(out var id)) continue;
                var label = names.TryGetValue(id.ToLowerInvariant(), out var n) ? n : id;
                if (target == column) { row[column] = label; continue; }
                // Keep the column order: rebuild the row with the name in place of the id.
                var ordered = new List<KeyValuePair<string, JsonNode?>>();
                foreach (var (k, value) in row.ToList())
                    ordered.Add(k == column ? new KeyValuePair<string, JsonNode?>(target, label) : new KeyValuePair<string, JsonNode?>(k, value?.DeepClone()));
                row.Clear();
                foreach (var (k, value) in ordered) row[k] = value;
            }
            var index = columns.IndexOf(column);
            if (index >= 0) columns[index] = target;
            notes.Add($"{column} → {found} ({names.Count}/{distinct.Count} resolved)");
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

    private async Task<Dictionary<string, string>> LookupNamesAsync(string collection, string nameField, List<string> ids,
        IReadOnlyDictionary<string, CollectionSchema> bySchema, AppSettings settings, CancellationToken ct, string? comment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var values = new JsonArray();
            foreach (var id in ids)
            {
                values.Add(new JsonObject { ["$oid"] = id.ToLowerInvariant() });
                values.Add(JsonValue.Create(id)); // some collections store ids as strings
            }
            var pipeline = new JsonArray
            {
                new JsonObject { ["$match"] = new JsonObject { ["_id"] = new JsonObject { ["$in"] = values } } },
                new JsonObject { ["$project"] = new JsonObject { ["_id"] = 1, [nameField] = 1 } }
            };
            TenantBinding? Resolve(string c)
            {
                if (!bySchema.TryGetValue(c, out var cs)) return _guard.Default;
                if (string.IsNullOrEmpty(cs.TenantField)) return null;
                var isObjectId = cs.TenantValueType switch { "objectId" => true, "string" => false, _ => _tenant.ValueIsObjectId };
                return new TenantBinding(cs.TenantField, isObjectId);
            }
            var scoped = _user.TenantIsDatabase
                ? _guard.Apply(pipeline, _user.CompanyId, ids.Count, collection, _ => null)
                : _guard.Apply(pipeline, _user.CompanyId, ids.Count, collection, Resolve);
            var found = await _executor.ExecuteAsync(collection, scoped,
                new QueryExecutionOptions { MaxDocuments = ids.Count + 1, TimeoutMs = Math.Min(settings.Query.QueryTimeoutMs, 3000), Comment = comment }, ct);
            foreach (var doc in found.Rows)
            {
                var id = doc["_id"] is JsonValue iv && iv.TryGetValue<string>(out var s) ? s : doc["_id"]?.ToString();
                var name = doc[nameField] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : doc[nameField]?.ToString();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) result[id.ToLowerInvariant()] = name.Trim();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result.Clear(); // name lookup is best-effort
        }
        return result;
    }

    private IEnumerable<(string Collection, string NameField)> CandidateCollections(string column, string? groupField, string baseName,
        CollectionSchema? rootSchema, IEnumerable<CollectionSchema> all, string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = all.Where(cs => !string.Equals(cs.Name, root, StringComparison.OrdinalIgnoreCase) || (column == "_id" && groupField is null)).ToList();

        // 1. Schema relationships ("Suppliers._id") of the grouped / projected field
        foreach (var name in new[] { groupField, column }.Where(n => !string.IsNullOrEmpty(n)))
        {
            var rel = rootSchema?.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Relationship;
            var target = rel?.Split('.')[0].Trim();
            if (!string.IsNullOrEmpty(target) && list.FirstOrDefault(cs => string.Equals(cs.Name, target, StringComparison.OrdinalIgnoreCase)) is { } rc
                && NameField(rc) is { } nf && seen.Add(rc.Name))
                yield return (rc.Name, nf);
        }

        // 2. Collection name matches the field ("supplierId" → Suppliers, SupplierMaster; "partyId" → Parties)
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
                if (NameField(c) is { } nf && seen.Add(c.Name)) yield return (c.Name, nf);
        }

        // 3. Master-data collections with a name field (bounded by the caller)
        var masters = list.Where(cs => NameField(cs) is not null)
            .OrderByDescending(cs => IsMasterData(cs.Name)).ThenBy(cs => cs.DocumentCount ?? long.MaxValue).ToList();
        foreach (var c in masters)
            if (seen.Add(c.Name)) yield return (c.Name, NameField(c)!);
    }

    private string? NameField(CollectionSchema c)
    {
        var fields = c.Fields.Where(f => !f.Hidden && f.Type is "string" or "String"
                                         && !string.Equals(f.Name, _tenant.FieldName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var p in PreferredNameFields)
            if (fields.FirstOrDefault(f => string.Equals(f.Name, p, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit.Name;
        return fields.FirstOrDefault(f => f.Name.EndsWith("Name", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains('.'))?.Name;
    }

    private static bool IsMasterData(string name)
    {
        var n = name.ToLowerInvariant();
        return new[] { "supplier", "customer", "vendor", "part", "ledger", "account", "item", "product", "employee", "contact", "branch", "warehouse" }
            .Any(n.Contains);
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

    private static string BaseName(string field)
    {
        var last = field.Split('.').Last();
        if (last.Equals("_id", StringComparison.Ordinal)) return string.Empty;
        foreach (var suffix in new[] { "_id", "Id", "ID", "Ids" })
            if (last.Length > suffix.Length && last.EndsWith(suffix, StringComparison.Ordinal)) return last[..^suffix.Length].TrimEnd('_');
        return last;
    }

    private static string Singular(string word)
    {
        var w = word.ToLowerInvariant().Replace("_", string.Empty);
        if (w.EndsWith("ies") && w.Length > 4) return w[..^3] + "y";
        if (w.EndsWith("ses") || w.EndsWith("xes")) return w[..^2];
        if (w.EndsWith('s') && !w.EndsWith("ss") && w.Length > 3) return w[..^1];
        return w;
    }

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
