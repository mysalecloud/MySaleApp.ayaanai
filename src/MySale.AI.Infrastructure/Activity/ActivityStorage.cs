using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.Persistence;

namespace MySale.AI.Infrastructure.ActivityTracking;

/// <summary>
/// Activity log in the agent's own system MongoDB (collections "ai_activities" and "ai_conversation_activity").
/// It never touches the customer's database. Replace this class to store the log elsewhere.
/// </summary>
public sealed class MongoActivityRepository : IAIActivityRepository
{
    private readonly SystemDbContext _db;
    public MongoActivityRepository(SystemDbContext db) => _db = db;

    private IMongoCollection<AIActivity> Activities => _db.Activities;
    private IMongoCollection<AIConversationActivity> Conversations => _db.ConversationActivity;

    public Task InsertAsync(AIActivity activity, CancellationToken ct)
        => Activities.InsertOneAsync(activity, cancellationToken: ct);

    public Task UpsertConversationAsync(ConversationActivityUpdate u, CancellationToken ct)
    {
        var update = Builders<AIConversationActivity>.Update
            .SetOnInsert(c => c.CreatedAt, u.At)
            .SetOnInsert(c => c.UserId, u.UserId)
            .SetOnInsert(c => c.TenantRef, u.TenantRef)
            .Set(c => c.Title, u.Title)
            .Set(c => c.LastActivityAt, u.At)
            .Set(c => c.Status, u.Status)
            .Set(c => c.LastProvider, u.Provider)
            .Set(c => c.LastModel, u.Model)
            .Max(c => c.MessageCount, u.MessageCount)
            .Inc(c => c.TotalRequests, 1)
            .Inc(c => c.SuccessfulRequests, u.Succeeded ? 1 : 0)
            .Inc(c => c.FailedRequests, u.Succeeded ? 0 : 1)
            .Inc(c => c.TotalExecutionMs, u.TotalMs)
            .Inc(c => c.TotalAiMs, u.AiMs)
            .Inc(c => c.TotalMongoMs, u.MongoMs);
        return Conversations.UpdateOneAsync(c => c.Id == u.ConversationId, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<(List<AIActivity> Items, long Total)> SearchAsync(ActivityFilter f, CancellationToken ct)
    {
        var b = Builders<AIActivity>.Filter;
        var filter = b.Empty;
        if (!string.IsNullOrWhiteSpace(f.ActivityId)) filter &= b.Eq(a => a.ActivityId, f.ActivityId.Trim());
        if (!string.IsNullOrWhiteSpace(f.ConversationId)) filter &= b.Eq(a => a.ConversationId, f.ConversationId.Trim());
        if (!string.IsNullOrWhiteSpace(f.CorrelationId)) filter &= b.Eq(a => a.CorrelationId, f.CorrelationId.Trim());
        if (f.From is { } from) filter &= b.Gte(a => a.Timestamp, from.ToUniversalTime());
        if (f.To is { } to) filter &= b.Lte(a => a.Timestamp, to.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(f.Status))
            filter &= string.Equals(f.Status, "failed", StringComparison.OrdinalIgnoreCase)
                ? b.Nin(a => a.Status, new[] { ActivityStatuses.Success, ActivityStatuses.NoResults })
                : b.Eq(a => a.Status, f.Status);
        if (!string.IsNullOrWhiteSpace(f.FailedStage)) filter &= b.Eq(a => a.FailedStage, f.FailedStage);
        if (!string.IsNullOrWhiteSpace(f.Provider)) filter &= b.Eq(a => a.Ai.ProviderName, f.Provider);
        if (!string.IsNullOrWhiteSpace(f.Model)) filter &= b.Eq(a => a.Ai.Model, f.Model);
        if (!string.IsNullOrWhiteSpace(f.TenantRef)) filter &= b.Eq(a => a.TenantRef, f.TenantRef);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = f.Search.Trim();
            var regex = new BsonRegularExpression(Regex.Escape(term[..Math.Min(term.Length, 200)]), "i");
            filter &= b.Or(
                b.Regex(a => a.Request.Question, regex),
                b.Eq(a => a.ActivityId, term),
                b.Eq(a => a.CorrelationId, term),
                b.Eq(a => a.ConversationId, term),
                b.Eq(a => a.Request.SessionId, term),
                b.Eq(a => a.Query!.QueryHash, term));
        }

        var total = await Activities.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await Activities.Find(filter)
            .SortByDescending(a => a.Timestamp)
            .Skip((f.Page - 1) * f.PageSize)
            .Limit(f.PageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<AIActivity?> GetAsync(string activityId, CancellationToken ct)
        => await Activities.Find(a => a.ActivityId == activityId).FirstOrDefaultAsync(ct);

    public Task<List<AIActivity>> ListByConversationAsync(string conversationId, int limit, CancellationToken ct)
        => Activities.Find(a => a.ConversationId == conversationId).SortBy(a => a.Timestamp).Limit(limit).ToListAsync(ct);

    public async Task<(List<AIConversationActivity> Items, long Total)> SearchConversationsAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        var b = Builders<AIConversationActivity>.Filter;
        var filter = b.Empty;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filter = b.Or(
                b.Eq(c => c.Id, term),
                b.Regex(c => c.Title, new BsonRegularExpression(Regex.Escape(term[..Math.Min(term.Length, 200)]), "i")));
        }
        var total = await Conversations.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await Conversations.Find(filter).SortByDescending(c => c.LastActivityAt)
            .Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<AIConversationActivity?> GetConversationAsync(string conversationId, CancellationToken ct)
        => await Conversations.Find(c => c.Id == conversationId).FirstOrDefaultAsync(ct);

    public async Task<List<string>> DistinctAsync(string field, CancellationToken ct)
    {
        FieldDefinition<AIActivity, string> path = field switch
        {
            "provider" => "Ai.ProviderName",
            "model" => "Ai.Model",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        var values = await (await Activities.DistinctAsync(path, Builders<AIActivity>.Filter.Empty, cancellationToken: ct)).ToListAsync(ct);
        return values.Where(v => !string.IsNullOrEmpty(v)).OrderBy(v => v).ToList();
    }

    public Task<List<AIActivity>> RecentForStatsAsync(DateTime from, int limit, CancellationToken ct)
        => Activities.Find(a => a.Timestamp >= from)
            .Project<AIActivity>(Builders<AIActivity>.Projection
                .Include(a => a.Status).Include(a => a.FailedStage).Include(a => a.Performance).Include(a => a.Timestamp))
            .SortByDescending(a => a.Timestamp)
            .Limit(limit)
            .ToListAsync(ct);

    public async Task<long> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct)
    {
        var removed = await Activities.DeleteManyAsync(a => a.Timestamp < cutoff, ct);
        await Conversations.DeleteManyAsync(c => c.LastActivityAt < cutoff, ct);
        return removed.DeletedCount;
    }
}

/// <summary>
/// Write-only activity sink backed by a bounded in-memory queue and a background writer. The chat request only
/// enqueues (never waits for the database); if the activity database is unavailable, writes are retried a few
/// times and then dropped with a warning — the AI request keeps working.
/// </summary>
public sealed class ActivityWriterService : BackgroundService, IAIActivitySink
{
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };

    private readonly Channel<(AIActivity Activity, ConversationActivityUpdate? Conversation)> _channel;
    private readonly IAIActivityRepository _repository;
    private readonly ILogger<ActivityWriterService> _logger;
    private DateTime _lastFailureLog = DateTime.MinValue;
    private long _dropped;

    public ActivityWriterService(IAIActivityRepository repository, ActivityOptions options, ILogger<ActivityWriterService> logger)
    {
        _repository = repository;
        _logger = logger;
        _channel = Channel.CreateBounded<(AIActivity, ConversationActivityUpdate?)>(new BoundedChannelOptions(Math.Max(10, options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full → counted as dropped
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool TryEnqueue(AIActivity activity, ConversationActivityUpdate? conversation)
    {
        try
        {
            if (_channel.Writer.TryWrite((activity, conversation))) return true;
            Interlocked.Increment(ref _dropped);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
                await WriteAsync(item.Activity, item.Conversation, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: flush what is queued (bounded time).
            using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!flush.IsCancellationRequested && _channel.Reader.TryRead(out var item))
                await WriteAsync(item.Activity, item.Conversation, flush.Token, retry: false);
        }
    }

    private async Task WriteAsync(AIActivity activity, ConversationActivityUpdate? conversation, CancellationToken ct, bool retry = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _repository.InsertAsync(activity, ct);
                if (conversation is not null) await _repository.UpsertConversationAsync(conversation, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                return; // already written by an earlier attempt
            }
            catch (Exception ex)
            {
                if (retry && attempt < RetryDelays.Length)
                {
                    try { await Task.Delay(RetryDelays[attempt], ct); } catch (OperationCanceledException) { return; }
                    continue;
                }
                var now = DateTime.UtcNow;
                if (now - _lastFailureLog > TimeSpan.FromMinutes(1))
                {
                    _lastFailureLog = now;
                    _logger.LogWarning(ex, "Activity log write failed; activity {ActivityId} (correlation {CorrelationId}) was not stored. Dropped so far: {Dropped}",
                        activity.ActivityId, activity.CorrelationId, Interlocked.Read(ref _dropped));
                }
                Interlocked.Increment(ref _dropped);
                return;
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }
}

/// <summary>Deletes activity records older than Activity:RetentionDays (AI_ACTIVITY_RETENTION_DAYS). Runs every 6 hours.</summary>
public sealed class ActivityRetentionService : BackgroundService
{
    private readonly IAIActivityRepository _repository;
    private readonly ActivityOptions _options;
    private readonly ILogger<ActivityRetentionService> _logger;

    public ActivityRetentionService(IAIActivityRepository repository, ActivityOptions options, ILogger<ActivityRetentionService> logger)
    {
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var days = Math.Max(1, _options.RetentionDays);
                var removed = await _repository.PurgeOlderThanAsync(DateTime.UtcNow.AddDays(-days), stoppingToken);
                if (removed > 0) _logger.LogInformation("Activity retention: removed {Count} records older than {Days} days", removed, days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Activity retention cleanup failed");
            }
            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
