using System.Text.Json;
using System.Text.Json.Nodes;

namespace MySale.AI.Application.Mql;

/// <summary>Structured query produced by the AI (untrusted until validated).</summary>
public sealed class MqlQuery
{
    public string Type { get; set; } = "query";          // query | unsupported | clarify | report
    public string Operation { get; set; } = "aggregate"; // find | aggregate | count | distinct
    public string Collection { get; set; } = string.Empty;
    public JsonArray? Pipeline { get; set; }
    public JsonObject? Filter { get; set; }
    public JsonObject? Projection { get; set; }
    public JsonObject? Sort { get; set; }
    public int? Limit { get; set; }
    public string? Field { get; set; }
    public string? Explanation { get; set; }
    public string? Visualization { get; set; }
    public string? Reason { get; set; }
    /// <summary>Arguments of a server report plan ({"type":"report","report":"ledgerStatement",…}).</summary>
    public JsonObject? Arguments { get; set; }

    public bool IsUnsupported => string.Equals(Type, "unsupported", StringComparison.OrdinalIgnoreCase);
    /// <summary>The model could not map a business term and asks the user a short question (text in <see cref="Reason"/>).</summary>
    public bool IsClarification => string.Equals(Type, "clarify", StringComparison.OrdinalIgnoreCase);
    /// <summary>A deterministic server report (ledger statement, stock movement) instead of a query.</summary>
    public bool IsReport => string.Equals(Type, "report", StringComparison.OrdinalIgnoreCase);
    /// <summary>Not a database query to validate/execute (unsupported, clarification or server report).</summary>
    public bool IsNonQuery => IsUnsupported || IsClarification || IsReport;

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["type"] = Type,
            ["operation"] = Operation,
            ["collection"] = Collection
        };
        if (Pipeline is not null) o["pipeline"] = Pipeline.DeepClone();
        if (Filter is not null) o["filter"] = Filter.DeepClone();
        if (Projection is not null) o["projection"] = Projection.DeepClone();
        if (Sort is not null) o["sort"] = Sort.DeepClone();
        if (Limit is not null) o["limit"] = Limit;
        if (Field is not null) o["field"] = Field;
        if (Explanation is not null) o["explanation"] = Explanation;
        if (Visualization is not null) o["visualization"] = Visualization;
        if (Reason is not null) o["reason"] = Reason;
        if (Arguments is not null) o["arguments"] = Arguments.DeepClone();
        return o;
    }
}

public sealed class MqlParseResult
{
    public bool Success { get; init; }
    public MqlQuery? Query { get; init; }
    public string? Error { get; init; }
    public JsonObject? Json { get; init; }

    public static MqlParseResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Extracts the query object from free-form model output. Tolerates markdown fences, leading chatter,
/// a wrapping {"query": {...}} object and case differences in property names.
/// </summary>
public static class MqlParser
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64
    };

    public static MqlParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return MqlParseResult.Fail("The model returned an empty response.");

        // Reasoning models (DeepSeek-R1, Qwen3) may prepend <think>…</think>; braces in there are not the query.
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"<think>[\s\S]*?</think>", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var json = ExtractJsonObject(raw);
        if (json is null)
            return MqlParseResult.Fail("No JSON object found in the model output.");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: DocOptions);
        }
        catch (JsonException ex)
        {
            return MqlParseResult.Fail($"Invalid JSON: {ex.Message}");
        }

        if (node is not JsonObject root)
            return MqlParseResult.Fail("The model output is not a JSON object.");

        // Some models wrap the query: {"query": {...}} or {"mql": {...}}
        if (Get(root, "collection") is null)
        {
            foreach (var wrapper in new[] { "query", "mql", "result" })
            {
                if (Get(root, wrapper) is JsonObject inner && Get(inner, "collection") is not null)
                {
                    foreach (var extra in new[] { "type", "explanation", "visualization" })
                        if (Get(inner, extra) is null && Get(root, extra) is JsonNode v)
                            inner[extra] = v.DeepClone();
                    root = (JsonObject)inner.DeepClone();
                    break;
                }
            }
        }

        var q = new MqlQuery
        {
            Type = GetString(root, "type") ?? "query",
            Collection = GetString(root, "collection") ?? string.Empty,
            Explanation = GetString(root, "explanation"),
            Visualization = GetString(root, "visualization")?.ToLowerInvariant(),
            Reason = GetString(root, "reason") ?? GetString(root, "question"),
            Field = GetString(root, "field"),
            Pipeline = Get(root, "pipeline") as JsonArray,
            Filter = Get(root, "filter") as JsonObject ?? Get(root, "query") as JsonObject,
            Projection = Get(root, "projection") as JsonObject,
            Sort = Get(root, "sort") as JsonObject,
            Limit = GetInt(root, "limit")
        };

        if (q.IsReport)
        {
            q.Arguments = (JsonObject)root.DeepClone();
            return new MqlParseResult { Success = true, Query = q, Json = root };
        }
        if (q.IsUnsupported || q.IsClarification)
            return new MqlParseResult { Success = true, Query = q, Json = root };

        q.Type = "query";
        var op = GetString(root, "operation");
        q.Operation = NormalizeOperation(op, q);

        if (string.IsNullOrWhiteSpace(q.Collection))
            return MqlParseResult.Fail("The query does not specify a collection.");

        // Detach nodes from the parsed document so they can be re-parented later.
        q.Pipeline = q.Pipeline?.DeepClone() as JsonArray;
        q.Filter = q.Filter?.DeepClone() as JsonObject;
        q.Projection = q.Projection?.DeepClone() as JsonObject;
        q.Sort = q.Sort?.DeepClone() as JsonObject;

        return new MqlParseResult { Success = true, Query = q, Json = root };
    }

    private static string NormalizeOperation(string? op, MqlQuery q)
    {
        var o = (op ?? string.Empty).Trim().ToLowerInvariant();
        return o switch
        {
            "aggregate" or "aggregation" => "aggregate",
            "find" or "findone" or "select" => "find",
            "count" or "countdocuments" or "estimateddocumentcount" => "count",
            "distinct" => "distinct",
            "" when q.Pipeline is not null => "aggregate",
            "" when q.Field is not null => "distinct",
            "" => "find",
            _ => o // unknown / write operations are rejected by the validator with a clear message
        };
    }

    /// <summary>Returns the first balanced {...} block, ignoring braces inside strings.</summary>
    internal static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return text.Substring(start, i - start + 1);
                }
            }
            // Unbalanced from this start; try the next '{'
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }

    private static JsonNode? Get(JsonObject o, string name)
    {
        if (o.TryGetPropertyValue(name, out var v)) return v;
        foreach (var kv in o)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    private static string? GetString(JsonObject o, string name)
    {
        var n = Get(o, name);
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static int? GetInt(JsonObject o, string name)
    {
        var n = Get(o, name);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        }
        return null;
    }
}
