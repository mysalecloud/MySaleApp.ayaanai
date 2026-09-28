using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MySale.AI.Application.ActivityTracking;

namespace MySale.AI.Application.AIDashboard;

/// <summary>
/// Section keys of the AIDashboard (formerly permission keys — the AIDashboard permission check has been removed;
/// access is MySaleBooks authentication + the existing AYAAN Dashboard authentication). They follow the MySaleBooks convention
/// ("module.action", lower case) so they can be added to the MySaleBooks PermissionRecord list and assigned to
/// roles in Settings → User Role. Comparison is case-insensitive ("AIDashboard.View" = "aidashboard.view").
/// </summary>
public static class AIDashboardPermissions
{
    public const string View = "aidashboard.view";
    public const string Conversations = "aidashboard.conversations";
    public const string QueryLogs = "aidashboard.querylogs";
    public const string Activity = "aidashboard.activity";
    public const string Usage = "aidashboard.usage";
    public const string Providers = "aidashboard.providers";
    public const string Settings = "aidashboard.settings";
    public const string Models = "aidashboard.models";
    public const string Schema = "aidashboard.schema";
    /// <summary>Agent, capabilities, prompt versions, tools and channels (voice / attachments).</summary>
    public const string Agent = "aidashboard.agent";
    /// <summary>Security events and the audit log of the company.</summary>
    public const string Security = "aidashboard.security";

    public static readonly IReadOnlyList<string> All = new[]
    {
        View, Conversations, QueryLogs, Activity, Usage, Models, Agent, Schema, Providers, Security, Settings
    };

    /// <summary>Sections holding restricted data (kept for reference; access is no longer permission-based).</summary>
    public static readonly IReadOnlySet<string> Sensitive = new HashSet<string>(StringComparer.Ordinal)
    {
        Conversations, QueryLogs, Activity, Providers, Settings, Schema, Security
    };

    public static string Normalize(string permission) => permission.Trim().ToLowerInvariant();

    /// <summary>Section name used in the UI and the audit log.</summary>
    public static string SectionOf(string permission) => Normalize(permission) switch
    {
        View => "Overview",
        Conversations => "Conversations",
        QueryLogs => "QueryLogs",
        Activity => "Activity",
        Usage => "Usage",
        Providers => "Providers",
        Settings => "Settings",
        Models => "Models",
        Schema => "Schema",
        Agent => "Agent",
        Security => "Security",
        _ => "Unknown"
    };
}

/// <summary>Configuration section "AIDashboard".</summary>
public sealed class AIDashboardOptions
{
    public const string Section = "AIDashboard";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where the user's MySaleBooks permission record (branch/store mappings) comes from — used for store access, not to
    /// gate the AIDashboard (the AIDashboard permission check was removed; access = MySaleBooks + AYAAN authentication):
    /// "MySaleBooksApi" — server-to-server call to the MySaleBooks permission API with the user's own token (default);
    /// "Claims" — permission claims inside the MySaleBooks JWT;
    /// "Disabled" — nobody gets access.
    /// </summary>
    public string PermissionSource { get; set; } = "MySaleBooksApi";

    /// <summary>
    /// MySaleBooks permission endpoint. {userId} and {userRoleId} are filled from the validated token only
    /// (never from the browser). Example:
    /// https://…companyapi…/api/v1/Permissions?searchType=permissions&amp;userId={userId}&amp;userRoleId={userRoleId}
    /// </summary>
    public string? PermissionsUrl { get; set; }
    /// <summary>API key of the MySaleBooks company API (environment variable / secret manager — never in the frontend of this service).</summary>
    public string? PermissionsApiKey { get; set; }
    public string PermissionsApiKeyHeader { get; set; } = "api-key";
    /// <summary>Header MySaleBooks expects the user's token in.</summary>
    public string TokenHeader { get; set; } = "Token";
    public int PermissionTimeoutSeconds { get; set; } = 10;

    /// <summary>Claims of the MySaleBooks JWT holding the MySaleBooks user id (first non-empty wins).</summary>
    public List<string> UserIdClaims { get; set; } = new()
    {
        "userGuid", "UserGuid", "userId", "UserId", "sub", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"
    };
    /// <summary>Claims holding the MySaleBooks user role id.</summary>
    public List<string> UserRoleIdClaims { get; set; } = new()
    {
        "userRoleGuid", "UserRoleGuid", "userRoleId", "UserRoleId", "roleId", "RoleId"
    };
    /// <summary>Claims holding permission keys (PermissionSource = Claims).</summary>
    public List<string> PermissionClaims { get; set; } = new() { "permission", "permissions", "Permission" };
    /// <summary>Optional claim with the time the user last signed in (defaults to "auth_time", then "iat").</summary>
    public List<string> AuthTimeClaims { get; set; } = new() { "auth_time", "iat" };

    /// <summary>MySaleBooks role ids treated as system (administrator) roles, like VITE_ADMIN_ROLE_ID in the web app.</summary>
    public List<string> AdminRoleIds { get; set; } = new();
    /// <summary>How long a MySaleBooks permission record (store mappings) is reused. 0 = never.</summary>
    public int CacheSeconds { get; set; } = 60;

    /// <summary>
    /// "Recent sign-in" requirement for the permissions listed in <see cref="RecentAuthPermissions"/>: the MySaleBooks token
    /// must have been issued (auth_time/iat) within this many minutes. 0 = off. MySaleBooks has no MFA/OTP step-up today;
    /// this is the strongest re-authentication available without one.
    /// </summary>
    public int RecentAuthMinutes { get; set; }
    public List<string> RecentAuthPermissions { get; set; } = new() { AIDashboardPermissions.QueryLogs, AIDashboardPermissions.Providers };

    /// <summary>Idle time after which the web UI locks the dashboard and clears the loaded data (minutes).</summary>
    public int SessionIdleMinutes { get; set; } = 15;
}

/// <summary>
/// Facts about the caller, taken only from the validated MySaleBooks token (built by the API layer).
/// Nothing here comes from the request body, query string or client headers.
/// </summary>
public sealed class AIDashboardCaller
{
    public bool IsAuthenticated { get; init; }
    public bool IsMySaleBooksUser { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string CompanyId { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string? DatabaseName { get; init; }
    /// <summary>MySaleBooks user id / role id from the token (see AIDashboard:UserIdClaims / UserRoleIdClaims).</summary>
    public string? MySaleBooksUserId { get; init; }
    public string? MySaleBooksRoleId { get; init; }
    public IReadOnlyList<string> PermissionClaims { get; init; } = Array.Empty<string>();
    public DateTime? AuthenticatedAt { get; init; }
    /// <summary>The user's own token — used only to call the MySaleBooks permission API on their behalf. Never logged or stored.</summary>
    public string? Token { get; init; }
    public string? IpAddress { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    /// <summary>Claim TYPES present in the token (never values) — only for the "permission context missing" diagnostic.</summary>
    public IReadOnlyList<string> ClaimTypes { get; init; } = Array.Empty<string>();
    /// <summary>
    /// Second layer: the caller's existing AYAAN Dashboard session (X-Ayaan-Session), validated by the API layer with the
    /// same token service and user store as the AYAAN Dashboard login. Never the MySaleBooks token.
    /// </summary>
    public AyaanDashboardSession Ayaan { get; init; } = AyaanDashboardSession.Missing;

    public string TenantRef => ActivityHashing.TenantRef(DatabaseName, CompanyId);
}

/// <summary>State of the AYAAN Dashboard session presented with an AIDashboard request.</summary>
public enum AyaanSessionState
{
    /// <summary>No AYAAN session sent → the AYAAN sign-in is required.</summary>
    Missing,
    /// <summary>Signature / issuer / format not valid, or the AYAAN account no longer exists or is inactive.</summary>
    Invalid,
    /// <summary>The AYAAN session token has expired (Auth:TokenLifetimeMinutes).</summary>
    Expired,
    /// <summary>AYAAN sign-in is not configured on this server (Auth:SigningKey missing).</summary>
    NotConfigured,
    Valid
}

/// <summary>Result of validating the AYAAN Dashboard session. Contains no token.</summary>
public sealed record AyaanDashboardSession(AyaanSessionState State, string? UserId = null, string? UserName = null,
    string? Role = null, DateTime? ExpiresAt = null)
{
    public static readonly AyaanDashboardSession Missing = new(AyaanSessionState.Missing);
    public bool IsValid => State == AyaanSessionState.Valid;
}

public interface IAIDashboardCallerAccessor
{
    AIDashboardCaller Current { get; }
}

public sealed class PermissionLookupResult
{
    public bool Succeeded { get; init; }
    public bool IsSystemRole { get; init; }
    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>();
    /// <summary>
    /// Branches (stores) the user is mapped to in MySaleBooks. null = the response did not say; empty = no restriction.
    /// </summary>
    public IReadOnlyList<string>? BranchIds { get; init; }
    /// <summary>"unavailable" (service down → 503) or "context-missing" / "not-configured" (→ 403).</summary>
    public string? Failure { get; init; }

    public static PermissionLookupResult Fail(string failure) => new() { Succeeded = false, Failure = failure };
}

/// <summary>Reads the caller's MySaleBooks permissions (server side). Implementations never log the token.</summary>
public interface IAIDashboardPermissionSource
{
    string Name { get; }
    Task<PermissionLookupResult> LookupAsync(AIDashboardCaller caller, CancellationToken ct);
}

/// <summary>Permissions carried as claims inside the MySaleBooks JWT.</summary>
public sealed class ClaimsPermissionSource : IAIDashboardPermissionSource
{
    private readonly AIDashboardOptions _options;
    public ClaimsPermissionSource(AIDashboardOptions options) => _options = options;
    public string Name => "Claims";

    public Task<PermissionLookupResult> LookupAsync(AIDashboardCaller caller, CancellationToken ct)
    {
        var permissions = caller.PermissionClaims
            .SelectMany(p => p.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(AIDashboardPermissions.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        var admin = caller.MySaleBooksRoleId is { Length: > 0 } role
                    && _options.AdminRoleIds.Contains(role, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new PermissionLookupResult { Succeeded = true, IsSystemRole = admin, Permissions = permissions });
    }
}

/// <summary>Refused access to the AIDashboard. <see cref="Exception.Message"/> is safe to show to the user.</summary>
public sealed class AIDashboardAccessException : Exception
{
    public int StatusCode { get; }
    /// <summary>unauthenticated | tenant_required | disabled | ayaan_login_required | ayaan_session_expired | ayaan_auth_unavailable | reauthentication_required</summary>
    public string Code { get; }

    public AIDashboardAccessException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public static AIDashboardAccessException Forbidden(string code = "forbidden",
        string message = "You don't have permission to access this section.") => new(403, code, message);
}

/// <summary>The authorised caller of one AIDashboard request. Tenant scope values come from the token only.</summary>
public sealed class AIDashboardAccess
{
    public string UserId { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    /// <summary>Tenant key of conversations and query logs ("db:{dbName}" or the company claim).</summary>
    public string CompanyId { get; init; } = string.Empty;
    /// <summary>Tenant key of the activity log (hash of the customer database).</summary>
    public string TenantRef { get; init; } = string.Empty;
    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>();
    public string? IpAddress { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    /// <summary>AYAAN Dashboard account that unlocked this request (second authentication layer).</summary>
    public string? AyaanUserId { get; init; }
    public string? AyaanUserName { get; init; }
    public DateTime? AyaanSessionExpiresAt { get; init; }

    public bool Has(string permission) => Permissions.Contains(AIDashboardPermissions.Normalize(permission));
}

/// <summary>Short-lived in-memory cache of permission lookups, keyed by a hash of tenant + user + token.</summary>
public sealed class AIDashboardPermissionCache
{
    private readonly ConcurrentDictionary<string, (DateTime Expires, PermissionLookupResult Result)> _entries = new();

    public bool TryGet(string key, DateTime now, out PermissionLookupResult result)
    {
        if (_entries.TryGetValue(key, out var e) && e.Expires > now)
        {
            result = e.Result;
            return true;
        }
        result = null!;
        return false;
    }

    public void Set(string key, PermissionLookupResult result, DateTime expires)
    {
        if (_entries.Count > 10_000)
            foreach (var old in _entries.Where(e => e.Value.Expires <= DateTime.UtcNow).Select(e => e.Key).ToList())
                _entries.TryRemove(old, out _);
        _entries[key] = (expires, result);
    }
}

/// <summary>
/// Server-side authorisation of every AIDashboard request (two authentication layers, no permission check):
/// 1. MySaleBooks authentication (validated MySaleBooks JWT) → tenant (dbName from that token);
/// 2. the existing AYAAN Dashboard authentication (valid, unexpired AYAAN session of an active AYAAN account);
/// → optional recent sign-in → access object carrying the tenant scope. Fails closed.
/// </summary>
public sealed class AIDashboardAccessService
{
    private readonly IAIDashboardCallerAccessor _caller;
    private readonly IEnumerable<IAIDashboardPermissionSource> _sources;
    private readonly AIDashboardOptions _options;
    private readonly AIDashboardPermissionCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<AIDashboardAccessService> _logger;

    public AIDashboardAccessService(IAIDashboardCallerAccessor caller, IEnumerable<IAIDashboardPermissionSource> sources,
        AIDashboardOptions options, AIDashboardPermissionCache cache, TimeProvider time, ILogger<AIDashboardAccessService> logger)
    {
        _caller = caller;
        _sources = sources;
        _options = options;
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    public AIDashboardOptions Options => _options;

    /// <summary>
    /// The caller's MySaleBooks permission record (cached like non-sensitive lookups), or null when it cannot be read
    /// (not a MySaleBooks user, source not configured, service unavailable). Used for store (branch) access.
    /// </summary>
    public async Task<PermissionLookupResult?> TryLookupAsync(CancellationToken ct)
    {
        var caller = _caller.Current;
        if (!caller.IsAuthenticated || !caller.IsMySaleBooksUser) return null;
        try
        {
            var result = await LookupAsync(caller, sensitive: false, ct);
            return result.Succeeded ? result : null;
        }
        catch (AIDashboardAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the access for a section or throws <see cref="AIDashboardAccessException"/>. <paramref name="section"/> is the
    /// aidashboard.* key of the section (used for the audit log and the optional recent sign-in rule) — it is not a permission.
    /// </summary>
    public Task<AIDashboardAccess> AuthorizeAsync(string section, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = AIDashboardPermissions.Normalize(section);
        var caller = _caller.Current;

        // 1. MySaleBooks authentication
        if (!caller.IsAuthenticated)
            throw new AIDashboardAccessException(401, "unauthenticated", "Your session has expired. Please sign in again.");

        // 2. Tenant: only MySaleBooks tokens with a validated dbName. Nothing tenant-related is read from the request.
        if (!caller.IsMySaleBooksUser || string.IsNullOrEmpty(caller.DatabaseName) || string.IsNullOrEmpty(caller.CompanyId))
            throw AIDashboardAccessException.Forbidden("tenant_required", "AI Dashboard is available only inside MySaleBooks.");

        if (!_options.Enabled)
            throw AIDashboardAccessException.Forbidden("disabled", "AI Dashboard is not enabled.");

        // 3. Existing AYAAN Dashboard authentication — required even when the user is signed in to MySaleBooks.
        switch (caller.Ayaan.State)
        {
            case AyaanSessionState.Valid:
                break;
            case AyaanSessionState.Expired:
                throw new AIDashboardAccessException(401, "ayaan_session_expired", "Your AYAAN session has expired. Please sign in to AYAAN again.");
            case AyaanSessionState.NotConfigured:
                _logger.LogWarning("AIDashboard: AYAAN sign-in is not configured (Auth:SigningKey); access refused (correlation {CorrelationId})", caller.CorrelationId);
                throw new AIDashboardAccessException(503, "ayaan_auth_unavailable", "AYAAN sign-in is not available right now. Please try again later.");
            default:
                throw new AIDashboardAccessException(401, "ayaan_login_required", "Please sign in to AYAAN to open the AI Dashboard.");
        }

        // 4. Recent MySaleBooks sign-in for the most sensitive sections (optional, off by default)
        if (_options.RecentAuthMinutes > 0
            && _options.RecentAuthPermissions.Select(AIDashboardPermissions.Normalize).Contains(key)
            && (caller.AuthenticatedAt is not { } at || _time.GetUtcNow().UtcDateTime - at > TimeSpan.FromMinutes(_options.RecentAuthMinutes)))
            throw AIDashboardAccessException.Forbidden("reauthentication_required",
                "For your security, please sign in again to open this section.");

        return Task.FromResult(new AIDashboardAccess
        {
            UserId = caller.UserId,
            UserName = caller.UserName,
            CompanyName = caller.CompanyName,
            CompanyId = caller.CompanyId,
            TenantRef = caller.TenantRef,
            // No AIDashboard permission check: every authenticated user sees every section of their own company.
            Permissions = AIDashboardPermissions.All.ToHashSet(StringComparer.Ordinal),
            IpAddress = caller.IpAddress,
            CorrelationId = caller.CorrelationId,
            AyaanUserId = caller.Ayaan.UserId,
            AyaanUserName = caller.Ayaan.UserName,
            AyaanSessionExpiresAt = caller.Ayaan.ExpiresAt
        });
    }

    private async Task<PermissionLookupResult> LookupAsync(AIDashboardCaller caller, bool sensitive, CancellationToken ct)
    {
        var source = _sources.FirstOrDefault(s => string.Equals(s.Name, _options.PermissionSource, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            _logger.LogWarning("AIDashboard: permission source {Source} is not available; access refused", _options.PermissionSource);
            throw AIDashboardAccessException.Forbidden("disabled", "AI Dashboard is not enabled.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        // Cache key: hash only (tenant + user + role + token), so a new token or role always re-checks.
        var key = ActivityHashing.Sha256($"{caller.TenantRef}|{caller.MySaleBooksUserId}|{caller.MySaleBooksRoleId}|{caller.Token}");
        if (!sensitive && _options.CacheSeconds > 0 && _cache.TryGet(key, now, out var cached)) return cached;

        PermissionLookupResult result;
        try
        {
            result = await source.LookupAsync(caller, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AIDashboard: permission lookup via {Source} failed ({Error}); access refused (correlation {CorrelationId})",
                source.Name, ex.GetType().Name, caller.CorrelationId);
            result = PermissionLookupResult.Fail("unavailable");
        }

        if (!result.Succeeded)
        {
            if (result.Failure == "context-missing")
            {
                // Claim TYPES only (no values) so the claim names can be configured.
                _logger.LogWarning("AIDashboard: the MySaleBooks token has no user/role id claim usable for the permission check. " +
                                   "Claim types present: {ClaimTypes}. Configure AIDashboard:UserIdClaims / UserRoleIdClaims.",
                    string.Join(", ", caller.ClaimTypes));
                throw AIDashboardAccessException.Forbidden("permission_context_missing");
            }
            if (result.Failure == "not-configured")
                throw AIDashboardAccessException.Forbidden("disabled", "AI Dashboard is not enabled.");
            throw new AIDashboardAccessException(503, "permission_check_unavailable",
                "Unable to verify your AI Dashboard permission. Please try again.");
        }

        if (_options.CacheSeconds > 0) _cache.Set(key, result, now.AddSeconds(_options.CacheSeconds));
        return result;
    }
}
