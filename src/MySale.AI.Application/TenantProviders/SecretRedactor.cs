using System.Text.RegularExpressions;

namespace MySale.AI.Application.TenantProviders;

/// <summary>
/// Removes credentials from text before it is stored, logged or returned (provider error messages sometimes echo
/// the key or part of it, e.g. "Incorrect API key provided: sk-ab***xyz").
/// </summary>
public static class SecretRedactor
{
    public const string Mask = "[redacted]";

    // Well-known key formats (OpenAI, Anthropic, Google, xAI, Groq, NVIDIA, Perplexity, Fireworks, AWS, Bearer values …)
    private static readonly Regex KnownKeys = new(
        @"(?<![A-Za-z0-9])(sk-[A-Za-z0-9_\-\*\.]{6,}|sk_[A-Za-z0-9_\-\*]{6,}|AIza[0-9A-Za-z_\-]{10,}|xai-[A-Za-z0-9_\-\*]{6,}|gsk_[A-Za-z0-9_\-\*]{6,}|nvapi-[A-Za-z0-9_\-\*]{6,}|pplx-[A-Za-z0-9_\-\*]{6,}|fw_[A-Za-z0-9_\-\*]{6,}|ABSK[A-Za-z0-9+/=_\-]{10,}|AKIA[0-9A-Z]{12,}|ASIA[0-9A-Z]{12,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AuthValues = new(
        @"(?i)\b(bearer\s+|(?:api[-_ ]?key|x-api-key|authorization)\s*[:=]\s*(?:bearer\s+)?)([^\s""',;]{6,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Long opaque tokens: 32+ characters of key alphabet containing both letters and digits.
    private static readonly Regex Opaque = new(@"[A-Za-z0-9_\-\*+/=]{32,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string? text, string? secret = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = text;
        if (!string.IsNullOrEmpty(secret))
        {
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
            // Partial echoes (first / last characters of the key)
            if (secret.Length >= 16)
            {
                result = result.Replace(secret[..12], Mask, StringComparison.Ordinal);
                result = result.Replace(secret[^8..], Mask, StringComparison.Ordinal);
            }
        }
        result = KnownKeys.Replace(result, Mask);
        result = AuthValues.Replace(result, m => m.Groups[1].Value + Mask);
        result = Opaque.Replace(result, m => m.Value.Any(char.IsDigit) && m.Value.Any(char.IsLetter) && !m.Value.Contains('/') ? Mask : m.Value);
        return result.Length > 400 ? result[..400] + "…" : result;
    }
}
