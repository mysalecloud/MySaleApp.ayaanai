using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Services;
using MySale.AI.Application.TenantProviders;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.TenantProviders;

namespace MySale.AI.Tests;

// AI Settings → AI Providers & API Keys: validation, secrecy, tenant isolation, add / update / remove flows,
// chat resolution (default unchanged unless chosen) and the provider adapters.

public sealed class TenantMysaleUser : IUserContext
{
    public bool IsAuthenticated { get; set; } = true;
    public string UserId { get; set; } = "u1";
    public string UserName { get; set; } = "owner";
    public string DisplayName { get; set; } = "Owner";
    public UserRole Role { get; set; } = UserRole.Viewer;
    public string CompanyId { get; set; } = "db:tenant_a";
    public string CompanyName { get; set; } = "Tenant A";
    public string Currency { get; set; } = "AED";
    public string TimeZone { get; set; } = "Asia/Dubai";
    public string? DatabaseName { get; set; } = "tenant_a";
    public bool IsMySaleBooksUser { get; set; } = true;
}

public sealed class MemTenantProviders : ITenantProviderRepository
{
    public List<TenantProviderConfig> Configs { get; } = new();
    public List<TenantProviderSecret> Secrets { get; } = new();
    public bool FailNextReplace { get; set; }

    public string NewId() => MemoryStore.NewId();
    public Task<List<TenantProviderConfig>> ListAsync(string tenantKey, CancellationToken ct)
        => Task.FromResult(Configs.Where(c => c.TenantKey == tenantKey && c.RemovedAt is null).Select(Copy).ToList());
    public Task<TenantProviderConfig?> GetAsync(string tenantKey, string id, CancellationToken ct)
        => Task.FromResult(Configs.Where(c => c.TenantKey == tenantKey && c.Id == id).Select(Copy).FirstOrDefault());
    public Task<TenantProviderConfig?> GetDefaultAsync(string tenantKey, CancellationToken ct)
        => Task.FromResult(Configs.Where(c => c.TenantKey == tenantKey && c.IsDefault && c.RemovedAt is null).Select(Copy).FirstOrDefault());
    public Task InsertAsync(TenantProviderConfig config, CancellationToken ct) { Configs.Add(Copy(config)); return Task.CompletedTask; }
    public Task<bool> ReplaceAsync(TenantProviderConfig config, int expectedVersion, CancellationToken ct)
    {
        if (FailNextReplace) { FailNextReplace = false; return Task.FromResult(false); }
        var i = Configs.FindIndex(c => c.TenantKey == config.TenantKey && c.Id == config.Id && c.Version == expectedVersion);
        if (i < 0) return Task.FromResult(false);
        Configs[i] = Copy(config);
        return Task.FromResult(true);
    }
    public Task ClearDefaultAsync(string tenantKey, string? exceptId, CancellationToken ct)
    {
        foreach (var c in Configs.Where(c => c.TenantKey == tenantKey && c.IsDefault && c.Id != exceptId)) { c.IsDefault = false; c.Version++; }
        return Task.CompletedTask;
    }
    public Task UpdateStatusAsync(string tenantKey, string id, TenantProviderStatus status, string? error, long? latencyMs, int? modelCount, DateTime at, CancellationToken ct)
    {
        foreach (var c in Configs.Where(c => c.TenantKey == tenantKey && c.Id == id)) { c.Status = status; c.LastError = error; if (latencyMs is not null) c.LastTestedAt = at; }
        return Task.CompletedTask;
    }
    public Task<TenantProviderSecret?> GetSecretAsync(string tenantKey, string configId, string secretId, CancellationToken ct)
        => Task.FromResult(Secrets.FirstOrDefault(s => s.TenantKey == tenantKey && s.ConfigId == configId && s.Id == secretId));
    public Task InsertSecretAsync(TenantProviderSecret secret, CancellationToken ct) { Secrets.Add(secret); return Task.CompletedTask; }
    public Task DeleteSecretsAsync(string tenantKey, string configId, string? exceptSecretId, CancellationToken ct)
    {
        Secrets.RemoveAll(s => s.TenantKey == tenantKey && s.ConfigId == configId && s.Id != exceptSecretId);
        return Task.CompletedTask;
    }

    private static TenantProviderConfig Copy(TenantProviderConfig c)
        => JsonSerializer.Deserialize<TenantProviderConfig>(JsonSerializer.Serialize(c))!;
}

/// <summary>Provider returned by the fake adapter factory: succeeds / fails the test as configured and records the key it got.</summary>
public sealed class FakeTenantAdapters : ITenantProviderAdapterFactory
{
    public bool TestSucceeds { get; set; } = true;
    public string FailureMessage { get; set; } = "Provider: authentication failed — check the API key.";
    public List<TenantProviderConnection> Created { get; } = new();
    public ScriptedProvider Chat { get; } = new();

    public IAIProvider Create(TenantProviderConnection connection)
    {
        Created.Add(connection);
        return new Adapter(this);
    }

    private sealed class Adapter : IAIProvider
    {
        private readonly FakeTenantAdapters _owner;
        public Adapter(FakeTenantAdapters owner) => _owner = owner;
        public string Kind => "fake";
        public Task<AIQueryResponse> GenerateQueryAsync(AIChatRequest request, CancellationToken ct) => _owner.Chat.GenerateQueryAsync(request, ct);
        public Task<AIResponse> GenerateResponseAsync(AIChatRequest request, CancellationToken ct) => _owner.Chat.GenerateResponseAsync(request, ct);
        public IAsyncEnumerable<AIStreamChunk> StreamResponseAsync(AIChatRequest request, CancellationToken ct) => _owner.Chat.StreamResponseAsync(request, ct);
        public Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct) => Task.FromResult(_owner.TestSucceeds
            ? new ProviderTestResult { Success = true, Message = "Connected. 2 model(s) available.", ModelCount = 2, LatencyMs = 12 }
            : new ProviderTestResult { Success = false, Message = _owner.FailureMessage });
        public Task<IReadOnlyList<AIModel>> GetModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AIModel>>(new[] { new AIModel { Id = "gpt-4.1-mini", Name = "gpt-4.1-mini" }, new AIModel { Id = "gpt-4.1", Name = "gpt-4.1" } });
    }
}

public class TenantProviderKeyTests
{
    private const string KeyA = "sk-proj-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
    private const string KeyB = "sk-proj-ZyXwVuTsRqPoNmLkJiHgFeDcBa9876543210";

    private sealed class Rig
    {
        public MemTenantProviders Repo { get; } = new();
        public FakeTenantAdapters Adapters { get; } = new();
        public MemoryStore Store { get; } = new();
        public TenantAiProviderOptions Options { get; } = new();
        public ITenantSecretProtector Protector { get; } = new DataProtectionTenantSecretProtector(new EphemeralDataProtectionProvider());
        public TenantProviderService Service { get; }
        public TenantMysaleUser User { get; } = new();

        public Rig()
        {
            Service = new TenantProviderService(Repo, Protector, Adapters, Options, new MemAudit(Store), NullLogger<TenantProviderService>.Instance);
        }

        public TenantProviderResolver Resolver() => new(User, Repo, Protector, Adapters, Options, NullLogger<TenantProviderResolver>.Instance);

        public ProviderService Providers()
        {
            Store.Providers.Add(new ProviderConfig
            {
                Id = "66c000000000000000000001", Name = "Ollama", Kind = "ollama", Category = ProviderCategory.Local,
                BaseUrl = "http://localhost:11434", DefaultModel = "llama3.1", Enabled = true, IsDefault = true
            });
            return new ProviderService(new MemProviders(Store), new FakeProviderFactory(new ScriptedProvider()), new PlainSecrets(),
                new AuditService(new MemAudit(Store), new FakeUser()), null, Resolver());
        }
    }

    private static TenantProviderActor Admin(string tenant = "db:tenant_a") => new() { TenantKey = tenant, TenantRef = "ref-" + tenant, UserId = "u1", UserName = "owner", AyaanUserName = "ops", AyaanRole = "Admin" };
    private static TenantProviderActor Viewer(string tenant = "db:tenant_a") => new() { TenantKey = tenant, UserId = "u2", UserName = "viewer", AyaanRole = "Viewer" };

    private static TenantProviderUpsertRequest OpenAI(string? key = KeyA, bool untested = false) => new()
    {
        ProviderId = "openai", ApiKey = key, DefaultModel = "gpt-4.1-mini", Enabled = true, SaveWithoutTest = untested
    };

    private static string Json(object o) => JsonSerializer.Serialize(o);

    // ------------------------------------------------------------------ validation / SSRF

    [Theory]
    [InlineData("http://gateway.example.com/v1")]
    [InlineData("https://localhost:8080/v1")]
    [InlineData("https://127.0.0.1/v1")]
    [InlineData("https://10.1.2.3/v1")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://192.168.1.10/v1")]
    [InlineData("https://[::1]/v1")]
    [InlineData("https://user:pass@gateway.example.com/v1")]
    [InlineData("https://gateway.example.com/v1?x=1")]
    [InlineData("https://metadata.internal/v1")]
    [InlineData("ftp://gateway.example.com")]
    public void Unsafe_endpoints_are_rejected(string url)
    {
        var request = new TenantProviderUpsertRequest { ProviderId = "openai-compatible", BaseUrl = url };
        Assert.Throws<AppValidationException>(() => TenantProviderValidation.Normalize(request, new TenantAiProviderOptions()));
    }

    [Fact]
    public void Public_https_endpoint_is_accepted_and_normalised()
    {
        var n = TenantProviderValidation.Normalize(new TenantProviderUpsertRequest { ProviderId = "openai-compatible", BaseUrl = " https://gateway.example.com/v1/ " },
            new TenantAiProviderOptions());
        Assert.Equal("https://gateway.example.com/v1", n.BaseUrl);
    }

    [Fact]
    public void Fixed_provider_endpoint_cannot_be_redirected()
    {
        var n = TenantProviderValidation.Normalize(new TenantProviderUpsertRequest { ProviderId = "openai", BaseUrl = "https://evil.example.com/v1" },
            new TenantAiProviderOptions());
        Assert.Equal("https://api.openai.com/v1", n.BaseUrl);
    }

    [Theory]
    [InlineData("sk-abc def ghijkl")]
    [InlineData("sk-abc\r\nX-Evil: 1")]
    [InlineData("short")]
    public void Malformed_keys_are_rejected_without_echoing_them(string key)
    {
        var ex = Assert.Throws<AppValidationException>(() =>
            TenantProviderValidation.Normalize(new TenantProviderUpsertRequest { ProviderId = "openai", ApiKey = key }, new TenantAiProviderOptions()));
        Assert.DoesNotContain(key.Trim(), ex.Message);
    }

    [Fact]
    public void Unknown_and_header_unsafe_settings_are_rejected()
    {
        var options = new TenantAiProviderOptions();
        Assert.Throws<AppValidationException>(() => TenantProviderValidation.Normalize(new TenantProviderUpsertRequest
        {
            ProviderId = "openai", Settings = new() { ["x-forwarded-host"] = "a" }
        }, options));
        Assert.Throws<AppValidationException>(() => TenantProviderValidation.Normalize(new TenantProviderUpsertRequest
        {
            ProviderId = "openai", Settings = new() { ["organization"] = "org-1\r\nX: y" }
        }, options));
        Assert.Throws<AppValidationException>(() => TenantProviderValidation.Normalize(new TenantProviderUpsertRequest
        {
            ProviderId = "azure-openai", BaseUrl = "https://res.openai.azure.com"
        }, options)); // deployment is required
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Public_address_check(string ip, bool expected)
        => Assert.Equal(expected, TenantProviderValidation.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void Redactor_removes_keys_but_keeps_useful_text()
    {
        var text = $"Incorrect API key provided: {KeyA[..10]}***{KeyA[^4..]}. Authorization: Bearer {KeyB} model meta/llama-3.3-70b-instruct";
        var safe = SecretRedactor.Redact(text, KeyA);
        Assert.DoesNotContain(KeyA[..10], safe);
        Assert.DoesNotContain(KeyB, safe);
        Assert.Contains("meta/llama-3.3-70b-instruct", safe);
        Assert.Contains("Incorrect API key provided", safe);
        Assert.Equal("[redacted] failed", SecretRedactor.Redact(KeyA + " failed", KeyA));
    }

    // ------------------------------------------------------------------ add / update / remove

    [Fact]
    public async Task Add_with_failed_test_saves_nothing()
    {
        var rig = new Rig { Adapters = { TestSucceeds = false } };
        var result = await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None);
        Assert.False(result.Saved);
        Assert.False(result.Test!.Success);
        Assert.Empty(rig.Repo.Configs);
        Assert.Empty(rig.Repo.Secrets);
    }

    [Fact]
    public async Task Add_encrypts_the_key_and_never_returns_it()
    {
        var rig = new Rig();
        var result = await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Equal("Configured", result.Item!.Credential);
        Assert.Equal("Connected", result.Item.Status);
        Assert.False(result.Item.IsDefault); // adding a key never changes what chat uses
        Assert.DoesNotContain(KeyA, Json(result));
        Assert.DoesNotContain(KeyA[^6..], Json(result));

        var overview = await rig.Service.OverviewAsync(Admin(), CancellationToken.None);
        Assert.DoesNotContain(KeyA, Json(overview));
        Assert.Equal("system", overview.ActiveSource);

        var secret = Assert.Single(rig.Repo.Secrets);
        Assert.DoesNotContain(KeyA, secret.Ciphertext);
        Assert.DoesNotContain(KeyA, Json(rig.Repo.Configs)); // metadata never holds the key
        Assert.Equal(KeyA, rig.Adapters.Created.Last().ApiKey); // the provider (only) got the key
        Assert.All(rig.Store.Audit, a => Assert.DoesNotContain(KeyA[..12], a.Details ?? string.Empty));
        Assert.Contains(rig.Store.Audit, a => a.Action == "AIProviderKeyAdded" && a.CompanyId == "db:tenant_a");
    }

    [Fact]
    public async Task Save_without_test_is_possible_and_marked_not_connected()
    {
        var rig = new Rig { Adapters = { TestSucceeds = false } };
        var result = await rig.Service.CreateAsync(Admin(), OpenAI(untested: true), CancellationToken.None);
        Assert.True(result.Saved);
        Assert.Equal("NotConnected", result.Item!.Status);
        Assert.Empty(rig.Adapters.Created); // no call to the provider
    }

    [Fact]
    public async Task Failed_update_keeps_the_previous_configuration_and_key()
    {
        var rig = new Rig();
        var created = (await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None)).Item!;
        var before = Json(rig.Repo.Configs);
        var secretBefore = rig.Repo.Secrets.Single().Ciphertext;

        rig.Adapters.TestSucceeds = false;
        var update = OpenAI(KeyB);
        update.Version = created.Version;
        var result = await rig.Service.UpdateAsync(Admin(), created.Id, update, CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Equal(before, Json(rig.Repo.Configs));
        Assert.Equal(secretBefore, rig.Repo.Secrets.Single().Ciphertext);
    }

    [Fact]
    public async Task Update_replaces_the_key_and_destroys_the_old_one()
    {
        var rig = new Rig();
        var created = (await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None)).Item!;
        var oldSecretId = rig.Repo.Secrets.Single().Id;
        var update = OpenAI(KeyB);
        update.Version = created.Version;
        var result = await rig.Service.UpdateAsync(Admin(), created.Id, update, CancellationToken.None);

        Assert.True(result.Saved);
        var secret = Assert.Single(rig.Repo.Secrets);
        Assert.NotEqual(oldSecretId, secret.Id);
        Assert.Equal(KeyB, rig.Protector.Unprotect("db:tenant_a", created.Id, secret.Ciphertext));
        Assert.Equal(created.Version + 1, result.Item!.Version);
    }

    [Fact]
    public async Task Concurrent_update_is_rejected_and_the_new_secret_discarded()
    {
        var rig = new Rig();
        var created = (await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None)).Item!;
        var oldSecretId = rig.Repo.Secrets.Single().Id;
        rig.Repo.FailNextReplace = true;
        await Assert.ThrowsAsync<TenantProviderConflictException>(() => rig.Service.UpdateAsync(Admin(), created.Id, OpenAI(KeyB), CancellationToken.None));
        Assert.Equal(oldSecretId, Assert.Single(rig.Repo.Secrets).Id);
    }

    [Fact]
    public async Task Changing_the_endpoint_requires_the_key_again()
    {
        var rig = new Rig();
        var created = (await rig.Service.CreateAsync(Admin(), new TenantProviderUpsertRequest
        {
            ProviderId = "openai-compatible", BaseUrl = "https://gw-one.example.com/v1", ApiKey = KeyA, DefaultModel = "m1", Enabled = true
        }, CancellationToken.None)).Item!;

        var move = new TenantProviderUpsertRequest { ProviderId = "openai-compatible", BaseUrl = "https://attacker.example.net/v1", DefaultModel = "m1", Enabled = true };
        await Assert.ThrowsAsync<AppValidationException>(() => rig.Service.UpdateAsync(Admin(), created.Id, move, CancellationToken.None));
        await Assert.ThrowsAsync<AppValidationException>(() => rig.Service.TestDraftAsync(Admin(), new TenantProviderTestRequest { Config = move, ExistingId = created.Id }, CancellationToken.None));
        Assert.DoesNotContain(rig.Adapters.Created, c => c.BaseUrl.Contains("attacker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_destroys_the_key_and_keeps_an_audit_record()
    {
        var rig = new Rig();
        var created = (await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None)).Item!;
        await rig.Service.SetDefaultAsync(Admin(), new TenantProviderDefaultRequest { ConfigurationId = created.Id }, CancellationToken.None);

        await rig.Service.RemoveAsync(Admin(), created.Id, null, CancellationToken.None);

        Assert.Empty(rig.Repo.Secrets);
        var stored = Assert.Single(rig.Repo.Configs);
        Assert.NotNull(stored.RemovedAt);
        Assert.False(stored.IsDefault);
        Assert.Empty((await rig.Service.OverviewAsync(Admin(), CancellationToken.None)).Items);
        Assert.Contains(rig.Store.Audit, a => a.Action == "AIProviderKeyRemoved");
        Assert.Null(await rig.Resolver().ResolveAsync(null, new AppSettings(), CancellationToken.None)); // back to the AYAAN default
    }

    // ------------------------------------------------------------------ authorisation / isolation

    [Fact]
    public async Task Viewers_can_read_but_not_change()
    {
        var rig = new Rig();
        var overview = await rig.Service.OverviewAsync(Viewer(), CancellationToken.None);
        Assert.False(overview.CanManage);
        Assert.NotEmpty(overview.Catalog);
        var ex = await Assert.ThrowsAsync<TenantProviderAccessException>(() => rig.Service.CreateAsync(Viewer(), OpenAI(), CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
        Assert.Empty(rig.Repo.Configs);
    }

    [Fact]
    public async Task Another_company_cannot_see_or_touch_the_configuration()
    {
        var rig = new Rig();
        var created = (await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None)).Item!;
        var other = Admin("db:tenant_b");

        Assert.Empty((await rig.Service.OverviewAsync(other, CancellationToken.None)).Items);
        await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.UpdateAsync(other, created.Id, OpenAI(KeyB), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.RemoveAsync(other, created.Id, null, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.TestSavedAsync(other, created.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.SetDefaultAsync(other, new TenantProviderDefaultRequest { ConfigurationId = created.Id }, CancellationToken.None));
        Assert.Single(rig.Repo.Secrets);
    }

    [Fact]
    public void Ciphertext_is_bound_to_tenant_and_configuration()
    {
        ITenantSecretProtector p = new DataProtectionTenantSecretProtector(new EphemeralDataProtectionProvider());
        var c = p.Protect("db:tenant_a", "cfg1", KeyA);
        Assert.Equal(KeyA, p.Unprotect("db:tenant_a", "cfg1", c));
        Assert.Null(p.Unprotect("db:tenant_b", "cfg1", c));
        Assert.Null(p.Unprotect("db:tenant_a", "cfg2", c));
        Assert.Null(p.Unprotect("db:tenant_a", "cfg1", "garbage"));
    }

    // ------------------------------------------------------------------ chat resolution

    [Fact]
    public async Task Chat_keeps_the_existing_default_until_the_company_chooses_its_provider()
    {
        var rig = new Rig();
        var providers = rig.Providers();
        await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None);

        var before = await providers.ResolveAsync(null, null, new AppSettings(), CancellationToken.None);
        Assert.Equal("Ollama", before.Config.Name);

        var id = rig.Repo.Configs.Single().Id;
        await rig.Service.SetDefaultAsync(Admin(), new TenantProviderDefaultRequest { ConfigurationId = id }, CancellationToken.None);
        var after = await providers.ResolveAsync(null, null, new AppSettings(), CancellationToken.None);
        Assert.Equal(TenantProviderResolver.IdPrefix + id, after.Config.Id);
        Assert.Equal("gpt-4.1-mini", after.Model);
        Assert.Null(after.Config.ApiKeyEncrypted); // the shared provider shape never carries the company's ciphertext
        Assert.Equal(KeyA, rig.Adapters.Created.Last().ApiKey);

        // An explicit provider selection still wins.
        var explicitSel = await providers.ResolveAsync("66c000000000000000000001", null, new AppSettings(), CancellationToken.None);
        Assert.Equal("Ollama", explicitSel.Config.Name);

        // Back to the AYAAN default.
        await rig.Service.SetDefaultAsync(Admin(), new TenantProviderDefaultRequest { ConfigurationId = null }, CancellationToken.None);
        Assert.Equal("Ollama", (await providers.ResolveAsync(null, null, new AppSettings(), CancellationToken.None)).Config.Name);
    }

    [Fact]
    public async Task Non_company_callers_and_other_tenants_are_unaffected()
    {
        var rig = new Rig();
        var id = (await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None)).Item!.Id;
        await rig.Service.SetDefaultAsync(Admin(), new TenantProviderDefaultRequest { ConfigurationId = id }, CancellationToken.None);

        rig.User.IsMySaleBooksUser = false; // AYAAN operator / prototype user
        Assert.Null(await rig.Resolver().ResolveAsync(null, new AppSettings(), CancellationToken.None));

        rig.User.IsMySaleBooksUser = true;
        rig.User.CompanyId = "db:tenant_b";
        rig.User.DatabaseName = "tenant_b";
        Assert.Null(await rig.Resolver().ResolveAsync(null, new AppSettings(), CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_company_provider_falls_back_to_the_existing_default()
    {
        var rig = new Rig();
        var id = (await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None)).Item!.Id;
        await rig.Service.SetDefaultAsync(Admin(), new TenantProviderDefaultRequest { ConfigurationId = id }, CancellationToken.None);
        await rig.Service.SetEnabledAsync(Admin(), id, false, CancellationToken.None);
        Assert.Null(await rig.Resolver().ResolveAsync(null, new AppSettings(), CancellationToken.None));
    }

    [Fact]
    public async Task Unreadable_key_fails_clearly_unless_fallback_is_allowed()
    {
        var rig = new Rig();
        var id = (await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None)).Item!.Id;
        await rig.Service.SetDefaultAsync(Admin(), new TenantProviderDefaultRequest { ConfigurationId = id }, CancellationToken.None);
        rig.Repo.Secrets.Single().Ciphertext = "tampered";

        var ex = await Assert.ThrowsAsync<AIProviderException>(() => rig.Resolver().ResolveAsync(null, new AppSettings(), CancellationToken.None));
        Assert.Contains("AI Providers & API Keys", ex.Message);

        rig.Repo.Configs.Single().AllowSystemFallback = true;
        Assert.Null(await rig.Resolver().ResolveAsync(null, new AppSettings(), CancellationToken.None));
    }

    [Fact]
    public async Task Chat_failures_mark_the_provider_as_error_without_leaking_the_key()
    {
        var rig = new Rig();
        var id = (await rig.Service.CreateAsync(Admin(), OpenAI(), CancellationToken.None)).Item!.Id;
        await rig.Service.SetDefaultAsync(Admin(), new TenantProviderDefaultRequest { ConfigurationId = id }, CancellationToken.None);
        var resolved = (await rig.Resolver().ResolveAsync(null, new AppSettings(), CancellationToken.None))!;

        rig.Adapters.Chat.FailAnswer = true;
        var ex = await Assert.ThrowsAsync<AIProviderException>(() =>
            resolved.Provider.GenerateResponseAsync(new AIChatRequest { Messages = new[] { AIChatMessage.User("hi") }, Model = "m" }, CancellationToken.None));
        Assert.DoesNotContain(KeyA, ex.Message);
        Assert.Equal(TenantProviderStatus.Error, rig.Repo.Configs.Single().Status);
    }

    // ------------------------------------------------------------------ adapters (HTTP)

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TenantProviderConnection Connection(string providerId, string? model = null) => new()
    {
        Descriptor = ProviderRegistry.Find(providerId)!,
        DisplayName = providerId,
        BaseUrl = ProviderRegistry.Find(providerId)!.DefaultBaseUrl,
        ApiKey = KeyA,
        Model = model ?? string.Empty
    };

    [Fact]
    public async Task Model_list_accepts_array_and_data_shapes()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, "[{\"id\":\"meta-llama/Llama-3.3-70B\"},{\"id\":\"Qwen/Qwen2.5\"}]"));
        var together = new TenantOpenAICompatibleProvider(new HttpClient(handler), Connection("together"), "https://api.together.xyz/v1", "openai-compatible", KeyA);
        var models = await together.GetModelsAsync(CancellationToken.None);
        Assert.Equal(2, models.Count);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);

        var gemini = new TenantOpenAICompatibleProvider(new HttpClient(new StubHandler(_ => JsonResponse(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"models/gemini-2.5-flash\"}]}"))), Connection("gemini"), "https://generativelanguage.googleapis.com/v1beta/openai", "openai-compatible", KeyA);
        Assert.Equal("gemini-2.5-flash", Assert.Single(await gemini.GetModelsAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task Test_without_model_listing_pings_the_model()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/models")
            ? JsonResponse(HttpStatusCode.NotFound, "{}")
            : JsonResponse(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"OK\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}"));
        var p = new TenantOpenAICompatibleProvider(new HttpClient(handler), Connection("cohere", "command-r"), "https://api.cohere.ai/compatibility/v1", "openai-compatible", KeyA);
        var result = await p.TestConnectionAsync(CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/chat/completions"));
    }

    [Fact]
    public async Task Invalid_key_is_reported_without_the_key()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.Unauthorized,
            "{\"error\":{\"message\":\"Incorrect API key provided: " + KeyA + "\"}}"));
        var p = new TenantOpenAICompatibleProvider(new HttpClient(handler), Connection("openai"), "https://api.openai.com/v1", "openai", KeyA);
        var result = await p.TestConnectionAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("authentication failed", result.Message);
        Assert.DoesNotContain(KeyA, result.Message);
    }

    [Fact]
    public void Adapter_factory_refuses_private_endpoints_at_use_time()
    {
        using var clients = new TenantHttpClients(new TenantAiProviderOptions());
        var factory = new TenantProviderAdapterFactory(clients, new TenantAiProviderOptions());
        var c = new TenantProviderConnection { Descriptor = ProviderRegistry.Find("openai-compatible")!, DisplayName = "x", BaseUrl = "https://10.0.0.5/v1", ApiKey = KeyA };
        Assert.Throws<AIProviderException>(() => factory.Create(c));
    }

    [Fact]
    public void Bedrock_body_merges_roles_and_moves_system_prompt()
    {
        var body = BedrockConverseProvider.BuildBody(new AIChatRequest
        {
            Model = "us.amazon.nova-pro-v1:0",
            Messages = new[] { AIChatMessage.System("rules"), AIChatMessage.Assistant("earlier"), AIChatMessage.User("a"), AIChatMessage.User("b") }
        });
        var json = body.ToJsonString();
        Assert.Contains("\"system\":[{\"text\":\"rules\"}]", json);
        var messages = body["messages"]!.AsArray();
        Assert.Equal("user", messages[0]!["role"]!.ToString());
        Assert.Equal("assistant", messages[1]!["role"]!.ToString());
        Assert.Equal("user", messages[2]!["role"]!.ToString());
        Assert.Equal(2, messages[2]!["content"]!.AsArray().Count);
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public void Registry_covers_the_required_providers()
    {
        foreach (var id in new[] { "openai", "anthropic", "gemini", "azure-openai", "mistral", "deepseek", "xai", "cohere", "groq", "together",
                     "fireworks", "perplexity", "bedrock", "cerebras", "sambanova", "ollama", "openai-compatible" })
            Assert.NotNull(ProviderRegistry.Find(id));
        Assert.All(ProviderRegistry.All.Where(d => !d.BaseUrlEditable && d.Adapter != ProviderAdapterType.BedrockConverse),
            d => Assert.StartsWith("https://", d.DefaultBaseUrl));
    }
}
