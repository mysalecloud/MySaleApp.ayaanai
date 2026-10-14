using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using MySale.AI.Api.Security;
using MySale.AI.Application.AIDashboard;
using MySale.AI.Application.TenantProviders;

namespace MySale.AI.Api.Controllers;

/// <summary>
/// AI Settings → AI Providers &amp; API Keys (inside the MySaleBooks AI Dashboard). New, isolated endpoints; the existing
/// read-only <c>GET api/ai-dashboard/providers</c> and the operator console's <c>api/providers</c> are unchanged.
/// Gate: the same two authentication layers as every AI Dashboard request (MySaleBooks JWT → tenant from the token;
/// existing AYAAN session). Changes additionally require an AYAAN role listed in TenantAiProviders:ManageRoles.
/// There is no tenant / company / database parameter: the scope comes only from the token.
/// API keys are write-only — accepted on add / update / test, never returned.
/// </summary>
[ApiController]
[Route("api/ai-dashboard/provider-keys")]
[Authorize(Policy = Policies.Chat)]
[EnableRateLimiting("dashboard")]
[TenantProviderExceptionFilter]
public sealed class TenantProvidersController : ControllerBase
{
    private const int MaxBody = 64 * 1024;

    private readonly TenantProviderService _service;
    private readonly IAIDashboardCallerAccessor _caller;
    private readonly IWebHostEnvironment _environment;

    public TenantProvidersController(TenantProviderService service, IAIDashboardCallerAccessor caller, IWebHostEnvironment environment)
    {
        _service = service;
        _caller = caller;
        _environment = environment;
    }

    /// <summary>The company's configurations (credentials masked as "Configured"), what chat uses now, and the provider catalogue.</summary>
    [HttpGet]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<ActionResult<TenantProvidersOverviewDto>> Overview(CancellationToken ct)
        => Ok(await _service.OverviewAsync(Actor(), ct));

    [HttpPost]
    [RequestSizeLimit(MaxBody)]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<ActionResult<TenantProviderSaveResultDto>> Create([FromBody] TenantProviderUpsertRequest request, CancellationToken ct)
    {
        RequireHttps();
        var result = await _service.CreateAsync(Actor(), request, ct);
        if (!result.Saved) return UnprocessableEntity(result); // connection test failed: nothing changed
        return Ok(result);
    }

    [HttpPut("{id}")]
    [RequestSizeLimit(MaxBody)]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<ActionResult<TenantProviderSaveResultDto>> Update(string id, [FromBody] TenantProviderUpsertRequest request, CancellationToken ct)
    {
        RequireHttps();
        var result = await _service.UpdateAsync(Actor(), id, request, ct);
        if (!result.Saved) return UnprocessableEntity(result); // connection test failed: nothing changed
        return Ok(result);
    }

    /// <summary>Removes the configuration and destroys its credential. Conversations and history are kept.</summary>
    [HttpDelete("{id}")]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<IActionResult> Remove(string id, [FromQuery] int? version, CancellationToken ct)
    {
        RequireHttps();
        await _service.RemoveAsync(Actor(), id, version, ct);
        return NoContent();
    }

    [HttpPost("{id}/test")]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<ActionResult<TenantProviderTestResultDto>> Test(string id, CancellationToken ct)
    {
        RequireHttps();
        return Ok(await _service.TestSavedAsync(Actor(), id, ct));
    }

    /// <summary>Tests unsaved settings from the Add / Edit dialog. Nothing is stored.</summary>
    [HttpPost("test")]
    [RequestSizeLimit(MaxBody)]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<ActionResult<TenantProviderTestResultDto>> TestDraft([FromBody] TenantProviderTestRequest request, CancellationToken ct)
    {
        RequireHttps();
        return Ok(await _service.TestDraftAsync(Actor(), request, ct));
    }

    [HttpGet("{id}/models")]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<ActionResult<TenantProviderTestResultDto>> Models(string id, CancellationToken ct)
        => Ok(await _service.ModelsAsync(Actor(), id, ct));

    [HttpPut("{id}/enabled")]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<ActionResult<TenantProviderDto>> SetEnabled(string id, [FromBody] TenantProviderEnableRequest request, CancellationToken ct)
    {
        RequireHttps();
        return Ok(await _service.SetEnabledAsync(Actor(), id, request.Enabled, ct));
    }

    /// <summary>Chooses the provider + model AYAAN chat uses for this company (or, with no id, the AYAAN default again).</summary>
    [HttpPut("active")]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<ActionResult<TenantProvidersOverviewDto>> SetActive([FromBody] TenantProviderDefaultRequest request, CancellationToken ct)
    {
        RequireHttps();
        return Ok(await _service.SetDefaultAsync(Actor(), request, ct));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Caller facts from the validated tokens only (set by <see cref="AIDashboardAuthorizationFilter"/>).</summary>
    private TenantProviderActor Actor()
    {
        var access = HttpContext.GetAIDashboardAccess();
        return new TenantProviderActor
        {
            TenantKey = access.CompanyId,
            TenantRef = access.TenantRef,
            UserId = access.UserId,
            UserName = access.UserName,
            AyaanUserName = access.AyaanUserName,
            AyaanRole = _caller.Current.Ayaan.Role,
            IpAddress = access.IpAddress,
            CorrelationId = access.CorrelationId
        };
    }

    /// <summary>Credentials only travel over HTTPS (TLS may end at the load balancer: X-Forwarded-Proto is honoured).</summary>
    private void RequireHttps()
    {
        if (!_service.Options.RequireHttpsRequests || _environment.IsDevelopment()) return;
        var forwarded = Request.Headers["X-Forwarded-Proto"].ToString();
        if (Request.IsHttps || forwarded.Split(',').Select(p => p.Trim()).FirstOrDefault()?.Equals("https", StringComparison.OrdinalIgnoreCase) == true)
            return;
        throw new TenantProviderAccessException(403, "https_required", "AI provider keys can only be managed over a secure (HTTPS) connection.");
    }
}

/// <summary>Maps the feature's own exceptions to problem responses (user-safe messages, no details).</summary>
public sealed class TenantProviderExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        (int Status, string Code, string Title, string Message)? mapped = context.Exception switch
        {
            TenantProviderAccessException e => (e.StatusCode, e.Code, "Forbidden", e.Message),
            TenantProviderConflictException e => (409, "conflict", "Conflict", e.Message),
            _ => null
        };
        if (mapped is not { } m) return;
        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = m.Status,
            Title = m.Title,
            Detail = m.Message,
            Extensions = { ["code"] = m.Code, ["traceId"] = context.HttpContext.TraceIdentifier }
        })
        {
            StatusCode = m.Status,
            ContentTypes = { "application/problem+json" }
        };
        context.ExceptionHandled = true;
    }
}
