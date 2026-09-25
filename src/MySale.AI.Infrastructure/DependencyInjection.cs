using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
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
        return services;
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

        // Seeding
        services.AddSingleton<SampleDataSeeder>();
        services.AddScoped<SystemSeeder>();
        return services;
    }
}
