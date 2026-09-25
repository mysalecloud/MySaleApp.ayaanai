using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;

namespace MySale.AI.Infrastructure.Media;

/// <summary>
/// Speech-to-text through an OpenAI-compatible <c>/audio/transcriptions</c> endpoint:
/// OpenAI (whisper-1, gpt-4o-mini-transcribe, gpt-4o-transcribe) or a local Whisper server exposing the same API
/// (e.g. faster-whisper / speaches), so recordings can stay on-premises. Whisper supports Malayalam, Arabic and English.
/// </summary>
public sealed class OpenAICompatibleSpeechToText : ISpeechToTextProvider
{
    private readonly HttpClient _http;
    private readonly ProviderConfig _config;
    private readonly string _model;

    public OpenAICompatibleSpeechToText(HttpClient http, ProviderConfig config, string? apiKey, string model)
    {
        _http = http;
        _config = config;
        _model = model;
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 30, 600));
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, string? language, CancellationToken ct = default)
    {
        // whisper-1 returns language + duration with verbose_json; the gpt-4o transcribe models only support json/text.
        var verbose = _model.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) || !_model.Contains("transcribe", StringComparison.OrdinalIgnoreCase);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(fileName));
        form.Add(file, "file", fileName);
        form.Add(new StringContent(_model), "model");
        form.Add(new StringContent(verbose ? "verbose_json" : "json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");
        // Hint for mixed Malayalam/English business speech (keeps English words like "sales" in Latin script).
        form.Add(new StringContent("Business question about sales, invoices, stock, customers. മലയാളം, English, العربية."), "prompt");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync($"{_config.BaseUrl.TrimEnd('/')}/audio/transcriptions", form, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SpeechException($"Cannot reach the speech-to-text service ({_config.Name}).", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SpeechException("Speech-to-text timed out.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                string? detail = null;
                try { detail = JsonNode.Parse(body)?["error"]?["message"]?.ToString(); } catch (JsonException) { }
                throw new SpeechException($"Speech-to-text failed ({(int)response.StatusCode}){(detail is null ? "" : ": " + detail)}");
            }
            JsonNode? json;
            try { json = JsonNode.Parse(body); }
            catch (JsonException ex) { throw new SpeechException("Speech-to-text returned an invalid response.", ex); }

            double? duration = json?["duration"] is JsonValue d && d.TryGetValue<double>(out var dv) ? dv : null;
            return new TranscriptionResult
            {
                Text = json?["text"]?.ToString() ?? string.Empty,
                Language = json?["language"]?.ToString() ?? language,
                DurationSeconds = duration,
                Provider = _config.Name,
                Model = _model
            };
        }
    }

    private static string GuessMime(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".webm" => "audio/webm",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".m4a" or ".mp4" => "audio/mp4",
        _ => "application/octet-stream"
    };
}

public sealed class SpeechToTextProviderFactory : ISpeechToTextProviderFactory
{
    private readonly IHttpClientFactory _http;
    private readonly ISecretProtector _secrets;

    public SpeechToTextProviderFactory(IHttpClientFactory http, ISecretProtector secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    public bool Supports(string providerKind) => providerKind is "openai" or "openai-compatible";

    public ISpeechToTextProvider Create(ProviderConfig config, string model)
    {
        if (!Supports(config.Kind))
            throw new SpeechException($"Provider '{config.Name}' ({config.Kind}) cannot transcribe audio. Use OpenAI or an OpenAI-compatible Whisper server.");
        return new OpenAICompatibleSpeechToText(_http.CreateClient("ai"), config, _secrets.Unprotect(config.ApiKeyEncrypted), model);
    }
}
