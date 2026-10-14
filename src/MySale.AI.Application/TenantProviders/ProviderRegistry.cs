namespace MySale.AI.Application.TenantProviders;

/// <summary>How a provider is called. Each adapter turns a tenant configuration into an <see cref="Abstractions.IAIProvider"/>.</summary>
public enum ProviderAdapterType
{
    /// <summary>OpenAI Chat Completions protocol (/chat/completions, /models) with a Bearer key.</summary>
    OpenAICompatible,
    /// <summary>Azure OpenAI v1 API ({endpoint}/openai/v1) with the "api-key" header; model = deployment name.</summary>
    AzureOpenAI,
    /// <summary>Anthropic OpenAI-compatible endpoint with x-api-key + anthropic-version headers.</summary>
    Anthropic,
    /// <summary>Amazon Bedrock Converse API with a Bedrock API key (Bearer).</summary>
    BedrockConverse
}

public enum ProviderFieldType { Text, Url, Select }

/// <summary>A provider-specific, NON-secret field shown in the editor (the API key is a separate write-only field).</summary>
public sealed record ProviderFieldDescriptor(
    string Key,
    string Label,
    ProviderFieldType Type = ProviderFieldType.Text,
    bool Required = false,
    string? Placeholder = null,
    string? Help = null,
    IReadOnlyList<string>? Options = null,
    string? DefaultValue = null,
    int MaxLength = 200);

/// <summary>One provider the company can connect. Adding a provider = adding one descriptor (and, rarely, an adapter).</summary>
public sealed class ProviderDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Short text for the logo badge (no external images are loaded).</summary>
    public required string LogoText { get; init; }
    /// <summary>Badge colour (hex).</summary>
    public string LogoColor { get; init; } = "#5569ff";
    public string Description { get; init; } = string.Empty;
    public ProviderAdapterType Adapter { get; init; } = ProviderAdapterType.OpenAICompatible;
    /// <summary>Endpoint used when the company does not enter one.</summary>
    public string DefaultBaseUrl { get; init; } = string.Empty;
    /// <summary>The company may change the endpoint (custom gateways, Azure resources, self-hosted Ollama …).</summary>
    public bool BaseUrlEditable { get; init; }
    /// <summary>The endpoint must be entered (no usable default).</summary>
    public bool BaseUrlRequired { get; init; }
    public string BaseUrlLabel { get; init; } = "API endpoint";
    public string? BaseUrlPlaceholder { get; init; }
    public bool KeyRequired { get; init; } = true;
    public string KeyLabel { get; init; } = "API key";
    public string? KeyHelp { get; init; }
    public IReadOnlyList<ProviderFieldDescriptor> Fields { get; init; } = Array.Empty<ProviderFieldDescriptor>();
    /// <summary>Well-known chat models (suggestions; live discovery is merged in when the provider supports listing).</summary>
    public IReadOnlyList<string> SuggestedModels { get; init; } = Array.Empty<string>();
    public bool SupportsModelListing { get; init; } = true;
    /// <summary>Model used by the connection test when the provider cannot list models and none was chosen.</summary>
    public string? TestModel { get; init; }
    /// <summary>Provider console page where the key is created (shown as a hint, opened by the user).</summary>
    public string? KeyConsoleUrl { get; init; }
    /// <summary>Kind recorded on chat messages / activity (keeps existing reports meaningful).</summary>
    public string Kind { get; init; } = "openai-compatible";
    /// <summary>Local / self-hosted runtime (private network endpoints only with TenantAiProviders:AllowPrivateEndpoints).</summary>
    public bool SelfHosted { get; init; }
}

/// <summary>Built-in provider catalogue. Unknown / future providers can use "openai-compatible".</summary>
public static class ProviderRegistry
{
    public static readonly IReadOnlyList<ProviderDescriptor> All = new List<ProviderDescriptor>
    {
        new()
        {
            Id = "openai", DisplayName = "OpenAI", LogoText = "OA", LogoColor = "#10a37f", Kind = "openai",
            Description = "GPT models through the OpenAI API.",
            DefaultBaseUrl = "https://api.openai.com/v1",
            KeyConsoleUrl = "https://platform.openai.com/api-keys",
            Fields = new[]
            {
                new ProviderFieldDescriptor("organization", "Organization ID", Placeholder: "org-…", Help: "Optional. Bills usage to this organization."),
                new ProviderFieldDescriptor("project", "Project ID", Placeholder: "proj_…", Help: "Optional. Bills usage to this project.")
            },
            SuggestedModels = new[] { "gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini", "gpt-4o", "o4-mini" }
        },
        new()
        {
            Id = "anthropic", DisplayName = "Anthropic", LogoText = "AN", LogoColor = "#d97757", Adapter = ProviderAdapterType.Anthropic,
            Description = "Claude models through the Anthropic API.",
            DefaultBaseUrl = "https://api.anthropic.com/v1",
            KeyConsoleUrl = "https://console.anthropic.com/settings/keys",
            SuggestedModels = new[] { "claude-sonnet-4-5", "claude-haiku-4-5", "claude-opus-4-1" }
        },
        new()
        {
            Id = "gemini", DisplayName = "Google Gemini", LogoText = "GE", LogoColor = "#4285f4",
            Description = "Gemini models through the Gemini API (OpenAI-compatible endpoint).",
            DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
            KeyConsoleUrl = "https://aistudio.google.com/apikey",
            SuggestedModels = new[] { "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash" }
        },
        new()
        {
            Id = "azure-openai", DisplayName = "Azure OpenAI", LogoText = "AZ", LogoColor = "#0078d4", Adapter = ProviderAdapterType.AzureOpenAI,
            Description = "OpenAI models deployed in your Azure subscription.",
            BaseUrlEditable = true, BaseUrlRequired = true, BaseUrlLabel = "Azure endpoint",
            BaseUrlPlaceholder = "https://your-resource.openai.azure.com",
            Fields = new[]
            {
                new ProviderFieldDescriptor("deployment", "Deployment name", Required: true, Placeholder: "gpt-4o-mini",
                    Help: "The deployment to use. It is sent as the model."),
                new ProviderFieldDescriptor("apiVersion", "API version", Placeholder: "v1",
                    Help: "Leave empty for the Azure OpenAI v1 API (recommended).")
            },
            SupportsModelListing = false
        },
        new()
        {
            Id = "mistral", DisplayName = "Mistral AI", LogoText = "MI", LogoColor = "#fa520f",
            DefaultBaseUrl = "https://api.mistral.ai/v1", KeyConsoleUrl = "https://console.mistral.ai/api-keys",
            SuggestedModels = new[] { "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest" }
        },
        new()
        {
            Id = "deepseek", DisplayName = "DeepSeek", LogoText = "DS", LogoColor = "#4d6bfe",
            DefaultBaseUrl = "https://api.deepseek.com/v1", KeyConsoleUrl = "https://platform.deepseek.com/api_keys",
            SuggestedModels = new[] { "deepseek-chat", "deepseek-reasoner" }
        },
        new()
        {
            Id = "xai", DisplayName = "xAI (Grok)", LogoText = "xA", LogoColor = "#111111",
            DefaultBaseUrl = "https://api.x.ai/v1", KeyConsoleUrl = "https://console.x.ai",
            SuggestedModels = new[] { "grok-4", "grok-3-mini", "grok-3" }
        },
        new()
        {
            Id = "cohere", DisplayName = "Cohere", LogoText = "CO", LogoColor = "#39594d",
            Description = "Command models through Cohere's OpenAI-compatible API.",
            DefaultBaseUrl = "https://api.cohere.ai/compatibility/v1", KeyConsoleUrl = "https://dashboard.cohere.com/api-keys",
            SuggestedModels = new[] { "command-a-03-2025", "command-r-plus", "command-r" },
            TestModel = "command-r"
        },
        new()
        {
            Id = "groq", DisplayName = "Groq", LogoText = "GQ", LogoColor = "#f55036",
            DefaultBaseUrl = "https://api.groq.com/openai/v1", KeyConsoleUrl = "https://console.groq.com/keys",
            SuggestedModels = new[] { "llama-3.3-70b-versatile", "openai/gpt-oss-120b", "llama-3.1-8b-instant" }
        },
        new()
        {
            Id = "together", DisplayName = "Together AI", LogoText = "TG", LogoColor = "#0f6fff",
            DefaultBaseUrl = "https://api.together.xyz/v1", KeyConsoleUrl = "https://api.together.ai/settings/api-keys",
            SuggestedModels = new[] { "meta-llama/Llama-3.3-70B-Instruct-Turbo", "Qwen/Qwen2.5-72B-Instruct-Turbo", "deepseek-ai/DeepSeek-V3" }
        },
        new()
        {
            Id = "fireworks", DisplayName = "Fireworks AI", LogoText = "FW", LogoColor = "#6720ff",
            DefaultBaseUrl = "https://api.fireworks.ai/inference/v1", KeyConsoleUrl = "https://fireworks.ai/account/api-keys",
            SuggestedModels = new[] { "accounts/fireworks/models/llama-v3p3-70b-instruct", "accounts/fireworks/models/deepseek-v3" }
        },
        new()
        {
            Id = "perplexity", DisplayName = "Perplexity", LogoText = "PX", LogoColor = "#20808d",
            DefaultBaseUrl = "https://api.perplexity.ai", KeyConsoleUrl = "https://www.perplexity.ai/settings/api",
            SuggestedModels = new[] { "sonar", "sonar-pro", "sonar-reasoning" },
            SupportsModelListing = false, TestModel = "sonar"
        },
        new()
        {
            Id = "bedrock", DisplayName = "Amazon Bedrock", LogoText = "AWS", LogoColor = "#ff9900", Adapter = ProviderAdapterType.BedrockConverse,
            Kind = "bedrock",
            Description = "Foundation models in your AWS account (Converse API, Bedrock API key).",
            KeyLabel = "Bedrock API key",
            KeyHelp = "Create an Amazon Bedrock API key in the AWS console (Bedrock → API keys).",
            Fields = new[]
            {
                new ProviderFieldDescriptor("region", "AWS region", ProviderFieldType.Select, Required: true, DefaultValue: "us-east-1",
                    Options: new[] { "us-east-1", "us-east-2", "us-west-2", "ca-central-1", "eu-central-1", "eu-west-1", "eu-west-2", "eu-west-3",
                        "eu-north-1", "ap-south-1", "ap-southeast-1", "ap-southeast-2", "ap-northeast-1", "ap-northeast-2", "me-central-1", "sa-east-1" })
            },
            SuggestedModels = new[] { "us.anthropic.claude-sonnet-4-5-20250929-v1:0", "us.amazon.nova-pro-v1:0", "us.meta.llama3-3-70b-instruct-v1:0" },
            KeyConsoleUrl = "https://console.aws.amazon.com/bedrock/home#/api-keys"
        },
        new()
        {
            Id = "cerebras", DisplayName = "Cerebras", LogoText = "CB", LogoColor = "#f15a29",
            DefaultBaseUrl = "https://api.cerebras.ai/v1", KeyConsoleUrl = "https://cloud.cerebras.ai",
            SuggestedModels = new[] { "llama-3.3-70b", "gpt-oss-120b", "qwen-3-32b" }
        },
        new()
        {
            Id = "sambanova", DisplayName = "SambaNova", LogoText = "SN", LogoColor = "#ee7624",
            DefaultBaseUrl = "https://api.sambanova.ai/v1", KeyConsoleUrl = "https://cloud.sambanova.ai/apis",
            SuggestedModels = new[] { "Meta-Llama-3.3-70B-Instruct", "DeepSeek-V3-0324", "Llama-4-Maverick-17B-128E-Instruct" }
        },
        new()
        {
            Id = "nvidia", DisplayName = "NVIDIA NIM", LogoText = "NV", LogoColor = "#76b900",
            DefaultBaseUrl = "https://integrate.api.nvidia.com/v1", KeyConsoleUrl = "https://build.nvidia.com",
            SuggestedModels = new[] { "meta/llama-3.3-70b-instruct", "deepseek-ai/deepseek-r1", "qwen/qwen2.5-coder-32b-instruct" }
        },
        new()
        {
            Id = "ollama", DisplayName = "Ollama", LogoText = "OL", LogoColor = "#3d3d3d", SelfHosted = true,
            Description = "Your own Ollama server (OpenAI-compatible /v1 endpoint). It must be reachable from the AYAAN server over HTTPS.",
            BaseUrlEditable = true, BaseUrlRequired = true, BaseUrlLabel = "Ollama URL", BaseUrlPlaceholder = "https://ollama.example.com",
            KeyRequired = false, KeyLabel = "API key (optional)", KeyHelp = "Only when your Ollama server is behind a gateway that requires a key.",
            SuggestedModels = new[] { "llama3.1", "qwen2.5", "mistral" }
        },
        new()
        {
            Id = "openai-compatible", DisplayName = "OpenAI-compatible API", LogoText = "API", LogoColor = "#6e759f",
            Description = "Any endpoint implementing /chat/completions (OpenRouter, LiteLLM, vLLM, LM Studio, new providers …).",
            BaseUrlEditable = true, BaseUrlRequired = true, BaseUrlPlaceholder = "https://gateway.example.com/v1",
            KeyRequired = false, KeyLabel = "API key"
        }
    };

    private static readonly Dictionary<string, ProviderDescriptor> ById =
        All.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public static ProviderDescriptor? Find(string? id)
        => !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id.Trim(), out var d) ? d : null;
}
