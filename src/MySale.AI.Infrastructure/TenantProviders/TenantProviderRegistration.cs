using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySale.AI.Application.TenantProviders;

namespace MySale.AI.Infrastructure.TenantProviders;

public static class TenantProviderRegistration
{
    /// <summary>
    /// AI Settings → AI Providers &amp; API Keys (company-owned provider credentials). Additive: registers new services only;
    /// <see cref="Application.Services.ProviderService"/> picks up <see cref="ITenantProviderResolver"/> through its optional
    /// constructor parameter.
    /// </summary>
    public static IServiceCollection AddTenantAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(BindOptions(configuration));
        services.AddSingleton<ITenantProviderRepository, MongoTenantProviderRepository>();
        services.AddSingleton<ITenantSecretProtector, DataProtectionTenantSecretProtector>();
        services.AddSingleton<TenantHttpClients>();
        services.AddSingleton<ITenantProviderAdapterFactory, TenantProviderAdapterFactory>();
        services.AddScoped<ITenantProviderResolver, TenantProviderResolver>();
        services.AddScoped<TenantProviderService>();
        return services;
    }

    /// <summary>"TenantAiProviders" section; configured lists replace the defaults.</summary>
    public static TenantAiProviderOptions BindOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(TenantAiProviderOptions.Section);
        var options = section.Get<TenantAiProviderOptions>() ?? new TenantAiProviderOptions();
        List<string>? Configured(string key) => section.GetSection(key).Exists() ? section.GetSection(key).Get<List<string>>() ?? new() : null;
        options.ManageRoles = (Configured(nameof(TenantAiProviderOptions.ManageRoles)) ?? new TenantAiProviderOptions().ManageRoles)
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        options.AllowedProviders = (Configured(nameof(TenantAiProviderOptions.AllowedProviders)) ?? new List<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
        options.TestTimeoutSeconds = Math.Clamp(options.TestTimeoutSeconds, 5, 120);
        options.MaxConfigurationsPerCompany = Math.Clamp(options.MaxConfigurationsPerCompany, 1, 200);
        return options;
    }
}
