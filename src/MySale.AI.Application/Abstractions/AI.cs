using MySale.AI.Domain;

namespace MySale.AI.Application.Abstractions;

public sealed record AIChatMessage(string Role, string Content)
{
    /// <summary>Images for vision-capable models (sent only when the model supports vision).</summary>
    public IReadOnlyList<AIImage>? Images { get; init; }

    public static AIChatMessage System(string content) => new("system", content);
    public static AIChatMessage User(string content) => new("user", content);
    public static AIChatMessage Assistant(string content) => new("assistant", content);
}

public sealed class AIChatRequest
{
    public required IReadOnlyList<AIChatMessage> Messages { get; init; }
    public required string Model { get; init; }
    public double Temperature { get; init; } = 0.1;
    public int MaxTokens { get; init; } = 1024;
    /// <summary>Ask the provider to constrain output to a JSON object (Ollama "format":"json", OpenAI json_object).</summary>
    public bool JsonMode { get; init; }
}

public class AIResponse
{
    public string Text { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>Response of the query-generation step (raw text; parsing is done by the MQL engine).</summary>
public sealed class AIQueryResponse : AIResponse { }

public sealed class AIStreamChunk
{
    public string Text { get; init; } = string.Empty;
    public bool Done { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

public sealed class ProviderTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long LatencyMs { get; init; }
    public string? Version { get; init; }
    public int? ModelCount { get; init; }
}

public sealed class AIModel
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long? SizeBytes { get; init; }
    public string? Family { get; init; }
    public string? ParameterSize { get; init; }
    public string? OwnedBy { get; init; }
    public DateTime? ModifiedAt { get; init; }
    public string? Description { get; init; }
    /// <summary>Suggested for NL → MQL in this prototype.</summary>
    public bool Recommended { get; init; }
    /// <summary>True when the provider reports the model (installed / accessible). False = catalog suggestion only.</summary>
    public bool Available { get; init; } = true;
    /// <summary>"live" (reported by the provider) or "catalog".</summary>
    public string Source { get; init; } = "live";
}

/// <summary>Optional capability: providers that can download models on demand (Ollama).</summary>
public interface IModelInstaller
{
    Task<string> PullModelAsync(string model, CancellationToken ct);
}

/// <summary>
/// The single abstraction every AI backend implements. The rest of the application depends only on this.
/// </summary>
public interface IAIProvider
{
    string Kind { get; }
    Task<AIQueryResponse> GenerateQueryAsync(AIChatRequest request, CancellationToken ct);
    Task<AIResponse> GenerateResponseAsync(AIChatRequest request, CancellationToken ct);
    IAsyncEnumerable<AIStreamChunk> StreamResponseAsync(AIChatRequest request, CancellationToken ct);
    Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct);
    Task<IReadOnlyList<AIModel>> GetModelsAsync(CancellationToken ct);
}

public sealed class ProviderKindInfo
{
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    public ProviderCategory Category { get; init; }
    public string DefaultBaseUrl { get; init; } = string.Empty;
    public bool RequiresApiKey { get; init; }
    public bool SupportsModelListing { get; init; } = true;
    public string Description { get; init; } = string.Empty;
}

/// <summary>Creates a provider instance for one kind. Register one per implementation.</summary>
public interface IAIProviderBuilder
{
    ProviderKindInfo Info { get; }
    IAIProvider Build(ProviderConfig config, string? apiKey);
}

public interface IAIProviderFactory
{
    IReadOnlyList<ProviderKindInfo> Kinds { get; }
    bool Supports(string kind);
    IAIProvider Create(ProviderConfig config);
}

/// <summary>Raised by providers for connectivity / HTTP / protocol failures. Message must be user-safe.</summary>
public sealed class AIProviderException : Exception
{
    public AIProviderException(string message, Exception? inner = null) : base(message, inner) { }
}
