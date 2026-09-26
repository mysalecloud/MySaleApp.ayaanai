using Microsoft.Extensions.Options;
using MySale.AI.Application.Services;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.AI;

namespace MySale.AI.Tests;

public class ProviderOverridesTests
{
    private static (ProviderService Service, MemoryStore Store) Build(AiProvidersOptions? options)
    {
        var store = new MemoryStore();
        store.Providers.Add(new ProviderConfig
        {
            Id = "66c000000000000000000001", Name = "Ollama", Kind = "ollama", Category = ProviderCategory.Local,
            BaseUrl = "http://localhost:11434", DefaultModel = "llama3.1", Enabled = true, IsDefault = true
        });
        store.Providers.Add(new ProviderConfig
        {
            Id = "66c000000000000000000002", Name = "Google Gemini", Kind = "ollama", Category = ProviderCategory.Cloud,
            BaseUrl = "https://example.invalid", DefaultModel = "m1", Enabled = false, ApiKeyEncrypted = "undecryptable"
        });
        var audit = new AuditService(new MemAudit(store), new FakeUser());
        var overrides = options is null ? null : new ConfigurationProviderOverrides(Options.Create(options));
        var service = new ProviderService(new MemProviders(store), new FakeProviderFactory(new ScriptedProvider()), new PlainSecrets(), audit, overrides);
        return (service, store);
    }

    [Fact]
    public async Task Without_overrides_the_dashboard_default_is_used()
    {
        var (service, _) = Build(null);
        var resolved = await service.ResolveAsync(null, null, new AppSettings(), CancellationToken.None);
        Assert.Equal("Ollama", resolved.Config.Name);
        Assert.Equal("llama3.1", resolved.Model);
    }

    [Fact]
    public async Task Deployment_default_key_url_and_model_take_precedence()
    {
        var options = new AiProvidersOptions { Default = "Google_Gemini", DefaultModel = "m2" };
        options.Overrides["Google_Gemini"] = new AiProviderOverride { ApiKey = " key-123 ", BaseUrl = "https://gw.example.com/v1/" };
        var (service, store) = Build(options);

        var resolved = await service.ResolveAsync(null, null, new AppSettings(), CancellationToken.None);

        Assert.Equal("Google Gemini", resolved.Config.Name);
        Assert.Equal("m2", resolved.Model);
        Assert.Equal("enc:key-123", resolved.Config.ApiKeyEncrypted);
        Assert.Equal("https://gw.example.com/v1", resolved.Config.BaseUrl);
    }

    [Fact]
    public async Task Explicit_provider_selection_still_wins()
    {
        var (service, _) = Build(new AiProvidersOptions { Default = "Google Gemini" });
        var resolved = await service.ResolveAsync("66c000000000000000000001", null, new AppSettings(), CancellationToken.None);
        Assert.Equal("Ollama", resolved.Config.Name);
    }
}
