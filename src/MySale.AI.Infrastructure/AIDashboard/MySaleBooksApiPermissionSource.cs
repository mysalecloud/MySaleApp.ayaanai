using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MySale.AI.Application.AIDashboard;

namespace MySale.AI.Infrastructure.AIDashboard;

/// <summary>
/// Reads the caller's permissions from the existing MySaleBooks permission API — the same endpoint the MySaleBooks web
/// app uses for checkPermission() — server to server, with the caller's own token. User id and role id are taken from
/// the validated token only, so a browser cannot claim another role. Expected response (MySaleBooks company API):
/// { "data": { "userRole": { "isSystemRole": false, "permissions": [ { "systemName": "aidashboard.view", "status": true } ] } } }
/// </summary>
public sealed class MySaleBooksApiPermissionSource : IAIDashboardPermissionSource
{
    public const string HttpClientName = "mysalebooks-permissions";

    private readonly IHttpClientFactory _http;
    private readonly AIDashboardOptions _options;
    private readonly ILogger<MySaleBooksApiPermissionSource> _logger;

    public MySaleBooksApiPermissionSource(IHttpClientFactory http, AIDashboardOptions options, ILogger<MySaleBooksApiPermissionSource> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public string Name => "MySaleBooksApi";

    public async Task<PermissionLookupResult> LookupAsync(AIDashboardCaller caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.PermissionsUrl) || !Uri.TryCreate(_options.PermissionsUrl.Replace("{userId}", "x").Replace("{userRoleId}", "x"), UriKind.Absolute, out _))
        {
            _logger.LogWarning("AIDashboard: AIDashboard:PermissionsUrl is not configured; access refused");
            return PermissionLookupResult.Fail("not-configured");
        }
        if (string.IsNullOrEmpty(caller.Token) || string.IsNullOrEmpty(caller.MySaleBooksUserId) || string.IsNullOrEmpty(caller.MySaleBooksRoleId))
            return PermissionLookupResult.Fail("context-missing");

        var url = _options.PermissionsUrl
            .Replace("{userId}", Uri.EscapeDataString(caller.MySaleBooksUserId))
            .Replace("{userRoleId}", Uri.EscapeDataString(caller.MySaleBooksRoleId));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(_options.TokenHeader, caller.Token);
        if (!string.IsNullOrEmpty(_options.PermissionsApiKey))
            request.Headers.TryAddWithoutValidation(_options.PermissionsApiKeyHeader, _options.PermissionsApiKey);
        request.Headers.Accept.ParseAdd("application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.PermissionTimeoutSeconds, 2, 60)));

        HttpResponseMessage response;
        try
        {
            response = await _http.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("AIDashboard: MySaleBooks permission API timed out (correlation {CorrelationId})", caller.CorrelationId);
            return PermissionLookupResult.Fail("unavailable");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("AIDashboard: MySaleBooks permission API unreachable ({Status}) (correlation {CorrelationId})",
                ex.StatusCode, caller.CorrelationId);
            return PermissionLookupResult.Fail("unavailable");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // MySaleBooks rejected the token/role → no permissions (not an outage).
                return new PermissionLookupResult { Succeeded = true, Permissions = new HashSet<string>() };
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AIDashboard: MySaleBooks permission API returned {Status} (correlation {CorrelationId})",
                    (int)response.StatusCode, caller.CorrelationId);
                return PermissionLookupResult.Fail("unavailable");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return Parse(body, caller.MySaleBooksRoleId, _options.AdminRoleIds);
        }
    }

    /// <summary>Parses the MySaleBooks permission response (data.userRole, or userRole at the root).</summary>
    public static PermissionLookupResult Parse(string json, string? roleId, IReadOnlyCollection<string> adminRoleIds)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (TryGet(root, "data", out var data)) root = data;
            if (!TryGet(root, "userRole", out var role)) return PermissionLookupResult.Fail("unavailable");

            var isSystem = TryGet(role, "isSystemRole", out var sys) && sys.ValueKind == JsonValueKind.True;
            if (roleId is { Length: > 0 } && adminRoleIds.Contains(roleId, StringComparer.OrdinalIgnoreCase)) isSystem = true;

            var permissions = new HashSet<string>(StringComparer.Ordinal);
            if (TryGet(role, "permissions", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in list.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var granted = TryGet(p, "status", out var status) && status.ValueKind == JsonValueKind.True; // same rule as checkPermission()
                    if (granted && TryGet(p, "systemName", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } n)
                        permissions.Add(AIDashboardPermissions.Normalize(n));
                }
            }
            // Store (branch) restrictions: data.userBranchMappings[].branchId (absent = unknown, empty = unrestricted).
            List<string>? branches = null;
            var container = doc.RootElement;
            if (TryGet(container, "data", out var d2)) container = d2;
            if (TryGet(container, "userBranchMappings", out var mappings) && mappings.ValueKind == JsonValueKind.Array)
            {
                branches = new List<string>();
                foreach (var m in mappings.EnumerateArray())
                    if (TryGet(m, "branchId", out var b) && b.ValueKind == JsonValueKind.String && b.GetString() is { Length: > 0 } id)
                        branches.Add(id);
                // Mappings present but none readable (unexpected shape) = unknown, never "unrestricted".
                if (branches.Count == 0 && mappings.GetArrayLength() > 0) branches = null;
            }
            return new PermissionLookupResult { Succeeded = true, IsSystemRole = isSystem, Permissions = permissions, BranchIds = branches };
        }
        catch (JsonException)
        {
            return PermissionLookupResult.Fail("unavailable");
        }
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in element.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        return false;
    }
}

/// <summary>PermissionSource = "Disabled": nobody gets access.</summary>
public sealed class DisabledPermissionSource : IAIDashboardPermissionSource
{
    public string Name => "Disabled";
    public Task<PermissionLookupResult> LookupAsync(AIDashboardCaller caller, CancellationToken ct)
        => Task.FromResult(PermissionLookupResult.Fail("not-configured"));
}
