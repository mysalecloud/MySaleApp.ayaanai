using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MySale.AI.Application.Common;

public static class JsonHelpers
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToCompact(this JsonNode? node) => node is null ? "null" : node.ToJsonString(Compact);

    public static string ToIndented(this JsonNode? node) => node is null ? "null" : node.ToJsonString(Indented);

    public static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue<string>(out var s))
        {
            value = s;
            return true;
        }
        return false;
    }

    public static bool TryGetNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;
        if (v.TryGetValue<double>(out var d)) { value = d; return true; }
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<decimal>(out var m)) { value = (double)m; return true; }
        if (v.TryGetValue<float>(out var f)) { value = f; return true; }
        return double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public static bool IsBoolean(JsonNode? node, out bool value)
    {
        value = false;
        if (node is not JsonValue v) return false;
        var kind = v.GetValueKind();
        if (kind == JsonValueKind.True) { value = true; return true; }
        if (kind == JsonValueKind.False) { value = false; return true; }
        return false;
    }

    /// <summary>Truncates serialized JSON for prompts / logs.</summary>
    public static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + $"… (truncated, {text.Length - maxChars} more chars)";
}
