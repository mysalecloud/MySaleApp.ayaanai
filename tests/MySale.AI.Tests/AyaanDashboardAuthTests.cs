using Microsoft.Extensions.Options;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.AIDashboard;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Services;
using MySale.AI.Domain;
using MySale.AI.Infrastructure;
using MySale.AI.Infrastructure.Security;

namespace MySale.AI.Tests;

/// <summary>
/// AIDashboard second layer: the existing AYAAN Dashboard session (issued by the existing AYAAN login) is validated with the
/// existing token service and user store — no new authentication system.
/// </summary>
public class AyaanDashboardAuthTests
{
    private const string Key = "unit-test-signing-key-0123456789-abcdefgh";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Users : IUserRepository
    {
        public List<AppUser> Items { get; } = new();
        public Task<AppUser?> FindByUserNameAsync(string userName, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(u => u.UserName == userName));
        public Task<AppUser?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(u => u.Id == id));
        public Task<List<AppUser>> ListAsync(CancellationToken ct) => Task.FromResult(Items.ToList());
        public Task UpsertAsync(AppUser user, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateLastLoginAsync(string id, DateTime at, CancellationToken ct) => Task.CompletedTask;
    }

    private readonly Clock _clock = new();
    private readonly Users _users = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();

    private HmacTokenService Tokens(string key = Key, int minutes = 480)
        => new(Options.Create(new AuthOptions { SigningKey = key, TokenLifetimeMinutes = minutes }), _clock);

    private AppUser AddUser(bool active = true)
    {
        var user = new AppUser
        {
            Id = "66b000000000000000000001", UserName = "admin.a", DisplayName = "Admin A", Role = UserRole.Admin,
            PasswordHash = _hasher.Hash("Demo@123"), IsActive = active
        };
        _users.Items.Add(user);
        return user;
    }

    /// <summary>Signs in exactly like the AYAAN Dashboard login page (POST /api/auth/login → AuthService).</summary>
    private async Task<LoginResponse?> Login(HmacTokenService tokens, string password = "Demo@123")
    {
        var store = new MemoryStore();
        var audit = new AuditService(new MemAudit(store), new FakeUser());
        return await new AuthService(_users, _hasher, tokens, audit).LoginAsync(new LoginRequest { UserName = "admin.a", Password = password }, "127.0.0.1", default);
    }

    [Fact]
    public async Task A_session_from_the_existing_AYAAN_login_is_accepted()
    {
        AddUser();
        var tokens = Tokens();
        var login = await Login(tokens);
        var session = await new AyaanDashboardSessionValidator(tokens, _users).ValidateAsync(login!.Token, default);

        Assert.Equal(AyaanSessionState.Valid, session.State);
        Assert.Equal("Admin A", session.UserName);
        Assert.Equal("Admin", session.Role);
        Assert.Equal(login.ExpiresAt, session.ExpiresAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(AyaanSessionState.Valid, (await new AyaanDashboardSessionValidator(tokens, _users).ValidateAsync("Bearer " + login.Token, default)).State);
    }

    [Fact]
    public async Task Failed_AYAAN_logins_give_no_session()
    {
        AddUser();
        Assert.Null(await Login(Tokens(), password: "wrong"));
    }

    [Fact]
    public async Task Missing_invalid_and_foreign_tokens_are_refused()
    {
        AddUser();
        var tokens = Tokens();
        var validator = new AyaanDashboardSessionValidator(tokens, _users);
        var token = (await Login(tokens))!.Token;

        Assert.Equal(AyaanSessionState.Missing, (await validator.ValidateAsync(null, default)).State);
        Assert.Equal(AyaanSessionState.Missing, (await validator.ValidateAsync("  ", default)).State);
        Assert.Equal(AyaanSessionState.Invalid, (await validator.ValidateAsync(token[..^4] + "AAAA", default)).State);          // tampered
        Assert.Equal(AyaanSessionState.Invalid, (await validator.ValidateAsync("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln", default)).State); // e.g. a MySaleBooks JWT
        var foreign = (await Login(Tokens("another-signing-key-0123456789-abcdefgh")))!.Token;
        Assert.Equal(AyaanSessionState.Invalid, (await validator.ValidateAsync(foreign, default)).State);
        Assert.Equal(AyaanSessionState.Invalid, (await validator.ValidateAsync(new string('a', 5000), default)).State);
    }

    [Fact]
    public async Task Expired_sessions_require_a_new_AYAAN_sign_in()
    {
        AddUser();
        var tokens = Tokens(minutes: 30);
        var token = (await Login(tokens))!.Token;
        _clock.Now = _clock.Now.AddMinutes(31);
        var session = await new AyaanDashboardSessionValidator(tokens, _users).ValidateAsync(token, default);
        Assert.Equal(AyaanSessionState.Expired, session.State);
        Assert.Null(session.UserName);
    }

    [Fact]
    public async Task Deactivated_or_deleted_AYAAN_accounts_lose_access_immediately()
    {
        var user = AddUser();
        var tokens = Tokens();
        var validator = new AyaanDashboardSessionValidator(tokens, _users);
        var token = (await Login(tokens))!.Token;

        user.IsActive = false;
        Assert.Equal(AyaanSessionState.Invalid, (await validator.ValidateAsync(token, default)).State);
        _users.Items.Clear();
        Assert.Equal(AyaanSessionState.Invalid, (await validator.ValidateAsync(token, default)).State);
    }

    [Fact]
    public async Task Without_an_AYAAN_signing_key_the_dashboard_stays_closed()
    {
        var validator = new AyaanDashboardSessionValidator(Tokens(key: ""), _users);
        Assert.Equal(AyaanSessionState.NotConfigured, (await validator.ValidateAsync("a.b.c", default)).State);
        Assert.Equal(AyaanSessionState.Missing, (await validator.ValidateAsync(null, default)).State);
    }
}
