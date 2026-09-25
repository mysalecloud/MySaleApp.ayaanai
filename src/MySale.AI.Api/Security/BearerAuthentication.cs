using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MySale.AI.Application.Abstractions;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.Security;

namespace MySale.AI.Api.Security;

public static class AgentClaims
{
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string UserName = ClaimTypes.Name;
    public const string DisplayName = "display_name";
    public const string Role = ClaimTypes.Role;
    public const string CompanyId = "company_id";
    public const string CompanyName = "company_name";
    public const string Currency = "currency";
    public const string TimeZone = "time_zone";
}

public static class Policies
{
    public const string Admin = "Admin";
    public const string Developer = "Developer"; // Admin or Tester
    public const string Chat = "Chat";
}

/// <summary>Maps claims of the existing MySaleBooks JWT to the agent's user context (section "MySaleBooksAuth").</summary>
public sealed class MySaleBooksAuthOptions
{
    public const string Section = "MySaleBooksAuth";
    /// <summary>Optional claim holding the company id. Empty = the customer database is the tenant boundary.</summary>
    public string? CompanyIdClaim { get; set; }
    public string? CompanyNameClaim { get; set; }
    public string Currency { get; set; } = "AED";
    public string TimeZone { get; set; } = "Asia/Dubai";
    /// <summary>Agent role given to MySaleBooks users (Viewer = chat only).</summary>
    public string Role { get; set; } = nameof(UserRole.Viewer);
}

/// <summary>
/// Accepts two kinds of bearer token on the same "Authorization: Bearer ..." header:
/// 1. the existing MySaleBooks JWT (validated + decrypted with JwtSettings:*; the "dbName" claim selects the customer database);
/// 2. the prototype's own demo tokens (admin dashboard).
/// No new authentication system is created for MySaleBooks users — their token is validated exactly like MySaleBooks does.
/// </summary>
public sealed class BearerTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Bearer";
    private const string FailureKey = "auth_failure";
    private readonly HmacTokenService _tokens;
    private readonly ITenantDatabaseResolver _tenantResolver;
    private readonly MySaleBooksAuthOptions _mySaleBooks;

    public BearerTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        HmacTokenService tokens, ITenantDatabaseResolver tenantResolver, IOptions<MySaleBooksAuthOptions> mySaleBooks)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
        _tenantResolver = tenantResolver;
        _mySaleBooks = mySaleBooks.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Accepts both "Authorization: Bearer <token>" and the raw "Authorization: <token>" (test chat client).
        var header = Request.Headers.Authorization.ToString().Trim();
        if (string.IsNullOrEmpty(header))
            return Task.FromResult(AuthenticateResult.NoResult());

        string token;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = header["Bearer ".Length..].Trim();
        else if (!header.Contains(' ') && header.Count(c => c == '.') >= 2) token = header; // bare JWT
        else return Task.FromResult(AuthenticateResult.NoResult());                          // other schemes
        if (!_tokens.TryValidate(token, out var payload) || payload is null)
            return Task.FromResult(AuthenticateMySaleBooks(token));

        var claims = new List<Claim>
        {
            new(AgentClaims.UserId, payload.Sub),
            new(AgentClaims.UserName, payload.Name),
            new(AgentClaims.DisplayName, payload.DisplayName),
            new(AgentClaims.Role, payload.Role),
            new(AgentClaims.CompanyId, payload.CompanyId),
            new(AgentClaims.CompanyName, payload.CompanyName),
            new(AgentClaims.Currency, payload.Currency),
            new(AgentClaims.TimeZone, payload.TimeZone)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>Existing MySaleBooks JWT → validated principal + "dbName". The token itself is never logged.</summary>
    private AuthenticateResult AuthenticateMySaleBooks(string token)
    {
        if (!_tenantResolver.IsConfigured)
        {
            Context.Items[FailureKey] = "invalid";
            return AuthenticateResult.Fail("Invalid token.");
        }

        var result = _tenantResolver.Validate(token);
        if (!result.IsValid || result.Principal is null)
        {
            Context.Items[FailureKey] = result.Failure ?? "invalid";
            return AuthenticateResult.Fail(result.Failure == "expired" ? "Token expired." : "Invalid token.");
        }

        var source = result.Principal;
        string? First(params string?[] types) => types
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => source.FindFirst(t!)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var userId = First(ClaimTypes.NameIdentifier, "sub", "userId", "UserId", "uid", "id") ?? "mysalebooks-user";
        var userName = First(ClaimTypes.Name, "unique_name", "name", "userName", "UserName", ClaimTypes.Email, "email") ?? userId;
        var companyId = First(_mySaleBooks.CompanyIdClaim);
        var claims = new List<Claim>
        {
            new(AgentClaims.UserId, userId),
            new(AgentClaims.UserName, userName),
            new(AgentClaims.DisplayName, userName),
            new(AgentClaims.Role, Enum.TryParse<UserRole>(_mySaleBooks.Role, out var role) ? role.ToString() : nameof(UserRole.Viewer)),
            new(AgentClaims.CompanyName, First(_mySaleBooks.CompanyNameClaim) ?? result.DatabaseName ?? "MySaleBooks"),
            new(AgentClaims.Currency, _mySaleBooks.Currency),
            new(AgentClaims.TimeZone, _mySaleBooks.TimeZone),
            new(TenantClaimTypes.AuthSource, TenantClaimTypes.MySaleBooks)
        };
        if (!string.IsNullOrEmpty(companyId)) claims.Add(new Claim(AgentClaims.CompanyId, companyId));
        // Only the validated claim is carried forward; a missing dbName is handled by the chat endpoint (400).
        if (!string.IsNullOrEmpty(result.DatabaseName)) claims.Add(new Claim(TenantClaimTypes.DatabaseName, result.DatabaseName));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>401 with a safe, specific message (missing / expired / invalid) — no internal details.</summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failure = Context.Items.TryGetValue(FailureKey, out var f) ? f as string : null;
        var detail = failure switch
        {
            "expired" => "Your session has expired. Please sign in again.",
            "invalid" => "The access token is invalid.",
            _ => "Authorization is required."
        };
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        Response.ContentType = "application/problem+json";
        await Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new { title = "Unauthorized", status = 401, detail, code = failure ?? "missing" }));
    }
}

/// <summary>IUserContext backed by the authenticated principal. The company always comes from here — never from the AI.</summary>
public sealed class HttpUserContext : IUserContext
{
    private readonly IHttpContextAccessor _accessor;
    public HttpUserContext(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? User => _accessor.HttpContext?.User;
    private string Claim(string type) => User?.FindFirst(type)?.Value ?? string.Empty;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;
    public string UserId => Claim(AgentClaims.UserId);
    public string UserName => Claim(AgentClaims.UserName);
    public string DisplayName => Claim(AgentClaims.DisplayName) is { Length: > 0 } d ? d : UserName;
    public UserRole Role => Enum.TryParse<UserRole>(Claim(AgentClaims.Role), out var r) ? r : UserRole.Viewer;
    /// <summary>MySaleBooks users without a company claim are scoped by database: "db:{dbName}".</summary>
    public string CompanyId => Claim(AgentClaims.CompanyId) is { Length: > 0 } c ? c
        : DatabaseName is { } db ? "db:" + db : string.Empty;
    public string CompanyName => Claim(AgentClaims.CompanyName);
    public string Currency => Claim(AgentClaims.Currency) is { Length: > 0 } c ? c : "AED";
    public string TimeZone => Claim(AgentClaims.TimeZone) is { Length: > 0 } t ? t : "UTC";

    public bool IsMySaleBooksUser => Claim(TenantClaimTypes.AuthSource) == TenantClaimTypes.MySaleBooks;
    public string? DatabaseName => IsMySaleBooksUser && Claim(TenantClaimTypes.DatabaseName) is { Length: > 0 } db ? db : null;
    public bool TenantIsDatabase => IsMySaleBooksUser && string.IsNullOrEmpty(Claim(AgentClaims.CompanyId));
}
