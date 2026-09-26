using System.Text.Json.Nodes;
using MySale.AI.Domain;

namespace MySale.AI.Application.Abstractions;

/// <summary>Authenticated user + tenant. In MySaleBooks this is resolved from the real session.</summary>
public interface IUserContext
{
    bool IsAuthenticated { get; }
    string UserId { get; }
    string UserName { get; }
    string DisplayName { get; }
    UserRole Role { get; }
    string CompanyId { get; }
    string CompanyName { get; }
    string Currency { get; }
    string TimeZone { get; }

    /// <summary>
    /// Customer database from the validated MySaleBooks JWT ("dbName" claim). Null for prototype (demo) users,
    /// who use the database configured on the Database page. Never taken from the request body or the AI.
    /// </summary>
    string? DatabaseName => null;

    /// <summary>True when the caller authenticated with the existing MySaleBooks JWT.</summary>
    bool IsMySaleBooksUser => false;

    /// <summary>
    /// True when the customer database is the tenant boundary (MySaleBooks JWT without a company claim).
    /// CompanyId is then "db:{DatabaseName}" so conversations and logs stay separated per customer.
    /// </summary>
    bool TenantIsDatabase => false;
}

/// <summary>Claim names shared by the authentication handler, the user context and the business DB resolver.</summary>
public static class TenantClaimTypes
{
    /// <summary>Claim written by MySaleBooks when it issues the JWT.</summary>
    public const string DatabaseName = "dbName";
    public const string AuthSource = "auth_source";
    public const string MySaleBooks = "mysalebooks";
}

/// <summary>Result of validating an existing MySaleBooks JWT.</summary>
public sealed class TenantTokenValidation
{
    public bool IsValid { get; init; }
    /// <summary>"missing" | "expired" | "invalid" (only when IsValid is false).</summary>
    public string? Failure { get; init; }
    public System.Security.Claims.ClaimsPrincipal? Principal { get; init; }
    /// <summary>The "dbName" claim, or null when the token is valid but has no database.</summary>
    public string? DatabaseName { get; init; }
}

/// <summary>
/// Wraps the existing MySaleBooks JWT logic (GetDbNameFromToken): validate issuer, audience, lifetime,
/// signature and decrypt with JwtSettings:*, then read the "dbName" claim.
/// </summary>
public interface ITenantDatabaseResolver
{
    /// <summary>True when JwtSettings are configured (MySaleBooks JWTs can be accepted).</summary>
    bool IsConfigured { get; }

    /// <summary>Same contract as the existing GetDbNameFromToken: the dbName, or null if invalid / missing.</summary>
    string? GetDatabaseName(string jwtToken);

    /// <summary>Full validation result (used by the authentication handler to tell expired from invalid).</summary>
    TenantTokenValidation Validate(string jwtToken);
}

/// <summary>Raised when an authenticated MySaleBooks request has no usable customer database.</summary>
public sealed class TenantResolutionException : Exception
{
    public const string UserMessage = "Unable to resolve customer database.";
    public TenantResolutionException() : base(UserMessage) { }
}

public sealed class TenantOptions
{
    public const string Section = "Tenant";
    /// <summary>Field present on every business collection that identifies the company.</summary>
    public string FieldName { get; set; } = "CompanyId";
    /// <summary>True when the tenant field is stored as ObjectId (MySaleBooks), false for plain strings.</summary>
    public bool ValueIsObjectId { get; set; } = true;
}

public sealed class FieldSchema
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string? Description { get; set; }
    public string? Example { get; set; }
    public string? Relationship { get; set; }
    public List<string> Operations { get; set; } = new();
    public bool Documented { get; set; } = true;
    public bool Discovered { get; set; }
    /// <summary>Excluded from the AI prompt and from queries.</summary>
    public bool Hidden { get; set; }
}

public sealed class CollectionSchema
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Tenant field of this collection. Empty string = shared data (no company filter).</summary>
    public string TenantField { get; set; } = "CompanyId";
    /// <summary>"objectId" | "string" | null (use the global Tenant:ValueIsObjectId).</summary>
    public string? TenantValueType { get; set; }
    public long? DocumentCount { get; set; }
    public bool Allowed { get; set; } = true;
    /// <summary>True when the collection has a description (curated catalog or saved metadata).</summary>
    public bool Documented { get; set; }
    /// <summary>"catalog" | "saved" | "discovered"</summary>
    public string MetadataSource { get; set; } = "discovered";
    public DateTime? MetadataUpdatedAt { get; set; }
    public List<FieldSchema> Fields { get; set; } = new();
}

/// <summary>Distinct tenant value found in a collection (used to pick a test company).</summary>
public sealed class TenantValueInfo
{
    public string Value { get; init; } = string.Empty;
    public string Type { get; init; } = "objectId";
    public long Count { get; init; }
    public string? Label { get; init; }
}

/// <summary>Schema metadata. Prototype: curated sample schema + saved metadata + live discovery. Later: MySaleBooks schema service.</summary>
public interface ISchemaService
{
    Task<IReadOnlyList<CollectionSchema>> GetSchemaAsync(bool includeDiscovery, CancellationToken ct);
    /// <summary>Random sample documents (strings/arrays truncated) — used to generate metadata.</summary>
    Task<List<JsonObject>> SampleAsync(string collection, int count, CancellationToken ct);
    /// <summary>Most frequent values of a tenant field.</summary>
    Task<List<TenantValueInfo>> TenantValuesAsync(string collection, string field, CancellationToken ct);
    void Invalidate();
}

public interface ISchemaMetadataRepository
{
    Task<List<SchemaMetadata>> ListAsync(CancellationToken ct);
    Task<SchemaMetadata?> GetAsync(string collection, CancellationToken ct);
    Task SaveAsync(SchemaMetadata metadata, CancellationToken ct);
    Task DeleteAsync(string collection, CancellationToken ct);
}

public sealed class QueryExecutionOptions
{
    public int MaxDocuments { get; init; } = 200;
    public int TimeoutMs { get; init; } = 5000;
    /// <summary>Optional MongoDB command comment (correlation id). Never contains customer data or secrets.</summary>
    public string? Comment { get; init; }
}

public sealed class QueryExecutionResult
{
    public List<JsonObject> Rows { get; init; } = new();
    public long ElapsedMs { get; init; }
}

/// <summary>Executes an already validated and tenant-scoped aggregation pipeline (Extended JSON).</summary>
public interface IQueryExecutor
{
    Task<QueryExecutionResult> ExecuteAsync(string collection, JsonArray pipeline, QueryExecutionOptions options, CancellationToken ct);
}

public sealed class QueryExecutionException : Exception
{
    public QueryExecutionException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class QueryTimeoutException : Exception
{
    public QueryTimeoutException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface ISecretProtector
{
    string Protect(string plaintext);
    string? Unprotect(string? ciphertext);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public sealed record IssuedToken(string Token, DateTime ExpiresAt);

public interface ITokenService
{
    IssuedToken Issue(AppUser user);
}

// ---------- Repositories (system DB) ----------

public interface IUserRepository
{
    Task<AppUser?> FindByUserNameAsync(string userName, CancellationToken ct);
    Task<AppUser?> GetAsync(string id, CancellationToken ct);
    Task<List<AppUser>> ListAsync(CancellationToken ct);
    Task UpsertAsync(AppUser user, CancellationToken ct);
    Task UpdateLastLoginAsync(string id, DateTime at, CancellationToken ct);
}

public interface IProviderRepository
{
    Task<List<ProviderConfig>> ListAsync(CancellationToken ct);
    Task<ProviderConfig?> GetAsync(string id, CancellationToken ct);
    Task InsertAsync(ProviderConfig config, CancellationToken ct);
    Task UpdateAsync(ProviderConfig config, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task ClearDefaultAsync(string exceptId, CancellationToken ct);
    Task UpdateTestStatusAsync(string id, ProviderStatus status, string? message, DateTime at, CancellationToken ct);
}

public interface IConversationRepository
{
    Task<List<Conversation>> ListAsync(string userId, string companyId, string? search, bool? archived, int limit, CancellationToken ct);
    Task<Conversation?> GetAsync(string id, string userId, string companyId, CancellationToken ct);
    Task InsertAsync(Conversation conversation, CancellationToken ct);
    Task UpdateAsync(Conversation conversation, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

public interface IMessageRepository
{
    Task<List<ChatMessage>> ListAsync(string conversationId, CancellationToken ct);
    Task<List<ChatMessage>> ListRecentAsync(string conversationId, int count, CancellationToken ct);
    Task<ChatMessage?> GetAsync(string id, CancellationToken ct);
    Task InsertAsync(ChatMessage message, CancellationToken ct);
    Task UpdateAsync(ChatMessage message, CancellationToken ct);
    Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct);
    Task DeleteByConversationAsync(string conversationId, CancellationToken ct);
    Task<long> CountAsync(string conversationId, CancellationToken ct);
}

public sealed class QueryLogFilter
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? ProviderId { get; set; }
    public string? Model { get; set; }
    public ChatStatus? Status { get; set; }
    public string? UserId { get; set; }
    public string? Operation { get; set; }
    public string? Search { get; set; }
    public string? CompanyId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

/// <summary>Lightweight projection of a query log used for usage statistics.</summary>
public sealed class UsageRow
{
    public DateTime CreatedAt { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ProviderKind { get; set; }
    public bool IsLocalProvider { get; set; }
    public string? Model { get; set; }
    public ChatStatus Status { get; set; }
    public bool QueryGenerated { get; set; }
    public bool ValidationPassed { get; set; }
    public bool Blocked { get; set; }
    public bool Executed { get; set; }
    public long ExecutionTimeMs { get; set; }
    public long TotalTimeMs { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
}

public interface IQueryLogRepository
{
    Task InsertAsync(QueryLog log, CancellationToken ct);
    Task UpdateAsync(QueryLog log, CancellationToken ct);
    Task<QueryLog?> GetAsync(string id, CancellationToken ct);
    Task<(List<QueryLog> Items, long Total)> SearchAsync(QueryLogFilter filter, CancellationToken ct);
    Task<List<QueryLog>> RecentAsync(string? companyId, int count, CancellationToken ct);
    Task<List<UsageRow>> GetUsageRowsAsync(DateTime from, DateTime to, string? companyId, CancellationToken ct);
    Task<List<string>> DistinctModelsAsync(CancellationToken ct);
}

public interface ISettingsRepository
{
    Task<AppSettings?> GetAsync(CancellationToken ct);
    Task SaveAsync(AppSettings settings, CancellationToken ct);
}

public interface IAuditLogRepository
{
    Task InsertAsync(AuditLog log, CancellationToken ct);
}

// ---------- Business database management (Database page) ----------

public sealed class DatabaseConfigDto
{
    public bool Configured { get; init; }
    public string DatabaseName { get; init; } = string.Empty;
    public string DisplayHost { get; init; } = string.Empty;
    public bool HasCredentials { get; init; }
    public string Source { get; init; } = "appsettings";
    /// <summary>True when the database name comes from the signed-in user's JWT "dbName" claim (not editable).</summary>
    public bool DatabaseFromToken { get; init; }
    public string? LastTestStatus { get; init; }
    public DateTime? LastTestedAt { get; init; }
}

public sealed class DatabaseConfigUpdate
{
    public string ConnectionUrl { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? Password { get; set; }
}

public sealed class DatabaseTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long LatencyMs { get; init; }
    public string? ServerVersion { get; init; }
    public bool? DatabaseExists { get; init; }
    public int? CollectionCount { get; init; }
}

public sealed class CollectionInfo
{
    public string Name { get; init; } = string.Empty;
    public long DocumentCount { get; init; }
    public bool Allowed { get; init; }
    public bool Documented { get; init; }
}

public sealed class SeedResult
{
    public Dictionary<string, long> Collections { get; init; } = new();
    public long ElapsedMs { get; init; }
}

public interface IBusinessDatabaseManager
{
    Task<DatabaseConfigDto> GetConfigAsync(CancellationToken ct);
    Task<DatabaseConfigDto> UpdateConfigAsync(DatabaseConfigUpdate update, string updatedBy, CancellationToken ct);
    Task<DatabaseTestResult> TestAsync(DatabaseConfigUpdate? draft, CancellationToken ct);
    Task<List<string>> ListDatabasesAsync(CancellationToken ct);
    Task<List<CollectionInfo>> ListCollectionsAsync(IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> documented, CancellationToken ct);
    Task<SeedResult> SeedAsync(bool reset, CancellationToken ct);
    Task<bool> PingAsync(CancellationToken ct);
}
