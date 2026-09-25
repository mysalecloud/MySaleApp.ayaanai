using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;
using MySale.AI.Infrastructure.Persistence;

namespace MySale.AI.Infrastructure.BusinessData;

/// <summary>
/// Resolves the business database the AI queries. The connection comes from the Database page
/// (encrypted in the system DB) or falls back to appsettings. In MySaleBooks this becomes the tenant DB resolver.
/// </summary>
public interface IBusinessDatabase
{
    Task<IMongoDatabase> GetDatabaseAsync(CancellationToken ct);
    Task<(string ConnectionString, string DatabaseName, bool FromSettings)> GetConnectionAsync(CancellationToken ct);
    void Invalidate();
}

public sealed class BusinessDatabase : IBusinessDatabase
{
    private readonly DatabaseConfigStore _store;
    private readonly ISecretProtector _secrets;
    private readonly BusinessDbOptions _fallback;
    private readonly IHttpContextAccessor _http;
    private readonly ConcurrentDictionary<string, IMongoClient> _clients = new();
    private (string Cs, string Db, bool FromSettings)? _cached;
    private DateTime _cachedAt;

    public BusinessDatabase(DatabaseConfigStore store, ISecretProtector secrets, IOptions<BusinessDbOptions> fallback, IHttpContextAccessor http)
    {
        _http = http;
        _store = store;
        _secrets = secrets;
        _fallback = fallback.Value;
    }

    public async Task<(string ConnectionString, string DatabaseName, bool FromSettings)> GetConnectionAsync(CancellationToken ct)
    {
        var (cs, db, fromSettings) = await GetConfiguredConnectionAsync(ct);

        // MySaleBooks users: the database ALWAYS comes from the validated JWT "dbName" claim — same server
        // connection, customer database from the token. Never from the request body, never from the AI,
        // and no fallback to the configured database when the claim is missing.
        var user = _http.HttpContext?.User;
        if (user?.FindFirst(TenantClaimTypes.AuthSource)?.Value == TenantClaimTypes.MySaleBooks)
        {
            var tenantDb = user.FindFirst(TenantClaimTypes.DatabaseName)?.Value;
            if (string.IsNullOrWhiteSpace(tenantDb)) throw new TenantResolutionException();
            return (cs, tenantDb, fromSettings);
        }
        return (cs, db, fromSettings);
    }

    private async Task<(string ConnectionString, string DatabaseName, bool FromSettings)> GetConfiguredConnectionAsync(CancellationToken ct)
    {
        if (_cached is { } c && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30)) return c;

        var config = await _store.GetAsync(ct);
        var cs = _secrets.Unprotect(config?.ConnectionStringEncrypted);
        (string, string, bool) resolved = !string.IsNullOrEmpty(cs) && !string.IsNullOrEmpty(config?.DatabaseName)
            ? (cs, config!.DatabaseName, true)
            : (_fallback.ConnectionString, _fallback.DatabaseName, false);
        _cached = resolved;
        _cachedAt = DateTime.UtcNow;
        return resolved;
    }

    public async Task<IMongoDatabase> GetDatabaseAsync(CancellationToken ct)
    {
        var (cs, db, _) = await GetConnectionAsync(ct);
        return GetClient(cs).GetDatabase(db);
    }

    public IMongoClient GetClient(string connectionString)
        => _clients.GetOrAdd(connectionString, CreateClient);

    public static IMongoClient CreateClient(string connectionString)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.ApplicationName = "mysale-ai-agent-query";
        settings.RetryWrites = false;
        return new MongoClient(settings);
    }

    public void Invalidate() => _cached = null;
}

/// <summary>Converts BSON results to plain JSON for the UI and the answer model (no Extended JSON wrappers).</summary>
public static class BsonJsonConverter
{
    public static JsonObject ToJsonObject(BsonDocument doc)
    {
        var obj = new JsonObject();
        foreach (var element in doc.Elements)
            obj[element.Name] = ToJsonNode(element.Value);
        return obj;
    }

    public static JsonNode? ToJsonNode(BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                return ToJsonObject(value.AsBsonDocument);
            case BsonType.Array:
                var arr = new JsonArray();
                foreach (var item in value.AsBsonArray) arr.Add(ToJsonNode(item));
                return arr;
            case BsonType.Int32:
                return JsonValue.Create(value.AsInt32);
            case BsonType.Int64:
                return JsonValue.Create(value.AsInt64);
            case BsonType.Double:
                var d = value.AsDouble;
                return double.IsNaN(d) || double.IsInfinity(d) ? null : JsonValue.Create(Math.Round(d, 6));
            case BsonType.Decimal128:
                var dec = value.AsDecimal128;
                try { return JsonValue.Create(Decimal128.ToDecimal(dec)); }
                catch (OverflowException) { return JsonValue.Create(Decimal128.ToDouble(dec)); }
            case BsonType.String:
                return JsonValue.Create(value.AsString);
            case BsonType.Boolean:
                return JsonValue.Create(value.AsBoolean);
            case BsonType.DateTime:
                try
                {
                    return JsonValue.Create(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                }
                catch (ArgumentOutOfRangeException)
                {
                    return JsonValue.Create(value.AsBsonDateTime.MillisecondsSinceEpoch);
                }
            case BsonType.ObjectId:
                return JsonValue.Create(value.AsObjectId.ToString());
            case BsonType.Null:
            case BsonType.Undefined:
                return null;
            case BsonType.Timestamp:
                return JsonValue.Create(value.AsBsonTimestamp.Value);
            case BsonType.Binary:
                return JsonValue.Create("<binary>");
            default:
                return JsonValue.Create(value.ToString());
        }
    }
}
