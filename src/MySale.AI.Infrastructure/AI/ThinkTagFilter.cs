using System.Text;
using System.Text.RegularExpressions;

namespace MySale.AI.Infrastructure.AI;

/// <summary>
/// Removes reasoning blocks (&lt;think&gt;…&lt;/think&gt;) emitted by reasoning models such as DeepSeek-R1 / Qwen3,
/// so only the final answer reaches the MQL parser and the user. Works on whole texts and on streamed chunks.
/// </summary>
public sealed class ThinkTagFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";
    private static readonly Regex Block = new(@"<think>[\s\S]*?(</think>|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly StringBuilder _pending = new();
    private bool _inside;

    public static string Strip(string text) => string.IsNullOrEmpty(text) ? text : Block.Replace(text, string.Empty).TrimStart();

    /// <summary>Feeds a streamed chunk; returns the visible part (may be empty while inside a think block).</summary>
    public string Push(string chunk)
    {
        _pending.Append(chunk);
        var output = new StringBuilder();
        while (_pending.Length > 0)
        {
            var buffer = _pending.ToString();
            var tag = _inside ? Close : Open;
            var idx = buffer.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                if (!_inside) output.Append(buffer, 0, idx);
                _pending.Remove(0, idx + tag.Length);
                _inside = !_inside;
                continue;
            }
            // Keep a possible partial tag at the end for the next chunk.
            var keep = PartialTagSuffix(buffer, tag);
            if (!_inside) output.Append(buffer, 0, buffer.Length - keep);
            _pending.Remove(0, buffer.Length - keep);
            break;
        }
        return output.ToString();
    }

    /// <summary>Returns whatever is left when the stream ends (never reasoning text).</summary>
    public string Flush()
    {
        var rest = _inside ? string.Empty : _pending.ToString();
        _pending.Clear();
        return rest;
    }

    private static int PartialTagSuffix(string buffer, string tag)
    {
        for (int len = Math.Min(tag.Length - 1, buffer.Length); len > 0; len--)
            if (tag.StartsWith(buffer[^len..], StringComparison.OrdinalIgnoreCase)) return len;
        return 0;
    }
}
