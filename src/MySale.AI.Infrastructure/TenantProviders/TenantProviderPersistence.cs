using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MySale.AI.Application.TenantProviders;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.Persistence;

namespace MySale.AI.Infrastructure.TenantProviders;

/// <summary>
/// MongoDB storage of company AI provider configurations (system database).
/// "tenant_ai_providers" — metadata; "tenant_ai_provider_secrets" — encrypted credentials only.
/// Every filter includes TenantKey. Indexes are created on first use (no change to the existing startup).
/// </summary>
public sealed class MongoTenantProviderRepository : ITenantProviderRepository
{
    public const string ConfigCollection = "tenant_ai_providers";
    public const string SecretCollection = "tenant_ai_provider_secrets";

    private readonly IMongoCollection<TenantProviderConfig> _configs;
    private readonly IMongoCollection<TenantProviderSecret> _secrets;
    private readonly ILogger<MongoTenantProviderRepository> _logger;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private volatile bool _indexed;

    public MongoTenantProviderRepository(SystemDbContext db, ILogger<MongoTenantProviderRepository> logger)
    {
        _configs = db.Database.GetCollection<TenantProviderConfig>(ConfigCollection);
        _secrets = db.Database.GetCollection<TenantProviderSecret>(SecretCollection);
        _logger = logger;
    }

    private static FilterDefinitionBuilder<TenantProviderConfig> F => Builders<TenantProviderConfig>.Filter;
    private static FilterDefinitionBuilder<TenantProviderSecret> S => Builders<TenantProviderSecret>.Filter;

    public string NewId() => ObjectId.GenerateNewId().ToString();

    public async Task<List<TenantProviderConfig>> ListAsync(string tenantKey, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct);
        return await _configs.Find(F.Eq(c => c.TenantKey, tenantKey) & F.Eq(c => c.RemovedAt, null))
            .SortBy(c => c.DisplayName).Limit(200).ToListAsync(ct);
    }

    public async Task<TenantProviderConfig?> GetAsync(string tenantKey, string id, CancellationToken ct)
    {
        if (!SystemDbContext.IsObjectId(id)) return null;
        await EnsureIndexesAsync(ct);
        return await _configs.Find(F.Eq(c => c.TenantKey, tenantKey) & F.Eq(c => c.Id, id)).FirstOrDefaultAsync(ct);
    }

    public async Task<TenantProviderConfig?> GetDefaultAsync(string tenantKey, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct);
        return await _configs.Find(F.Eq(c => c.TenantKey, tenantKey) & F.Eq(c => c.IsDefault, true) & F.Eq(c => c.RemovedAt, null))
            .SortByDescending(c => c.UpdatedAt).FirstOrDefaultAsync(ct);
    }

    public async Task InsertAsync(TenantProviderConfig config, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct);
        await _configs.InsertOneAsync(config, cancellationToken: ct);
    }

    public async Task<bool> ReplaceAsync(TenantProviderConfig config, int expectedVersion, CancellationToken ct)
    {
        var result = await _configs.ReplaceOneAsync(
            F.Eq(c => c.TenantKey, config.TenantKey) & F.Eq(c => c.Id, config.Id) & F.Eq(c => c.Version, expectedVersion),
            config, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public Task ClearDefaultAsync(string tenantKey, string? exceptId, CancellationToken ct)
    {
        var filter = F.Eq(c => c.TenantKey, tenantKey) & F.Eq(c => c.IsDefault, true);
        if (!string.IsNullOrEmpty(exceptId)) filter &= F.Ne(c => c.Id, exceptId);
        return _configs.UpdateManyAsync(filter, Builders<TenantProviderConfig>.Update
            .Set(c => c.IsDefault, false)
            .Set(c => c.UpdatedAt, DateTime.UtcNow)
            .Inc(c => c.Version, 1), cancellationToken: ct);
    }

    public Task UpdateStatusAsync(string tenantKey, string id, TenantProviderStatus status, string? error, long? latencyMs, int? modelCount, DateTime at, CancellationToken ct)
    {
        var update = Builders<TenantProviderConfig>.Update.Set(c => c.Status, status).Set(c => c.LastError, error);
        if (latencyMs is not null)
        {
            // A connection test (chat requests only update the status).
            update = update.Set(c => c.LastTestedAt, at).Set(c => c.LastLatencyMs, latencyMs);
            if (modelCount is not null) update = update.Set(c => c.ModelCount, modelCount);
        }
        return _configs.UpdateOneAsync(F.Eq(c => c.TenantKey, tenantKey) & F.Eq(c => c.Id, id) & F.Eq(c => c.RemovedAt, null), update, cancellationToken: ct);
    }

    public async Task<TenantProviderSecret?> GetSecretAsync(string tenantKey, string configId, string secretId, CancellationToken ct)
    {
        if (!SystemDbContext.IsObjectId(secretId)) return null;
        return await _secrets.Find(S.Eq(s => s.TenantKey, tenantKey) & S.Eq(s => s.ConfigId, configId) & S.Eq(s => s.Id, secretId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task InsertSecretAsync(TenantProviderSecret secret, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct);
        await _secrets.InsertOneAsync(secret, cancellationToken: ct);
    }

    public Task DeleteSecretsAsync(string tenantKey, string configId, string? exceptSecretId, CancellationToken ct)
    {
        var filter = S.Eq(s => s.TenantKey, tenantKey) & S.Eq(s => s.ConfigId, configId);
        if (!string.IsNullOrEmpty(exceptSecretId)) filter &= S.Ne(s => s.Id, exceptSecretId);
        return _secrets.DeleteManyAsync(filter, ct);
    }

    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        if (_indexed) return;
        await _indexLock.WaitAsync(ct);
        try
        {
            if (_indexed) return;
            await _configs.Indexes.CreateManyAsync(new[]
            {
                new CreateIndexModel<TenantProviderConfig>(Builders<TenantProviderConfig>.IndexKeys
                    .Ascending(c => c.TenantKey).Ascending(c => c.RemovedAt).Ascending(c => c.DisplayName)),
                new CreateIndexModel<TenantProviderConfig>(Builders<TenantProviderConfig>.IndexKeys
                    .Ascending(c => c.TenantKey).Ascending(c => c.IsDefault))
            }, ct);
            await _secrets.Indexes.CreateOneAsync(new CreateIndexModel<TenantProviderSecret>(
                Builders<TenantProviderSecret>.IndexKeys.Ascending(s => s.TenantKey).Ascending(s => s.ConfigId)), cancellationToken: ct);
            _indexed = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block the feature because of an index problem; retried on the next call.
            _logger.LogWarning("Indexes of {Collection} could not be created ({ErrorType})", ConfigCollection, ex.GetType().Name);
        }
        finally
        {
            _indexLock.Release();
        }
    }
}

/// <summary>
/// ASP.NET Core Data Protection (the same key ring as the existing provider keys — DataProtection:KeysPath) with a
/// purpose chain bound to the tenant and the configuration: a ciphertext copied to another company or configuration
/// cannot be decrypted.
/// </summary>
public sealed class DataProtectionTenantSecretProtector : ITenantSecretProtector
{
    public const string Purpose = "MySale.AI.TenantProviderSecrets.v1";
    private readonly IDataProtector _root;

    public DataProtectionTenantSecretProtector(IDataProtectionProvider provider) => _root = provider.CreateProtector(Purpose);

    public string Protect(string tenantKey, string configId, string plaintext)
        => _root.CreateProtector(tenantKey, configId).Protect(plaintext);

    public string? Unprotect(string tenantKey, string configId, string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return _root.CreateProtector(tenantKey, configId).Unprotect(ciphertext); }
        catch (CryptographicException) { return null; }
    }
}
