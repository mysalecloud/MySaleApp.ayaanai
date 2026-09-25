using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;

namespace MySale.AI.Infrastructure.AI;

public sealed class OllamaProviderBuilder : IAIProviderBuilder
{
    private readonly IHttpClientFactory _http;
    public OllamaProviderBuilder(IHttpClientFactory http) => _http = http;

    public ProviderKindInfo Info { get; } = new()
    {
        Kind = "ollama",
        DisplayName = "Ollama",
        Category = ProviderCategory.Local,
        DefaultBaseUrl = "http://localhost:11434",
        RequiresApiKey = false,
        Description = "Local models served by Ollama (llama3.1, qwen2.5, mistral, gemma…). No API cost."
    };

    public IAIProvider Build(ProviderConfig config, string? apiKey) => new OllamaProvider(_http.CreateClient("ai"), config);
}

public sealed class OpenAIProviderBuilder : IAIProviderBuilder
{
    private readonly IHttpClientFactory _http;
    public OpenAIProviderBuilder(IHttpClientFactory http) => _http = http;

    public ProviderKindInfo Info { get; } = new()
    {
        Kind = "openai",
        DisplayName = "OpenAI",
        Category = ProviderCategory.Cloud,
        DefaultBaseUrl = "https://api.openai.com/v1",
        RequiresApiKey = true,
        Description = "OpenAI Chat Completions API (gpt-4o-mini, gpt-4.1, …)."
    };

    public IAIProvider Build(ProviderConfig config, string? apiKey) => new OpenAIProvider(_http.CreateClient("ai"), config, apiKey, Info.Kind);
}

public sealed class OpenAICompatibleProviderBuilder : IAIProviderBuilder
{
    private readonly IHttpClientFactory _http;
    public OpenAICompatibleProviderBuilder(IHttpClientFactory http) => _http = http;

    public ProviderKindInfo Info { get; } = new()
    {
        Kind = "openai-compatible",
        DisplayName = "OpenAI-compatible API",
        Category = ProviderCategory.Cloud,
        DefaultBaseUrl = "https://",
        RequiresApiKey = false,
        Description = "Any /chat/completions endpoint: Google Gemini, Anthropic Claude, Azure/OpenRouter gateways, LM Studio, vLLM."
    };

    public IAIProvider Build(ProviderConfig config, string? apiKey) => new OpenAIProvider(_http.CreateClient("ai"), config, apiKey, Info.Kind);
}

/// <summary>Resolves the implementation for a provider configuration. New providers only need a new builder.</summary>
public sealed class AIProviderFactory : IAIProviderFactory
{
    private readonly Dictionary<string, IAIProviderBuilder> _builders;
    private readonly ISecretProtector _secrets;

    public AIProviderFactory(IEnumerable<IAIProviderBuilder> builders, ISecretProtector secrets)
    {
        _builders = builders.ToDictionary(b => b.Info.Kind, StringComparer.OrdinalIgnoreCase);
        _secrets = secrets;
        Kinds = _builders.Values.Select(b => b.Info).ToList();
    }

    public IReadOnlyList<ProviderKindInfo> Kinds { get; }

    public bool Supports(string kind) => _builders.ContainsKey(kind);

    public IAIProvider Create(ProviderConfig config)
    {
        if (!_builders.TryGetValue(config.Kind, out var builder))
            throw new AIProviderException($"Provider type '{config.Kind}' is not supported.");
        return builder.Build(config, _secrets.Unprotect(config.ApiKeyEncrypted));
    }
}
