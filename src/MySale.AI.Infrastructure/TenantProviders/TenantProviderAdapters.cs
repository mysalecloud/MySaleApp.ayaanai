using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.TenantProviders;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.AI;

namespace MySale.AI.Infrastructure.TenantProviders;

/// <summary>
/// One shared, SSRF-safe connection pool for company endpoints. Each adapter instance gets its own lightweight
/// HttpClient (so per-provider auth headers never mix) on top of the shared handler.
/// </summary>
public sealed class TenantHttpClients : IDisposable
{
    private readonly SocketsHttpHandler _handler;

    public TenantHttpClients(TenantAiProviderOptions options) => _handler = SafeEndpointHttp.CreateHandler(options);

    public HttpClient Create(int timeoutSeconds, string? apiVersion = null)
    {
        HttpMessageHandler handler = string.IsNullOrEmpty(apiVersion)
            ? _handler
            : new ApiVersionHandler(apiVersion) { InnerHandler = _handler };
        return new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600)) };
    }

    public void Dispose() => _handler.Dispose();

    /// <summary>Adds ?api-version=… (Azure OpenAI classic API) to every request.</summary>
    private sealed class ApiVersionHandler : DelegatingHandler
    {
        private readonly string _version;
        public ApiVersionHandler(string version) => _version = version;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is { } uri && !uri.Query.Contains("api-version=", StringComparison.Ordinal))
            {
                var b = new UriBuilder(uri) { Query = (string.IsNullOrEmpty(uri.Query) ? string.Empty : uri.Query.TrimStart('?') + "&") + "api-version=" + Uri.EscapeDataString(_version) };
                request.RequestUri = b.Uri;
            }
            return base.SendAsync(request, ct);
        }
    }
}

/// <summary>Turns a company configuration into an <see cref="IAIProvider"/> (registry adapter type → implementation).</summary>
public sealed class TenantProviderAdapterFactory : ITenantProviderAdapterFactory
{
    private readonly TenantHttpClients _clients;
    private readonly TenantAiProviderOptions _options;

    public TenantProviderAdapterFactory(TenantHttpClients clients, TenantAiProviderOptions options)
    {
        _clients = clients;
        _options = options;
    }

    public IAIProvider Create(TenantProviderConnection c)
    {
        // Defence in depth: the endpoint is validated again right before use (stored data may predate a policy change).
        var baseUri = new Uri(c.Descriptor.Adapter == ProviderAdapterType.BedrockConverse ? BedrockConverseProvider.RuntimeUrl(Region(c)) : c.BaseUrl);
        var problem = TenantProviderValidation.CheckEndpoint(baseUri, _options);
        if (problem is not null) throw new AIProviderException(problem);

        return c.Descriptor.Adapter switch
        {
            ProviderAdapterType.BedrockConverse => new BedrockConverseProvider(_clients.Create(c.TimeoutSeconds), c, Region(c)),
            ProviderAdapterType.AzureOpenAI => AzureOpenAI(c),
            ProviderAdapterType.Anthropic => OpenAICompatible(c, c.BaseUrl, headers =>
            {
                headers.TryAddWithoutValidation("x-api-key", c.ApiKey ?? string.Empty);
                headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }, bearer: false), // Anthropic authenticates with x-api-key (an Authorization header would be read as an OAuth token)
            _ => OpenAICompatible(c, ChatBaseUrl(c), headers =>
            {
                if (c.Settings.TryGetValue("organization", out var org) && !string.IsNullOrEmpty(org)) headers.TryAddWithoutValidation("OpenAI-Organization", org);
                if (c.Settings.TryGetValue("project", out var project) && !string.IsNullOrEmpty(project)) headers.TryAddWithoutValidation("OpenAI-Project", project);
            }, bearer: true)
        };
    }

    private static string Region(TenantProviderConnection c)
        => c.Settings.TryGetValue("region", out var r) && !string.IsNullOrWhiteSpace(r) ? r : "us-east-1";

    /// <summary>Ollama serves the OpenAI protocol under /v1.</summary>
    private static string ChatBaseUrl(TenantProviderConnection c)
        => c.Descriptor.Id == "ollama" && !c.BaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? c.BaseUrl.TrimEnd('/') + "/v1" : c.BaseUrl;

    private IAIProvider AzureOpenAI(TenantProviderConnection c)
    {
        var endpoint = c.BaseUrl.TrimEnd('/');
        var deployment = c.Settings.TryGetValue("deployment", out var d) && !string.IsNullOrWhiteSpace(d) ? d : c.Model;
        var version = c.Settings.TryGetValue("apiVersion", out var v) ? v.Trim() : string.Empty;
        var v1 = version.Length == 0 || version.Equals("v1", StringComparison.OrdinalIgnoreCase) || version.Equals("preview", StringComparison.OrdinalIgnoreCase);
        // Endpoint entered with or without the /openai… path.
        var root = endpoint.Contains("/openai", StringComparison.OrdinalIgnoreCase) ? endpoint[..endpoint.IndexOf("/openai", StringComparison.OrdinalIgnoreCase)] : endpoint;
        var baseUrl = v1 ? root + "/openai/v1" : $"{root}/openai/deployments/{Uri.EscapeDataString(deployment)}";
        var http = _clients.Create(c.TimeoutSeconds, v1 ? null : version);
        http.DefaultRequestHeaders.TryAddWithoutValidation("api-key", c.ApiKey ?? string.Empty);
        var model = string.IsNullOrWhiteSpace(c.Model) ? deployment : c.Model;
        var connection = new TenantProviderConnection
        {
            Descriptor = c.Descriptor,
            DisplayName = c.DisplayName,
            BaseUrl = c.BaseUrl,
            Settings = c.Settings,
            ApiKey = c.ApiKey,
            Model = model,
            TimeoutSeconds = c.TimeoutSeconds
        };
        return new TenantOpenAICompatibleProvider(http, connection, baseUrl, "openai-compatible", bearerKey: null);
    }

    private IAIProvider OpenAICompatible(TenantProviderConnection c, string baseUrl, Action<HttpRequestHeaders> headers, bool bearer)
    {
        var http = _clients.Create(c.TimeoutSeconds);
        if (!string.IsNullOrEmpty(c.ApiKey) || c.Descriptor.Adapter != ProviderAdapterType.Anthropic) headers(http.DefaultRequestHeaders);
        return new TenantOpenAICompatibleProvider(http, c, baseUrl, c.Descriptor.Kind == "openai" ? "openai" : "openai-compatible",
            bearer ? c.ApiKey : null);
    }
}

/// <summary>
/// OpenAI-protocol provider for company keys: chat goes through the existing <see cref="OpenAIProvider"/> (JSON mode,
/// streaming, reasoning-model handling, think-tag filtering — unchanged); model discovery and the connection test are
/// implemented here to cope with the variations between providers (array / data / models shapes, no /models endpoint).
/// </summary>
public sealed class TenantOpenAICompatibleProvider : IAIProvider
{
    private readonly HttpClient _http;
    private readonly TenantProviderConnection _connection;
    private readonly string _baseUrl;
    private readonly OpenAIProvider _inner;

    public TenantOpenAICompatibleProvider(HttpClient http, TenantProviderConnection connection, string baseUrl, string kind, string? bearerKey)
    {
        _http = http;
        _connection = connection;
        _baseUrl = baseUrl.TrimEnd('/');
        var config = new ProviderConfig
        {
            Id = "tenant",
            Name = connection.DisplayName,
            Kind = kind,
            Category = ProviderCategory.Cloud,
            BaseUrl = _baseUrl,
            DefaultModel = connection.Model,
            TimeoutSeconds = Math.Clamp(connection.TimeoutSeconds, 5, 600)
        };
        // OpenAIProvider sets the Bearer header on this instance's HttpClient (never on a shared one).
        _inner = new OpenAIProvider(http, config, bearerKey, kind);
        Kind = kind;
    }

    public string Kind { get; }

    public Task<AIQueryResponse> GenerateQueryAsync(AIChatRequest request, CancellationToken ct) => _inner.GenerateQueryAsync(request, ct);
    public Task<AIResponse> GenerateResponseAsync(AIChatRequest request, CancellationToken ct) => _inner.GenerateResponseAsync(request, ct);
    public IAsyncEnumerable<AIStreamChunk> StreamResponseAsync(AIChatRequest request, CancellationToken ct) => _inner.StreamResponseAsync(request, ct);

    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (_connection.Descriptor.SupportsModelListing)
        {
            var (status, models, error) = await ListAsync(ct);
            if (models is not null && string.IsNullOrWhiteSpace(_connection.Model))
                return new ProviderTestResult
                {
                    Success = true, LatencyMs = sw.ElapsedMilliseconds, ModelCount = models.Count,
                    Message = $"Connected to {_connection.DisplayName}. {models.Count} model(s) available."
                };
            if (models is not null)
            {
                // The key works; also check that the chosen model really answers (a listed model may not be
                // available to this account — chat would then fail although the key is fine).
                try
                {
                    await _inner.GenerateResponseAsync(new AIChatRequest
                    {
                        Messages = new[] { AIChatMessage.User("Reply with OK.") },
                        Model = _connection.Model,
                        MaxTokens = 16,
                        Temperature = 0
                    }, ct);
                    return new ProviderTestResult
                    {
                        Success = true, LatencyMs = sw.ElapsedMilliseconds, ModelCount = models.Count,
                        Message = $"Connected to {_connection.DisplayName}. {models.Count} model(s) available; {_connection.Model} responded."
                    };
                }
                catch (AIProviderException ex)
                {
                    return new ProviderTestResult
                    {
                        Success = false, LatencyMs = sw.ElapsedMilliseconds, ModelCount = models.Count,
                        Message = $"The API key works, but model {_connection.Model} did not answer: {ex.Message}"
                    };
                }
            }
            // Endpoints without a model list: confirm with a minimal completion instead.
            if (status is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
                return new ProviderTestResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Message = error ?? $"{_connection.DisplayName}: connection failed." };
        }

        var model = TestModel();
        if (model is null)
            return new ProviderTestResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Message = "Choose a model to test the connection." };
        try
        {
            await _inner.GenerateResponseAsync(new AIChatRequest
            {
                Messages = new[] { AIChatMessage.User("Reply with OK.") },
                Model = model,
                MaxTokens = 16,
                Temperature = 0
            }, ct);
            return new ProviderTestResult
            {
                Success = true, LatencyMs = sw.ElapsedMilliseconds,
                Message = $"Connected to {_connection.DisplayName}. Model {model} responded."
            };
        }
        catch (AIProviderException ex)
        {
            return new ProviderTestResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Message = ex.Message };
        }
    }

    public async Task<IReadOnlyList<AIModel>> GetModelsAsync(CancellationToken ct)
    {
        if (!_connection.Descriptor.SupportsModelListing) return Array.Empty<AIModel>();
        var (_, models, error) = await ListAsync(ct);
        return models ?? throw new AIProviderException(error ?? $"{_connection.DisplayName}: the model list is not available.");
    }

    private string? TestModel()
    {
        if (!string.IsNullOrWhiteSpace(_connection.Model)) return _connection.Model;
        if (!string.IsNullOrWhiteSpace(_connection.Descriptor.TestModel)) return _connection.Descriptor.TestModel;
        return _connection.Descriptor.SuggestedModels.FirstOrDefault();
    }

    private async Task<(HttpStatusCode? Status, List<AIModel>? Models, string? Error)> ListAsync(CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"{_baseUrl}/models", ct);
        }
        catch (HttpRequestException ex)
        {
            return (null, null, $"Cannot reach {_connection.DisplayName}. {ReachabilityHint(ex)}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, null, $"{_connection.DisplayName} did not respond in time.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return (response.StatusCode, null, await TenantHttpErrors.DescribeAsync(_connection.DisplayName, response, ct));
            JsonNode? json;
            try
            {
                json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception)
            {
                return (response.StatusCode, null, $"{_connection.DisplayName} returned an unexpected model list.");
            }
            var items = json switch
            {
                JsonArray a => a,
                JsonObject o when o["data"] is JsonArray dataItems => dataItems,
                JsonObject o when o["models"] is JsonArray modelItems => modelItems,
                _ => new JsonArray()
            };
            var isOpenAI = _connection.Descriptor.Id == "openai";
            var list = new List<AIModel>();
            foreach (var m in items)
            {
                var id = (m as JsonObject)?["id"]?.ToString() ?? (m as JsonObject)?["name"]?.ToString() ?? (m is JsonValue v ? v.ToString() : null);
                if (string.IsNullOrWhiteSpace(id) || id.Length > 200) continue;
                if (id.StartsWith("models/", StringComparison.Ordinal)) id = id["models/".Length..]; // Gemini
                if (isOpenAI && TenantModelFilter.IsNonChat(id)) continue;
                var name = (m as JsonObject)?["display_name"]?.ToString() ?? (m as JsonObject)?["displayName"]?.ToString() ?? id;
                list.Add(new AIModel { Id = id, Name = name, OwnedBy = (m as JsonObject)?["owned_by"]?.ToString() });
            }
            return (response.StatusCode, list.GroupBy(x => x.Id, StringComparer.Ordinal).Select(g => g.First()).OrderBy(x => x.Id, StringComparer.Ordinal).ToList(), null);
        }
    }

    internal static string ReachabilityHint(HttpRequestException ex)
        => ex.Message.Contains("private or internal", StringComparison.Ordinal)
            ? "The endpoint resolves to a private network address, which is not allowed."
            : "Check the endpoint and your network.";
}

internal static class TenantModelFilter
{
    private static readonly string[] NonChat =
        { "embedding", "whisper", "tts", "dall-e", "moderation", "davinci", "babbage", "audio", "realtime", "transcribe", "image", "search" };

    public static bool IsNonChat(string id) => NonChat.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase));
}

/// <summary>User-safe description of a failed provider call (the provider's own message is added after redaction).</summary>
internal static class TenantHttpErrors
{
    public static async Task<string> DescribeAsync(string name, HttpResponseMessage response, CancellationToken ct)
    {
        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length <= 20_000)
            {
                var node = JsonNode.Parse(body);
                detail = node?["error"]?["message"]?.ToString() ?? node?["message"]?.ToString() ?? node?["Message"]?.ToString()
                         ?? (node?["error"] is JsonValue ev ? ev.ToString() : null) ?? node?["detail"]?.ToString();
            }
        }
        catch (Exception) { /* not JSON */ }

        var prefix = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{name}: authentication failed — check the API key and its permissions.",
            HttpStatusCode.TooManyRequests => $"{name}: rate limit or quota exceeded. Try again later or check your plan.",
            HttpStatusCode.NotFound => $"{name}: not found (HTTP 404) — check the endpoint and model name.",
            HttpStatusCode.PaymentRequired => $"{name}: billing problem (HTTP 402) — check your account balance.",
            HttpStatusCode.Gone => $"{name}: HTTP 410 — this model is no longer available from the provider. Choose another model.",
            >= HttpStatusCode.InternalServerError => $"{name}: the service is unavailable (HTTP {(int)response.StatusCode}). Try again later.",
            _ => $"{name}: HTTP {(int)response.StatusCode}."
        };
        if (string.IsNullOrWhiteSpace(detail)) return prefix;
        detail = SecretRedactor.Redact(detail);
        return $"{prefix} {(detail.Length > 300 ? detail[..300] + "…" : detail)}";
    }
}

/// <summary>
/// Amazon Bedrock through the Converse API with a Bedrock API key (Authorization: Bearer). Streaming is emulated
/// (the answer arrives as one chunk) because ConverseStream uses the AWS binary event-stream encoding.
/// </summary>
public sealed class BedrockConverseProvider : IAIProvider
{
    private readonly HttpClient _http;
    private readonly TenantProviderConnection _connection;
    private readonly string _runtime;
    private readonly string _control;

    public static string RuntimeUrl(string region) => $"https://bedrock-runtime.{region}.amazonaws.com";
    public static string ControlUrl(string region) => $"https://bedrock.{region}.amazonaws.com";

    public BedrockConverseProvider(HttpClient http, TenantProviderConnection connection, string region)
    {
        _http = http;
        _connection = connection;
        _runtime = RuntimeUrl(region);
        _control = ControlUrl(region);
        if (!string.IsNullOrEmpty(connection.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
    }

    public string Kind => "bedrock";

    public async Task<AIQueryResponse> GenerateQueryAsync(AIChatRequest request, CancellationToken ct)
    {
        var r = await ConverseAsync(request, ct);
        return new AIQueryResponse { Text = r.Text, Model = r.Model, InputTokens = r.InputTokens, OutputTokens = r.OutputTokens, DurationMs = r.DurationMs };
    }

    public Task<AIResponse> GenerateResponseAsync(AIChatRequest request, CancellationToken ct) => ConverseAsync(request, ct);

    public async IAsyncEnumerable<AIStreamChunk> StreamResponseAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var r = await ConverseAsync(request, ct);
        if (r.Text.Length > 0) yield return new AIStreamChunk { Text = r.Text };
        yield return new AIStreamChunk { Done = true, InputTokens = r.InputTokens, OutputTokens = r.OutputTokens };
    }

    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        IReadOnlyList<AIModel>? models = null;
        string? listError = null;
        try { models = await GetModelsAsync(ct); }
        catch (AIProviderException ex) { listError = ex.Message; }

        var model = !string.IsNullOrWhiteSpace(_connection.Model) ? _connection.Model : null;
        if (model is null)
        {
            return models is not null
                ? new ProviderTestResult { Success = true, LatencyMs = sw.ElapsedMilliseconds, ModelCount = models.Count, Message = $"Connected to {_connection.DisplayName}. {models.Count} model(s) available." }
                : new ProviderTestResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Message = listError ?? "Choose a model to test the connection." };
        }
        // The key may only allow inference: a minimal Converse call is the real check.
        try
        {
            await ConverseAsync(new AIChatRequest { Messages = new[] { AIChatMessage.User("Reply with OK.") }, Model = model, MaxTokens = 16, Temperature = 0 }, ct);
            return new ProviderTestResult
            {
                Success = true, LatencyMs = sw.ElapsedMilliseconds, ModelCount = models?.Count,
                Message = $"Connected to {_connection.DisplayName}. Model {model} responded."
            };
        }
        catch (AIProviderException ex)
        {
            return new ProviderTestResult { Success = false, LatencyMs = sw.ElapsedMilliseconds, Message = ex.Message };
        }
    }

    public async Task<IReadOnlyList<AIModel>> GetModelsAsync(CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_control}/foundation-models?byOutputModality=TEXT"), ct);
        using (response)
        {
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var list = new List<AIModel>();
            if (json?["modelSummaries"] is JsonArray items)
                foreach (var m in items)
                {
                    var id = m?["modelId"]?.ToString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    list.Add(new AIModel { Id = id, Name = m?["modelName"]?.ToString() ?? id, OwnedBy = m?["providerName"]?.ToString() });
                }
            return list.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        }
    }

    private async Task<AIResponse> ConverseAsync(AIChatRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var body = BuildBody(request);
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_runtime}/model/{Uri.EscapeDataString(request.Model)}/converse")
        {
            Content = JsonContent.Create(body)
        }, ct);
        using (response)
        {
            JsonNode? json;
            try { json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); }
            catch (Exception ex) { throw new AIProviderException($"{_connection.DisplayName} returned an unexpected response.", ex); }
            var text = string.Concat((json?["output"]?["message"]?["content"] as JsonArray ?? new JsonArray())
                .Select(p => p?["text"]?.ToString() ?? string.Empty));
            sw.Stop();
            return new AIResponse
            {
                Text = ThinkTagFilter.Strip(text),
                Model = request.Model,
                InputTokens = Int(json?["usage"]?["inputTokens"]),
                OutputTokens = Int(json?["usage"]?["outputTokens"]),
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    public static JsonObject BuildBody(AIChatRequest request)
    {
        var system = new JsonArray();
        var messages = new JsonArray();
        string? lastRole = null;
        JsonArray? lastContent = null;
        foreach (var m in request.Messages)
        {
            if (m.Role == "system")
            {
                if (!string.IsNullOrEmpty(m.Content)) system.Add(new JsonObject { ["text"] = m.Content });
                continue;
            }
            var role = m.Role == "assistant" ? "assistant" : "user";
            if (lastRole is null && role == "assistant")
            {
                // Converse requires the conversation to start with the user.
                lastContent = new JsonArray { new JsonObject { ["text"] = "(conversation continues)" } };
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = lastContent });
                lastRole = "user";
            }
            var parts = new JsonArray();
            if (!string.IsNullOrEmpty(m.Content)) parts.Add(new JsonObject { ["text"] = m.Content });
            foreach (var img in m.Images ?? Array.Empty<AIImage>())
            {
                var format = img.MediaType.Replace("image/", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("jpg", "jpeg");
                parts.Add(new JsonObject { ["image"] = new JsonObject { ["format"] = format, ["source"] = new JsonObject { ["bytes"] = img.Base64 } } });
            }
            if (parts.Count == 0) continue;
            if (role == lastRole && lastContent is not null)
            {
                // Consecutive messages of the same role are merged (Converse requires alternation).
                foreach (var p in parts.ToList()) { parts.Remove(p); lastContent.Add(p); }
                continue;
            }
            lastContent = parts;
            lastRole = role;
            messages.Add(new JsonObject { ["role"] = role, ["content"] = parts });
        }

        var body = new JsonObject
        {
            ["messages"] = messages,
            ["inferenceConfig"] = new JsonObject { ["maxTokens"] = request.MaxTokens, ["temperature"] = request.Temperature }
        };
        if (system.Count > 0) body["system"] = system;
        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var message = create();
            response = await _http.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AIProviderException($"Cannot reach {_connection.DisplayName}. {TenantOpenAICompatibleProvider.ReachabilityHint(ex)}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AIProviderException($"{_connection.DisplayName} did not respond within {_http.Timeout.TotalSeconds:0} seconds.", ex);
        }
        if (response.IsSuccessStatusCode) return response;
        var error = await TenantHttpErrors.DescribeAsync(_connection.DisplayName, response, ct);
        response.Dispose();
        throw new AIProviderException(error);
    }

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
