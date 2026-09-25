using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Services;

namespace MySale.AI.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void Live_models_are_marked_available_and_catalog_fills_the_rest()
    {
        var live = new List<AIModel> { new() { Id = "llama3.1:latest", Name = "llama3.1:latest" }, new() { Id = "my-custom:7b", Name = "my-custom:7b" } };
        var merged = ModelCatalog.Merge("ollama", "http://localhost:11434", live);

        var llama = Assert.Single(merged, m => m.Id == "llama3.1:latest");
        Assert.True(llama.Available);
        Assert.True(llama.Recommended);
        Assert.DoesNotContain(merged, m => m.Id == "llama3.1"); // not duplicated as a suggestion
        Assert.Contains(merged, m => m.Id == "my-custom:7b" && m.Available);
        Assert.Contains(merged, m => m.Id == "qwen2.5" && !m.Available);
        Assert.True(merged.TakeWhile(m => m.Available).Count() == 2); // available first
    }

    [Theory]
    [InlineData("openai-compatible", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash")]
    [InlineData("openai-compatible", "https://api.anthropic.com/v1", "claude-sonnet-4-5")]
    [InlineData("openai", "https://api.openai.com/v1", "gpt-4o-mini")]
    public void Catalog_is_chosen_by_kind_and_url(string kind, string url, string expected)
        => Assert.Contains(ModelCatalog.Merge(kind, url, null), m => m.Id == expected);

    [Fact]
    public void Unknown_compatible_endpoint_has_no_suggestions()
        => Assert.Empty(ModelCatalog.Merge("openai-compatible", "http://localhost:1234/v1", null));
}
