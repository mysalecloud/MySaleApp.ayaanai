using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;

namespace MySale.AI.Infrastructure.AI;

/// <summary>
/// OpenAI Chat Completions API. Also used for kind "openai-compatible" (Gemini, Anthropic, Azure-style gateways,
/// LM Studio, vLLM, …) — anything that implements /chat/completions and /models.
/// </summary>
public sealed class OpenAIProvider : IAIProvider
{
    private static readonly string[] NonChatModelMarkers =
        { "embedding", "whisper", "tts", "dall-e", "moderation", "davinci", "babbage", "audio", "realtime", "transcribe", "image", "search" };

    private readonly HttpClient _http;
    private readonly ProviderConfig _config;
    private readonly string _baseUrl;
    private readonly bool _isOpenAI;

    public OpenAIProvider(HttpClient http, ProviderConfig config, string? apiKey, string kind)
    {
        _http = http;
        _config = config;
        _baseUrl = config.BaseUrl.TrimEnd('/');
        Kind = kind;
        _isOpenAI = kind == "openai";
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 600));
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public string Kind { get; }

    public async Task<AIQueryResponse> GenerateQueryAsync(AIChatRequest request, CancellationToken ct)
    {
        var r = await CompleteAsync(request, ct);
        return new AIQueryResponse { Text = r.Text, Model = r.Model, InputTokens = r.InputTokens, OutputTokens = r.OutputTokens, DurationMs = r.DurationMs };
    }

    public Task<AIResponse> GenerateResponseAsync(AIChatRequest request, CancellationToken ct) => CompleteAsync(request, ct);

    private async Task<AIResponse> CompleteAsync(AIChatRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var response = await SendAsync(BuildBody(request, stream: false), HttpCompletionOption.ResponseContentRead, ct);
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (JsonException ex)
        {
            throw new AIProviderException($"{_config.Name} returned an invalid response.", ex);
        }
        sw.Stop();
        return new AIResponse
        {
            Text = ThinkTagFilter.Strip(ExtractContent(json?["choices"]?[0]?["message"]?["content"])),
            Model = json?["model"]?.GetValue<string>() ?? request.Model,
            InputTokens = GetInt(json?["usage"], "prompt_tokens"),
            OutputTokens = GetInt(json?["usage"], "completion_tokens"),
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    public async IAsyncEnumerable<AIStreamChunk> StreamResponseAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendAsync(BuildBody(request, stream: true), HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var think = new ThinkTagFilter();

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (IOException ex)
            {
                throw new AIProviderException($"The connection to {_config.Name} was interrupted.", ex);
            }
            if (line is null) yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload == "[DONE]")
            {
                yield return new AIStreamChunk { Text = think.Flush(), Done = true };
                yield break;
            }

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(payload); }
            catch (JsonException) { continue; }

            if (chunk?["error"] is JsonNode err)
                throw new AIProviderException($"{_config.Name} error: {(err is JsonObject eo ? eo["message"]?.ToString() ?? eo.ToJsonString() : err.ToString())}");

            var text = think.Push(ExtractContent(chunk?["choices"]?[0]?["delta"]?["content"]));
            var usage = chunk?["usage"];
            yield return new AIStreamChunk
            {
                Text = text,
                InputTokens = usage is JsonObject ? GetInt(usage, "prompt_tokens") : null,
                OutputTokens = usage is JsonObject ? GetInt(usage, "completion_tokens") : null
            };
        }
    }

    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var models = await GetModelsAsync(ct);
            sw.Stop();
            return new ProviderTestResult
            {
                Success = true,
                LatencyMs = sw.ElapsedMilliseconds,
                ModelCount = models.Count,
                Message = $"Connected to {_config.Name}. {models.Count} model(s) available."
            };
        }
        catch (AIProviderException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            // Some compatible endpoints don't implement /models: fall back to a 1-token completion.
            try
            {
                await CompleteAsync(new AIChatRequest
                {
                    Messages = new[] { AIChatMessage.User("ping") },
                    Model = _config.DefaultModel,
                    MaxTokens = 5
                }, ct);
                return new ProviderTestResult { Success = true, LatencyMs = sw.ElapsedMilliseconds, Message = $"Connected to {_config.Name} (model listing not supported)." };
            }
            catch (AIProviderException inner)
            {
                return new ProviderTestResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Message = inner.Message };
            }
        }
        catch (AIProviderException ex)
        {
            return new ProviderTestResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Message = ex.Message };
        }
    }

    public async Task<IReadOnlyList<AIModel>> GetModelsAsync(CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"{_baseUrl}/models", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AIProviderException($"Cannot reach {_config.Name} at {_baseUrl}.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AIProviderException($"{_config.Name} did not respond in time.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new AIProviderException(await DescribeError(response, ct));

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            var list = new List<AIModel>();
            if (json?["data"] is JsonArray data)
            {
                foreach (var m in data)
                {
                    var id = m?["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id)) continue;
                    if (_isOpenAI && NonChatModelMarkers.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                    var name = id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id; // Gemini ids
                    DateTime? created = m?["created"] is JsonValue cv && cv.TryGetValue<long>(out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                        : null;
                    list.Add(new AIModel
                    {
                        Id = name,
                        Name = m?["display_name"]?.GetValue<string>() ?? name,
                        OwnedBy = m?["owned_by"]?.GetValue<string>(),
                        ModifiedAt = created
                    });
                }
            }
            return list.OrderBy(m => m.Id).ToList();
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>o-series and GPT-5 reasoning models reject max_tokens and custom temperature.</summary>
    private static bool IsReasoningModel(string model)
    {
        var m = model.ToLowerInvariant();
        return m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") || m.StartsWith("gpt-5");
    }

    private JsonObject BuildBody(AIChatRequest request, bool stream)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            if (m.Images is { Count: > 0 })
            {
                // Vision: content parts (text + data-URL images)
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Content } };
                foreach (var img in m.Images)
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{img.MediaType};base64,{img.Base64}" }
                    });
                messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = parts });
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = stream
        };

        if (_isOpenAI && IsReasoningModel(request.Model))
        {
            body["max_completion_tokens"] = Math.Max(request.MaxTokens, 2048);
        }
        else
        {
            body["max_tokens"] = request.MaxTokens;
            body["temperature"] = request.Temperature;
        }

        // deepseek-reasoner (DeepSeek-R1 API) doesn't support JSON mode; the prompt still asks for JSON only.
        if (request.JsonMode && !request.Model.Contains("reasoner", StringComparison.OrdinalIgnoreCase))
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (stream && _isOpenAI)
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(JsonObject body, HttpCompletionOption completion, CancellationToken ct)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions") { Content = JsonContent.Create(body) };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AIProviderException($"Cannot reach {_config.Name} at {_baseUrl}.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AIProviderException($"{_config.Name} did not respond within {_http.Timeout.TotalSeconds:0} seconds.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await DescribeError(response, ct);
            response.Dispose();
            throw new AIProviderException(error);
        }
        return response;
    }

    private async Task<string> DescribeError(HttpResponseMessage response, CancellationToken ct)
    {
        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(body);
            detail = node?["error"]?["message"]?.ToString() ?? node?["error"]?.ToString() ?? node?[0]?["error"]?["message"]?.ToString();
        }
        catch (Exception) { /* ignore body parsing errors */ }

        var prefix = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{_config.Name}: authentication failed — check the API key.",
            HttpStatusCode.TooManyRequests => $"{_config.Name}: rate limit or quota exceeded.",
            HttpStatusCode.NotFound => $"{_config.Name}: HTTP 404 (check the base URL and model name).",
            _ => $"{_config.Name}: HTTP {(int)response.StatusCode}."
        };
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix} {Trim(detail, 300)}";
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>content may be a string or an array of content parts (some compatible APIs).</summary>
    private static string ExtractContent(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is JsonArray parts)
            return string.Concat(parts.Select(p => p?["text"]?.ToString() ?? string.Empty));
        return string.Empty;
    }

    private static int GetInt(JsonNode? node, string name)
    {
        var v = node?[name];
        if (v is JsonValue value && value.TryGetValue<int>(out var i)) return i;
        if (v is JsonValue value2 && value2.TryGetValue<long>(out var l)) return (int)l;
        return 0;
    }
}
