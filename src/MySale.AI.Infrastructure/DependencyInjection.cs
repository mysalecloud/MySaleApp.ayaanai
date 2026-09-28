using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.AIDashboard;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Application.Stores;
using MySale.AI.Infrastructure.ActivityTracking;
using MySale.AI.Infrastructure.AIDashboard;
using MySale.AI.Infrastructure.AI;
using MySale.AI.Infrastructure.BusinessData;
using MySale.AI.Infrastructure.Media;
using MySale.AI.Infrastructure.Persistence;
using MySale.AI.Infrastructure.Security;
using MySale.AI.Infrastructure.Seeding;

namespace MySale.AI.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Application layer services (orchestrator, MQL engine, services).</summary>
    public static IServiceCollection AddAgentApplication(this IServiceCollection services, IConfiguration configuration)
    {
        var tenant = configuration.GetSection(TenantOptions.Section).Get<TenantOptions>() ?? new TenantOptions();
        services.AddSingleton(tenant);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<MqlValidator>();
        services.AddSingleton<TenantQueryGuard>();
        services.AddSingleton<PromptBuilder>();

        services.AddScoped<AuditService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<ProviderService>();
        services.AddScoped<ConversationService>();
        services.AddScoped<QueryLogService>();
        services.AddScoped<UsageService>();
        services.AddScoped<AuthService>();
        services.AddScoped<QueryEngine>();
        // MySaleBooks: company base currency / financial year, and deterministic reports (ledger statement, stock movement).
        services.AddScoped<CompanyContextService>();
        services.AddScoped<MySaleBooksReports>();
        services.AddScoped<QuerySandboxService>();
        services.AddScoped<SchemaMetadataService>();
        services.AddScoped<AttachmentService>();
        services.AddScoped<ModelCapabilityService>();
        services.AddScoped<VoiceService>();
        services.AddScoped<AIAgentOrchestrator>();

        // Activity tracking (audit trail of every AI request). The agent only gets the write-only tracker;
        // reading is limited to ActivityQueryService (developer/admin API).
        services.AddSingleton(BindActivityOptions(configuration));
        services.TryAddScoped<IRequestContext, NullRequestContext>();
        services.AddScoped<ActivityTracker>();
        services.AddScoped<ActivityQueryService>();

        // AIDashboard inside MySaleBooks: server-side permission check + tenant-scoped read models.
        services.AddSingleton(BindAIDashboardOptions(configuration));
        services.AddSingleton<AIDashboardPermissionCache>();
        services.AddScoped<AIDashboardAccessService>();
        services.AddScoped<AIDashboardService>();
        services.AddScoped<AIDashboardInsightsService>();

        // Store (MySaleBooks "Store Location") filtering of operational reports
        services.AddSingleton(BindStoreFilterOptions(configuration));
        services.TryAddScoped<IStoreSelection, NoStoreSelection>();
        services.AddScoped<IStoreAccessProvider, MySaleBooksStoreAccessProvider>();
        services.AddScoped<StoreContextResolver>();

        // Business calendar: financial year start, date order of typed dates, business date field overrides
        var calendar = configuration.GetSection(BusinessCalendarOptions.Section).Get<BusinessCalendarOptions>() ?? new BusinessCalendarOptions();
        calendar.FinancialYearStartMonth = Math.Clamp(calendar.FinancialYearStartMonth, 1, 12);
        calendar.BusinessDateFields = new Dictionary<string, string>(calendar.BusinessDateFields, StringComparer.OrdinalIgnoreCase);
        services.AddSingleton(calendar);

        // Business terminology: semantic dictionary (customer ↔ Sundry Debtors, vendor ↔ Sundry Creditors …)
        services.AddSingleton(BindBusinessTermOptions(configuration));
        return services;
    }

    /// <summary>"BusinessTerms" section; a configured GroupFields list replaces the default list.</summary>
    public static BusinessTermOptions BindBusinessTermOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(BusinessTermOptions.Section);
        var options = new BusinessTermOptions();
        if (bool.TryParse(section["VerifyGroupValues"], out var verify)) options.VerifyGroupValues = verify;
        if (bool.TryParse(section["RetryOnZeroResults"], out var retry)) options.RetryOnZeroResults = retry;
        var groupFields = section.GetSection("GroupFields").Get<List<string>>();
        if (groupFields is { Count: > 0 }) options.GroupFields = groupFields;
        var concepts = section.GetSection("Concepts").Get<Dictionary<string, BusinessConceptOptions>>();
        if (concepts is not null) options.Concepts = new Dictionary<string, BusinessConceptOptions>(concepts, StringComparer.OrdinalIgnoreCase);
        return options;
    }

    /// <summary>"StoreFilter" section; configured lists replace the defaults.</summary>
    public static StoreFilterOptions BindStoreFilterOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(StoreFilterOptions.Section);
        var options = section.Get<StoreFilterOptions>() ?? new StoreFilterOptions();
        List<string>? Configured(string key) => section.GetSection(key).Exists() ? section.GetSection(key).Get<List<string>>() ?? new() : null;
        var defaults = new StoreFilterOptions();
        options.StoreFields = Configured(nameof(StoreFilterOptions.StoreFields)) ?? defaults.StoreFields;
        options.ExemptCollections = Configured(nameof(StoreFilterOptions.ExemptCollections)) ?? defaults.ExemptCollections;
        options.AllStoresValues = Configured(nameof(StoreFilterOptions.AllStoresValues)) ?? defaults.AllStoresValues;
        options.SharedCollections = Configured(nameof(StoreFilterOptions.SharedCollections)) ?? defaults.SharedCollections;
        options.SharedStoreValues = Configured(nameof(StoreFilterOptions.SharedStoreValues)) ?? defaults.SharedStoreValues;
        options.CollectionFields = new Dictionary<string, string>(options.CollectionFields, StringComparer.OrdinalIgnoreCase);
        return options;
    }

    /// <summary>"AIDashboard" section (secrets such as PermissionsApiKey via environment: AIDashboard__PermissionsApiKey).</summary>
    public static AIDashboardOptions BindAIDashboardOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(AIDashboardOptions.Section);
        var options = section.Get<AIDashboardOptions>() ?? new AIDashboardOptions();
        // The binder appends configured list items to the defaults; configured lists replace them instead.
        List<string>? Configured(string key) => section.GetSection(key).Exists() ? section.GetSection(key).Get<List<string>>() ?? new() : null;
        options.UserIdClaims = Configured(nameof(AIDashboardOptions.UserIdClaims)) ?? new AIDashboardOptions().UserIdClaims;
        options.UserRoleIdClaims = Configured(nameof(AIDashboardOptions.UserRoleIdClaims)) ?? new AIDashboardOptions().UserRoleIdClaims;
        options.PermissionClaims = Configured(nameof(AIDashboardOptions.PermissionClaims)) ?? new AIDashboardOptions().PermissionClaims;
        options.AuthTimeClaims = Configured(nameof(AIDashboardOptions.AuthTimeClaims)) ?? new AIDashboardOptions().AuthTimeClaims;
        options.RecentAuthPermissions = Configured(nameof(AIDashboardOptions.RecentAuthPermissions)) ?? new AIDashboardOptions().RecentAuthPermissions;
        options.AdminRoleIds = Configured(nameof(AIDashboardOptions.AdminRoleIds)) ?? new List<string>();
        options.CacheSeconds = Math.Clamp(options.CacheSeconds, 0, 600);
        options.SessionIdleMinutes = Math.Clamp(options.SessionIdleMinutes, 1, 240);
        options.RecentAuthMinutes = Math.Max(0, options.RecentAuthMinutes);
        return options;
    }

    /// <summary>"Activity" section; the AI_ACTIVITY_RETENTION_DAYS environment variable overrides RetentionDays.</summary>
    public static ActivityOptions BindActivityOptions(IConfiguration configuration)
    {
        var options = configuration.GetSection(ActivityOptions.Section).Get<ActivityOptions>() ?? new ActivityOptions();
        if (int.TryParse(configuration["AI_ACTIVITY_RETENTION_DAYS"], out var days) && days > 0) options.RetentionDays = days;
        options.RetentionDays = Math.Max(1, options.RetentionDays);
        options.MaximumDocumentsToStore = Math.Clamp(options.MaximumDocumentsToStore, 0, 500);
        options.MaximumResultSize = Math.Clamp(options.MaximumResultSize, 1024, 1_000_000);
        options.MaximumTextLength = Math.Clamp(options.MaximumTextLength, 500, 200_000);
        return options;
    }

    /// <summary>MongoDB, AI providers, security and seeding.</summary>
    public static IServiceCollection AddAgentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SystemDbOptions>(configuration.GetSection(SystemDbOptions.Section));
        services.Configure<BusinessDbOptions>(configuration.GetSection(BusinessDbOptions.Section));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));

        // Secrets: Data Protection keys persisted to disk so encrypted API keys survive restarts.
        var keysPath = configuration["DataProtection:KeysPath"];
        var dp = services.AddDataProtection().SetApplicationName("mysale-ai-agent");
        if (!string.IsNullOrWhiteSpace(keysPath))
            dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<HmacTokenService>();
        services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<HmacTokenService>());
        // AIDashboard second layer: validates the existing AYAAN Dashboard session (same token service + user store).
        services.AddSingleton<AyaanDashboardSessionValidator>();
        // Existing MySaleBooks JWT (JwtSettings:*) → "dbName" claim → customer database.
        services.AddSingleton<ITenantDatabaseResolver, TenantDatabaseResolver>();

        // System DB
        services.AddHttpContextAccessor();
        services.AddSingleton<SystemDbContext>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IProviderRepository, ProviderRepository>();
        services.AddSingleton<IConversationRepository, ConversationRepository>();
        services.AddSingleton<IMessageRepository, MessageRepository>();
        services.AddSingleton<IConversationStateRepository, ConversationStateRepository>();
        services.AddSingleton<IQueryLogRepository, QueryLogRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IAuditLogRepository, AuditLogRepository>();
        services.AddSingleton<ISchemaMetadataRepository, SchemaMetadataRepository>();
        services.AddSingleton<DatabaseConfigStore>();

        // Business DB
        services.AddSingleton<BusinessDatabase>();
        services.AddSingleton<IBusinessDatabase>(sp => sp.GetRequiredService<BusinessDatabase>());
        services.AddSingleton<IQueryExecutor, MongoQueryExecutor>();
        services.AddSingleton<ISchemaService, SchemaService>();
        services.AddScoped<IBusinessDatabaseManager, BusinessDatabaseManager>();

        // AI providers — add a new provider by registering another IAIProviderBuilder.
        services.AddHttpClient("ai");
        services.AddSingleton<IAIProviderBuilder, OllamaProviderBuilder>();
        services.AddSingleton<IAIProviderBuilder, OpenAIProviderBuilder>();
        services.AddSingleton<IAIProviderBuilder, OpenAICompatibleProviderBuilder>();
        services.AddSingleton<IAIProviderFactory, AIProviderFactory>();
        services.Configure<AiProvidersOptions>(configuration.GetSection(AiProvidersOptions.Section));
        services.AddSingleton<IProviderOverrides, ConfigurationProviderOverrides>();

        // Voice & attachments — files live outside the web root, keyed by id; never executed or served inline.
        services.Configure<AttachmentStorageOptions>(configuration.GetSection(AttachmentStorageOptions.Section));
        services.AddSingleton<IAttachmentStore, FileSystemAttachmentStore>();
        services.AddSingleton<IAttachmentRepository, AttachmentRepository>();
        if (configuration.GetValue<bool>("Attachments:ClamAv:Enabled"))
            services.AddSingleton<IFileScanner, ClamAvFileScanner>();
        else
            services.AddSingleton<IFileScanner, NoOpFileScanner>();
        services.AddSingleton<IAttachmentProcessor, PdfProcessor>();
        services.AddSingleton<IAttachmentProcessor, TextFileProcessor>();
        services.AddSingleton<IAttachmentProcessor, CsvProcessor>();
        services.AddSingleton<IAttachmentProcessor, XlsxProcessor>();
        services.AddSingleton<ISpeechToTextProviderFactory, SpeechToTextProviderFactory>();
        services.AddHostedService<AttachmentCleanupService>();

        // Activity log storage (system DB) + background writer + retention
        services.AddSingleton<IAIActivityRepository, MongoActivityRepository>();
        services.AddSingleton<ActivityWriterService>();
        services.AddSingleton<IAIActivitySink>(sp => sp.GetRequiredService<ActivityWriterService>());
        services.AddHostedService(sp => sp.GetRequiredService<ActivityWriterService>());
        services.AddHostedService<ActivityRetentionService>();

        // AIDashboard permission sources (AIDashboard:PermissionSource picks one)
        services.AddHttpClient(MySaleBooksApiPermissionSource.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(65));
        services.AddSingleton<IAIDashboardPermissionSource, MySaleBooksApiPermissionSource>();
        services.AddSingleton<IAIDashboardPermissionSource, ClaimsPermissionSource>();
        services.AddSingleton<IAIDashboardPermissionSource, DisabledPermissionSource>();

        // Seeding
        services.AddSingleton<SampleDataSeeder>();
        services.AddScoped<SystemSeeder>();
        return services;
    }
}
