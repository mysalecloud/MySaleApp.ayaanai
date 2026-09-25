using MySale.AI.Application.Abstractions;

namespace MySale.AI.Application.Services;

public sealed record CatalogModel(string Id, string Name, string Description, string? ParameterSize = null, bool Recommended = false);

/// <summary>
/// Well-known models per provider, shown next to the models reported live by the provider.
/// Live models always win; catalog entries let testers pick (or, for Ollama, pull) models that aren't installed yet.
/// Edit this list to change the suggestions — nothing else depends on it.
/// </summary>
public static class ModelCatalog
{
    private static readonly List<CatalogModel> Ollama = new()
    {
        new("llama3.1", "Llama 3.1 8B", "Meta. Good general default for NL → MQL on a laptop GPU.", "8B", true),
        new("qwen2.5", "Qwen 2.5 7B", "Alibaba. Strong at structured JSON output; very good for MQL.", "7B", true),
        new("qwen2.5:14b", "Qwen 2.5 14B", "Better MQL accuracy than 7B; needs ~10 GB VRAM.", "14B", true),
        new("qwen2.5-coder", "Qwen 2.5 Coder 7B", "Code-tuned; reliable query syntax.", "7B"),
        new("qwen3", "Qwen 3 8B", "Newer Qwen generation, good reasoning and JSON.", "8B", true),
        new("qwen3:14b", "Qwen 3 14B", "Larger Qwen 3.", "14B"),
        new("llama3.2", "Llama 3.2 3B", "Small and fast; weaker on complex aggregations.", "3B"),
        new("llama3.3", "Llama 3.3 70B", "High accuracy; needs a large GPU / lots of RAM.", "70B"),
        new("mistral", "Mistral 7B", "Fast general model.", "7B"),
        new("mistral-nemo", "Mistral Nemo 12B", "128k context, good multilingual support.", "12B"),
        new("gemma3", "Gemma 3 4B", "Google. Small, fast.", "4B"),
        new("gemma3:12b", "Gemma 3 12B", "Google. Good quality for its size.", "12B"),
        new("gemma2", "Gemma 2 9B", "Google, previous generation.", "9B"),
        new("phi4", "Phi-4 14B", "Microsoft. Strong reasoning for its size.", "14B"),
        new("deepseek-r1", "DeepSeek-R1 8B", "DeepSeek reasoning model. Thinks before answering (slower); reasoning is stripped automatically.", "8B", true),
        new("deepseek-r1:14b", "DeepSeek-R1 14B", "Better accuracy than 8B; needs ~10 GB VRAM.", "14B"),
        new("deepseek-r1:32b", "DeepSeek-R1 32B", "High accuracy; needs a large GPU.", "32B"),
        new("deepseek-coder-v2", "DeepSeek-Coder-V2 16B", "Code-tuned MoE model; good query syntax.", "16B")
    };

    private static readonly List<CatalogModel> DeepSeek = new()
    {
        new("deepseek-chat", "DeepSeek-V3 (chat)", "DeepSeek API. Low cost, supports JSON mode — good default.", null, true),
        new("deepseek-reasoner", "DeepSeek-R1 (reasoner)", "DeepSeek API reasoning model. Slower; no JSON mode (prompt-enforced).", null, true)
    };

    private static readonly List<CatalogModel> OpenAI = new()
    {
        new("gpt-4o-mini", "GPT-4o mini", "Cheap and fast. Good default for testing.", null, true),
        new("gpt-4.1-mini", "GPT-4.1 mini", "Better instruction following than 4o-mini, still cheap.", null, true),
        new("gpt-4.1", "GPT-4.1", "High accuracy for complex queries.", null, true),
        new("gpt-4.1-nano", "GPT-4.1 nano", "Lowest cost / latency."),
        new("gpt-4o", "GPT-4o", "Previous flagship."),
        new("gpt-5-mini", "GPT-5 mini", "Reasoning model; slower, uses more output tokens."),
        new("gpt-5", "GPT-5", "Reasoning flagship."),
        new("o4-mini", "o4-mini", "Reasoning model.")
    };

    private static readonly List<CatalogModel> Gemini = new()
    {
        new("gemini-2.5-flash", "Gemini 2.5 Flash", "Fast, low cost, good JSON output.", null, true),
        new("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite", "Lowest cost."),
        new("gemini-2.5-pro", "Gemini 2.5 Pro", "Highest accuracy.", null, true),
        new("gemini-2.0-flash", "Gemini 2.0 Flash", "Previous generation.")
    };

    private static readonly List<CatalogModel> Anthropic = new()
    {
        new("claude-sonnet-4-5", "Claude Sonnet 4.5", "Strong reasoning and structured output.", null, true),
        new("claude-haiku-4-5", "Claude Haiku 4.5", "Fast and low cost.", null, true),
        new("claude-opus-4-1", "Claude Opus 4.1", "Highest accuracy, highest cost.")
    };

    public static IReadOnlyList<CatalogModel> For(string kind, string? baseUrl)
    {
        var url = (baseUrl ?? string.Empty).ToLowerInvariant();
        return kind switch
        {
            "ollama" => Ollama,
            "openai" => OpenAI,
            "openai-compatible" when url.Contains("generativelanguage.googleapis.com") => Gemini,
            "openai-compatible" when url.Contains("anthropic.com") => Anthropic,
            "openai-compatible" when url.Contains("deepseek.com") => DeepSeek,
            "openai-compatible" when url.Contains("openai.com") => OpenAI,
            "openai-compatible" when url.Contains(":11434") => Ollama,
            _ => Array.Empty<CatalogModel>()
        };
    }

    /// <summary>Live models (available) first, then catalog entries not reported by the provider.</summary>
    public static List<AIModel> Merge(string kind, string? baseUrl, IReadOnlyList<AIModel>? live)
    {
        var catalog = For(kind, baseUrl);
        var result = new List<AIModel>();
        var liveList = live ?? Array.Empty<AIModel>();

        foreach (var m in liveList)
        {
            var info = catalog.FirstOrDefault(c => Matches(m.Id, c.Id));
            result.Add(new AIModel
            {
                Id = m.Id,
                Name = info?.Name ?? m.Name,
                SizeBytes = m.SizeBytes,
                Family = m.Family,
                ParameterSize = m.ParameterSize ?? info?.ParameterSize,
                OwnedBy = m.OwnedBy,
                ModifiedAt = m.ModifiedAt,
                Description = info?.Description,
                Recommended = info?.Recommended ?? false,
                Available = true,
                Source = "live"
            });
        }

        foreach (var c in catalog)
        {
            if (liveList.Any(m => Matches(m.Id, c.Id))) continue;
            result.Add(new AIModel
            {
                Id = c.Id,
                Name = c.Name,
                ParameterSize = c.ParameterSize,
                Description = c.Description,
                Recommended = c.Recommended,
                Available = false,
                Source = "catalog"
            });
        }

        return result
            .OrderByDescending(m => m.Available)
            .ThenByDescending(m => m.Recommended)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"llama3.1:latest" matches "llama3.1"; "models/gemini-2.5-flash" matches "gemini-2.5-flash".</summary>
    public static bool Matches(string installed, string catalogId)
    {
        var a = installed.StartsWith("models/", StringComparison.Ordinal) ? installed[7..] : installed;
        return string.Equals(a, catalogId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(a, catalogId + ":latest", StringComparison.OrdinalIgnoreCase);
    }
}
