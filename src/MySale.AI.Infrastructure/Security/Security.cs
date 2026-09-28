using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;

namespace MySale.AI.Infrastructure.Security;

/// <summary>Encrypts secrets (API keys, connection strings) with ASP.NET Core Data Protection.</summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("MySale.AI.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return _protector.Unprotect(ciphertext); }
        catch (CryptographicException) { return null; } // key ring rotated/lost: treat as not configured
    }
}

/// <summary>PBKDF2-SHA256 password hashing. Format: PBKDF2$SHA256$iterations$salt$hash.</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"PBKDF2$SHA256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 5 || parts[0] != "PBKDF2" || parts[1] != "SHA256" || !int.TryParse(parts[2], out var iterations))
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class TokenPayload
{
    [JsonPropertyName("sub")] public string Sub { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("dn")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("cid")] public string CompanyId { get; set; } = string.Empty;
    [JsonPropertyName("cname")] public string CompanyName { get; set; } = string.Empty;
    [JsonPropertyName("cur")] public string Currency { get; set; } = string.Empty;
    [JsonPropertyName("tz")] public string TimeZone { get; set; } = string.Empty;
    [JsonPropertyName("iss")] public string Issuer { get; set; } = string.Empty;
    [JsonPropertyName("iat")] public long IssuedAt { get; set; }
    [JsonPropertyName("exp")] public long ExpiresAt { get; set; }
}

/// <summary>
/// Minimal HS256 JWT issuer/validator (no external packages). In MySaleBooks this is replaced by the
/// existing authentication; only the claims contract (user, role, company) matters to the agent.
/// </summary>
public sealed class HmacTokenService : ITokenService
{
    private static readonly byte[] Header = Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
    private readonly AuthOptions _options;
    private readonly byte[]? _key;
    private readonly TimeProvider _time;

    public HmacTokenService(IOptions<AuthOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        // Prototype (local-user) tokens are optional. When no signing key is configured
        // (e.g. a MySaleBooks-JWT-only deployment) they are simply disabled instead of
        // failing every request.
        _key = !string.IsNullOrWhiteSpace(_options.SigningKey) && _options.SigningKey.Length >= 32
            ? Encoding.UTF8.GetBytes(_options.SigningKey)
            : null;
    }

    /// <summary>True when prototype tokens can be issued/validated.</summary>
    public bool IsEnabled => _key is not null;

    public IssuedToken Issue(AppUser user)
    {
        if (_key is null)
            throw new InvalidOperationException("Auth:SigningKey must be configured with at least 32 characters.");
        var now = _time.GetUtcNow();
        var expires = now.AddMinutes(_options.TokenLifetimeMinutes);
        var payload = new TokenPayload
        {
            Sub = user.Id,
            Name = user.UserName,
            DisplayName = user.DisplayName,
            Role = user.Role.ToString(),
            CompanyId = user.CompanyId,
            CompanyName = user.CompanyName,
            Currency = user.Currency,
            TimeZone = user.TimeZone,
            Issuer = _options.Issuer,
            IssuedAt = now.ToUnixTimeSeconds(),
            ExpiresAt = expires.ToUnixTimeSeconds()
        };
        var head = Base64Url(Header);
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = Base64Url(Sign($"{head}.{body}"));
        return new IssuedToken($"{head}.{body}.{signature}", expires.UtcDateTime);
    }

    public bool TryValidate(string token, out TokenPayload? payload) => TryValidate(token, out payload, out _);

    /// <summary>Same validation; <paramref name="expired"/> tells an expired (but otherwise genuine) token apart from an invalid one.</summary>
    public bool TryValidate(string token, out TokenPayload? payload, out bool expired)
    {
        payload = null;
        expired = false;
        if (_key is null) return false;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        byte[] expected = Sign($"{parts[0]}.{parts[1]}");
        byte[] actual;
        try { actual = FromBase64Url(parts[2]); }
        catch (FormatException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return false;

        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(FromBase64Url(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
        if (payload is null || payload.Issuer != _options.Issuer) return false;
        if (payload.ExpiresAt > _time.GetUtcNow().ToUnixTimeSeconds()) return true;
        expired = true;
        return false;
    }

    private byte[] Sign(string data)
    {
        using var hmac = new HMACSHA256(_key!);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4)
        {
            case 2: b += "=="; break;
            case 3: b += "="; break;
        }
        return Convert.FromBase64String(b);
    }
}
