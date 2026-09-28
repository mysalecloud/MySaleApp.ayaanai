using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.BusinessData;

namespace MySale.AI.Tests;

public static class TestData
{
    public const string CompanyA = "66a000000000000000000001";
    public const string CompanyB = "66a000000000000000000002";

    public static IReadOnlyList<CollectionSchema> Schema() => SampleSchemaCatalog.Build()
        .Where(c => c.Name is not ("Companies" or "Users"))
        .ToList();

    public static MqlValidationContext Context(int maxRecords = 200, int maxStages = 12) => new()
    {
        Collections = Schema(),
        TenantField = "CompanyId",
        MaxRecords = maxRecords,
        MaxStages = maxStages
    };

    public static MqlQuery Parse(string json)
    {
        var parsed = MqlParser.Parse(json);
        Assert.True(parsed.Success, parsed.Error);
        return parsed.Query!;
    }
}

public sealed class FakeUser : IUserContext
{
    public bool IsAuthenticated => true;
    public string UserId { get; set; } = "66b000000000000000000001";
    public string UserName { get; set; } = "tester.a";
    public string DisplayName { get; set; } = "Tester A";
    public UserRole Role { get; set; } = UserRole.Tester;
    public string CompanyId { get; set; } = TestData.CompanyA;
    public string CompanyName { get; set; } = "Al Noor Trading LLC";
    public string Currency { get; set; } = "AED";
    public string TimeZone { get; set; } = "Asia/Dubai";
    /// <summary>True = the caller used a customer MySaleBooks token (no technical details in responses).</summary>
    public bool IsMySaleBooksUser { get; set; }
}

/// <summary>Scripted AI provider: returns queued responses in order, or throws.</summary>
public sealed class ScriptedProvider : IAIProvider
{
    public Queue<Func<AIChatRequest, string>> QueryResponses { get; } = new();
    public Func<AIChatRequest, string> Answer { get; set; } = _ => "Your total sales this month are AED 1,500.00.";
    public bool FailQuery { get; set; }
    public bool FailAnswer { get; set; }
    public List<AIChatRequest> Requests { get; } = new();

    public string Kind => "fake";

    public Task<AIQueryResponse> GenerateQueryAsync(AIChatRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (FailQuery) throw new AIProviderException("Cannot reach Ollama at http://localhost:11434.");
        var text = QueryResponses.Count > 0 ? QueryResponses.Dequeue()(request) : "{}";
        return Task.FromResult(new AIQueryResponse { Text = text, InputTokens = 100, OutputTokens = 20, Model = request.Model });
    }

    public Task<AIResponse> GenerateResponseAsync(AIChatRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (FailAnswer) throw new AIProviderException("Provider went away.");
        return Task.FromResult(new AIResponse { Text = Answer(request), InputTokens = 50, OutputTokens = 10, Model = request.Model });
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamResponseAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var text = Answer(request);
        foreach (var word in text.Split(' '))
        {
            await Task.Yield();
            yield return new AIStreamChunk { Text = word + " " };
        }
        yield return new AIStreamChunk { Done = true, InputTokens = 50, OutputTokens = 10 };
    }

    public Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct) => Task.FromResult(new ProviderTestResult { Success = true });

    public Task<IReadOnlyList<AIModel>> GetModelsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AIModel>>(new[] { new AIModel { Id = "llama3.1", Name = "llama3.1" } });
}

public sealed class FakeProviderFactory : IAIProviderFactory
{
    private readonly IAIProvider _provider;
    public FakeProviderFactory(IAIProvider provider) => _provider = provider;

    public IReadOnlyList<ProviderKindInfo> Kinds { get; } = new[]
    {
        new ProviderKindInfo { Kind = "ollama", DisplayName = "Ollama", Category = ProviderCategory.Local }
    };

    public bool Supports(string kind) => kind == "ollama";
    public IAIProvider Create(ProviderConfig config) => _provider;
}

public sealed class FakeExecutor : IQueryExecutor
{
    public Func<string, JsonArray, List<JsonObject>> Handler { get; set; } =
        (_, _) => new List<JsonObject> { new() { ["totalSales"] = 1500.0, ["invoiceCount"] = 12 } };
    public Exception? Throw { get; set; }
    public (string Collection, JsonArray Pipeline)? LastCall { get; private set; }

    public Task<QueryExecutionResult> ExecuteAsync(string collection, JsonArray pipeline, QueryExecutionOptions options, CancellationToken ct)
    {
        LastCall = (collection, pipeline);
        if (Throw is not null) throw Throw;
        var rows = Handler(collection, pipeline).Take(options.MaxDocuments).ToList();
        return Task.FromResult(new QueryExecutionResult { Rows = rows, ElapsedMs = 7 });
    }
}

public sealed class FakeSchema : ISchemaService
{
    /// <summary>Extra collections added to the sample catalog (e.g. a Ledger collection with account groups).</summary>
    public List<CollectionSchema> Extra { get; } = new();

    public Task<IReadOnlyList<CollectionSchema>> GetSchemaAsync(bool includeDiscovery, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CollectionSchema>>(SampleSchemaCatalog.Build().Concat(Extra).ToList());
    public Task<List<JsonObject>> SampleAsync(string collection, int count, CancellationToken ct) => Task.FromResult(new List<JsonObject>());
    public Task<List<TenantValueInfo>> TenantValuesAsync(string collection, string field, CancellationToken ct) => Task.FromResult(new List<TenantValueInfo>());
    public void Invalidate() { }
}

public sealed class PlainSecrets : ISecretProtector
{
    public string Protect(string plaintext) => "enc:" + plaintext;
    public string? Unprotect(string? ciphertext) => ciphertext?.Replace("enc:", string.Empty);
}

// ---------------- in-memory repositories ----------------

public sealed class MemoryStore
{
    public List<ProviderConfig> Providers { get; } = new();
    public List<Conversation> Conversations { get; } = new();
    public List<ChatMessage> Messages { get; } = new();
    public List<QueryLog> Logs { get; } = new();
    public List<AuditLog> Audit { get; } = new();
    public AppSettings Settings { get; set; } = new();
    public static string NewId() => Guid.NewGuid().ToString("N")[..24];
}

public sealed class MemProviders : IProviderRepository
{
    private readonly MemoryStore _s;
    public MemProviders(MemoryStore s) => _s = s;
    public Task<List<ProviderConfig>> ListAsync(CancellationToken ct) => Task.FromResult(_s.Providers.ToList());
    public Task<ProviderConfig?> GetAsync(string id, CancellationToken ct) => Task.FromResult(_s.Providers.FirstOrDefault(p => p.Id == id));
    public Task InsertAsync(ProviderConfig c, CancellationToken ct) { c.Id = MemoryStore.NewId(); _s.Providers.Add(c); return Task.CompletedTask; }
    public Task UpdateAsync(ProviderConfig c, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(string id, CancellationToken ct) { _s.Providers.RemoveAll(p => p.Id == id); return Task.CompletedTask; }
    public Task ClearDefaultAsync(string exceptId, CancellationToken ct) { foreach (var p in _s.Providers.Where(p => p.Id != exceptId)) p.IsDefault = false; return Task.CompletedTask; }
    public Task UpdateTestStatusAsync(string id, ProviderStatus status, string? message, DateTime at, CancellationToken ct) => Task.CompletedTask;
}

public sealed class MemConversations : IConversationRepository
{
    private readonly MemoryStore _s;
    public MemConversations(MemoryStore s) => _s = s;
    public Task<List<Conversation>> ListAsync(string userId, string companyId, string? search, bool? archived, int limit, CancellationToken ct)
        => Task.FromResult(_s.Conversations.Where(c => c.UserId == userId && c.CompanyId == companyId).ToList());
    public Task<Conversation?> GetAsync(string id, string userId, string companyId, CancellationToken ct)
        => Task.FromResult(_s.Conversations.FirstOrDefault(c => c.Id == id && c.UserId == userId && c.CompanyId == companyId));
    public Task<(List<Conversation> Items, long Total)> SearchByCompanyAsync(string companyId, string? search, DateTime? from, DateTime? to,
        int page, int pageSize, CancellationToken ct)
    {
        var all = _s.Conversations.Where(c => !string.IsNullOrEmpty(companyId) && c.CompanyId == companyId).ToList();
        return Task.FromResult((all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), (long)all.Count));
    }
    public Task<Conversation?> GetForCompanyAsync(string id, string companyId, CancellationToken ct)
        => Task.FromResult(_s.Conversations.FirstOrDefault(c => c.Id == id && !string.IsNullOrEmpty(companyId) && c.CompanyId == companyId));
    public Task<long> CountByCompanyAsync(string companyId, DateTime from, CancellationToken ct)
        => Task.FromResult((long)_s.Conversations.Count(c => c.CompanyId == companyId && c.UpdatedAt >= from));
    public Task InsertAsync(Conversation c, CancellationToken ct) { c.Id = MemoryStore.NewId(); _s.Conversations.Add(c); return Task.CompletedTask; }
    public Task UpdateAsync(Conversation c, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(string id, CancellationToken ct) { _s.Conversations.RemoveAll(c => c.Id == id); return Task.CompletedTask; }
}

public sealed class MemMessages : IMessageRepository
{
    private readonly MemoryStore _s;
    public MemMessages(MemoryStore s) => _s = s;
    public Task<List<ChatMessage>> ListAsync(string conversationId, CancellationToken ct)
        => Task.FromResult(_s.Messages.Where(m => m.ConversationId == conversationId).ToList());
    public Task<List<ChatMessage>> ListRecentAsync(string conversationId, int count, CancellationToken ct)
        => Task.FromResult(_s.Messages.Where(m => m.ConversationId == conversationId).TakeLast(count).ToList());
    public Task<ChatMessage?> GetAsync(string id, CancellationToken ct) => Task.FromResult(_s.Messages.FirstOrDefault(m => m.Id == id));
    public Task InsertAsync(ChatMessage m, CancellationToken ct) { m.Id = MemoryStore.NewId(); _s.Messages.Add(m); return Task.CompletedTask; }
    public Task UpdateAsync(ChatMessage m, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct) { var set = ids.ToHashSet(); _s.Messages.RemoveAll(m => set.Contains(m.Id)); return Task.CompletedTask; }
    public Task DeleteByConversationAsync(string conversationId, CancellationToken ct) { _s.Messages.RemoveAll(m => m.ConversationId == conversationId); return Task.CompletedTask; }
    public Task<long> CountAsync(string conversationId, CancellationToken ct) => Task.FromResult((long)_s.Messages.Count(m => m.ConversationId == conversationId));
}

public sealed class MemLogs : IQueryLogRepository
{
    private readonly MemoryStore _s;
    public MemLogs(MemoryStore s) => _s = s;
    public Task InsertAsync(QueryLog log, CancellationToken ct) { log.Id = MemoryStore.NewId(); _s.Logs.Add(log); return Task.CompletedTask; }
    public Task UpdateAsync(QueryLog log, CancellationToken ct) => Task.CompletedTask;
    public Task<QueryLog?> GetAsync(string id, CancellationToken ct) => Task.FromResult(_s.Logs.FirstOrDefault(l => l.Id == id));
    public Task<(List<QueryLog> Items, long Total)> SearchAsync(QueryLogFilter filter, CancellationToken ct)
    {
        var all = _s.Logs.Where(l => filter.CompanyId is null || l.CompanyId == filter.CompanyId).ToList();
        return Task.FromResult((all, (long)all.Count));
    }
    public Task<List<QueryLog>> RecentAsync(string? companyId, int count, CancellationToken ct) => Task.FromResult(_s.Logs.TakeLast(count).ToList());
    public Task<List<UsageRow>> GetUsageRowsAsync(DateTime from, DateTime to, string? companyId, CancellationToken ct)
        => Task.FromResult(_s.Logs.Where(l => (companyId is null || l.CompanyId == companyId) && l.CreatedAt >= from && l.CreatedAt <= to)
            .Select(l => new UsageRow { CreatedAt = l.CreatedAt, Status = l.Status, Model = l.Model, ProviderName = l.ProviderName,
                InputTokens = l.InputTokens, OutputTokens = l.OutputTokens, Executed = l.Executed, TotalTimeMs = l.TotalTimeMs,
                QueryGenerated = l.QueryGenerated, ValidationPassed = l.ValidationPassed, Blocked = l.Blocked }).ToList());
    public Task<List<string>> DistinctModelsAsync(CancellationToken ct) => Task.FromResult(_s.Logs.Select(l => l.Model ?? "").Distinct().ToList());
}

public sealed class MemSettings : ISettingsRepository
{
    private readonly MemoryStore _s;
    public MemSettings(MemoryStore s) => _s = s;
    public Task<AppSettings?> GetAsync(CancellationToken ct) => Task.FromResult<AppSettings?>(_s.Settings);
    public Task SaveAsync(AppSettings settings, CancellationToken ct) { _s.Settings = settings; return Task.CompletedTask; }
}

public sealed class MemAudit : IAuditLogRepository
{
    private readonly MemoryStore _s;
    public MemAudit(MemoryStore s) => _s = s;
    public Task InsertAsync(AuditLog log, CancellationToken ct) { _s.Audit.Add(log); return Task.CompletedTask; }
    public Task<(List<AuditLog> Items, long Total)> SearchForTenantAsync(AuditLogFilter f, CancellationToken ct)
    {
        var all = _s.Audit.Where(l => (!string.IsNullOrEmpty(f.CompanyId) && l.CompanyId == f.CompanyId)
                                      || (!string.IsNullOrEmpty(f.TenantRef) && l.TenantRef == f.TenantRef))
            .Where(l => string.IsNullOrEmpty(f.Action) || (f.Action.EndsWith('*') ? l.Action.StartsWith(f.Action.TrimEnd('*')) : l.Action == f.Action))
            .OrderByDescending(l => l.CreatedAt).ToList();
        return Task.FromResult((all.Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToList(), (long)all.Count));
    }
}

/// <summary>Builds a fully wired orchestrator over in-memory fakes.</summary>
public sealed class Harness
{
    public MemoryStore Store { get; } = new();
    public ScriptedProvider Provider { get; } = new();
    public FakeExecutor Executor { get; } = new();
    public FakeUser User { get; } = new();
    public AIAgentOrchestrator Orchestrator { get; }
    public FakeRequestContext Request { get; } = new();
    public FakeSchema Schema { get; } = new();
    public InMemoryConversationStateRepository States { get; } = new();

    public Harness(IAIActivitySink? activitySink = null, ActivityOptions? activityOptions = null, ISecretProtector? activityProtector = null,
        MySale.AI.Application.Stores.IStoreSelection? storeSelection = null, MySale.AI.Application.Stores.IStoreAccessProvider? storeAccess = null)
    {
        Store.Providers.Add(new ProviderConfig
        {
            Id = "66c000000000000000000001", Name = "Ollama", Kind = "ollama", Category = ProviderCategory.Local,
            BaseUrl = "http://localhost:11434", DefaultModel = "llama3.1", Enabled = true, IsDefault = true
        });

        var audit = new AuditService(new MemAudit(Store), User);
        var settings = new SettingsService(new MemSettings(Store), audit);
        var providers = new ProviderService(new MemProviders(Store), new FakeProviderFactory(Provider), new PlainSecrets(), audit);
        var tenant = new TenantOptions();
        var engine = new QueryEngine(Schema, new MqlValidator(), new TenantQueryGuard(tenant), tenant, Executor, User);

        Orchestrator = new AIAgentOrchestrator(
            new MemConversations(Store), new MemMessages(Store), new MemLogs(Store), providers, settings, engine,
            new PromptBuilder(), audit, User, TimeProvider.System, NullLogger<AIAgentOrchestrator>.Instance,
            activity: activitySink is null ? null
                : new ActivityTracker(activitySink, activityOptions ?? new ActivityOptions(), Request, User, TimeProvider.System,
                    protector: activityProtector),
            stores: storeSelection is null ? null
                : new MySale.AI.Application.Stores.StoreContextResolver(User, storeSelection,
                    storeAccess ?? new MySale.AI.Application.Stores.UnknownStoreAccessProvider(), new MySale.AI.Application.Stores.StoreFilterOptions(), engine),
            companies: new CompanyContextService(engine, User),
            reports: new MySaleBooksReports(engine),
            states: States);
    }
}

public sealed class FakeRequestContext : IRequestContext
{
    public string CorrelationId { get; set; } = "corr-test-0001";
    public string? ClientApp { get; set; } = "mysalebooks-web";
    public string? ClientVersion { get; set; } = "1.0.212";
    public string? SessionId { get; set; } = "session-abc";
    public string? Endpoint { get; set; } = "/api/ai/chat";
}

/// <summary>In-memory activity sink (captures what would be written to the activity log).</summary>
public sealed class MemActivitySink : IAIActivitySink
{
    public List<AIActivity> Activities { get; } = new();
    public List<ConversationActivityUpdate> Conversations { get; } = new();
    public bool Throw { get; set; }

    public bool TryEnqueue(AIActivity activity, ConversationActivityUpdate? conversation)
    {
        if (Throw) throw new InvalidOperationException("activity database unavailable");
        Activities.Add(activity);
        if (conversation is not null) Conversations.Add(conversation);
        return true;
    }
}
