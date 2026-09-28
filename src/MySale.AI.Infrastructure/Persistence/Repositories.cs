using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;

namespace MySale.AI.Infrastructure.Persistence;

public sealed class UserRepository : IUserRepository
{
    private readonly SystemDbContext _db;
    public UserRepository(SystemDbContext db) => _db = db;

    public async Task<AppUser?> FindByUserNameAsync(string userName, CancellationToken ct)
        => await _db.Users.Find(u => u.UserName == userName).FirstOrDefaultAsync(ct);

    public async Task<AppUser?> GetAsync(string id, CancellationToken ct)
        => SystemDbContext.IsObjectId(id) ? await _db.Users.Find(u => u.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task<List<AppUser>> ListAsync(CancellationToken ct)
        => _db.Users.Find(FilterDefinition<AppUser>.Empty).SortBy(u => u.UserName).ToListAsync(ct);

    public async Task UpsertAsync(AppUser user, CancellationToken ct)
    {
        var existing = await FindByUserNameAsync(user.UserName, ct);
        if (existing is null)
        {
            await _db.Users.InsertOneAsync(user, cancellationToken: ct);
        }
        else
        {
            user.Id = existing.Id;
            await _db.Users.ReplaceOneAsync(u => u.Id == existing.Id, user, cancellationToken: ct);
        }
    }

    public Task UpdateLastLoginAsync(string id, DateTime at, CancellationToken ct)
        => _db.Users.UpdateOneAsync(u => u.Id == id, Builders<AppUser>.Update.Set(u => u.LastLoginAt, at), cancellationToken: ct);
}

public sealed class ProviderRepository : IProviderRepository
{
    private readonly SystemDbContext _db;
    public ProviderRepository(SystemDbContext db) => _db = db;

    public Task<List<ProviderConfig>> ListAsync(CancellationToken ct)
        => _db.Providers.Find(FilterDefinition<ProviderConfig>.Empty).SortBy(p => p.Name).ToListAsync(ct);

    public async Task<ProviderConfig?> GetAsync(string id, CancellationToken ct)
        => SystemDbContext.IsObjectId(id) ? await _db.Providers.Find(p => p.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task InsertAsync(ProviderConfig config, CancellationToken ct)
        => _db.Providers.InsertOneAsync(config, cancellationToken: ct);

    public Task UpdateAsync(ProviderConfig config, CancellationToken ct)
        => _db.Providers.ReplaceOneAsync(p => p.Id == config.Id, config, cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct)
        => _db.Providers.DeleteOneAsync(p => p.Id == id, ct);

    public Task ClearDefaultAsync(string exceptId, CancellationToken ct)
        => _db.Providers.UpdateManyAsync(p => p.Id != exceptId && p.IsDefault,
            Builders<ProviderConfig>.Update.Set(p => p.IsDefault, false), cancellationToken: ct);

    public Task UpdateTestStatusAsync(string id, ProviderStatus status, string? message, DateTime at, CancellationToken ct)
        => _db.Providers.UpdateOneAsync(p => p.Id == id, Builders<ProviderConfig>.Update
            .Set(p => p.LastTestStatus, status)
            .Set(p => p.LastTestMessage, message)
            .Set(p => p.LastTestedAt, at), cancellationToken: ct);
}

public sealed class ConversationRepository : IConversationRepository
{
    private readonly SystemDbContext _db;
    public ConversationRepository(SystemDbContext db) => _db = db;

    public Task<List<Conversation>> ListAsync(string userId, string companyId, string? search, bool? archived, int limit, CancellationToken ct)
    {
        var f = Builders<Conversation>.Filter;
        var filter = f.Eq(c => c.UserId, userId) & f.Eq(c => c.CompanyId, companyId);
        if (archived.HasValue) filter &= f.Eq(c => c.Archived, archived.Value);
        if (!string.IsNullOrWhiteSpace(search))
            filter &= f.Regex(c => c.Title, new BsonRegularExpression(Regex.Escape(search), "i"));
        return _db.Conversations.Find(filter).SortByDescending(c => c.UpdatedAt).Limit(limit).ToListAsync(ct);
    }

    public async Task<Conversation?> GetAsync(string id, string userId, string companyId, CancellationToken ct)
        => SystemDbContext.IsObjectId(id)
            ? await _db.Conversations.Find(c => c.Id == id && c.UserId == userId && c.CompanyId == companyId).FirstOrDefaultAsync(ct)
            : null;

    public async Task<(List<Conversation> Items, long Total)> SearchByCompanyAsync(string companyId, string? search, DateTime? from, DateTime? to,
        int page, int pageSize, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(companyId)) return (new List<Conversation>(), 0); // never "all tenants"
        var f = Builders<Conversation>.Filter;
        var filter = f.Eq(c => c.CompanyId, companyId);
        if (from is { } a) filter &= f.Gte(c => c.UpdatedAt, a.ToUniversalTime());
        if (to is { } b) filter &= f.Lte(c => c.UpdatedAt, b.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filter &= f.Regex(c => c.Title, new BsonRegularExpression(Regex.Escape(term[..Math.Min(term.Length, 200)]), "i"));
        }
        var total = await _db.Conversations.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _db.Conversations.Find(filter).SortByDescending(c => c.UpdatedAt)
            .Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<Conversation?> GetForCompanyAsync(string id, string companyId, CancellationToken ct)
        => SystemDbContext.IsObjectId(id) && !string.IsNullOrEmpty(companyId)
            ? await _db.Conversations.Find(c => c.Id == id && c.CompanyId == companyId).FirstOrDefaultAsync(ct)
            : null;

    public Task<long> CountByCompanyAsync(string companyId, DateTime from, CancellationToken ct)
        => string.IsNullOrEmpty(companyId)
            ? Task.FromResult(0L)
            : _db.Conversations.CountDocumentsAsync(c => c.CompanyId == companyId && c.UpdatedAt >= from, cancellationToken: ct);

    public Task InsertAsync(Conversation conversation, CancellationToken ct)
        => _db.Conversations.InsertOneAsync(conversation, cancellationToken: ct);

    public Task UpdateAsync(Conversation conversation, CancellationToken ct)
        => _db.Conversations.ReplaceOneAsync(c => c.Id == conversation.Id, conversation, cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct)
        => _db.Conversations.DeleteOneAsync(c => c.Id == id, ct);
}

public sealed class MessageRepository : IMessageRepository
{
    private readonly SystemDbContext _db;
    public MessageRepository(SystemDbContext db) => _db = db;

    public Task<List<ChatMessage>> ListAsync(string conversationId, CancellationToken ct)
        => _db.Messages.Find(m => m.ConversationId == conversationId).SortBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(ct);

    public async Task<List<ChatMessage>> ListRecentAsync(string conversationId, int count, CancellationToken ct)
    {
        var list = await _db.Messages.Find(m => m.ConversationId == conversationId)
            .SortByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Limit(count).ToListAsync(ct);
        list.Reverse();
        return list;
    }

    public async Task<ChatMessage?> GetAsync(string id, CancellationToken ct)
        => SystemDbContext.IsObjectId(id) ? await _db.Messages.Find(m => m.Id == id).FirstOrDefaultAsync(ct) : null;

    public Task InsertAsync(ChatMessage message, CancellationToken ct)
        => _db.Messages.InsertOneAsync(message, cancellationToken: ct);

    public Task UpdateAsync(ChatMessage message, CancellationToken ct)
        => _db.Messages.ReplaceOneAsync(m => m.Id == message.Id, message, cancellationToken: ct);

    public Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var valid = ids.Where(SystemDbContext.IsObjectId).ToList();
        return valid.Count == 0 ? Task.CompletedTask : _db.Messages.DeleteManyAsync(Builders<ChatMessage>.Filter.In(m => m.Id, valid), ct);
    }

    public Task DeleteByConversationAsync(string conversationId, CancellationToken ct)
        => _db.Messages.DeleteManyAsync(m => m.ConversationId == conversationId, ct);

    public Task<long> CountAsync(string conversationId, CancellationToken ct)
        => _db.Messages.CountDocumentsAsync(m => m.ConversationId == conversationId, cancellationToken: ct);
}

public sealed class QueryLogRepository : IQueryLogRepository
{
    private readonly SystemDbContext _db;
    public QueryLogRepository(SystemDbContext db) => _db = db;

    public Task InsertAsync(QueryLog log, CancellationToken ct) => _db.QueryLogs.InsertOneAsync(log, cancellationToken: ct);

    public Task UpdateAsync(QueryLog log, CancellationToken ct) => _db.QueryLogs.ReplaceOneAsync(l => l.Id == log.Id, log, cancellationToken: ct);

    public async Task<QueryLog?> GetAsync(string id, CancellationToken ct)
        => SystemDbContext.IsObjectId(id) ? await _db.QueryLogs.Find(l => l.Id == id).FirstOrDefaultAsync(ct) : null;

    public async Task<(List<QueryLog> Items, long Total)> SearchAsync(QueryLogFilter filter, CancellationToken ct)
    {
        var f = Builders<QueryLog>.Filter;
        var q = f.Empty;
        if (filter.From.HasValue) q &= f.Gte(l => l.CreatedAt, filter.From.Value.ToUniversalTime());
        if (filter.To.HasValue) q &= f.Lt(l => l.CreatedAt, filter.To.Value.ToUniversalTime());
        if (!string.IsNullOrEmpty(filter.ProviderId)) q &= f.Eq(l => l.ProviderId, filter.ProviderId);
        if (!string.IsNullOrEmpty(filter.Model)) q &= f.Eq(l => l.Model, filter.Model);
        if (filter.Status.HasValue) q &= f.Eq(l => l.Status, filter.Status.Value);
        if (!string.IsNullOrEmpty(filter.UserId)) q &= f.Eq(l => l.UserId, filter.UserId);
        if (!string.IsNullOrEmpty(filter.Operation)) q &= f.Eq(l => l.Operation, filter.Operation);
        if (!string.IsNullOrEmpty(filter.CompanyId)) q &= f.Eq(l => l.CompanyId, filter.CompanyId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
            q &= f.Regex(l => l.Question, new BsonRegularExpression(Regex.Escape(filter.Search.Trim()), "i"));

        var total = await _db.QueryLogs.CountDocumentsAsync(q, cancellationToken: ct);
        var items = await _db.QueryLogs.Find(q)
            .Project<QueryLog>(Builders<QueryLog>.Projection.Exclude(l => l.DebugTraceJson).Exclude(l => l.GeneratedMql))
            .SortByDescending(l => l.CreatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Limit(filter.PageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public Task<List<QueryLog>> RecentAsync(string? companyId, int count, CancellationToken ct)
    {
        var filter = string.IsNullOrEmpty(companyId)
            ? Builders<QueryLog>.Filter.Empty
            : Builders<QueryLog>.Filter.Eq(l => l.CompanyId, companyId);
        return _db.QueryLogs.Find(filter)
            .Project<QueryLog>(Builders<QueryLog>.Projection.Exclude(l => l.DebugTraceJson).Exclude(l => l.GeneratedMql))
            .SortByDescending(l => l.CreatedAt).Limit(count).ToListAsync(ct);
    }

    public Task<List<UsageRow>> GetUsageRowsAsync(DateTime from, DateTime to, string? companyId, CancellationToken ct)
    {
        var f = Builders<QueryLog>.Filter;
        var filter = f.Gte(l => l.CreatedAt, from) & f.Lt(l => l.CreatedAt, to);
        if (!string.IsNullOrEmpty(companyId)) filter &= f.Eq(l => l.CompanyId, companyId);
        return _db.QueryLogs.Find(filter)
            .Project(l => new UsageRow
            {
                CreatedAt = l.CreatedAt,
                ProviderId = l.ProviderId,
                ProviderName = l.ProviderName,
                ProviderKind = l.ProviderKind,
                IsLocalProvider = l.IsLocalProvider,
                Model = l.Model,
                Status = l.Status,
                QueryGenerated = l.QueryGenerated,
                ValidationPassed = l.ValidationPassed,
                Blocked = l.Blocked,
                Executed = l.Executed,
                ExecutionTimeMs = l.ExecutionTimeMs,
                TotalTimeMs = l.TotalTimeMs,
                InputTokens = l.InputTokens,
                OutputTokens = l.OutputTokens,
                EstimatedCost = l.EstimatedCost
            })
            .ToListAsync(ct);
    }

    public async Task<List<string>> DistinctModelsAsync(CancellationToken ct)
    {
        var cursor = await _db.QueryLogs.DistinctAsync(l => l.Model, FilterDefinition<QueryLog>.Empty, cancellationToken: ct);
        var list = await cursor.ToListAsync(ct);
        return list.Where(m => m is not null).Select(m => m!).ToList();
    }
}

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly SystemDbContext _db;
    public SettingsRepository(SystemDbContext db) => _db = db;

    public async Task<AppSettings?> GetAsync(CancellationToken ct)
        => await _db.Settings.Find(s => s.Id == "global").FirstOrDefaultAsync(ct);

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        settings.Id = "global";
        return _db.Settings.ReplaceOneAsync(s => s.Id == "global", settings, new ReplaceOptions { IsUpsert = true }, ct);
    }
}

public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly SystemDbContext _db;
    public AuditLogRepository(SystemDbContext db) => _db = db;

    public Task InsertAsync(AuditLog log, CancellationToken ct) => _db.AuditLogs.InsertOneAsync(log, cancellationToken: ct);

    public async Task<(List<AuditLog> Items, long Total)> SearchForTenantAsync(AuditLogFilter f, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(f.CompanyId) && string.IsNullOrEmpty(f.TenantRef)) return (new List<AuditLog>(), 0);
        var b = Builders<AuditLog>.Filter;
        var scopes = new List<FilterDefinition<AuditLog>>();
        if (!string.IsNullOrEmpty(f.CompanyId)) scopes.Add(b.Eq(l => l.CompanyId, f.CompanyId));
        if (!string.IsNullOrEmpty(f.TenantRef)) scopes.Add(b.Eq(l => l.TenantRef, f.TenantRef));
        var filter = b.Or(scopes);
        if (!string.IsNullOrWhiteSpace(f.Action))
            filter &= f.Action.EndsWith('*')
                ? b.Regex(l => l.Action, new BsonRegularExpression("^" + Regex.Escape(f.Action.TrimEnd('*'))))
                : b.Eq(l => l.Action, f.Action);
        if (!string.IsNullOrWhiteSpace(f.UserId)) filter &= b.Eq(l => l.UserId, f.UserId);
        if (f.From is { } from) filter &= b.Gte(l => l.CreatedAt, from.ToUniversalTime());
        if (f.To is { } to) filter &= b.Lte(l => l.CreatedAt, to.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = f.Search.Trim();
            var rx = new BsonRegularExpression(Regex.Escape(term[..Math.Min(term.Length, 200)]), "i");
            filter &= b.Or(b.Regex(l => l.Action, rx), b.Regex(l => l.Resource, rx), b.Regex(l => l.UserName, rx), b.Eq(l => l.ResourceId, term));
        }
        var total = await _db.AuditLogs.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _db.AuditLogs.Find(filter).SortByDescending(l => l.CreatedAt)
            .Skip((f.Page - 1) * f.PageSize).Limit(f.PageSize).ToListAsync(ct);
        return (items, total);
    }
}

public sealed class DatabaseConfigStore
{
    private readonly SystemDbContext _db;
    public DatabaseConfigStore(SystemDbContext db) => _db = db;

    public async Task<DatabaseConfig?> GetAsync(CancellationToken ct)
        => await _db.DatabaseConfigs.Find(c => c.Id == "business").FirstOrDefaultAsync(ct);

    public Task SaveAsync(DatabaseConfig config, CancellationToken ct)
    {
        config.Id = "business";
        return _db.DatabaseConfigs.ReplaceOneAsync(c => c.Id == "business", config, new ReplaceOptions { IsUpsert = true }, ct);
    }
}

public sealed class SchemaMetadataRepository : ISchemaMetadataRepository
{
    private readonly SystemDbContext _db;
    public SchemaMetadataRepository(SystemDbContext db) => _db = db;

    public Task<List<SchemaMetadata>> ListAsync(CancellationToken ct)
        => _db.SchemaMetadata.Find(FilterDefinition<SchemaMetadata>.Empty).ToListAsync(ct);

    public async Task<SchemaMetadata?> GetAsync(string collection, CancellationToken ct)
        => await _db.SchemaMetadata.Find(m => m.Id == collection).FirstOrDefaultAsync(ct);

    public Task SaveAsync(SchemaMetadata metadata, CancellationToken ct)
        => _db.SchemaMetadata.ReplaceOneAsync(m => m.Id == metadata.Id, metadata, new ReplaceOptions { IsUpsert = true }, ct);

    public Task DeleteAsync(string collection, CancellationToken ct)
        => _db.SchemaMetadata.DeleteOneAsync(m => m.Id == collection, ct);
}

/// <summary>
/// Conversation state in the system database ("conversation_states"). The owner (tenant + user + customer database)
/// is part of every filter, so a conversation id of another user or tenant never matches; the turn lease is taken
/// with one atomic find-and-update.
/// </summary>
public sealed class ConversationStateRepository : IConversationStateRepository
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(30);
    private readonly SystemDbContext _db;
    public ConversationStateRepository(SystemDbContext db) => _db = db;

    public async Task<(TurnStart Result, ConversationState? State)> TryBeginTurnAsync(string conversationId, string companyId, string userId,
        string? databaseName, string turnId, DateTime now, TimeSpan staleAfter, CancellationToken ct)
    {
        var f = Builders<ConversationState>.Filter;
        var owner = f.Eq(s => s.Id, conversationId) & f.Eq(s => s.CompanyId, companyId) & f.Eq(s => s.UserId, userId)
                    & f.Eq(s => s.DatabaseName, databaseName);
        var idle = f.Eq(s => s.ActiveTurnId, (string?)null) | f.Lt(s => s.ActiveTurnStartedAt, now - staleAfter);
        var update = Builders<ConversationState>.Update
            .Set(s => s.ActiveTurnId, turnId)
            .Set(s => s.ActiveTurnStartedAt, now)
            .Set(s => s.UpdatedAt, now)
            .Set(s => s.ExpiresAt, now + IdleLifetime)
            .SetOnInsert(s => s.CreatedAt, now)
            .SetOnInsert(s => s.Version, 0L);
        try
        {
            var state = await _db.ConversationStates.FindOneAndUpdateAsync(owner & idle, update,
                new FindOneAndUpdateOptions<ConversationState> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
            return state is null ? (TurnStart.Busy, null) : (TurnStart.Started, state);
        }
        catch (Exception ex) when (IsDuplicateKey(ex))
        {
            // The id exists but did not match: either another turn holds the lease or it belongs to someone else.
            var existing = await _db.ConversationStates.Find(f.Eq(s => s.Id, conversationId)).FirstOrDefaultAsync(ct);
            if (existing is not null && (existing.CompanyId != companyId || existing.UserId != userId
                                         || !string.Equals(existing.DatabaseName, databaseName, StringComparison.Ordinal)))
                return (TurnStart.NotOwner, null);
            return (TurnStart.Busy, null);
        }
    }

    public async Task<bool> SaveAsync(ConversationState state, string turnId, CancellationToken ct)
    {
        var f = Builders<ConversationState>.Filter;
        var now = DateTime.UtcNow;
        state.ActiveTurnId = null;
        state.ActiveTurnStartedAt = null;
        state.Version++;
        state.UpdatedAt = now;
        state.ExpiresAt = now + IdleLifetime;
        var result = await _db.ConversationStates.ReplaceOneAsync(f.Eq(s => s.Id, state.Id) & f.Eq(s => s.ActiveTurnId, turnId), state,
            cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    public Task ReleaseAsync(string conversationId, string turnId, CancellationToken ct)
        => _db.ConversationStates.UpdateOneAsync(
            Builders<ConversationState>.Filter.Eq(s => s.Id, conversationId) & Builders<ConversationState>.Filter.Eq(s => s.ActiveTurnId, turnId),
            Builders<ConversationState>.Update.Set(s => s.ActiveTurnId, (string?)null).Set(s => s.ActiveTurnStartedAt, (DateTime?)null),
            cancellationToken: ct);

    public Task DeleteAsync(string conversationId, string companyId, string userId, CancellationToken ct)
        => _db.ConversationStates.DeleteOneAsync(s => s.Id == conversationId && s.CompanyId == companyId && s.UserId == userId, ct);

    private static bool IsDuplicateKey(Exception ex) => ex switch
    {
        MongoWriteException w => w.WriteError?.Category == ServerErrorCategory.DuplicateKey,
        MongoCommandException c => c.Code == 11000,
        _ => false
    };
}
