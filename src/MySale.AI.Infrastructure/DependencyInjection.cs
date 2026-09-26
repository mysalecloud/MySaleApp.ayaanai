using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Infrastructure.ActivityTracking;
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
        return services;
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
        // Existing MySaleBooks JWT (JwtSettings:*) → "dbName" claim → customer database.
        services.AddSingleton<ITenantDatabaseResolver, TenantDatabaseResolver>();

        // System DB
        services.AddHttpContextAccessor();
        services.AddSingleton<SystemDbContext>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IProviderRepository, ProviderRepository>();
        services.AddSingleton<IConversationRepository, ConversationRepository>();
        services.AddSingleton<IMessageRepository, MessageRepository>();
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

        // Seeding
        services.AddSingleton<SampleDataSeeder>();
        services.AddScoped<SystemSeeder>();
        return services;
    }
}
