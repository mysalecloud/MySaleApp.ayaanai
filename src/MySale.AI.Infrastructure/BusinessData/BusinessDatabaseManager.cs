using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Services;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.Persistence;
using MySale.AI.Infrastructure.Seeding;

namespace MySale.AI.Infrastructure.BusinessData;

/// <summary>Backs the Database page. Connection strings are write-only and stored encrypted.</summary>
public sealed class BusinessDatabaseManager : IBusinessDatabaseManager
{
    private readonly BusinessDatabase _business;
    private readonly DatabaseConfigStore _store;
    private readonly ISecretProtector _secrets;
    private readonly SampleDataSeeder _seeder;
    private readonly AuditService _audit;
    private readonly ILogger<BusinessDatabaseManager> _logger;
    private readonly IUserContext _user;

    public BusinessDatabaseManager(BusinessDatabase business, DatabaseConfigStore store, ISecretProtector secrets,
        SampleDataSeeder seeder, AuditService audit, ILogger<BusinessDatabaseManager> logger, IUserContext user)
    {
        _user = user;
        _business = business;
        _store = store;
        _secrets = secrets;
        _seeder = seeder;
        _audit = audit;
        _logger = logger;
    }

    public async Task<DatabaseConfigDto> GetConfigAsync(CancellationToken ct)
    {
        var config = await _store.GetAsync(ct);
        var (cs, db, fromSettings) = await _business.GetConnectionAsync(ct);
        return new DatabaseConfigDto
        {
            Configured = true,
            DatabaseName = db,
            DisplayHost = config?.DisplayHost is { Length: > 0 } h && fromSettings ? h : DisplayHostOf(cs),
            HasCredentials = fromSettings ? config!.HasCredentials : HasCredentials(cs),
            Source = _user.IsMySaleBooksUser ? "jwt" : fromSettings ? "database-page" : "appsettings",
            DatabaseFromToken = _user.IsMySaleBooksUser,
            LastTestStatus = config?.LastTestStatus,
            LastTestedAt = config?.LastTestedAt
        };
    }

    public async Task<DatabaseConfigDto> UpdateConfigAsync(DatabaseConfigUpdate update, string updatedBy, CancellationToken ct)
    {
        var cs = BuildConnectionString(update);
        var existing = await _store.GetAsync(ct);
        var config = new DatabaseConfig
        {
            ConnectionStringEncrypted = _secrets.Protect(cs),
            DatabaseName = update.DatabaseName.Trim(),
            DisplayHost = DisplayHostOf(cs),
            HasCredentials = HasCredentials(cs),
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = updatedBy,
            LastTestStatus = existing?.LastTestStatus,
            LastTestedAt = existing?.LastTestedAt
        };
        await _store.SaveAsync(config, ct);
        _business.Invalidate();
        SchemaService.InvalidateCache();
        await _audit.LogAsync("DatabaseConfigUpdated", $"Business DB → {config.DisplayHost}/{config.DatabaseName}", ct);
        return await GetConfigAsync(ct);
    }

    public async Task<DatabaseTestResult> TestAsync(DatabaseConfigUpdate? draft, CancellationToken ct)
    {
        string cs, dbName;
        if (draft is not null && !string.IsNullOrWhiteSpace(draft.ConnectionUrl))
        {
            try { cs = BuildConnectionString(draft); }
            catch (AppValidationException ex) { return new DatabaseTestResult { Success = false, Message = ex.Message }; }
            // MySaleBooks users: the database is always the one in their token.
            dbName = _user.IsMySaleBooksUser ? (_user.DatabaseName ?? throw new TenantResolutionException()) : draft.DatabaseName.Trim();
        }
        else
        {
            (cs, dbName, _) = await _business.GetConnectionAsync(ct);
        }

        var sw = Stopwatch.StartNew();
        DatabaseTestResult result;
        try
        {
            var client = BusinessDatabase.CreateClient(cs);
            var admin = client.GetDatabase("admin");
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            var build = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: ct);
            var db = client.GetDatabase(dbName);
            var collections = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
            sw.Stop();
            result = new DatabaseTestResult
            {
                Success = true,
                Message = collections.Count == 0 ? $"Connected. Database '{dbName}' is empty — use Seed sample data." : $"Connected to '{dbName}'.",
                LatencyMs = sw.ElapsedMilliseconds,
                ServerVersion = build.GetValue("version", BsonNull.Value).ToString(),
                DatabaseExists = collections.Count > 0,
                CollectionCount = collections.Count
            };
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or MongoConfigurationException)
        {
            _logger.LogInformation(ex, "Business DB connection test failed");
            result = new DatabaseTestResult
            {
                Success = false,
                Message = ex is MongoAuthenticationException ? "Authentication failed. Check the username and password."
                        : ex is TimeoutException ? "Could not reach the MongoDB server (timeout)."
                        : "Connection failed: " + ex.Message,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }

        if (draft is null)
        {
            var config = await _store.GetAsync(ct);
            if (config is not null)
            {
                config.LastTestStatus = result.Success ? "Connected" : "Failed";
                config.LastTestedAt = DateTime.UtcNow;
                await _store.SaveAsync(config, ct);
            }
        }
        return result;
    }

    public async Task<List<string>> ListDatabasesAsync(CancellationToken ct)
    {
        var (cs, _, _) = await _business.GetConnectionAsync(ct);
        var client = _business.GetClient(cs);
        var names = await (await client.ListDatabaseNamesAsync(ct)).ToListAsync(ct);
        return names.Where(n => n is not ("admin" or "local" or "config")).OrderBy(n => n).ToList();
    }

    public async Task<List<CollectionInfo>> ListCollectionsAsync(IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> documented, CancellationToken ct)
    {
        var db = await _business.GetDatabaseAsync(ct);
        var names = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        var result = new List<CollectionInfo>();
        foreach (var name in names.Where(n => !n.StartsWith("system.", StringComparison.Ordinal)).OrderBy(n => n))
        {
            var count = await db.GetCollection<BsonDocument>(name).EstimatedDocumentCountAsync(cancellationToken: ct);
            result.Add(new CollectionInfo
            {
                Name = name,
                DocumentCount = count,
                Allowed = allowed.Contains(name),
                Documented = documented.Contains(name)
            });
        }
        return result;
    }

    public async Task<SeedResult> SeedAsync(bool reset, CancellationToken ct)
    {
        // Never write sample data into a real customer database resolved from a MySaleBooks token.
        if (_user.IsMySaleBooksUser)
            throw new AppValidationException("Sample data can't be seeded into a customer database. Sign in with a demo user to use the sample database.");
        var db = await _business.GetDatabaseAsync(ct);
        var result = await _seeder.SeedAsync(db, reset, ct);
        SchemaService.InvalidateCache();
        await _audit.LogAsync("DataSeeded", $"Sample data seeded into '{db.DatabaseNamespace.DatabaseName}' (reset={reset})", ct);
        return result;
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            var db = await _business.GetDatabaseAsync(ct);
            await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return true;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string BuildConnectionString(DatabaseConfigUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.DatabaseName))
            throw new AppValidationException("Database name is required.");
        try
        {
            var builder = new MongoUrlBuilder(update.ConnectionUrl.Trim());
            if (!string.IsNullOrWhiteSpace(update.UserName))
            {
                builder.Username = update.UserName.Trim();
                builder.Password = update.Password ?? string.Empty;
            }
            return builder.ToString();
        }
        catch (Exception ex) when (ex is MongoConfigurationException or ArgumentException or FormatException)
        {
            throw new AppValidationException("Invalid MongoDB connection URL. Expected e.g. mongodb://localhost:27017");
        }
    }

    private static string DisplayHostOf(string cs)
    {
        try
        {
            var url = new MongoUrl(cs);
            return string.Join(",", url.Servers.Select(s => $"{s.Host}:{s.Port}"));
        }
        catch (Exception)
        {
            return "(invalid)";
        }
    }

    private static bool HasCredentials(string cs)
    {
        try { return !string.IsNullOrEmpty(new MongoUrl(cs).Username); }
        catch (Exception) { return false; }
    }
}
