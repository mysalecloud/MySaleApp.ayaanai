using MySale.AI.Application.Abstractions;
using MySale.AI.Application.AIDashboard;

namespace MySale.AI.Infrastructure.Security;

/// <summary>
/// Second authentication layer of the AIDashboard inside MySaleBooks: validates the caller's EXISTING AYAAN Dashboard
/// session — the token issued by the AYAAN Dashboard login (POST /api/auth/login, AuthService)
/// — with the same <see cref="HmacTokenService"/> (signature, issuer, expiry) and the same user store (the account must still
/// exist and be active). No new authentication system: nothing is issued here, only the existing session is checked.
/// The token is never logged, stored or returned.
/// </summary>
public sealed class AyaanDashboardSessionValidator
{
    /// <summary>Header carrying the AYAAN Dashboard session token next to the MySaleBooks Authorization header.</summary>
    public const string HeaderName = "X-Ayaan-Session";
    private const int MaxTokenLength = 4096;

    private readonly HmacTokenService _tokens;
    private readonly IUserRepository _users;

    public AyaanDashboardSessionValidator(HmacTokenService tokens, IUserRepository users)
    {
        _tokens = tokens;
        _users = users;
    }

    public async Task<AyaanDashboardSession> ValidateAsync(string? headerValue, CancellationToken ct)
    {
        var token = headerValue?.Trim() ?? string.Empty;
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token["Bearer ".Length..].Trim();
        if (token.Length == 0) return AyaanDashboardSession.Missing;
        if (!_tokens.IsEnabled) return new AyaanDashboardSession(AyaanSessionState.NotConfigured);
        if (token.Length > MaxTokenLength) return new AyaanDashboardSession(AyaanSessionState.Invalid);

        if (!_tokens.TryValidate(token, out var payload, out var expired) || payload is null)
            return new AyaanDashboardSession(expired ? AyaanSessionState.Expired : AyaanSessionState.Invalid);

        // Same account rules as the AYAAN login: the account must still exist and be active (a deactivated account
        // loses access immediately, even with an unexpired session).
        MySale.AI.Domain.AppUser? user;
        try
        {
            user = string.IsNullOrEmpty(payload.Sub) ? null : await _users.GetAsync(payload.Sub, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AYAAN account store unavailable: fail closed with "not available" (503), never open the dashboard.
            return new AyaanDashboardSession(AyaanSessionState.NotConfigured);
        }
        if (user is null || !user.IsActive)
            return new AyaanDashboardSession(AyaanSessionState.Invalid);

        return new AyaanDashboardSession(AyaanSessionState.Valid, user.Id, user.DisplayName is { Length: > 0 } d ? d : user.UserName,
            user.Role.ToString(), DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt).UtcDateTime);
    }
}
