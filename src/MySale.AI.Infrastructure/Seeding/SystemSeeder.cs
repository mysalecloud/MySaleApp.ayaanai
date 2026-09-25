using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.BusinessData;
using MySale.AI.Infrastructure.Persistence;

namespace MySale.AI.Infrastructure.Seeding;

/// <summary>Creates indexes, default settings, demo users, default AI providers and (optionally) sample ERP data on startup.</summary>
public sealed class SystemSeeder
{
    private readonly SystemDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ISecretProtector _secrets;
    private readonly SeedOptions _options;
    private readonly IBusinessDatabase _business;
    private readonly SampleDataSeeder _sample;
    private readonly ILogger<SystemSeeder> _logger;

    public SystemSeeder(SystemDbContext db, IPasswordHasher hasher, ISecretProtector secrets, IOptions<SeedOptions> options,
        IBusinessDatabase business, SampleDataSeeder sample, ILogger<SystemSeeder> logger)
    {
        _db = db;
        _hasher = hasher;
        _secrets = secrets;
        _options = options.Value;
        _business = business;
        _sample = sample;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_options.SeedSystemDataOnStartup)
        {
            try
            {
                await _db.EnsureIndexesAsync(ct);
                await SeedSettingsAsync(ct);
                await SeedUsersAsync(ct);
                await SeedProvidersAsync(ct);
                _logger.LogInformation("System database ready");
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                _logger.LogError(ex, "System database is not reachable. Start MongoDB and restart the API.");
                return;
            }
        }

        if (_options.SeedBusinessDataOnStartup)
        {
            try
            {
                var db = await _business.GetDatabaseAsync(ct);
                if (!await _sample.HasDataAsync(db, ct))
                {
                    var result = await _sample.SeedAsync(db, reset: false, ct);
                    _logger.LogInformation("Seeded sample business data in {Ms} ms: {Counts}", result.ElapsedMs,
                        string.Join(", ", result.Collections.Select(kv => $"{kv.Key}={kv.Value}")));
                }
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                _logger.LogWarning(ex, "Business database is not reachable; sample data was not seeded");
            }
        }
    }

    private async Task SeedSettingsAsync(CancellationToken ct)
    {
        var existing = await _db.Settings.Find(s => s.Id == "global").FirstOrDefaultAsync(ct);
        if (existing is null)
            await _db.Settings.InsertOneAsync(new AppSettings(), cancellationToken: ct);
    }

    private async Task SeedUsersAsync(CancellationToken ct)
    {
        if (await _db.Users.CountDocumentsAsync(FilterDefinition<AppUser>.Empty, cancellationToken: ct) > 0) return;

        var a = SampleDataSeeder.CompanyA.ToString();
        var b = SampleDataSeeder.CompanyB.ToString();
        var c = SampleDataSeeder.CompanyC.ToString();
        var hash = _hasher.Hash(_options.DemoPassword);

        var users = new[]
        {
            new AppUser { UserName = "admin.a", DisplayName = "Aisha (Admin)", Role = UserRole.Admin, CompanyId = a, CompanyName = "Al Noor Trading LLC", Currency = "AED", TimeZone = "Asia/Dubai" },
            new AppUser { UserName = "tester.a", DisplayName = "Rahul (Tester)", Role = UserRole.Tester, CompanyId = a, CompanyName = "Al Noor Trading LLC", Currency = "AED", TimeZone = "Asia/Dubai" },
            new AppUser { UserName = "viewer.a", DisplayName = "Omar (Viewer)", Role = UserRole.Viewer, CompanyId = a, CompanyName = "Al Noor Trading LLC", Currency = "AED", TimeZone = "Asia/Dubai" },
            new AppUser { UserName = "admin.b", DisplayName = "Sara (Admin)", Role = UserRole.Admin, CompanyId = b, CompanyName = "Gulf Star Electronics", Currency = "AED", TimeZone = "Asia/Dubai" },
            new AppUser { UserName = "tester.c", DisplayName = "Faisal (Tester)", Role = UserRole.Tester, CompanyId = c, CompanyName = "Desert Rose Supermarket", Currency = "SAR", TimeZone = "Asia/Riyadh" }
        };
        foreach (var u in users) u.PasswordHash = hash;
        await _db.Users.InsertManyAsync(users, cancellationToken: ct);
    }

    private static ProviderConfig DeepSeekProvider() => new()
    {
        Name = "DeepSeek",
        Kind = "openai-compatible",
        Category = ProviderCategory.Cloud,
        BaseUrl = "https://api.deepseek.com/v1",
        DefaultModel = "deepseek-chat",
        Temperature = 0.1,
        MaxTokens = 1500,
        TimeoutSeconds = 120,
        Enabled = false,
        InputCostPer1M = 0.27m,
        OutputCostPer1M = 1.10m
    };

    private async Task SeedProvidersAsync(CancellationToken ct)
    {
        if (await _db.Providers.CountDocumentsAsync(FilterDefinition<ProviderConfig>.Empty, cancellationToken: ct) > 0)
        {
            // Existing installs: add providers introduced later, once.
            if (!await _db.Providers.Find(p => p.Name == "DeepSeek").AnyAsync(ct))
                await _db.Providers.InsertOneAsync(DeepSeekProvider(), cancellationToken: ct);
            return;
        }

        var openAi = new ProviderConfig
        {
            Name = "OpenAI",
            Kind = "openai",
            Category = ProviderCategory.Cloud,
            BaseUrl = "https://api.openai.com/v1",
            DefaultModel = _options.OpenAIModel,
            Temperature = 0.1,
            MaxTokens = 1500,
            TimeoutSeconds = 60,
            Enabled = true,
            InputCostPer1M = 0.15m,
            OutputCostPer1M = 0.60m
        };
        if (!string.IsNullOrWhiteSpace(_options.OpenAIApiKey))
        {
            var key = _options.OpenAIApiKey.Trim();
            openAi.ApiKeyEncrypted = _secrets.Protect(key);
            openAi.ApiKeyHint = key.Length > 8 ? "…" + key[^4..] : "set";
        }

        var providers = new[]
        {
            new ProviderConfig
            {
                Name = "Ollama",
                Kind = "ollama",
                Category = ProviderCategory.Local,
                BaseUrl = _options.OllamaUrl.TrimEnd('/'),
                DefaultModel = _options.OllamaModel,
                Temperature = 0.1,
                MaxTokens = 1500,
                TimeoutSeconds = 180,
                Enabled = true,
                IsDefault = true
            },
            openAi,
            new ProviderConfig
            {
                Name = "Google Gemini",
                Kind = "openai-compatible",
                Category = ProviderCategory.Cloud,
                BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
                DefaultModel = "gemini-2.0-flash",
                Temperature = 0.1,
                MaxTokens = 1500,
                TimeoutSeconds = 60,
                Enabled = false,
                InputCostPer1M = 0.10m,
                OutputCostPer1M = 0.40m
            },
            new ProviderConfig
            {
                Name = "Anthropic Claude",
                Kind = "openai-compatible",
                Category = ProviderCategory.Cloud,
                BaseUrl = "https://api.anthropic.com/v1",
                DefaultModel = "claude-sonnet-4-5",
                Temperature = 0.1,
                MaxTokens = 1500,
                TimeoutSeconds = 60,
                Enabled = false,
                InputCostPer1M = 3m,
                OutputCostPer1M = 15m
            },
            DeepSeekProvider()
        };
        await _db.Providers.InsertManyAsync(providers, cancellationToken: ct);
    }
}
