using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Infrastructure.AI;

namespace MySale.AI.Tests;

public class ThinkTagFilterTests
{
    [Fact]
    public void Strip_removes_reasoning_block()
        => Assert.Equal("Total is AED 5.", ThinkTagFilter.Strip("<think>Let me sum {x} and {y}…</think>\nTotal is AED 5."));

    [Fact]
    public void Streaming_hides_reasoning_split_across_chunks()
    {
        var f = new ThinkTagFilter();
        var output = string.Concat(new[] { "<thi", "nk>reasoning {", "} more</th", "ink>Your ", "sales are 5." }.Select(f.Push)) + f.Flush();
        Assert.Equal("Your sales are 5.", output);
    }

    [Fact]
    public void Parser_ignores_braces_inside_think_block()
    {
        var r = MqlParser.Parse("<think>maybe {\"collection\":\"Users\"}?</think>{\"operation\":\"count\",\"collection\":\"Sales\"}");
        Assert.True(r.Success, r.Error);
        Assert.Equal("Sales", r.Query!.Collection);
    }

    [Fact]
    public void DeepSeek_catalog_is_available()
    {
        Assert.Contains(ModelCatalog.Merge("openai-compatible", "https://api.deepseek.com/v1", null), m => m.Id == "deepseek-chat");
        Assert.Contains(ModelCatalog.Merge("ollama", null, null), m => m.Id == "deepseek-r1");
    }
}
