using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;

namespace MySale.AI.Infrastructure.AI;

/// <summary>
/// Local models via the Ollama HTTP API (/api/chat, /api/tags, /api/version).
/// The browser never talks to Ollama — only this backend class does.
/// </summary>
public sealed class OllamaProvider : IAIProvider, IModelInstaller, ICapabilityProvider
{
    private readonly HttpClient _http;
    private readonly ProviderConfig _config;
    private readonly string _baseUrl;

    public OllamaProvider(HttpClient http, ProviderConfig config)
    {
        _http = http;
        _config = config;
        _baseUrl = config.BaseUrl.TrimEnd('/');
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 600));
    }

    public string Kind => "ollama";

    public async Task<AIQueryResponse> GenerateQueryAsync(AIChatRequest request, CancellationToken ct)
    {
        var r = await ChatAsync(request, ct);
        return new AIQueryResponse { Text = r.Text, Model = r.Model, InputTokens = r.InputTokens, OutputTokens = r.OutputTokens, DurationMs = r.DurationMs };
    }

    public Task<AIResponse> GenerateResponseAsync(AIChatRequest request, CancellationToken ct) => ChatAsync(request, ct);

    private async Task<AIResponse> ChatAsync(AIChatRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var response = await SendAsync(BuildBody(request, stream: false), HttpCompletionOption.ResponseContentRead, ct);
        var json = await ReadJsonAsync(response, ct);
        sw.Stop();
        return new AIResponse
        {
            Text = ThinkTagFilter.Strip(json?["message"]?["content"]?.GetValue<string>() ?? string.Empty),
            Model = json?["model"]?.GetValue<string>() ?? request.Model,
            InputTokens = GetInt(json, "prompt_eval_count"),
            OutputTokens = GetInt(json, "eval_count"),
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
                throw new AIProviderException("The connection to Ollama was interrupted.", ex);
            }
            if (line is null)
            {
                var rest = think.Flush();
                if (rest.Length > 0) yield return new AIStreamChunk { Text = rest };
                yield break;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(line); }
            catch (JsonException) { continue; }

            if (chunk?["error"] is JsonNode err)
                throw new AIProviderException("Ollama error: " + err.ToString());

            var done = chunk?["done"]?.GetValue<bool>() ?? false;
            var text = think.Push(chunk?["message"]?["content"]?.GetValue<string>() ?? string.Empty);
            if (done) text += think.Flush();
            yield return new AIStreamChunk
            {
                Text = text,
                Done = done,
                InputTokens = done ? GetInt(chunk, "prompt_eval_count") : null,
                OutputTokens = done ? GetInt(chunk, "eval_count") : null
            };
            if (done) yield break;
        }
    }

    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? version = null;
        try
        {
            var models = await GetModelsAsync(ct);
            try
            {
                using var v = await _http.GetAsync($"{_baseUrl}/api/version", ct);
                if (v.IsSuccessStatusCode)
                    version = (await v.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct))?["version"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // version is informational only
            }
            sw.Stop();
            var hasModel = string.IsNullOrEmpty(_config.DefaultModel) || models.Any(m => MatchesModel(m.Name, _config.DefaultModel));
            return new ProviderTestResult
            {
                Success = true,
                LatencyMs = sw.ElapsedMilliseconds,
                Version = version,
                ModelCount = models.Count,
                Message = models.Count == 0
                    ? "Connected, but no models are installed. Run: ollama pull llama3.1"
                    : hasModel
                        ? $"Connected. {models.Count} model(s) available."
                        : $"Connected. {models.Count} model(s) available, but '{_config.DefaultModel}' is not installed (ollama pull {_config.DefaultModel})."
            };
        }
        catch (AIProviderException ex)
        {
            return new ProviderTestResult { Success = false, Message = ex.Message, LatencyMs = sw.ElapsedMilliseconds };
        }
    }

    public async Task<IReadOnlyList<AIModel>> GetModelsAsync(CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AIProviderException($"Cannot reach Ollama at {_baseUrl}. Is 'ollama serve' running?", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AIProviderException($"Ollama at {_baseUrl} did not respond in time.", ex);
        }

        using (response)
        {
            var json = await ReadJsonAsync(response, ct);
            var list = new List<AIModel>();
            if (json?["models"] is JsonArray models)
            {
                foreach (var m in models)
                {
                    var name = m?["name"]?.GetValue<string>() ?? m?["model"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(name)) continue;
                    DateTime? modified = null;
                    if (m?["modified_at"]?.GetValue<string>() is string mod && DateTime.TryParse(mod, out var dt)) modified = dt.ToUniversalTime();
                    list.Add(new AIModel
                    {
                        Id = name,
                        Name = name,
                        SizeBytes = m?["size"]?.GetValue<long>(),
                        Family = m?["details"]?["family"]?.GetValue<string>(),
                        ParameterSize = m?["details"]?["parameter_size"]?.GetValue<string>(),
                        ModifiedAt = modified
                    });
                }
            }
            return list.OrderBy(m => m.Name).ToList();
        }
    }

    /// <summary>Reads the model's capabilities from /api/show (Ollama 0.6+: "capabilities": ["completion","vision",…]).</summary>
    public async Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync($"{_baseUrl}/api/show", JsonContent.Create(new JsonObject { ["model"] = model }), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            JsonNode? json;
            try { json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); }
            catch (JsonException) { return null; }
            if (json?["capabilities"] is JsonArray caps)
            {
                var set = caps.Select(c => c?.ToString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new ModelCapabilities
                {
                    Text = set.Contains("completion") || set.Count == 0,
                    Vision = set.Contains("vision"),
                    Audio = set.Contains("audio"),
                    StructuredOutput = true,
                    Source = "provider"
                };
            }
            // Older Ollama: vision models list a "clip"/"mllama" projector family.
            var families = json?["details"]?["families"] as JsonArray;
            var hasProjector = families?.Any(f => f?.ToString() is "clip" or "mllama") == true || json?["projector_info"] is JsonObject;
            return new ModelCapabilities { Vision = hasProjector, Source = "provider" };
        }
    }

    /// <summary>
    /// Downloads a model with Ollama's /api/pull. Uses the streaming form so HttpClient's timeout only
    /// covers the response headers — large models can take many minutes.
    /// </summary>
    public async Task<string> PullModelAsync(string model, CancellationToken ct)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/pull")
        {
            Content = JsonContent.Create(new JsonObject { ["model"] = model, ["stream"] = true })
        };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AIProviderException($"Cannot reach Ollama at {_baseUrl}. Is 'ollama serve' running?", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new AIProviderException(await TryReadError(response, ct));

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string last = "started";
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                if (node?["error"] is JsonNode err)
                    throw new AIProviderException($"Ollama could not pull '{model}': {err}");
                last = node?["status"]?.ToString() ?? last;
            }
            if (!string.Equals(last, "success", StringComparison.OrdinalIgnoreCase))
                throw new AIProviderException($"Ollama pull of '{model}' ended with status '{last}'.");
            return $"Model '{model}' is installed.";
        }
    }

    // ------------------------------------------------------------------ helpers

    private JsonObject BuildBody(AIChatRequest request, bool stream)
    {
        var messages = new JsonArray();
        var promptChars = 0;
        foreach (var m in request.Messages)
        {
            var msg = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.Images is { Count: > 0 })
                msg["images"] = new JsonArray(m.Images.Select(i => (JsonNode?)JsonValue.Create(i.Base64)).ToArray());
            messages.Add(msg);
            promptChars += m.Content.Length + (m.Images?.Count ?? 0) * 3000;
        }

        // Ollama's default context window is small; size it to the prompt so the schema isn't silently truncated.
        var estimatedTokens = promptChars / 3 + request.MaxTokens + 256;
        var numCtx = Math.Clamp((int)Math.Ceiling(estimatedTokens / 2048.0) * 2048, 4096, 32768);

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["keep_alive"] = "15m",
            ["options"] = new JsonObject
            {
                ["temperature"] = request.Temperature,
                ["num_predict"] = request.MaxTokens,
                ["num_ctx"] = numCtx
            }
        };
        if (request.JsonMode) body["format"] = "json";
        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(JsonObject body, HttpCompletionOption completion, CancellationToken ct)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = JsonContent.Create(body)
        };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AIProviderException($"Cannot reach Ollama at {_baseUrl}. Is 'ollama serve' running?", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AIProviderException($"Ollama did not respond within {_http.Timeout.TotalSeconds:0} seconds. Try a smaller model or raise the timeout.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadError(response, ct);
            response.Dispose();
            throw new AIProviderException(error);
        }
        return response;
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new AIProviderException(await TryReadError(response, ct));
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new AIProviderException("Ollama returned an invalid response.", ex);
        }
    }

    private static async Task<string> TryReadError(HttpResponseMessage response, CancellationToken ct)
    {
        string body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(ct); } catch (Exception) { /* ignore */ }
        try
        {
            var err = JsonNode.Parse(body)?["error"]?.ToString();
            if (!string.IsNullOrEmpty(err))
                return err.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? $"Ollama: {err}. Pull it with 'ollama pull <model>'."
                    : $"Ollama error: {err}";
        }
        catch (JsonException) { }
        return $"Ollama returned HTTP {(int)response.StatusCode}.";
    }

    private static int GetInt(JsonNode? node, string name)
    {
        var v = node?[name];
        if (v is JsonValue value && value.TryGetValue<int>(out var i)) return i;
        if (v is JsonValue value2 && value2.TryGetValue<long>(out var l)) return (int)l;
        return 0;
    }

    private static bool MatchesModel(string installed, string configured)
        => string.Equals(installed, configured, StringComparison.OrdinalIgnoreCase)
           || string.Equals(installed, configured + ":latest", StringComparison.OrdinalIgnoreCase)
           || installed.StartsWith(configured + ":", StringComparison.OrdinalIgnoreCase);
}
