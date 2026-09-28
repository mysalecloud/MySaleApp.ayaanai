using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using MySale.AI.Api.Infrastructure;
using MySale.AI.Api.Security;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Domain;
using MySale.AI.Infrastructure;
using MySale.AI.Infrastructure.Persistence;
using MySale.AI.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------- services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserContext, HttpUserContext>();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>(); // correlation id + client app/version/session
builder.Services.AddScoped<MySale.AI.Application.AIDashboard.IAIDashboardCallerAccessor, HttpAIDashboardCallerAccessor>();
builder.Services.AddScoped<MySale.AI.Application.Stores.IStoreSelection, HttpStoreSelection>(); // X-Store-Id (MySaleBooks Store Location)
builder.Services.AddAgentInfrastructure(config);
builder.Services.AddAgentApplication(config);

builder.Services
    .AddControllers(options => options.SuppressAsyncSuffixInActionNames = false)
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    });

builder.Services.AddProblemDetails(o =>
{
    o.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
        ctx.ProblemDetails.Extensions.Remove("exception");
    };
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.Configure<MySaleBooksAuthOptions>(config.GetSection(MySaleBooksAuthOptions.Section));

// Authentication: prototype bearer tokens (swap for MySaleBooks auth during integration)
builder.Services
    .AddAuthentication(BearerTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, BearerTokenHandler>(BearerTokenHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(o =>
{
    // Customer (MySaleBooks) tokens may chat, but the developer/admin surface (providers, database, schema, settings,
    // query logs, usage, Query Lab, activity log) is for AI-dashboard accounts only, unless Activity:AllowMySaleBooksAdmins.
    var allowMySaleBooksAdmins = MySale.AI.Infrastructure.DependencyInjection.BindActivityOptions(config).AllowMySaleBooksAdmins;
    bool NotCustomerToken(AuthorizationHandlerContext ctx)
        => allowMySaleBooksAdmins || ctx.User.FindFirst(TenantClaimTypes.AuthSource)?.Value != TenantClaimTypes.MySaleBooks;
    o.AddPolicy(Policies.Chat, p => p.RequireAuthenticatedUser());
    o.AddPolicy(Policies.Dashboard, p => p.RequireAuthenticatedUser().RequireAssertion(NotCustomerToken));
    o.AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin)).RequireAssertion(NotCustomerToken));
    o.AddPolicy(Policies.Developer, p => p.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin), nameof(UserRole.Tester))
        .RequireAssertion(NotCustomerToken));
    o.AddPolicy(Policies.ActivityViewer, p => p.RequireAuthenticatedUser()
        .RequireRole(nameof(UserRole.Admin), nameof(UserRole.Tester))
        .RequireAssertion(ctx => allowMySaleBooksAdmins
            || ctx.User.FindFirst(TenantClaimTypes.AuthSource)?.Value != TenantClaimTypes.MySaleBooks));
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// Rate limiting: per user for chat, per IP for login
var chatPerMinute = config.GetValue("RateLimiting:ChatPerMinute", 30);
var loginPerMinute = config.GetValue("RateLimiting:LoginPerMinute", 10);
var uploadsPerMinute = config.GetValue("RateLimiting:UploadsPerMinute", 20);
var dashboardPerMinute = config.GetValue("RateLimiting:DashboardPerMinute", 120);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("chat", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = chatPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("upload", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = uploadsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("dashboard", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst(AgentClaims.UserId)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = dashboardPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/problem+json";
        await ctx.HttpContext.Response.WriteAsync(
            "{\"title\":\"Too many requests\",\"status\":429,\"detail\":\"Rate limit exceeded. Please wait a moment and try again.\"}", ct);
    };
});

var corsOrigins = config.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")));

// Request size limit (questions are short; config bodies are small). Upload endpoints raise it per action.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1_000_000);

var app = builder.Build();

// ---------------------------------------------------------------- pipeline
app.UseMiddleware<CorrelationIdMiddleware>(); // first: every log line, error and activity carries the same id
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", async (MySale.AI.Application.Abstractions.IBusinessDatabaseManager business, SystemDbContext system, CancellationToken ct) =>
{
    bool systemOk;
    try
    {
        await system.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1), cancellationToken: ct);
        systemOk = true;
    }
    catch (Exception)
    {
        systemOk = false;
    }
    var businessOk = await business.PingAsync(ct);
    return Results.Ok(new { status = systemOk && businessOk ? "Healthy" : "Degraded", systemDb = systemOk, businessDb = businessOk });
}).AllowAnonymous();

// ---------------------------------------------------------------- startup seeding
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<SystemSeeder>();
    await seeder.RunAsync(CancellationToken.None);
}

app.Run();

public partial class Program { }
