using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.AIDashboard;
using MySale.AI.Application.Services;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.AIDashboard;

namespace MySale.AI.Tests;

public sealed class FakeCallerAccessor : IAIDashboardCallerAccessor
{
    public AIDashboardCaller Current { get; set; } = Caller("cust_a");

    public static readonly AyaanDashboardSession ValidAyaan =
        new(AyaanSessionState.Valid, "66b000000000000000000001", "Admin A", "Admin", DateTime.UtcNow.AddHours(8));

    public static AIDashboardCaller Caller(string db, bool mySaleBooks = true, DateTime? authAt = null, AyaanDashboardSession? ayaan = null) => new()
    {
        Ayaan = ayaan ?? ValidAyaan,
        IsAuthenticated = true,
        IsMySaleBooksUser = mySaleBooks,
        UserId = "u1",
        UserName = "Owner A",
        DatabaseName = mySaleBooks ? db : null,
        CompanyId = mySaleBooks ? "db:" + db : TestData.CompanyA,
        CompanyName = db,
        MySaleBooksUserId = "user-guid-1",
        MySaleBooksRoleId = "role-guid-1",
        Token = "header.payload.signature",
        AuthenticatedAt = authAt,
        CorrelationId = "corr-1"
    };
}

public sealed class FakePermissionSource : IAIDashboardPermissionSource
{
    public string Name => "MySaleBooksApi";
    public Func<PermissionLookupResult> Result { get; set; } = () => Grant(AIDashboardPermissions.View);
    public int Calls { get; private set; }

    public Task<PermissionLookupResult> LookupAsync(AIDashboardCaller caller, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Result());
    }

    public static PermissionLookupResult Grant(params string[] permissions)
        => new() { Succeeded = true, Permissions = permissions.Select(AIDashboardPermissions.Normalize).ToHashSet() };
}

public sealed class MemActivities : IAIActivityRepository
{
    public List<AIActivity> Items { get; } = new();
    public Task InsertAsync(AIActivity activity, CancellationToken ct) { Items.Add(activity); return Task.CompletedTask; }
    public Task UpsertConversationAsync(ConversationActivityUpdate update, CancellationToken ct) => Task.CompletedTask;
    public Task<(List<AIActivity> Items, long Total)> SearchAsync(ActivityFilter filter, CancellationToken ct)
    {
        var list = Items.Where(a => filter.TenantRef is null || a.TenantRef == filter.TenantRef).ToList();
        return Task.FromResult((list, (long)list.Count));
    }
    public Task<AIActivity?> GetAsync(string activityId, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(a => a.ActivityId == activityId));
    public Task<List<AIActivity>> ListByConversationAsync(string conversationId, int limit, CancellationToken ct, string? tenantRef = null)
        => Task.FromResult(Items.Where(a => a.ConversationId == conversationId && (tenantRef is null || a.TenantRef == tenantRef)).ToList());
    public Task<(List<AIConversationActivity> Items, long Total)> SearchConversationsAsync(string? search, int page, int pageSize, CancellationToken ct, string? tenantRef = null)
        => Task.FromResult((new List<AIConversationActivity>(), 0L));
    public Task<AIConversationActivity?> GetConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<AIConversationActivity?>(null);
    public Task<List<string>> DistinctAsync(string field, CancellationToken ct, string? tenantRef = null) => Task.FromResult(new List<string>());
    public Task<List<AIActivity>> RecentForStatsAsync(DateTime from, int limit, CancellationToken ct, string? tenantRef = null) => Task.FromResult(Items.ToList());
    public Task<List<AIActivity>> RecentForAnalysisAsync(string tenantRef, DateTime from, int limit, CancellationToken ct)
        => Task.FromResult(Items.Where(a => a.TenantRef == tenantRef && a.Timestamp >= from).ToList());
    public Task<long> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct) => Task.FromResult(0L);
}

public class AIDashboardAccessTests
{
    private static (AIDashboardAccessService Service, FakePermissionSource Source, FakeCallerAccessor Caller) Build(AIDashboardOptions? options = null)
    {
        var source = new FakePermissionSource();
        var caller = new FakeCallerAccessor();
        var service = new AIDashboardAccessService(caller, new IAIDashboardPermissionSource[] { source }, options ?? new AIDashboardOptions(),
            new AIDashboardPermissionCache(), TimeProvider.System, NullLogger<AIDashboardAccessService>.Instance);
        return (service, source, caller);
    }

    [Fact]
    public async Task Tokens_without_a_MySaleBooks_tenant_are_refused()
    {
        var (service, _, caller) = Build();
        caller.Current = FakeCallerAccessor.Caller("cust_a", mySaleBooks: false);
        var ex = await Assert.ThrowsAsync<AIDashboardAccessException>(() => service.AuthorizeAsync(AIDashboardPermissions.View, default));
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("tenant_required", ex.Code);
    }

    [Fact]
    public async Task Unauthenticated_callers_get_401()
    {
        var (service, _, caller) = Build();
        caller.Current = new AIDashboardCaller { IsAuthenticated = false, Ayaan = FakeCallerAccessor.ValidAyaan };
        var ex = await Assert.ThrowsAsync<AIDashboardAccessException>(() => service.AuthorizeAsync(AIDashboardPermissions.View, default));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("unauthenticated", ex.Code);
    }

    [Theory]
    [InlineData(AyaanSessionState.Missing, 401, "ayaan_login_required")]
    [InlineData(AyaanSessionState.Invalid, 401, "ayaan_login_required")]
    [InlineData(AyaanSessionState.Expired, 401, "ayaan_session_expired")]
    [InlineData(AyaanSessionState.NotConfigured, 503, "ayaan_auth_unavailable")]
    public async Task A_MySaleBooks_session_alone_is_not_enough(AyaanSessionState state, int status, string code)
    {
        var (service, _, caller) = Build();
        caller.Current = FakeCallerAccessor.Caller("cust_a", ayaan: new AyaanDashboardSession(state));
        foreach (var section in AIDashboardPermissions.All)
        {
            var ex = await Assert.ThrowsAsync<AIDashboardAccessException>(() => service.AuthorizeAsync(section, default));
            Assert.Equal(status, ex.StatusCode);
            Assert.Equal(code, ex.Code);
        }
    }

    [Fact]
    public async Task Both_layers_open_every_section_of_the_callers_own_company_without_a_permission_check()
    {
        var (service, source, _) = Build();
        source.Result = () => FakePermissionSource.Grant("item.view"); // would have been refused by the old permission check
        foreach (var section in AIDashboardPermissions.All)
        {
            var access = await service.AuthorizeAsync(section, default);
            Assert.Equal("db:cust_a", access.CompanyId);
            Assert.Equal(ActivityHashing.TenantRef("cust_a", "db:cust_a"), access.TenantRef);
            Assert.True(access.Has(AIDashboardPermissions.QueryLogs));
            Assert.Equal("Admin A", access.AyaanUserName);
        }
        Assert.Equal(0, source.Calls); // no MySaleBooks permission lookup at all
    }

    [Fact]
    public async Task The_AYAAN_session_does_not_change_the_tenant()
    {
        var (service, _, caller) = Build();
        caller.Current = FakeCallerAccessor.Caller("cust_b");
        var access = await service.AuthorizeAsync(AIDashboardPermissions.QueryLogs, default);
        Assert.Equal("db:cust_b", access.CompanyId); // tenant = MySaleBooks token (dbName), whoever unlocked AYAAN
    }

    [Fact]
    public async Task Recent_sign_in_can_still_be_required_for_query_logs()
    {
        var (service, _, caller) = Build(new AIDashboardOptions { RecentAuthMinutes = 30 });
        caller.Current = FakeCallerAccessor.Caller("cust_a", authAt: DateTime.UtcNow.AddHours(-5));
        var ex = await Assert.ThrowsAsync<AIDashboardAccessException>(() => service.AuthorizeAsync(AIDashboardPermissions.QueryLogs, default));
        Assert.Equal("reauthentication_required", ex.Code);
        await service.AuthorizeAsync(AIDashboardPermissions.View, default); // overview unaffected

        caller.Current = FakeCallerAccessor.Caller("cust_a", authAt: DateTime.UtcNow.AddMinutes(-5));
        await service.AuthorizeAsync(AIDashboardPermissions.QueryLogs, default);
    }

    [Fact]
    public async Task Store_access_lookup_still_uses_the_MySaleBooks_permission_record()
    {
        var (service, source, _) = Build();
        source.Result = () => new PermissionLookupResult { Succeeded = true, BranchIds = new[] { "store-a" } };
        var lookup = await service.TryLookupAsync(default);
        Assert.Equal(new[] { "store-a" }, lookup!.BranchIds);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void MySaleBooks_permission_response_is_parsed_like_checkPermission()
    {
        const string json = """
            { "data": { "userRole": { "isSystemRole": false, "permissions": [
              { "systemName": "aidashboard.view", "status": true },
              { "systemName": "AIDashboard.QueryLogs", "status": false },
              { "systemName": "aidashboard.usage" } ] } } }
            """;
        var r = MySaleBooksApiPermissionSource.Parse(json, "role-1", Array.Empty<string>());
        Assert.True(r.Succeeded);
        Assert.Contains(AIDashboardPermissions.View, r.Permissions);
        Assert.DoesNotContain(AIDashboardPermissions.QueryLogs, r.Permissions); // status false
        Assert.DoesNotContain(AIDashboardPermissions.Usage, r.Permissions);     // no status
        Assert.False(r.IsSystemRole);
        Assert.True(MySaleBooksApiPermissionSource.Parse(json, "ADMIN-ROLE", new[] { "admin-role" }).IsSystemRole);
        Assert.False(MySaleBooksApiPermissionSource.Parse("not json", null, Array.Empty<string>()).Succeeded);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("""{"data":{"userRole":{"isSystemRole":false,"permissions":[{"systemName":"aidashboard.view","status":true}]}}}""")
            });
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    [Fact]
    public async Task Permission_api_is_called_with_ids_from_the_token_only()
    {
        var handler = new RecordingHandler();
        var options = new AIDashboardOptions
        {
            PermissionsUrl = "https://company.example/api/v1/Permissions?searchType=permissions&userId={userId}&userRoleId={userRoleId}",
            PermissionsApiKey = "server-side-key"
        };
        var source = new MySaleBooksApiPermissionSource(new Factory(handler), options, NullLogger<MySaleBooksApiPermissionSource>.Instance);

        var result = await source.LookupAsync(FakeCallerAccessor.Caller("cust_a"), default);
        Assert.True(result.Succeeded);
        Assert.Contains(AIDashboardPermissions.View, result.Permissions);
        Assert.Contains("userId=user-guid-1", handler.Request!.RequestUri!.Query);
        Assert.Contains("userRoleId=role-guid-1", handler.Request.RequestUri.Query);
        Assert.Equal("header.payload.signature", handler.Request.Headers.GetValues("Token").Single());
        Assert.Equal("server-side-key", handler.Request.Headers.GetValues("api-key").Single());

        handler.Status = HttpStatusCode.Unauthorized;
        result = await source.LookupAsync(FakeCallerAccessor.Caller("cust_a"), default);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Permissions);

        var noRole = FakeCallerAccessor.Caller("cust_a");
        noRole = new AIDashboardCaller
        {
            IsAuthenticated = true, IsMySaleBooksUser = true, DatabaseName = "cust_a", CompanyId = "db:cust_a",
            MySaleBooksUserId = "user-guid-1", Token = noRole.Token
        };
        Assert.Equal("context-missing", (await source.LookupAsync(noRole, default)).Failure);
    }
}

public class AIDashboardServiceTests
{
    private readonly MemoryStore _store = new();
    private readonly MemActivities _activities = new();
    private readonly AIDashboardService _service;
    private readonly AIDashboardInsightsService _insights;

    private static AIDashboardAccess Access(string db, params string[] permissions) => new()
    {
        UserId = "u1",
        UserName = "Owner",
        CompanyId = "db:" + db,
        TenantRef = ActivityHashing.TenantRef(db, "db:" + db),
        Permissions = (permissions.Length == 0 ? AIDashboardPermissions.All.ToArray() : permissions).ToHashSet(),
        CorrelationId = "corr-1"
    };

    public AIDashboardServiceTests()
    {
        var user = new FakeUser { Role = UserRole.Admin, IsMySaleBooksUser = true };
        var audit = new AuditService(new MemAudit(_store), user);
        var providers = new ProviderService(new MemProviders(_store), new FakeProviderFactory(new ScriptedProvider()), new PlainSecrets(), audit);
        _service = new AIDashboardService(new MemConversations(_store), new MemMessages(_store), new MemLogs(_store), _activities,
            providers, new SettingsService(new MemSettings(_store), audit), new UsageService(new MemLogs(_store), user, TimeProvider.System, providers),
            new MemAudit(_store), new ActivityOptions(), TimeProvider.System, new PlainSecrets());
        _insights = new AIDashboardInsightsService(providers, new SettingsService(new MemSettings(_store), audit),
            new UsageService(new MemLogs(_store), user, TimeProvider.System, providers), new FakeSchema(), _activities, new MemAudit(_store),
            _service, TimeProvider.System);

        _store.Logs.Add(new QueryLog
        {
            Id = "log-a", CompanyId = "db:cust_a", UserName = "Owner A", Question = "Top products", QueryGenerated = true,
            ValidationPassed = true, Executed = true, ExecutionSucceeded = true, Status = ChatStatus.Success, ResultCount = 5,
            FinalMql = "db.SaleItems.aggregate([{\"$match\":{\"note\":\"mongodb+srv://admin:pw@cluster0.example.net\"}}])",
            GeneratedMql = "{\"collection\":\"SaleItems\"}", CreatedAt = DateTime.UtcNow
        });
        _store.Logs.Add(new QueryLog { Id = "log-b", CompanyId = "db:cust_b", Question = "Company B secret question", CreatedAt = DateTime.UtcNow });
        _store.Conversations.Add(new Conversation { Id = "conv-a", CompanyId = "db:cust_a", UserId = "u1", UserName = "Owner A", Title = "Sales" });
        _store.Conversations.Add(new Conversation { Id = "conv-b", CompanyId = "db:cust_b", UserId = "u9", Title = "B" });
        _store.Messages.Add(new ChatMessage { Id = "m1", ConversationId = "conv-a", CompanyId = "db:cust_a", Role = MessageRole.User, Content = "token=abc123secret sales?" });
        _store.Providers.Add(new ProviderConfig
        {
            Id = "p1", Name = "DeepSeek", Kind = "openai-compatible", Category = ProviderCategory.Cloud, BaseUrl = "https://user:pw@api.example.com",
            ApiKeyEncrypted = "enc:sk-verysecretkey1234", ApiKeyHint = "…1234", DefaultModel = "deepseek-chat", IsDefault = true, Enabled = true
        });
        _activities.Items.Add(new AIActivity
        {
            ActivityId = "act-a", TenantRef = ActivityHashing.TenantRef("cust_a", "db:cust_a"), UserName = "Owner A",
            Request = new ActivityRequestInfo { Question = "Top products" },
            Query = new ActivityQueryInfo { Collection = "SaleItems", ExecutedMql = "db.SaleItems.aggregate([])", Stages = new() { "$match" } },
            Errors = new() { new ActivityErrorInfo { Type = ActivityErrorTypes.MongoDBError, Message = "MongoServerError: mongodb+srv://x", StackTrace = "at X" } }
        });
        _activities.Items.Add(new AIActivity { ActivityId = "act-b", TenantRef = ActivityHashing.TenantRef("cust_b", "db:cust_b") });
    }

    [Fact]
    public async Task Query_logs_are_limited_to_the_callers_tenant()
    {
        var page = await _service.QueryLogsAsync(Access("cust_a"), new QueryLogFilter { CompanyId = "db:cust_b" }, default);
        Assert.Equal(new[] { "log-a" }, page.Items.Select(i => i.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.QueryLogAsync(Access("cust_a"), "log-b", default));
    }

    [Fact]
    public async Task Mql_is_returned_with_secrets_masked_and_access_is_audited()
    {
        var detail = await _service.QueryLogAsync(Access("cust_a"), "log-a", default);
        Assert.Contains("db.SaleItems.aggregate", detail.ExecutedMql);
        Assert.DoesNotContain("admin:pw", detail.ExecutedMql);
        Assert.Equal("Passed", detail.ValidationStatus);
        var audit = Assert.Single(_store.Audit, a => a.Resource == "QueryLogs.Mql");
        Assert.Equal("log-a", audit.ResourceId);
        Assert.Equal("db:cust_a", audit.CompanyId);
        Assert.Equal(ActivityHashing.TenantRef("cust_a", "db:cust_a"), audit.TenantRef);
    }

    [Fact]
    public async Task Conversations_of_other_tenants_are_not_found()
    {
        var list = await _service.ConversationsAsync(Access("cust_a"), null, null, null, 1, 25, default);
        Assert.Equal(new[] { "conv-a" }, list.Items.Select(c => c.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.ConversationAsync(Access("cust_a"), "conv-b", default));
        var detail = await _service.ConversationAsync(Access("cust_a"), "conv-a", default);
        Assert.DoesNotContain("abc123secret", detail.Messages.Single().Text);
    }

    [Fact]
    public async Task Activity_detail_hides_mql_without_query_log_permission_and_never_returns_raw_errors()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _service.ActivityDetailAsync(Access("cust_a"), "act-b", default));

        var basic = await _service.ActivityDetailAsync(Access("cust_a", AIDashboardPermissions.View, AIDashboardPermissions.Activity), "act-a", default);
        Assert.Null(basic.Query);
        Assert.Equal(ActivityErrorTypes.MongoDBError, basic.Errors.Single().Category);

        var full = await _service.ActivityDetailAsync(Access("cust_a"), "act-a", default);
        Assert.Equal("db.SaleItems.aggregate([])", full.Query!.ExecutedMql);

        var list = await _service.ActivityAsync(Access("cust_a"), new ActivityFilter { TenantRef = "someone-else" }, default);
        Assert.Equal(new[] { "act-a" }, list.Items.Select(a => a.RequestId));
    }

    [Fact]
    public async Task Providers_never_expose_keys_or_urls()
    {
        var providers = await _service.ProvidersAsync(Access("cust_a"), default);
        var p = Assert.Single(providers);
        Assert.Equal("••••••••1234", p.ApiKey);
        var json = System.Text.Json.JsonSerializer.Serialize(providers);
        Assert.DoesNotContain("verysecret", json);
        Assert.DoesNotContain("api.example.com", json);
        Assert.Contains(_store.Audit, a => a.Resource == "Providers");
    }

    [Fact]
    public void Secret_masking_keeps_only_the_last_four_characters()
    {
        Assert.Equal("sk-••••••••1234", SecretMasker.MaskSecret("sk-abcdefghijklmnop1234"));
        Assert.Equal("Not set", SecretMasker.MaskKeyHint(false, null));
        Assert.Equal("••••••••", SecretMasker.MaskKeyHint(true, "set"));
        Assert.DoesNotContain("eyJ", SecretMasker.Text("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc"));
    }

    [Fact]
    public async Task Overview_and_usage_only_count_the_callers_tenant()
    {
        var overview = await _service.OverviewAsync(Access("cust_a"), 30, default);
        Assert.Equal(1, overview.Questions);
        Assert.Equal(1, overview.Conversations);
        var usage = await _service.UsageAsync(Access("cust_b"), 30, default);
        Assert.Equal(1, usage.Summary.TotalRequests);
    }

    [Fact]
    public async Task Agent_tools_and_prompt_versions_come_from_the_callers_own_activity()
    {
        var tenantA = ActivityHashing.TenantRef("cust_a", "db:cust_a");
        _activities.Items.Add(new AIActivity { ActivityId = "a1", TenantRef = tenantA, Timestamp = DateTime.UtcNow, Status = ActivityStatuses.Success,
            Ai = new ActivityAiInfo { ToolSelected = "database-query", PromptVersion = "v-old", SystemPromptRef = "ref1" }, Request = new ActivityRequestInfo { InputType = "voice", Language = "ml" } });
        _activities.Items.Add(new AIActivity { ActivityId = "a2", TenantRef = tenantA, Timestamp = DateTime.UtcNow, Status = ActivityStatuses.Failed,
            Ai = new ActivityAiInfo { ToolSelected = "database-query" }, Request = new ActivityRequestInfo { AttachmentCount = 2 } });
        _activities.Items.Add(new AIActivity { ActivityId = "b1", TenantRef = ActivityHashing.TenantRef("cust_b", "db:cust_b"), Timestamp = DateTime.UtcNow,
            Ai = new ActivityAiInfo { ToolSelected = "database-query" } });

        var agent = await _insights.AgentAsync(Access("cust_a"), 30, default);
        var tool = agent.Tools.Single(t => t.Name == "database-query");
        Assert.Equal(2, tool.Requests);
        Assert.Equal(1, tool.Succeeded);
        Assert.Equal(1, tool.Failed);
        Assert.Equal(1, agent.Tools.Single(t => t.Name == "voice-transcription").Requests);
        Assert.Contains(agent.PromptVersions, v => v.Version == "v-old" && v.PromptRef == "ref1" && !v.Current);
        Assert.Contains(agent.PromptVersions, v => v.Current);
        Assert.Equal(1, agent.Channels.VoiceRequests);
        Assert.Equal(2, agent.Channels.Attachments);
        Assert.Equal("Active", agent.Status);
        Assert.Equal("DeepSeek", agent.Provider);
    }

    [Fact]
    public async Task Security_events_and_audit_log_are_limited_to_the_callers_tenant()
    {
        var tenantA = ActivityHashing.TenantRef("cust_a", "db:cust_a");
        _activities.Items.Add(new AIActivity { ActivityId = "s1", TenantRef = tenantA, Timestamp = DateTime.UtcNow,
            Validation = new ActivityValidationInfo { Status = "Rejected", Blocked = true, BlockedOperations = new() { "$out" } } });
        _activities.Items.Add(new AIActivity { ActivityId = "s2", TenantRef = tenantA, Timestamp = DateTime.UtcNow,
            Ai = new ActivityAiInfo { ToolSelected = "refused-technical-details" } });
        _store.Audit.Add(new AuditLog { Action = "AIDashboard.Denied", CompanyId = "db:cust_a", TenantRef = tenantA, Resource = "QueryLogs", Outcome = "forbidden" });
        _store.Audit.Add(new AuditLog { Action = "AIDashboard.Denied", CompanyId = "db:cust_b", Resource = "QueryLogs", Outcome = "forbidden" });
        _store.Audit.Add(new AuditLog { Action = "ProviderUpdated", CompanyId = "db:cust_b", Details = "apiKey=sk-secretsecretsecret" });

        var security = await _insights.SecurityAsync(Access("cust_a"), 30, default);
        Assert.Equal(1, security.BlockedQueries);
        Assert.Equal(1, security.TechnicalDetailRequests);
        Assert.Equal(1, security.AccessDenied);
        Assert.Contains(security.Events, e => e.Type == "QueryBlocked" && e.Detail.Contains("$out"));

        var audit = await _insights.AuditLogAsync(Access("cust_a"), new AuditLogFilter { CompanyId = "db:cust_b" }, default);
        Assert.All(audit.Items, i => Assert.NotEqual("ProviderUpdated", i.Action));
        Assert.DoesNotContain(audit.Items, i => i.Details?.Contains("secretsecret") == true);
    }

    [Fact]
    public async Task Schema_lists_only_allowed_collections_without_sensitive_fields()
    {
        var schema = await _insights.SchemaAsync(Access("cust_a"), default);
        var allowed = new AppSettings().Query.AllowedCollections;
        Assert.All(schema.Collections, c => Assert.Contains(c.Name, allowed));
        Assert.DoesNotContain(schema.Collections.SelectMany(c => c.Fields), f => f.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(_store.Audit, a => a.Resource == "Schema");
    }

    [Fact]
    public async Task Models_combine_provider_configuration_with_the_companys_usage()
    {
        var models = await _insights.ModelsAsync(Access("cust_a"), 30, default);
        var m = Assert.Single(models, x => x.Model == "deepseek-chat");
        Assert.Equal("DeepSeek", m.Provider);
        Assert.True(m.IsDefault);
        var json = System.Text.Json.JsonSerializer.Serialize(models);
        Assert.DoesNotContain("verysecret", json);
        Assert.DoesNotContain("api.example.com", json);
    }
}
