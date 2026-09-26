using Microsoft.Extensions.Options;
using MySale.AI.Application.Abstractions;

namespace MySale.AI.Infrastructure.AI;

/// <summary>
/// "AiProviders" configuration section. Environment-variable form (Cloud Run):
///   AiProviders__Default=OpenAI
///   AiProviders__DefaultModel=gpt-4o-mini
///   AiProviders__Overrides__OpenAI__ApiKey=sk-...          (use Secret Manager)
///   AiProviders__Overrides__Ollama__BaseUrl=https://my-ollama.example.com
///   AiProviders__Overrides__DeepSeek__Model=deepseek-chat
/// Provider names match the names shown on the AI Providers page (case-insensitive).
/// </summary>
public sealed class AiProvidersOptions
{
    public const string Section = "AiProviders";
    public string? Default { get; set; }
    public string? DefaultModel { get; set; }
    public Dictionary<string, AiProviderOverride> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AiProviderOverride
{
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
}

public sealed class ConfigurationProviderOverrides : IProviderOverrides
{
    private readonly AiProvidersOptions _options;
    private readonly Dictionary<string, AiProviderOverride> _byName;

    public ConfigurationProviderOverrides(IOptions<AiProvidersOptions> options)
    {
        _options = options.Value;
        // Env var keys cannot contain spaces: "Google_Gemini" also matches "Google Gemini".
        _byName = new Dictionary<string, AiProviderOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in _options.Overrides)
            _byName[Normalize(name)] = value;
    }

    public string? DefaultProviderName => _options.Default;
    public string? DefaultModel => _options.DefaultModel;
    public string? ApiKeyFor(string providerName) => Find(providerName)?.ApiKey;
    public string? BaseUrlFor(string providerName) => Find(providerName)?.BaseUrl;
    public string? ModelFor(string providerName) => Find(providerName)?.Model;

    private AiProviderOverride? Find(string name) => _byName.GetValueOrDefault(Normalize(name));
    private static string Normalize(string name) => name.Replace('_', ' ').Trim();
}
