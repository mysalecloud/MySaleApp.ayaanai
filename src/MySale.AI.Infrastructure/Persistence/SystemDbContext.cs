using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MySale.AI.Domain;

namespace MySale.AI.Infrastructure.Persistence;

/// <summary>The agent's own database (users, providers, conversations, logs, settings).</summary>
public sealed class SystemDbContext
{
    private static readonly object MapLock = new();
    private static bool _mapped;

    public SystemDbContext(IOptions<SystemDbOptions> options)
    {
        RegisterMappings();
        var settings = MongoClientSettings.FromConnectionString(options.Value.ConnectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.ApplicationName = "mysale-ai-agent";
        Client = new MongoClient(settings);
        Database = Client.GetDatabase(options.Value.DatabaseName);
    }

    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }

    public IMongoCollection<AppUser> Users => Database.GetCollection<AppUser>("users");
    public IMongoCollection<ProviderConfig> Providers => Database.GetCollection<ProviderConfig>("ai_providers");
    public IMongoCollection<Conversation> Conversations => Database.GetCollection<Conversation>("conversations");
    public IMongoCollection<ChatMessage> Messages => Database.GetCollection<ChatMessage>("messages");
    public IMongoCollection<QueryLog> QueryLogs => Database.GetCollection<QueryLog>("query_logs");
    public IMongoCollection<AppSettings> Settings => Database.GetCollection<AppSettings>("app_settings");
    public IMongoCollection<DatabaseConfig> DatabaseConfigs => Database.GetCollection<DatabaseConfig>("database_config");
    public IMongoCollection<AuditLog> AuditLogs => Database.GetCollection<AuditLog>("audit_logs");
    public IMongoCollection<SchemaMetadata> SchemaMetadata => Database.GetCollection<SchemaMetadata>("schema_metadata");
    public IMongoCollection<Attachment> Attachments => Database.GetCollection<Attachment>("attachments");
    /// <summary>AYAAN AI activity log (one document per AI request). Written only by the background activity writer.</summary>
    public IMongoCollection<AIActivity> Activities => Database.GetCollection<AIActivity>("ai_activities");
    public IMongoCollection<AIConversationActivity> ConversationActivity => Database.GetCollection<AIConversationActivity>("ai_conversation_activity");

    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await Users.Indexes.CreateOneAsync(new CreateIndexModel<AppUser>(
            Builders<AppUser>.IndexKeys.Ascending(u => u.UserName), new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        await Conversations.Indexes.CreateOneAsync(new CreateIndexModel<Conversation>(
            Builders<Conversation>.IndexKeys.Ascending(c => c.UserId).Ascending(c => c.CompanyId).Descending(c => c.UpdatedAt)), cancellationToken: ct);
        await Messages.Indexes.CreateOneAsync(new CreateIndexModel<ChatMessage>(
            Builders<ChatMessage>.IndexKeys.Ascending(m => m.ConversationId).Ascending(m => m.CreatedAt)), cancellationToken: ct);
        await QueryLogs.Indexes.CreateOneAsync(new CreateIndexModel<QueryLog>(
            Builders<QueryLog>.IndexKeys.Descending(l => l.CreatedAt)), cancellationToken: ct);
        await QueryLogs.Indexes.CreateOneAsync(new CreateIndexModel<QueryLog>(
            Builders<QueryLog>.IndexKeys.Ascending(l => l.CompanyId).Descending(l => l.CreatedAt)), cancellationToken: ct);
        await AuditLogs.Indexes.CreateOneAsync(new CreateIndexModel<AuditLog>(
            Builders<AuditLog>.IndexKeys.Descending(l => l.CreatedAt)), cancellationToken: ct);
        await Attachments.Indexes.CreateOneAsync(new CreateIndexModel<Attachment>(
            Builders<Attachment>.IndexKeys.Ascending(a => a.UserId).Ascending(a => a.CompanyId).Descending(a => a.CreatedAt)), cancellationToken: ct);
        await Attachments.Indexes.CreateOneAsync(new CreateIndexModel<Attachment>(
            Builders<Attachment>.IndexKeys.Ascending(a => a.ExpiresAt)), cancellationToken: ct);
    }

    /// <summary>Indexes of the activity log (separate so a problem here never blocks the rest of the startup).</summary>
    public async Task EnsureActivityIndexesAsync(CancellationToken ct)
    {
        await Activities.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<AIActivity>(Builders<AIActivity>.IndexKeys.Ascending(a => a.ActivityId), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<AIActivity>(Builders<AIActivity>.IndexKeys.Descending(a => a.Timestamp)),
            new CreateIndexModel<AIActivity>(Builders<AIActivity>.IndexKeys.Ascending(a => a.CorrelationId)),
            new CreateIndexModel<AIActivity>(Builders<AIActivity>.IndexKeys.Ascending(a => a.ConversationId).Ascending(a => a.Timestamp)),
            new CreateIndexModel<AIActivity>(Builders<AIActivity>.IndexKeys.Ascending(a => a.Status).Descending(a => a.Timestamp)),
            new CreateIndexModel<AIActivity>(Builders<AIActivity>.IndexKeys.Ascending(a => a.TenantRef).Descending(a => a.Timestamp))
        }, ct);
        await ConversationActivity.Indexes.CreateOneAsync(new CreateIndexModel<AIConversationActivity>(
            Builders<AIConversationActivity>.IndexKeys.Descending(c => c.LastActivityAt)), cancellationToken: ct);
    }

    public static void RegisterMappings()
    {
        lock (MapLock)
        {
            if (_mapped) return;

            var pack = new ConventionPack
            {
                new IgnoreExtraElementsConvention(true),
                new EnumRepresentationConvention(BsonType.String)
            };
            ConventionRegistry.Register("MySaleAI", pack, t => t.Namespace?.StartsWith("MySale.AI", StringComparison.Ordinal) == true);

            // decimal as Decimal128 (exact money values)
            try { BsonSerializer.RegisterSerializer(new DecimalSerializer(BsonType.Decimal128)); }
            catch (BsonSerializationException) { /* already registered */ }

            if (!BsonClassMap.IsClassMapRegistered(typeof(Entity)))
            {
                BsonClassMap.RegisterClassMap<Entity>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(e => e.Id)
                      .SetIdGenerator(StringObjectIdGenerator.Instance)
                      .SetSerializer(new StringSerializer(BsonType.ObjectId));
                });
            }
            if (!BsonClassMap.IsClassMapRegistered(typeof(AppSettings)))
            {
                BsonClassMap.RegisterClassMap<AppSettings>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(s => s.Id);
                });
            }
            if (!BsonClassMap.IsClassMapRegistered(typeof(SchemaMetadata)))
            {
                BsonClassMap.RegisterClassMap<SchemaMetadata>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(s => s.Id);
                });
            }
            if (!BsonClassMap.IsClassMapRegistered(typeof(AIConversationActivity)))
            {
                BsonClassMap.RegisterClassMap<AIConversationActivity>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(c => c.Id); // conversation id (string, not necessarily an ObjectId)
                });
            }
            if (!BsonClassMap.IsClassMapRegistered(typeof(DatabaseConfig)))
            {
                BsonClassMap.RegisterClassMap<DatabaseConfig>(cm =>
                {
                    cm.AutoMap();
                    cm.MapIdMember(s => s.Id);
                });
            }
            _mapped = true;
        }
    }

    public static bool IsObjectId(string? id) => !string.IsNullOrEmpty(id) && ObjectId.TryParse(id, out _);
}
