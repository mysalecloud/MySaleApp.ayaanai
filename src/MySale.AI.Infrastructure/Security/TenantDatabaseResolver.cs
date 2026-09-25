using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MySale.AI.Application.Abstractions;

namespace MySale.AI.Infrastructure.Security;

/// <summary>
/// The existing MySaleBooks <c>GetDbNameFromToken</c> logic behind an interface. Uses the SAME JwtSettings
/// (Secret, EncryptionKey, Issuer, Audience) and the SAME validation rules — issuer, audience, lifetime and
/// signing key are all validated and the token is decrypted with the encryption key. Nothing is relaxed.
/// </summary>
public sealed class TenantDatabaseResolver : ITenantDatabaseResolver
{
    // MongoDB database names: no / \ . " $ * < > : | ? or spaces, max 63 characters.
    private static readonly Regex SafeDbName = new(@"^[A-Za-z0-9_\-]{1,63}$", RegexOptions.Compiled);

    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantDatabaseResolver> _logger;
    private readonly Lazy<TokenValidationParameters?> _parameters;

    public TenantDatabaseResolver(IConfiguration configuration, ILogger<TenantDatabaseResolver> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _parameters = new Lazy<TokenValidationParameters?>(BuildParameters);
    }

    public bool IsConfigured => _parameters.Value is not null;

    public string? GetDatabaseName(string jwtToken)
    {
        var result = Validate(jwtToken);
        return result.IsValid ? result.DatabaseName : null;
    }

    public TenantTokenValidation Validate(string jwtToken)
    {
        if (string.IsNullOrEmpty(jwtToken)) return new TenantTokenValidation { Failure = "missing" };
        var parameters = _parameters.Value;
        if (parameters is null) return new TenantTokenValidation { Failure = "invalid" };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(jwtToken, parameters, out _);

            var dbName = principal.Claims.FirstOrDefault(c => c.Type == TenantClaimTypes.DatabaseName)?.Value?.Trim();
            if (!string.IsNullOrEmpty(dbName) && !SafeDbName.IsMatch(dbName))
            {
                // Never log the token; the (invalid) name is logged only as a length.
                _logger.LogWarning("JWT dbName claim has an invalid format (length {Length})", dbName.Length);
                dbName = null;
            }
            return new TenantTokenValidation { IsValid = true, Principal = principal, DatabaseName = string.IsNullOrEmpty(dbName) ? null : dbName };
        }
        catch (SecurityTokenExpiredException)
        {
            return new TenantTokenValidation { Failure = "expired" };
        }
        catch (Exception ex)
        {
            // Same as the existing implementation: any failure means "no database". Only the type is logged.
            _logger.LogInformation("MySaleBooks JWT rejected: {Reason}", ex.GetType().Name);
            return new TenantTokenValidation { Failure = "invalid" };
        }
    }

    private TokenValidationParameters? BuildParameters()
    {
        var secret = _configuration["JwtSettings:Secret"];
        var encryptionKeyHex = _configuration["JwtSettings:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(encryptionKeyHex))
        {
            _logger.LogInformation("JwtSettings not configured — MySaleBooks JWT sign-in is disabled.");
            return null;
        }

        try
        {
            var secretKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var encryptionKey = new SymmetricSecurityKey(Convert.FromHexString(encryptionKeyHex));
            return new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _configuration["JwtSettings:Issuer"],

                ValidateAudience = true,
                ValidAudience = _configuration["JwtSettings:Audience"],

                ValidateLifetime = true,

                ValidateIssuerSigningKey = true,
                IssuerSigningKey = secretKey,

                TokenDecryptionKey = encryptionKey
            };
        }
        catch (FormatException)
        {
            _logger.LogError("JwtSettings:EncryptionKey is not valid hex — MySaleBooks JWT sign-in is disabled.");
            return null;
        }
    }
}
