using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySale.AI.Application.Attachments;
using MySale.AI.Application.Common;

namespace MySale.AI.Application.ActivityTracking;

/// <summary>
/// Removes secrets before anything is written to the activity log: JWTs, bearer tokens, API keys, passwords,
/// connection strings and encryption keys are replaced with [REDACTED]; sensitive JSON fields are masked.
/// </summary>
public static class ActivityRedactor
{
    public const string Redacted = "[REDACTED]";

    // Field names (normalised: lower-case, no '_', '-', '.') that are always masked.
    private static readonly string[] SensitiveContains =
    {
        "password", "passwd", "secret", "apikey", "accesstoken", "refreshtoken", "idtoken", "authtoken", "bearertoken",
        "authorization", "connectionstring", "connstr", "privatekey", "encryptionkey", "signingkey", "accesskey",
        "credential", "passwordhash", "cardnumber", "creditcard", "cvv", "cvc"
    };

    private static readonly HashSet<string> SensitiveExact = new(StringComparer.Ordinal)
    {
        "pwd", "pin", "otp", "jwt", "token", "auth", "salt", "iban", "apitoken", "sessiontoken", "key", "secretkey"
    };

    private static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]*){1,3}", RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{10,}", RegexOptions.Compiled);
    private static readonly Regex MongoUri = new(@"(?i)mongodb(?:\+srv)?://[^\s""'<>]+", RegexOptions.Compiled);
    private static readonly Regex ApiKeyLike = new(@"\b(?:sk|pk|rk)-[A-Za-z0-9_-]{16,}", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(
        @"(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|token|authorization|connection[_-]?string|encryption[_-]?key|private[_-]?key|signing[_-]?key|access[_-]?key|client[_-]?secret)(\s*[""']?\s*[:=]\s*[""']?)([^\s""',;}\]]+)",
        RegexOptions.Compiled);

    public static bool IsSensitiveField(string name, IReadOnlyCollection<string>? extra = null)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var n = Normalize(name);
        if (SensitiveExact.Contains(n)) return true;
        foreach (var s in SensitiveContains)
            if (n.Contains(s, StringComparison.Ordinal)) return true;
        if (extra is not null)
            foreach (var e in extra)
                if (!string.IsNullOrWhiteSpace(e) && string.Equals(n, Normalize(e), StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Redacts secrets inside free text (questions, answers, model output, error messages).</summary>
    public static string RedactText(string? text, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var t = Jwt.Replace(text, Redacted);
        t = Bearer.Replace(t, "Bearer " + Redacted);
        t = MongoUri.Replace(t, "mongodb://" + Redacted);
        t = ApiKeyLike.Replace(t, Redacted);
        t = KeyValue.Replace(t, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
        return Truncate(t, maxLength);
    }

    /// <summary>
    /// Deep copy of <paramref name="node"/> with sensitive fields masked, excluded fields removed and secrets
    /// redacted inside strings. <paramref name="maskPersonalData"/> also masks e-mails / phone / card-like numbers
    /// in string values (used for stored query results).
    /// </summary>
    public static JsonNode? RedactJson(JsonNode? node, IReadOnlyCollection<string>? extraSensitive = null,
        IReadOnlyCollection<string>? excluded = null, bool maskPersonalData = false)
    {
        if (node is null) return null;
        var clone = node.DeepClone();
        Walk(clone, extraSensitive, excluded, maskPersonalData, 0);
        return clone;
    }

    public static string? RedactJsonString(JsonNode? node, IReadOnlyCollection<string>? extraSensitive = null, int maxLength = int.MaxValue)
        => node is null ? null : Truncate(RedactJson(node, extraSensitive)!.ToCompact(), maxLength);

    private static void Walk(JsonNode node, IReadOnlyCollection<string>? extra, IReadOnlyCollection<string>? excluded, bool pii, int depth)
    {
        if (depth > 64) return;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var bare = key.TrimStart('$');
                    if (excluded is not null && excluded.Any(e => string.Equals(Normalize(e), Normalize(bare), StringComparison.Ordinal)))
                    {
                        obj.Remove(key);
                        continue;
                    }
                    if (IsSensitiveField(bare, extra))
                    {
                        obj[key] = Redacted;
                        continue;
                    }
                    var child = obj[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s)) obj[key] = Clean(s, pii);
                    else if (child is not null) Walk(child, extra, excluded, pii, depth + 1);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s)) arr[i] = Clean(s, pii);
                    else if (child is not null) Walk(child, extra, excluded, pii, depth + 1);
                }
                break;
        }
    }

    private static string Clean(string s, bool pii)
    {
        var r = RedactText(s);
        return pii ? SensitiveDataMasker.Mask(r) : r;
    }

    private static string Normalize(string name) => name.Replace("_", "").Replace("-", "").Replace(".", "").ToLowerInvariant();

    public static string Truncate(string text, int maxLength)
        => maxLength <= 0 || text.Length <= maxLength ? text : text[..maxLength] + $"… [truncated {text.Length - maxLength} chars]";
}

public static class ActivityHashing
{
    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Stable, non-reversible tenant reference: "t_" + 16 hex chars of SHA-256(tenant key).
    /// The customer database name itself is never stored in the activity log.
    /// </summary>
    public static string TenantRef(string? databaseName, string? companyId)
    {
        var key = !string.IsNullOrEmpty(databaseName) ? "db:" + databaseName : companyId ?? string.Empty;
        return key.Length == 0 ? "t_none" : "t_" + Sha256("ayaan-tenant|" + key)[..16];
    }
}
