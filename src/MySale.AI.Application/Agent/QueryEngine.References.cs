using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Common;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

/// <summary>What happened to one id column of a result (or one reference in the diagnostics).</summary>
public sealed class ReferenceDiagnostic
{
    public string Column { get; init; } = string.Empty;
    /// <summary>Entity key ("supplier", "item" …) or the generic base name for non-MySaleBooks databases.</summary>
    public string Entity { get; init; } = string.Empty;
    /// <summary>Stored field the ids come from ("Purchase.ledgerId"), when the pipeline shows it.</summary>
    public string? Source { get; init; }
    /// <summary>verified reference | column name | query $lookup | schema relationship | generic | none</summary>
    public string Method { get; init; } = "none";
    /// <summary>Master collection and name field used ("Ledger.ledgerName").</summary>
    public string? Master { get; init; }
    public string? DisplayColumn { get; init; }
    public int Checked { get; init; }
    public int Resolved { get; init; }
    public int Empty { get; init; }
    public int Unresolved { get; init; }
    /// <summary>Ids read successfully from an existing master that have no record there (deleted / broken reference).</summary>
    public int Orphans { get; init; }
    public List<string> OrphanIds { get; init; } = new();
    public bool LookupFailed { get; init; }
    public int DuplicateNames { get; init; }
    /// <summary>The id column was hidden from users (kept in the rows for drill-down / follow-ups).</summary>
    public bool Hidden { get; init; }
    public string? Note { get; init; }

    public string Summary() =>
        $"{Column} → {Entity}{(Master is null ? string.Empty : " via " + Master)} [{Method}]: {Resolved}/{Checked} resolved" +
        (Empty > 0 ? $", {Empty} empty" : string.Empty) +
        (Orphans > 0 ? $", {Orphans} orphan" : string.Empty) +
        (LookupFailed ? ", lookup failed" : string.Empty) +
        (DuplicateNames > 0 ? $", {DuplicateNames} duplicate name(s) disambiguated" : string.Empty) +
        (DisplayColumn is null ? string.Empty : $"; shown as {DisplayColumn}") + "; id kept" +
        (Note is null ? string.Empty : " — " + Note);
}

/// <summary>Names of entity ids read by <see cref="QueryEngine.ResolveEntityNamesAsync"/> (server reports).</summary>
public sealed class EntityNames
{
    public EntityType? Entity { get; init; }
    public Dictionary<string, string> Names { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Orphans { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Failed { get; init; }

    /// <summary>The name, "No warehouse" for an empty / "0" id, "Unknown warehouse" for an id without a record.</summary>
    public string Label(string? id)
    {
        if (EntityCatalog.IsNoneValue(id)) return Entity?.NoneLabel ?? "Not set";
        return Names.TryGetValue(id!.Trim(), out var name) ? name : Entity?.UnknownLabel ?? "Unknown record";
    }
}

/// <summary>Data-integrity check of one verified reference (collection.field → master) over the company database.</summary>
public sealed class EntityReferenceCheck
{
    public string Entity { get; init; } = string.Empty;
    public string Reference { get; init; } = string.Empty;
    public string Master { get; init; } = string.Empty;
    public string Status { get; set; } = "ok";
    public int Checked { get; set; }
    public int Resolved { get; set; }
    public int Unresolved { get; set; }
    public int Orphans { get; set; }
    public List<string> SampleOrphanIds { get; set; } = new();
    public bool Capped { get; set; }
    public string? Note { get; set; }
}

public sealed partial class QueryEngine
{
    // ------------------------------------------------------------------ ID → name mapping (centralised)

    private static readonly Regex ObjectIdText = new("^[0-9a-fA-F]{24}$", RegexOptions.Compiled);
    private const int MaxLookups = 24;
    private const int MaxIdColumns = 12;
    private const int MaxIdsPerLookup = 500;

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
    public static bool IsIdText(string value) => IsObjectIdText(value) || IsGuidText(value);

    /// <summary>A resolution target: the master collection, its name field and how users call the record.</summary>
    private sealed record Target(
        CollectionSchema Schema, string NameField, IReadOnlyList<string> KeyFields, IReadOnlyList<string> CodeFields,
        string Singular, string DisplayColumn, string Method, bool Verified, bool IntegerKey, EntityType? Entity);

    /// <summary>
    /// Replaces every ID/GUID column of a result (grouped "_id", customerId, itemId, "supplierId": "$_id" …) by the
    /// referenced record's business name or document number before the answer is written — tables, charts, KPI cards,
    /// rankings, exports and the answer model all see "ABC Trading", never "680abc…".
    /// <list type="bullet">
    /// <item>Ids stay in the rows (grouping, drill-down, follow-ups); only the name column is listed in <c>Columns</c>.</item>
    /// <item>MySaleBooks: the entity comes from the verified <see cref="EntityCatalog"/> reference of the field the
    /// pipeline groups on (<see cref="FieldLineage"/>), e.g. Purchase.ledgerId → supplier → Ledger.ledgerName; then from the
    /// column name (supplierId → supplier). Other databases: schema relationship, $lookup of the query, collection named
    /// after the field, then (≥ 80 % verified) a master-data collection.</item>
    /// <item>No guessing: a name is shown only for an id found in the master. Other ids get "Unknown supplier"
    /// (orphan = the master was read and has no such record — logged for data-integrity review), "0" / empty ids get
    /// "No supplier". Two records with the same name are told apart ("ABC Trading (SUP-01)").</item>
    /// <item>Every lookup is read-only, id-only, tenant-scoped and time-limited; at most 24 lookups per answer.</item>
    /// </list>
    /// </summary>
    public async Task<ReferenceResolution> ResolveReferencesAsync(PreparedQuery prepared, ExecutedQuery executed,
        IReadOnlyList<CollectionSchema> schema, AppSettings settings, CancellationToken ct, string? comment = null)
    {
        var sw = Stopwatch.StartNew();
        if (executed.Rows.Count == 0 || executed.Columns.Count == 0) return new ReferenceResolution { Result = executed };

        var bySchema = BySchema(schema);
        var root = prepared.Validation.Collection ?? prepared.Query.Collection;
        bySchema.TryGetValue(root, out var rootSchema);
        var pipeline = prepared.Validation.Pipeline;
        var lineage = FieldLineage.Trace(root, pipeline);
        var pipelineJson = pipeline?.ToJsonString();
        var groupField = GroupKeyField(pipeline);
        var lookupAliases = LookupAliases(pipeline);
        var msb = EntityCatalog.Applies(schema);

        var rows = executed.Rows.Select(r => (JsonObject)r.DeepClone()).ToList();
        var columns = executed.Columns.ToList();
        var notes = new List<string>();
        var diagnostics = new List<ReferenceDiagnostic>();
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lookups = 0;
        var mapped = 0;

        foreach (var column in executed.Columns)
        {
            if (mapped >= MaxIdColumns) break;
            var cells = rows.Select(r => r.TryGetPropertyValue(column, out var v) ? v : null).ToList();
            var ids = cells.Select(Text).Where(t => t is not null && IsIdText(t)).Select(t => t!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var source = lineage.TryGetValue(column, out var traced) ? traced : FieldLineage.Of(root, pipeline, column);
            var reference = msb ? EntityCatalog.Reference(source?.Collection, source?.Field) : null;
            var entity = reference is null ? null : EntityCatalog.Types[reference.Entity];

            // Integer keys (account group numbers) are ids too when the field is a verified reference.
            var integerIds = entity is { IntegerKey: true }
                ? cells.Select(c => c is JsonValue v && JsonHelpers.TryGetNumber(v, out var d) && d == Math.Floor(d) ? ((long)d).ToString(CultureInfo.InvariantCulture) : null)
                    .Where(x => x is not null && x != "0").Select(x => x!).Distinct().ToList()
                : new List<string>();
            if (ids.Count == 0 && integerIds.Count == 0) continue;
            if (integerIds.Count > 0) ids = integerIds;

            // Only an id column: every non-empty value must be an id (or the MySaleBooks "0" for "none").
            var nonEmpty = cells.Where(c => c is not null && c.GetValueKind() != JsonValueKind.Null).ToList();
            var noneCount = nonEmpty.Count(c => Text(c) is { } t && EntityCatalog.IsNoneValue(t));
            if (integerIds.Count == 0 && nonEmpty.Any(c => Text(c) is not { } t || (!IsIdText(t.Trim()) && !EntityCatalog.IsNoneValue(t)))) continue;
            ids = ids.Take(MaxIdsPerLookup).ToList();

            if (entity?.Key == "ledger")
            {
                // A ledger is shown as customer / supplier when the query says so (Sundry Debtors / Creditors filter, or
                // the alias the query gave the id: "customerId", "supplierId").
                var alias = EntityCatalog.ByName(column);
                entity = alias is { Key: "customer" or "supplier" } ? alias : EntityCatalog.LedgerRole(source?.Collection == root ? root : null, pipelineJson);
            }
            var sourceField = source?.Field ?? (column == "_id" ? groupField ?? column : column);
            var method = reference is not null ? "verified reference" : "none";
            if (entity is null && msb && (EntityCatalog.ByName(column) ?? EntityCatalog.ByName(sourceField)) is { } byName)
            {
                entity = byName.Key == "ledger" ? EntityCatalog.LedgerRole(root, pipelineJson) : byName;
                method = "column name";
            }

            // The record's own _id in a plain list (e.g. customers with their name): nothing to look up — hide the id.
            // (A list without its name column still hides the _id: its other columns — number, date, amount — describe it.)
            var ownList = column == "_id" && groupField is null && source is { Field: "_id" } && source.Collection == root;
            if (ownList && columns.Count > 1)
            {
                var ownName = rootSchema is null ? null : msb ? MasterName(rootSchema.Name) : NameField(rootSchema);
                var named = ownName is not null && rows.Any(r => r.ContainsKey(ownName));
                if (columns.Remove(column))
                    diagnostics.Add(new ReferenceDiagnostic { Column = column, Entity = Singular(root), Source = $"{root}._id", Method = "own record", Checked = ids.Count, Hidden = true,
                        Note = named ? "record list: the name column is shown" : "record list: id hidden" });
                continue;
            }

            // Candidate masters to read.
            var targets = new List<Target>();
            if (entity is not null)
            {
                if (entity.Collection is not null && bySchema.TryGetValue(entity.Collection, out var master) && entity.NameField is not null)
                    targets.Add(new Target(master, entity.NameField, new[] { entity.KeyField }, entity.CodeFields.Where(f => master.Fields.Any(x => x.Name == f)).ToList(),
                        entity.Singular, entity.DisplayColumn, method, true, entity.IntegerKey, entity));
                if (entity.Key == "document")
                    foreach (var (c, n) in EntityCatalog.DocumentMasters.Skip(1))
                        if (bySchema.TryGetValue(c, out var dm)) targets.Add(new Target(dm, n, new[] { "_id" }, Array.Empty<string>(), "document", entity.DisplayColumn, method, true, false, entity));
            }
            else if (!msb || integerIds.Count == 0)
            {
                var baseName = BaseName(sourceField);
                foreach (var c in CandidateCollections(column, sourceField, baseName, rootSchema, bySchema.Values, root, lookupAliases).Take(6))
                    targets.Add(new Target(c.Schema, c.NameField, KeyFields(c.Schema, c.ForeignField, ids), Array.Empty<string>(),
                        HumanSingular(baseName.Length > 0 ? baseName : Singular(c.Schema.Name)), DisplayColumn(column, baseName), c.Method, c.Verified, false, null));
            }

            // An existing column of the result already holds the names (e.g. the query looked them up): only hide the id.
            var names = new Dictionary<string, (string Name, string? Code)>(StringComparer.OrdinalIgnoreCase);
            Target? used = null;
            var failed = false;
            var masterRead = false;
            foreach (var target in targets)
            {
                if (lookups >= MaxLookups) break;
                lookups++;
                var remaining = ids.Where(i => !names.ContainsKey(i)).ToList();
                if (remaining.Count == 0) break;
                var (found, ok) = await LookupAsync(target.Schema.Name, target.NameField, target.KeyFields, target.CodeFields, remaining, target.IntegerKey, bySchema, settings, ct, comment);
                if (!ok) { failed = true; continue; }
                var enough = target.Verified ? found.Count > 0 || target.Entity is not null : found.Count >= Math.Max(1, (int)Math.Ceiling(ids.Count * 0.8));
                if (target.Entity is not null) masterRead = true;
                if (!enough) continue;
                foreach (var (k, v) in found) names[k] = v;
                used ??= target;
                if (target.Entity is null) { masterRead = true; break; }   // generic: first verified collection wins
                if (names.Count >= ids.Count) break;
            }

            var displayEntity = entity ?? used?.Entity;
            var singular = used?.Singular ?? entity?.Singular ?? HumanSingular(BaseName(sourceField).Length > 0 ? BaseName(sourceField) : "record");
            var display = used?.DisplayColumn ?? entity?.DisplayColumn ?? DisplayColumn(column, BaseName(sourceField));
            var unknown = displayEntity?.UnknownLabel ?? "Unknown " + singular;
            var none = displayEntity?.NoneLabel ?? "No " + singular;
            var masterLabel = used is null ? (entity?.Collection is null ? null : $"{entity.Collection}.{entity.NameField}") : $"{used.Schema.Name}.{used.NameField}";

            if (names.Count == 0 && entity is null)
            {
                // No verified master at all (generic database): the id is hidden, never shown as a label.
                if (columns.Count > 1 && columns.Any(c => c != column && !IsIdColumn(rows, c)) && columns.Remove(column))
                {
                    var d0 = new ReferenceDiagnostic { Column = column, Entity = singular, Source = source is null ? null : $"{source.Collection}.{source.Field}", Method = "none", Checked = ids.Count, Unresolved = ids.Count, Hidden = true, LookupFailed = failed, Note = "no verified master record — id hidden" };
                    diagnostics.Add(d0);
                    notes.Add(d0.Summary());
                }
                continue;
            }

            // A column of the result already shows these names (the query looked them up): hide the id only.
            var existing = columns.FirstOrDefault(c => c != column && names.Count > 0 && rows.All(r =>
                !(Text(r.TryGetPropertyValue(column, out var iv) ? iv : null) is { } id && names.TryGetValue(id.Trim(), out var n))
                || string.Equals(Text(r.TryGetPropertyValue(c, out var cv) ? cv : null)?.Trim(), n.Name.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (existing is not null)
            {
                columns.Remove(column);
                foreach (var (k, v) in names) labels[k] = v.Name;
                var d1 = new ReferenceDiagnostic { Column = column, Entity = displayEntity?.Key ?? singular, Source = source is null ? null : $"{source.Collection}.{source.Field}", Method = used?.Method ?? method, Master = masterLabel, DisplayColumn = existing, Checked = ids.Count, Resolved = names.Count, Hidden = true, Note = "name already in the result" };
                diagnostics.Add(d1);
                notes.Add(d1.Summary());
                mapped++;
                continue;
            }

            // Labels: names (duplicates told apart by code or number), "No …" for empty, "Unknown …" for the rest.
            var idLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = 0;
            foreach (var group in names.GroupBy(kv => kv.Value.Name.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var members = group.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                if (members.Count == 1) { idLabels[members[0].Key] = members[0].Value.Name.Trim(); continue; }
                duplicates += members.Count;
                var n = 0;
                foreach (var (id, (name, code)) in members)
                {
                    n++;
                    idLabels[id] = !string.IsNullOrWhiteSpace(code) && members.Count(m => m.Value.Code == code) == 1
                        ? $"{name.Trim()} ({code!.Trim()})"
                        : $"{name.Trim()} ({n})";
                }
            }
            var missing = ids.Where(i => !idLabels.ContainsKey(i)).ToList();
            for (var i = 0; i < missing.Count; i++)
                idLabels[missing[i]] = missing.Count == 1 ? unknown : $"{unknown} {i + 1}";
            var orphanIds = masterRead && !failed ? missing : new List<string>();

            if (columns.Contains(display) || rows.Any(r => r.ContainsKey(display))) display = column == "_id" ? Camel(singular.Replace(" ", string.Empty)) : column + "Label";
            foreach (var row in rows)
            {
                if (!row.TryGetPropertyValue(column, out var cell)) continue;
                var key = integerIds.Count > 0 && cell is JsonValue nv && JsonHelpers.TryGetNumber(nv, out var num)
                    ? ((long)num).ToString(CultureInfo.InvariantCulture)
                    : Text(cell)?.Trim();
                var label = key is null || EntityCatalog.IsNoneValue(key) ? none : idLabels.TryGetValue(key, out var l) ? l : unknown;
                // Name first, the id right after it (kept for relationships and follow-ups, never listed for display).
                var ordered = new List<KeyValuePair<string, JsonNode?>>();
                foreach (var (k, value) in row.ToList())
                {
                    if (k == column) ordered.Add(new KeyValuePair<string, JsonNode?>(display, label));
                    ordered.Add(new KeyValuePair<string, JsonNode?>(k, value?.DeepClone()));
                }
                row.Clear();
                foreach (var (k, value) in ordered) row[k] = value;
            }
            foreach (var (k, v) in idLabels) labels[k] = v;
            var index = columns.IndexOf(column);
            if (index >= 0) columns[index] = display;
            mapped++;

            var diag = new ReferenceDiagnostic
            {
                Column = column,
                Entity = displayEntity?.Key ?? singular,
                Source = source is null ? null : $"{source.Collection}.{source.Field}",
                Method = used?.Method ?? method,
                Master = masterLabel,
                DisplayColumn = display,
                Checked = ids.Count,
                Resolved = names.Count,
                Empty = noneCount,
                Unresolved = missing.Count,
                Orphans = orphanIds.Count,
                OrphanIds = orphanIds.Take(10).ToList(),
                LookupFailed = failed || (entity is not null && used is null && !masterRead),
                DuplicateNames = duplicates,
                Note = entity is { Collection: null } ? entity.Reason : null
            };
            diagnostics.Add(diag);
            notes.Add(diag.Summary());
        }

        sw.Stop();
        return notes.Count == 0 && diagnostics.Count == 0
            ? new ReferenceResolution { Result = executed, ElapsedMs = sw.ElapsedMilliseconds }
            : new ReferenceResolution
            {
                Changed = true,
                Notes = notes,
                Diagnostics = diagnostics,
                Labels = labels,
                ElapsedMs = sw.ElapsedMilliseconds,
                Result = new ExecutedQuery { Rows = rows, Columns = columns, Truncated = executed.Truncated, ElapsedMs = executed.ElapsedMs }
            };
    }

    /// <summary>
    /// Names of entity ids for the server reports (stock engine, item stock, stock movement): the same master, name field
    /// and orphan rules as <see cref="ResolveReferencesAsync"/>. Read-only, tenant-scoped, ≤ 500 ids.
    /// </summary>
    public async Task<EntityNames> ResolveEntityNamesAsync(string entityKey, IEnumerable<string?> ids, IReadOnlyList<CollectionSchema> schema,
        AppSettings settings, CancellationToken ct, string? comment = null)
    {
        var entity = EntityCatalog.Types.TryGetValue(entityKey, out var e) ? e : null;
        var wanted = ids.Where(i => i is not null && !EntityCatalog.IsNoneValue(i)).Select(i => i!.Trim())
            .Where(i => entity is { IntegerKey: true } ? long.TryParse(i, out _) : IsIdText(i))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxIdsPerLookup).ToList();
        if (entity?.Collection is null || entity.NameField is null || wanted.Count == 0)
            return new EntityNames { Entity = entity, Failed = entity?.Collection is null && wanted.Count > 0 };
        var bySchema = BySchema(schema);
        if (!bySchema.ContainsKey(entity.Collection)) return new EntityNames { Entity = entity, Failed = true };
        var (found, ok) = await LookupAsync(entity.Collection, entity.NameField, new[] { entity.KeyField }, Array.Empty<string>(), wanted, entity.IntegerKey, bySchema, settings, ct, comment);
        if (entity.Key == "document" && ok)
            foreach (var (c, n) in EntityCatalog.DocumentMasters.Skip(1))
            {
                var rest = wanted.Where(w => !found.ContainsKey(w)).ToList();
                if (rest.Count == 0 || !bySchema.ContainsKey(c)) continue;
                var (more, ok2) = await LookupAsync(c, n, new[] { "_id" }, Array.Empty<string>(), rest, false, bySchema, settings, ct, comment);
                if (ok2) foreach (var (k, v) in more) found[k] = v;
            }
        return new EntityNames
        {
            Entity = entity,
            Names = found.ToDictionary(kv => kv.Key, kv => kv.Value.Name.Trim(), StringComparer.OrdinalIgnoreCase),
            Orphans = ok ? wanted.Where(w => !found.ContainsKey(w)).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Failed = !ok
        };
    }

    /// <summary>
    /// Data-integrity check of every verified MySaleBooks reference over the whole company database: distinct ids used
    /// by the transactions (≤ <paramref name="maxIds"/> per reference), how many exist in the master, and the orphans.
    /// Read-only and tenant-scoped; used by the diagnostics endpoint, never by chat answers.
    /// </summary>
    public async Task<List<EntityReferenceCheck>> CheckEntityReferencesAsync(IReadOnlyList<CollectionSchema> schema, AppSettings settings,
        CancellationToken ct, int maxIds = 2000, string? comment = null)
    {
        var bySchema = BySchema(schema);
        var checks = new List<EntityReferenceCheck>();
        foreach (var reference in EntityCatalog.References)
        {
            var entity = EntityCatalog.Types[reference.Entity];
            var check = new EntityReferenceCheck
            {
                Entity = entity.Key,
                Reference = $"{reference.Collection}.{reference.Field}",
                Master = entity.Collection is null ? "(none)" : $"{entity.Collection}.{entity.NameField}"
            };
            checks.Add(check);
            if (!bySchema.ContainsKey(reference.Collection)) { check.Status = "skipped"; check.Note = $"{reference.Collection} is not in this database."; continue; }
            if (entity.Collection is null) { check.Status = "unresolvable"; check.Note = entity.Reason; }
            else if (!bySchema.ContainsKey(entity.Collection)) { check.Status = "unresolvable"; check.Note = $"Master {entity.Collection} is not in this database."; }
            try
            {
                var pipeline = new JsonArray
                {
                    new JsonObject { ["$match"] = new JsonObject { [reference.Field] = new JsonObject { ["$nin"] = new JsonArray(null, "", "0", 0) } } },
                    new JsonObject { ["$group"] = new JsonObject { ["_id"] = "$" + reference.Field } },
                    new JsonObject { ["$limit"] = maxIds + 1 }
                };
                if (MySaleBooksDomain.RequiredFilter(bySchema[reference.Collection]) is { } status)
                    ((JsonObject)pipeline[0]!["$match"]!)["isCanceled"] = status["isCanceled"]!.DeepClone();
                var found = await _executor.ExecuteAsync(reference.Collection, Scoped(pipeline, reference.Collection, maxIds + 1, bySchema),
                    new QueryExecutionOptions { MaxDocuments = maxIds + 1, TimeoutMs = Math.Max(settings.Query.QueryTimeoutMs, 5000), Comment = comment }, ct);
                var ids = found.Rows.Select(r => r["_id"] is JsonValue v && JsonHelpers.TryGetNumber(v, out var d) && entity.IntegerKey
                        ? ((long)d).ToString(CultureInfo.InvariantCulture) : Text(r["_id"]))
                    .Where(i => i is not null && !EntityCatalog.IsNoneValue(i)).Select(i => i!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                check.Capped = ids.Count > maxIds;
                ids = ids.Take(maxIds).ToList();
                check.Checked = ids.Count;
                if (check.Status == "unresolvable") { check.Unresolved = ids.Count; continue; }
                var usable = entity.IntegerKey ? ids : ids.Where(IsIdText).ToList();
                var notIds = ids.Count - usable.Count;
                var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var failed = false;
                foreach (var batch in usable.Chunk(MaxIdsPerLookup))
                {
                    var (names, ok) = await LookupAsync(entity.Collection!, entity.NameField!, new[] { entity.KeyField }, Array.Empty<string>(), batch.ToList(), entity.IntegerKey, bySchema, settings, ct, comment);
                    if (!ok) { failed = true; break; }
                    foreach (var k in names.Keys) resolved.Add(k);
                    if (entity.Key == "document")
                        foreach (var (c, n) in EntityCatalog.DocumentMasters.Skip(1))
                        {
                            var rest = batch.Where(b => !resolved.Contains(b)).ToList();
                            if (rest.Count == 0 || !bySchema.ContainsKey(c)) continue;
                            var (more, ok2) = await LookupAsync(c, n, new[] { "_id" }, Array.Empty<string>(), rest, false, bySchema, settings, ct, comment);
                            if (ok2) foreach (var k in more.Keys) resolved.Add(k);
                        }
                }
                check.Resolved = resolved.Count;
                check.Unresolved = ids.Count - resolved.Count;
                if (failed) { check.Status = "error"; check.Note = "The master could not be read (timeout or error)."; continue; }
                var orphans = usable.Where(i => !resolved.Contains(i)).ToList();
                check.Orphans = orphans.Count;
                check.SampleOrphanIds = orphans.Take(5).ToList();
                if (notIds > 0) check.Note = $"{notIds} value(s) are not ids (free text) and were not looked up.";
                check.Status = orphans.Count > 0 ? "orphans" : "ok";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                check.Status = "error";
                check.Note = ex is QueryTimeoutException ? "Timed out." : "The reference could not be read.";
            }
        }
        return checks;
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, CollectionSchema> BySchema(IReadOnlyList<CollectionSchema> schema)
        => schema.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private JsonArray Scoped(JsonArray pipeline, string collection, int max, IReadOnlyDictionary<string, CollectionSchema> bySchema)
    {
        TenantBinding? Resolve(string c)
        {
            if (!bySchema.TryGetValue(c, out var cs)) return _guard.Default;
            if (string.IsNullOrEmpty(cs.TenantField)) return null;
            var isObjectId = cs.TenantValueType switch { "objectId" => true, "string" => false, _ => _tenant.ValueIsObjectId };
            return new TenantBinding(cs.TenantField, isObjectId);
        }
        return _user.TenantIsDatabase
            ? _guard.Apply(pipeline, _user.CompanyId, max, collection, _ => null)
            : _guard.Apply(pipeline, _user.CompanyId, max, collection, Resolve);
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool IsIdColumn(List<JsonObject> rows, string column)
        => rows.Select(r => Text(r.TryGetPropertyValue(column, out var v) ? v : null)).Where(t => !string.IsNullOrEmpty(t)).DefaultIfEmpty().All(t => t is not null && IsIdText(t));

    /// <summary>Name field of a MySaleBooks master (Item → itemName …), from the catalog.</summary>
    private static string? MasterName(string collection)
        => EntityCatalog.Types.Values.FirstOrDefault(t => t.Collection == collection)?.NameField;

    /// <summary>Reads the name (and code) of the given ids from one collection. ok = false when the read failed.</summary>
    private async Task<(Dictionary<string, (string Name, string? Code)> Found, bool Ok)> LookupAsync(string collection, string nameField,
        IReadOnlyList<string> keyFields, IReadOnlyList<string> codeFields, List<string> ids, bool integerKey,
        IReadOnlyDictionary<string, CollectionSchema> bySchema, AppSettings settings, CancellationToken ct, string? comment)
    {
        var result = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var values = new JsonArray();
            foreach (var id in ids)
            {
                if (integerKey)
                {
                    if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) values.Add(n);
                    continue;
                }
                if (IsObjectIdText(id)) values.Add(new JsonObject { ["$oid"] = id.ToLowerInvariant() });
                values.Add(JsonValue.Create(id)); // ids stored as strings (GUIDs, string ObjectIds)
                if (IsGuidText(id) && Guid.TryParse(id, out var g))
                {
                    var canonical = g.ToString("D");
                    if (!string.Equals(canonical, id, StringComparison.OrdinalIgnoreCase)) values.Add(JsonValue.Create(canonical));
                }
            }
            if (values.Count == 0) return (result, true);
            var or = new JsonArray();
            foreach (var key in keyFields)
                or.Add(new JsonObject { [key] = new JsonObject { ["$in"] = values.DeepClone() } });
            var match = new JsonObject { ["$match"] = or.Count == 1 ? (JsonObject)or[0]!.DeepClone() : new JsonObject { ["$or"] = or } };
            JsonArray pipeline;
            // A key that is not unique per document (voucherGuId: one line per ledger) is grouped: one row per key.
            var grouped = keyFields.Count == 1 && keyFields[0] != "_id";
            if (grouped)
            {
                var group = new JsonObject { ["_id"] = "$" + keyFields[0], [nameField] = new JsonObject { ["$first"] = "$" + nameField } };
                foreach (var code in codeFields.Where(c => c != nameField)) group[code] = new JsonObject { ["$first"] = "$" + code };
                pipeline = new JsonArray { match, new JsonObject { ["$group"] = group } };
                keyFields = new[] { "_id" };
            }
            else
            {
                var project = new JsonObject { ["_id"] = 1, [nameField] = 1 };
                foreach (var key in keyFields.Where(k => k != "_id")) project[key] = 1;
                foreach (var code in codeFields.Where(c => c != nameField)) project[code] = 1;
                pipeline = new JsonArray { match, new JsonObject { ["$project"] = project } };
            }
            var found = await _executor.ExecuteAsync(collection, Scoped(pipeline, collection, ids.Count * 2, bySchema),
                new QueryExecutionOptions { MaxDocuments = ids.Count * 2 + 1, TimeoutMs = Math.Min(settings.Query.QueryTimeoutMs, 3000), Comment = comment }, ct);

            var wanted = new HashSet<string>(ids.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            foreach (var doc in found.Rows)
            {
                var name = doc[nameField] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : doc[nameField]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var code = codeFields.Select(c => Text(doc[c])).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && !string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
                foreach (var key in keyFields)
                {
                    var raw = doc[key] is JsonValue kv && JsonHelpers.TryGetNumber(kv, out var num) && integerKey
                        ? ((long)num).ToString(CultureInfo.InvariantCulture)
                        : doc[key] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : doc[key]?.ToString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var normalized = Normalize(raw);
                    if (!wanted.Contains(normalized)) continue; // only exact id matches are accepted
                    foreach (var id in ids.Where(i => Normalize(i) == normalized)) result[id] = (name.Trim(), code?.Trim());
                }
            }
            return (result, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase), false); // best effort
        }

        static string Normalize(string id) => IsGuidText(id) && Guid.TryParse(id, out var g) ? g.ToString("N") : id.Trim().ToLowerInvariant();
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

    /// <summary>"SalesPerson" → "sales person", "customer" → "customer".</summary>
    private static string HumanSingular(string word)
        => Singular(Regex.Replace(word, "(?<=[a-z])(?=[A-Z])", " ")).Trim();

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

    private sealed record Candidate(CollectionSchema Schema, string NameField, string? ForeignField, bool Verified, string Method);

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
                yield return new Candidate(rc, nf, parts.Length > 1 ? parts[1].Trim() : "_id", true, "schema relationship");
        }

        // 2. A $lookup in the query itself (localField → from.foreignField)
        if (lookupAliases.TryGetValue(sourceField, out var lk) && Find(lk.From) is { } lc && NameField(lc) is { } lnf && seen.Add(lc.Name))
            yield return new Candidate(lc, lnf, lk.ForeignField, true, "query $lookup");

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
                if (NameField(c) is { } nf && seen.Add(c.Name)) yield return new Candidate(c, nf, "_id", true, "column name");
        }

        // 4. Last resort: master-data collections (accepted only when ~all ids of the column exist there)
        var masters = list.Where(cs => IsMasterData(cs.Name) && NameField(cs) is not null)
            .OrderBy(cs => cs.DocumentCount ?? long.MaxValue).ToList();
        foreach (var c in masters)
            if (seen.Add(c.Name)) yield return new Candidate(c, NameField(c)!, "_id", false, "generic");
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
