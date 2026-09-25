using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MySale.AI.Infrastructure.Security;

namespace MySale.AI.Tests;

/// <summary>Tokens are created the way MySaleBooks does (signed + encrypted) and resolved with the same JwtSettings.</summary>
public class TenantDatabaseResolverTests
{
    private const string Secret = "unit-test-signing-secret-that-is-long-enough-0123456789";
    private const string EncryptionKeyHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const string Issuer = "MySaleBooks";
    private const string Audience = "MySaleBooksClients";

    private static TenantDatabaseResolver Resolver(string? audience = Audience) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:Secret"] = Secret,
            ["JwtSettings:EncryptionKey"] = EncryptionKeyHex,
            ["JwtSettings:Issuer"] = Issuer,
            ["JwtSettings:Audience"] = audience
        }).Build(),
        NullLogger<TenantDatabaseResolver>.Instance);

    private static string Token(string? dbName, DateTime? expires = null, string audience = Audience, string secret = Secret)
    {
        var claims = new List<Claim> { new("sub", "user-1") };
        if (dbName is not null) claims.Add(new Claim("dbName", dbName));
        var now = DateTime.UtcNow;
        var exp = expires ?? now.AddMinutes(30);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = Issuer,
            Audience = audience,
            NotBefore = exp < now ? exp.AddMinutes(-10) : now.AddMinutes(-1),
            IssuedAt = exp < now ? exp.AddMinutes(-10) : now.AddMinutes(-1),
            Expires = exp,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256),
            EncryptingCredentials = new EncryptingCredentials(
                new SymmetricSecurityKey(Convert.FromHexString(EncryptionKeyHex)), SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes128CbcHmacSha256)
        };
        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    [Fact]
    public void Valid_encrypted_token_resolves_dbName()
    {
        var r = Resolver().Validate(Token("ABC_DATABASE"));
        Assert.True(r.IsValid);
        Assert.Equal("ABC_DATABASE", r.DatabaseName);
        Assert.Equal("ABC_DATABASE", Resolver().GetDatabaseName(Token("ABC_DATABASE")));
    }

    [Fact]
    public void Missing_dbName_claim_returns_null_database()
    {
        var r = Resolver().Validate(Token(null));
        Assert.True(r.IsValid);
        Assert.Null(r.DatabaseName);
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var r = Resolver().Validate(Token("ABC", DateTime.UtcNow.AddHours(-2)));
        Assert.False(r.IsValid);
        Assert.Equal("expired", r.Failure);
    }

    [Fact]
    public void Wrong_audience_or_signature_is_rejected()
    {
        Assert.Null(Resolver().GetDatabaseName(Token("ABC", audience: "someone-else")));
        Assert.Null(Resolver().GetDatabaseName(Token("ABC", secret: "another-signing-secret-that-is-long-enough-987654321")));
        Assert.Null(Resolver().GetDatabaseName("not.a.jwt"));
    }

    [Fact]
    public void Unsafe_database_name_is_not_used()
        => Assert.Null(Resolver().GetDatabaseName(Token("admin$cmd/../x")));
}
