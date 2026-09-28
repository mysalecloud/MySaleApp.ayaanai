using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.AIDashboard;

namespace MySale.AI.Api.Security;

/// <summary>Builds <see cref="AIDashboardCaller"/> from the authenticated principal (validated token) — never from request data.</summary>
public sealed class HttpAIDashboardCallerAccessor : IAIDashboardCallerAccessor
{
    private readonly IHttpContextAccessor _http;
    private readonly IUserContext _user;
    private readonly IRequestContext _request;
    private AIDashboardCaller? _current;

    public HttpAIDashboardCallerAccessor(IHttpContextAccessor http, IUserContext user, IRequestContext request)
    {
        _http = http;
        _user = user;
        _request = request;
    }

    public AIDashboardCaller Current => _current ??= Build();

    private AIDashboardCaller Build()
    {
        var ctx = _http.HttpContext;
        var principal = ctx?.User;
        string? Claim(string type) => principal?.FindFirst(type)?.Value is { Length: > 0 } v ? v : null;

        DateTime? authTime = null;
        if (Claim(AgentClaims.AuthTime) is { } raw && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            authTime = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unix, 0, 253402300799)).UtcDateTime;

        return new AIDashboardCaller
        {
            IsAuthenticated = _user.IsAuthenticated,
            IsMySaleBooksUser = _user.IsMySaleBooksUser,
            UserId = _user.UserId,
            UserName = _user.DisplayName,
            CompanyId = _user.CompanyId,
            CompanyName = _user.CompanyName,
            DatabaseName = _user.DatabaseName,
            MySaleBooksUserId = Claim(AgentClaims.MySaleBooksUserId),
            MySaleBooksRoleId = Claim(AgentClaims.MySaleBooksRoleId),
            PermissionClaims = principal?.FindAll(AgentClaims.MySaleBooksPermission).Select(c => c.Value).ToList() ?? new List<string>(),
            AuthenticatedAt = authTime,
            Token = _user.IsMySaleBooksUser ? BearerToken(ctx) : null,
            IpAddress = ctx?.Connection.RemoteIpAddress?.ToString(),
            CorrelationId = _request.CorrelationId,
            ClaimTypes = (Claim(AgentClaims.SourceClaimTypes) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
        };
    }

    /// <summary>The caller's own token (same parsing as the authentication handler). Kept in memory for this request only.</summary>
    private static string? BearerToken(HttpContext? ctx)
    {
        var header = ctx?.Request.Headers.Authorization.ToString().Trim() ?? string.Empty;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header["Bearer ".Length..].Trim();
        return !header.Contains(' ') && header.Count(c => c == '.') >= 2 ? header : null;
    }
}

/// <summary>
/// Server-side gate of every AIDashboard endpoint: authenticated → tenant → MySaleBooks permission → section permission.
/// Hiding the menu in React is not relied on: a direct API call without the permission gets 403.
/// </summary>
public sealed class AIDashboardPermissionAttribute : TypeFilterAttribute
{
    public AIDashboardPermissionAttribute(string permission, bool auditDenied = true) : base(typeof(AIDashboardPermissionFilter))
        => Arguments = new object[] { permission, auditDenied };
}

public sealed class AIDashboardPermissionFilter : IAsyncAuthorizationFilter
{
    public const string AccessItemKey = "ai-dashboard-access";

    private readonly string _permission;
    private readonly bool _auditDenied;
    private readonly AIDashboardAccessService _access;
    private readonly IAIDashboardCallerAccessor _caller;
    private readonly IAuditLogRepository _audit;
    private readonly ILogger<AIDashboardPermissionFilter> _logger;

    public AIDashboardPermissionFilter(string permission, bool auditDenied, AIDashboardAccessService access, IAIDashboardCallerAccessor caller,
        IAuditLogRepository audit, ILogger<AIDashboardPermissionFilter> logger)
    {
        _permission = permission;
        _auditDenied = auditDenied;
        _access = access;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        // Restricted data: never cached by the browser or proxies.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";

        try
        {
            http.Items[AccessItemKey] = await _access.AuthorizeAsync(_permission, http.RequestAborted);
        }
        catch (AIDashboardAccessException ex)
        {
            if (_auditDenied && ex.StatusCode != 401)
            {
                try
                {
                    await _audit.InsertAsync(AIDashboardService.DeniedEntry(_caller.Current, AIDashboardPermissions.SectionOf(_permission), ex.Code),
                        http.RequestAborted);
                }
                catch (Exception auditError) when (auditError is not OperationCanceledException)
                {
                    _logger.LogWarning("AIDashboard: could not record denied access ({Error})", auditError.GetType().Name);
                }
            }
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = ex.StatusCode,
                Title = ex.StatusCode switch { 401 => "Unauthorized", 503 => "Service unavailable", _ => "Forbidden" },
                Detail = ex.Message,
                Extensions = { ["code"] = ex.Code, ["traceId"] = http.TraceIdentifier }
            })
            {
                StatusCode = ex.StatusCode,
                ContentTypes = { "application/problem+json" }
            };
        }
    }
}

public static class AIDashboardHttpExtensions
{
    /// <summary>The access object stored by <see cref="AIDashboardPermissionFilter"/> (always present inside an AIDashboard action).</summary>
    public static AIDashboardAccess GetAIDashboardAccess(this HttpContext http)
        => http.Items[AIDashboardPermissionFilter.AccessItemKey] as AIDashboardAccess
           ?? throw AIDashboardAccessException.Forbidden();
}

/// <summary>
/// The store selected in MySaleBooks ("Store Location"), sent by the web app as X-Store-Id. It is only a selection:
/// the server verifies it against the tenant's store master and the user's branch access before using it.
/// </summary>
public sealed class HttpStoreSelection : MySale.AI.Application.Stores.IStoreSelection
{
    public const string HeaderName = "X-Store-Id";
    private readonly IHttpContextAccessor _http;
    public HttpStoreSelection(IHttpContextAccessor http) => _http = http;

    public string? SelectedStoreId
    {
        get
        {
            var value = _http.HttpContext?.Request.Headers[HeaderName].ToString().Trim();
            if (string.IsNullOrEmpty(value) || value.Length > 64) return null;
            return value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') ? value : null;
        }
    }
}
