using System.Text.RegularExpressions;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Attachments;
using MySale.AI.Domain;

namespace MySale.AI.Application.Services;

public sealed class CapabilitiesDto
{
    public string ProviderId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public ModelCapabilities Capabilities { get; set; } = new();
    /// <summary>A vision provider is configured for images when the chat model has no vision.</summary>
    public bool VisionFallback { get; set; }
    public string? VisionFallbackName { get; set; }
    /// <summary>Server-side speech-to-text is configured.</summary>
    public bool SpeechToText { get; set; }
    public bool AttachmentsEnabled { get; set; }
    public int MaxFileSizeMb { get; set; }
    public int MaxFilesPerMessage { get; set; }
    public List<string> AllowedExtensions { get; set; } = new();
}

/// <summary>What the selected provider/model can do. The UI enables features from this; the orchestrator enforces it.</summary>
public sealed class ModelCapabilityService
{
    private readonly ProviderService _providers;
    private readonly SettingsService _settings;

    public ModelCapabilityService(ProviderService providers, SettingsService settings)
    {
        _providers = providers;
        _settings = settings;
    }

    public async Task<ModelCapabilities> GetAsync(ResolvedProvider provider, CancellationToken ct)
    {
        if (provider.Provider is ICapabilityProvider reporter)
        {
            try
            {
                var reported = await reporter.GetCapabilitiesAsync(provider.Model, ct);
                if (reported is not null) return reported;
            }
            catch (AIProviderException) { /* fall back to heuristics */ }
        }
        return Heuristic(provider.Config.Kind, provider.Config.BaseUrl, provider.Model);
    }

    public async Task<CapabilitiesDto> DescribeAsync(string? providerId, string? model, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        var provider = await _providers.ResolveAsync(providerId, model, settings, ct);
        var caps = await GetAsync(provider, ct);
        var vision = await ResolveVisionFallbackAsync(settings, ct);
        return new CapabilitiesDto
        {
            ProviderId = provider.Config.Id,
            Provider = provider.Config.Name,
            Kind = provider.Config.Kind,
            Model = provider.Model,
            Capabilities = caps,
            VisionFallback = vision is not null,
            VisionFallbackName = vision is null ? null : $"{vision.Config.Name} · {vision.Model}",
            SpeechToText = settings.Speech.Enabled && await HasSpeechProviderAsync(settings, ct),
            AttachmentsEnabled = settings.Attachments.Enabled,
            MaxFileSizeMb = settings.Attachments.MaxFileSizeMb,
            MaxFilesPerMessage = settings.Attachments.MaxFilesPerMessage,
            AllowedExtensions = FileTypeDetector.AllowedExtensions.Where(e => e is not (".webm" or ".ogg" or ".wav" or ".mp3" or ".m4a" or ".mp4")).ToList()
        };
    }

    /// <summary>The provider used to read images: the chat model if it has vision, else the configured fallback.</summary>
    public async Task<ResolvedProvider?> ResolveVisionAsync(ResolvedProvider chat, AppSettings settings, CancellationToken ct)
    {
        if ((await GetAsync(chat, ct)).Vision) return chat;
        return await ResolveVisionFallbackAsync(settings, ct);
    }

    private async Task<ResolvedProvider?> ResolveVisionFallbackAsync(AppSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Attachments.VisionProviderId)) return null;
        try
        {
            return await _providers.ResolveAsync(settings.Attachments.VisionProviderId, settings.Attachments.VisionModel, settings, ct);
        }
        catch (AIProviderException)
        {
            return null;
        }
    }

    private async Task<bool> HasSpeechProviderAsync(AppSettings settings, CancellationToken ct)
        => await _providers.ResolveSpeechProviderAsync(settings.Speech.ProviderId, ct) is not null;

    private static readonly Regex OllamaVision = new(
        @"(llava|bakllava|vision|moondream|minicpm-v|qwen2(\.5)?-?vl|qwen3-vl|gemma3(?!:1b)|llama4|mistral-small3\.[12]|granite3\.2-vision|pixtral)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CloudVision = new(
        @"^(gpt-4o|gpt-4\.1|gpt-5|o3|o4|chatgpt-4o|gemini|claude-(3|sonnet|opus|haiku)|grok-.*vision|pixtral|llama-4)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Name-based guess when the provider can't report capabilities.</summary>
    public static ModelCapabilities Heuristic(string kind, string? baseUrl, string model)
    {
        var m = model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model;
        var vision = kind == "ollama" ? OllamaVision.IsMatch(m) : CloudVision.IsMatch(m);
        var audio = kind == "openai" && (m.Contains("audio", StringComparison.OrdinalIgnoreCase) || m.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase));
        return new ModelCapabilities { Text = true, Vision = vision, Audio = audio, StructuredOutput = true, Source = "heuristic" };
    }
}

public sealed class TranscribeResponse
{
    public string Text { get; set; } = string.Empty;
    public string? Language { get; set; }
    public double? DurationSeconds { get; set; }
    public string? AttachmentId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}

public sealed class VoiceStatusDto
{
    public bool ServerSpeechToText { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? DefaultLanguage { get; set; }
    public int MaxSeconds { get; set; }
    public string? Hint { get; set; }
}

/// <summary>Voice is just another input channel: audio → text → the same chat pipeline.</summary>
public sealed class VoiceService
{
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["malayalam"] = "ml", ["arabic"] = "ar", ["hindi"] = "hi", ["tamil"] = "ta", ["urdu"] = "ur"
    };

    private readonly ProviderService _providers;
    private readonly SettingsService _settings;
    private readonly ISpeechToTextProviderFactory _factory;
    private readonly AttachmentService _attachments;
    private readonly AuditService _audit;

    public VoiceService(ProviderService providers, SettingsService settings, ISpeechToTextProviderFactory factory, AttachmentService attachments, AuditService audit)
    {
        _providers = providers;
        _settings = settings;
        _factory = factory;
        _attachments = attachments;
        _audit = audit;
    }

    public async Task<VoiceStatusDto> StatusAsync(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        var provider = s.Speech.Enabled ? await _providers.ResolveSpeechProviderAsync(s.Speech.ProviderId, ct) : null;
        return new VoiceStatusDto
        {
            ServerSpeechToText = provider is not null,
            Provider = provider?.Name,
            Model = provider is null ? null : s.Speech.Model,
            DefaultLanguage = s.Speech.DefaultLanguage,
            MaxSeconds = s.Speech.MaxSeconds,
            Hint = provider is null
                ? "No speech-to-text provider configured (Settings → Voice). The browser's built-in recognition is used where available."
                : null
        };
    }

    public async Task<TranscribeResponse> TranscribeAsync(Stream audio, string fileName, string? contentType, long length, string? language, CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (!s.Speech.Enabled) throw new SpeechException("Voice input is disabled in Settings.");
        var config = await _providers.ResolveSpeechProviderAsync(s.Speech.ProviderId, ct)
                     ?? throw new SpeechException("Speech-to-text is not configured. Choose a provider under Settings → Voice.");
        if (length > 25 * 1024 * 1024) throw new SpeechException("The recording is too long.");

        // Keep the recording as a (short-lived) attachment: validated like any upload, linkable from the message.
        var stored = await _attachments.UploadAsync(audio, fileName, contentType, length, null, allowAudio: true, ct);
        var (content, _, name) = await _attachments.OpenAsync(stored.AttachmentId, ct);
        await using (content)
        {
            var lang = NormalizeLanguage(language) ?? NormalizeLanguage(s.Speech.DefaultLanguage);
            var engine = _factory.Create(config, string.IsNullOrWhiteSpace(s.Speech.Model) ? "whisper-1" : s.Speech.Model);
            var result = await engine.TranscribeAsync(content, name, lang, ct);
            await _audit.LogAsync("VoiceTranscribed", $"{result.Provider}/{result.Model}, {result.DurationSeconds:0.0}s, lang={result.Language ?? lang ?? "auto"}", ct);
            return new TranscribeResponse
            {
                Text = result.Text.Trim(),
                Language = NormalizeLanguage(result.Language) ?? lang,
                DurationSeconds = result.DurationSeconds,
                AttachmentId = stored.AttachmentId,
                Provider = result.Provider,
                Model = result.Model
            };
        }
    }

    public static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        var l = language.Trim();
        if (LanguageNames.TryGetValue(l, out var code)) return code;
        return l.Length >= 2 ? l[..2].ToLowerInvariant() : null; // "ml-IN" → "ml"
    }
}
