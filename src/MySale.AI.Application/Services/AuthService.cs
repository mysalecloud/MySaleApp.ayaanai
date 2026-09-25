using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Contracts;
using MySale.AI.Domain;

namespace MySale.AI.Application.Services;

/// <summary>Test authentication. Replaced by MySaleBooks authentication during integration.</summary>
public sealed class AuthService
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly ITokenService _tokens;
    private readonly AuditService _audit;

    public AuthService(IUserRepository users, IPasswordHasher hasher, ITokenService tokens, AuditService audit)
    {
        _users = users;
        _hasher = hasher;
        _tokens = tokens;
        _audit = audit;
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, string? ip, CancellationToken ct)
    {
        var user = await _users.FindByUserNameAsync(request.UserName.Trim().ToLowerInvariant(), ct);
        if (user is null || !user.IsActive || !_hasher.Verify(request.Password, user.PasswordHash))
        {
            await _audit.LogAsAsync("LoginFailed", $"User '{request.UserName}'", user, ip, ct);
            return null;
        }

        var now = DateTime.UtcNow;
        await _users.UpdateLastLoginAsync(user.Id, now, ct);
        await _audit.LogAsAsync("Login", null, user, ip, ct);
        var token = _tokens.Issue(user);
        return new LoginResponse(token.Token, token.ExpiresAt, ToDto(user));
    }

    /// <summary>
    /// Prototype only: points the current (admin) user at another company id so real MySaleBooks data can be tested.
    /// Returns a fresh token carrying the new tenant. MySaleBooks auth replaces this.
    /// </summary>
    public async Task<LoginResponse> SwitchCompanyAsync(string userId, SwitchCompanyRequest request, CancellationToken ct)
    {
        var user = await _users.GetAsync(userId, ct) ?? throw new NotFoundException("User not found.");
        user.CompanyId = request.CompanyId.Trim();
        user.CompanyName = string.IsNullOrWhiteSpace(request.CompanyName) ? request.CompanyId.Trim() : request.CompanyName.Trim();
        if (!string.IsNullOrWhiteSpace(request.Currency)) user.Currency = request.Currency.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(request.TimeZone)) user.TimeZone = request.TimeZone.Trim();
        await _users.UpsertAsync(user, ct);
        await _audit.LogAsAsync("CompanySwitched", $"{user.UserName} → {user.CompanyName} ({user.CompanyId})", user, null, ct);
        var token = _tokens.Issue(user);
        return new LoginResponse(token.Token, token.ExpiresAt, ToDto(user));
    }

    public async Task<List<DemoUserDto>> DemoUsersAsync(CancellationToken ct)
        => (await _users.ListAsync(ct))
            .Where(u => u.IsActive)
            .OrderBy(u => u.CompanyName).ThenBy(u => u.Role)
            .Select(u => new DemoUserDto(u.UserName, u.DisplayName, u.Role, u.CompanyName))
            .ToList();

    public static UserDto ToDto(AppUser u)
        => new(u.Id, u.UserName, u.DisplayName, u.Role, u.CompanyId, u.CompanyName, u.Currency, u.TimeZone);

    public static UserDto FromContext(IUserContext u)
        => new(u.UserId, u.UserName, u.DisplayName, u.Role, u.CompanyId, u.CompanyName, u.Currency, u.TimeZone,
            u.DatabaseName, u.IsMySaleBooksUser ? TenantClaimTypes.MySaleBooks : "demo");
}
