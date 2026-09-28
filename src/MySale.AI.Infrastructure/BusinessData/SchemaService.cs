using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;

namespace MySale.AI.Infrastructure.BusinessData;

/// <summary>
/// Schema metadata = live discovery ($sample of every collection, nested fields flattened)
/// + curated catalog (sample ERP) + saved metadata (AI-generated or edited on the Schema page, wins over both).
/// Cached for a few minutes. Replace with the MySaleBooks schema service during integration.
/// </summary>
public sealed class SchemaService : ISchemaService
{
    private const int SampleSize = 40;
    private const int MaxFieldsPerCollection = 200;
    private const int MaxDepth = 3;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    // Discovered schema per database (each MySaleBooks customer has its own database).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<CollectionSchema> Schema, DateTime At)> Cache = new();

    private readonly IBusinessDatabase _business;
    private readonly ISchemaMetadataRepository _metadata;
    private readonly TenantOptions _tenant;
    private readonly ILogger<SchemaService> _logger;

    public SchemaService(IBusinessDatabase business, ISchemaMetadataRepository metadata, TenantOptions tenant, ILogger<SchemaService> logger)
    {
        _business = business;
        _metadata = metadata;
        _tenant = tenant;
        _logger = logger;
    }

    public static void InvalidateCache() => Cache.Clear();

    public void Invalidate() => InvalidateCache();

    public async Task<IReadOnlyList<CollectionSchema>> GetSchemaAsync(bool includeDiscovery, CancellationToken ct)
    {
        if (!includeDiscovery) return await CuratedWithMetadataAsync(ct);

        var (_, dbName, _) = await _business.GetConnectionAsync(ct);
        if (Cache.TryGetValue(dbName, out var hit) && DateTime.UtcNow - hit.At < CacheDuration) return Clone(hit.Schema);

        await Gate.WaitAsync(ct);
        try
        {
            if (Cache.TryGetValue(dbName, out hit) && DateTime.UtcNow - hit.At < CacheDuration) return Clone(hit.Schema);
            var merged = await DiscoverAndMergeAsync(ct);
            Cache[dbName] = (merged, DateTime.UtcNow);
            return Clone(merged);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            _logger.LogWarning(ex, "Schema discovery failed; using curated + saved metadata only");
            return await CuratedWithMetadataAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    // ------------------------------------------------------------------ merge

    private async Task<List<CollectionSchema>> CuratedWithMetadataAsync(CancellationToken ct)
    {
        var saved = await LoadMetadataAsync(ct);
        var result = SampleSchemaCatalog.Build().Select(c => Prepare(c, "catalog")).ToList();
        foreach (var m in saved.Values)
        {
            var target = result.FirstOrDefault(c => c.Name == m.Id);
            if (target is null)
            {
                target = new CollectionSchema { Name = m.Id, TenantField = _tenant.FieldName };
                result.Add(target);
            }
            ApplyMetadata(target, m);
        }
        return result;
    }

    private async Task<List<CollectionSchema>> DiscoverAndMergeAsync(CancellationToken ct)
    {
        var db = await _business.GetDatabaseAsync(ct);
        var names = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        // Curated metadata: the sample catalog plus the verified MySaleBooks catalog (used only for collections that exist).
        var curated = SampleSchemaCatalog.Build().Concat(MySaleBooksCatalog.Build())
            .GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var saved = await LoadMetadataAsync(ct);
        var result = new List<CollectionSchema>();

        foreach (var name in names.Where(n => !n.StartsWith("system.", StringComparison.Ordinal)).OrderBy(n => n))
        {
            var coll = db.GetCollection<BsonDocument>(name);
            var count = await coll.EstimatedDocumentCountAsync(cancellationToken: ct);
            var sample = count == 0 ? new List<BsonDocument>() : await coll.Aggregate().Sample(SampleSize).ToListAsync(ct);

            var discovered = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var doc in sample) Flatten(doc, string.Empty, 0, discovered);

            var schema = curated.TryGetValue(name, out var c)
                ? Prepare(c, "catalog")
                : new CollectionSchema { Name = name, TenantField = _tenant.FieldName, MetadataSource = "discovered" };
            schema.DocumentCount = count;

            foreach (var f in schema.Fields) f.Discovered = discovered.ContainsKey(f.Name);
            foreach (var (field, type) in discovered.Where(d => schema.Fields.All(f => f.Name != d.Key)).ToList())
                schema.Fields.Add(new FieldSchema { Name = field, Type = type, Documented = false, Discovered = true, Operations = DefaultOps(type) });

            if (saved.TryGetValue(name, out var meta)) ApplyMetadata(schema, meta);

            // Detect how the tenant field is stored (ObjectId vs string) from real data.
            if (!string.IsNullOrEmpty(schema.TenantField))
            {
                var tenantValue = sample.Select(d => d.GetValue(schema.TenantField, BsonNull.Value)).FirstOrDefault(v => !v.IsBsonNull);
                if (tenantValue is not null)
                    schema.TenantValueType = tenantValue.BsonType == BsonType.ObjectId ? "objectId" : "string";
            }

            schema.Documented = !string.IsNullOrWhiteSpace(schema.Description);
            result.Add(schema);
        }
        return result;
    }

    private CollectionSchema Prepare(CollectionSchema c, string source)
    {
        c.TenantField = _tenant.FieldName;
        c.MetadataSource = source;
        c.Documented = !string.IsNullOrWhiteSpace(c.Description);
        return c;
    }

    private static void ApplyMetadata(CollectionSchema schema, SchemaMetadata m)
    {
        if (!string.IsNullOrWhiteSpace(m.Description)) schema.Description = m.Description.Trim();
        if (m.TenantField is not null) schema.TenantField = m.TenantField.Trim();
        foreach (var fm in m.Fields)
        {
            var field = schema.Fields.FirstOrDefault(f => f.Name == fm.Name);
            if (field is null)
            {
                field = new FieldSchema { Name = fm.Name, Type = "unknown", Discovered = false, Operations = new() { "match" } };
                schema.Fields.Add(field);
            }
            if (!string.IsNullOrWhiteSpace(fm.Description)) field.Description = fm.Description.Trim();
            if (fm.Example is not null) field.Example = string.IsNullOrWhiteSpace(fm.Example) ? null : fm.Example.Trim();
            if (fm.Relationship is not null) field.Relationship = string.IsNullOrWhiteSpace(fm.Relationship) ? null : fm.Relationship.Trim();
            field.Hidden = fm.Hidden;
            field.Documented = !string.IsNullOrWhiteSpace(field.Description);
        }
        schema.MetadataSource = "saved";
        schema.MetadataUpdatedAt = m.UpdatedAt;
        schema.Documented = !string.IsNullOrWhiteSpace(schema.Description);
    }

    private async Task<Dictionary<string, SchemaMetadata>> LoadMetadataAsync(CancellationToken ct)
    {
        try
        {
            return (await _metadata.ListAsync(ct)).ToDictionary(m => m.Id, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not load saved schema metadata");
            return new Dictionary<string, SchemaMetadata>();
        }
    }

    /// <summary>Flattens nested documents / arrays of documents into dotted paths (depth ≤ 3).</summary>
    private static void Flatten(BsonDocument doc, string prefix, int depth, Dictionary<string, string> acc)
    {
        foreach (var el in doc.Elements)
        {
            if (acc.Count >= MaxFieldsPerCollection) return;
            var path = prefix + el.Name;
            var value = el.Value;
            if (value.IsBsonNull) continue;

            if (value is BsonDocument sub)
            {
                acc.TryAdd(path, "object");
                if (depth < MaxDepth) Flatten(sub, path + ".", depth + 1, acc);
            }
            else if (value is BsonArray arr)
            {
                var first = arr.FirstOrDefault(v => !v.IsBsonNull);
                acc.TryAdd(path, first is null ? "array" : $"array<{TypeName(first.BsonType)}>");
                if (depth < MaxDepth)
                    foreach (var item in arr.Take(3).OfType<BsonDocument>())
                        Flatten(item, path + ".", depth + 1, acc);
            }
            else
            {
                acc.TryAdd(path, TypeName(value.BsonType));
            }
        }
    }

    private static List<string> DefaultOps(string type) => type switch
    {
        "double" or "int" or "decimal" => new() { "match", "sum", "avg", "min", "max", "sort" },
        "date" => new() { "match", "range", "group-by-period", "sort" },
        "objectId" => new() { "match", "group", "lookup" },
        "bool" => new() { "match" },
        _ when type.StartsWith("array", StringComparison.Ordinal) => new() { "unwind", "match" },
        "object" => new() { "match" },
        _ => new() { "match", "group", "sort" }
    };

    // ------------------------------------------------------------------ samples / tenants

    public async Task<List<JsonObject>> SampleAsync(string collection, int count, CancellationToken ct)
    {
        var db = await _business.GetDatabaseAsync(ct);
        var docs = await db.GetCollection<BsonDocument>(collection).Aggregate().Sample(Math.Clamp(count, 1, 20)).ToListAsync(ct);
        return docs.Select(d => (JsonObject)Shrink(BsonJsonConverter.ToJsonObject(d), 0)!).ToList();
    }

    /// <summary>Keeps samples small: long strings cut, arrays limited to 2 items, depth limited.</summary>
    private static JsonNode? Shrink(JsonNode? node, int depth)
    {
        switch (node)
        {
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (k, v) in o) obj[k] = depth > 4 ? JsonValue.Create("…") : Shrink(v?.DeepClone(), depth + 1);
                return obj;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var v in a.Take(2)) arr.Add(Shrink(v?.DeepClone(), depth + 1));
                if (a.Count > 2) arr.Add(JsonValue.Create($"… {a.Count - 2} more"));
                return arr;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 60:
                return JsonValue.Create(s[..60] + "…");
            default:
                return node;
        }
    }

    public async Task<List<TenantValueInfo>> TenantValuesAsync(string collection, string field, CancellationToken ct)
    {
        var db = await _business.GetDatabaseAsync(ct);
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument(field, new BsonDocument("$exists", true))),
            new BsonDocument("$group", new BsonDocument { { "_id", "$" + field }, { "n", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$sort", new BsonDocument("n", -1)),
            new BsonDocument("$limit", 50)
        };
        var rows = await db.GetCollection<BsonDocument>(collection)
            .AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline), new AggregateOptions { MaxTime = TimeSpan.FromSeconds(15) }, ct);
        var list = await rows.ToListAsync(ct);
        return list.Where(r => !r["_id"].IsBsonNull).Select(r => new TenantValueInfo
        {
            Value = r["_id"].ToString() ?? string.Empty,
            Type = r["_id"].BsonType == BsonType.ObjectId ? "objectId" : "string",
            Count = r["n"].ToInt64()
        }).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private static List<CollectionSchema> Clone(List<CollectionSchema> source)
        => source.Select(c => new CollectionSchema
        {
            Name = c.Name,
            Description = c.Description,
            TenantField = c.TenantField,
            TenantValueType = c.TenantValueType,
            DocumentCount = c.DocumentCount,
            Allowed = c.Allowed,
            Documented = c.Documented,
            MetadataSource = c.MetadataSource,
            MetadataUpdatedAt = c.MetadataUpdatedAt,
            Fields = c.Fields.Select(f => new FieldSchema
            {
                Name = f.Name, Type = f.Type, Description = f.Description, Example = f.Example, Relationship = f.Relationship,
                Operations = f.Operations.ToList(), Documented = f.Documented, Discovered = f.Discovered, Hidden = f.Hidden
            }).ToList()
        }).ToList();

    private static string TypeName(BsonType t) => t switch
    {
        BsonType.Double => "double",
        BsonType.Decimal128 => "decimal",
        BsonType.Int32 or BsonType.Int64 => "int",
        BsonType.String => "string",
        BsonType.Boolean => "bool",
        BsonType.DateTime => "date",
        BsonType.ObjectId => "objectId",
        BsonType.Array => "array",
        BsonType.Document => "object",
        _ => t.ToString().ToLowerInvariant()
    };
}
