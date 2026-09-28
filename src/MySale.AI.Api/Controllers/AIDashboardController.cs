using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MySale.AI.Api.Security;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.AIDashboard;
using MySale.AI.Application.Contracts;
using MySale.AI.Domain;

namespace MySale.AI.Api.Controllers;

/// <summary>
/// AIDashboard section of MySaleBooks. Every action is gated server-side by <see cref="AIDashboardAuthorizeAttribute"/>:
/// MySaleBooks JWT → tenant from the token (dbName) → existing AYAAN Dashboard session (X-Ayaan-Session). No permission check.
/// There is no tenantId / companyId / dbName parameter anywhere: the scope comes only from the token.
/// Read-only: providers and settings are shared by all companies and cannot be changed from here.
/// </summary>
[ApiController]
[Route("api/ai-dashboard")]
[Authorize(Policy = Policies.Chat)]
[EnableRateLimiting("dashboard")]
public sealed class AIDashboardController : ControllerBase
{
    private readonly AIDashboardService _service;
    private readonly AIDashboardInsightsService _insights;
    private readonly AIDashboardOptions _options;

    public AIDashboardController(AIDashboardService service, AIDashboardInsightsService insights, AIDashboardOptions options)
    {
        _service = service;
        _insights = insights;
        _options = options;
    }

    private AIDashboardAccess Access => HttpContext.GetAIDashboardAccess();

    /// <summary>Access summary (sections, idle timeout, AYAAN account). 401 without the MySaleBooks and AYAAN sessions.</summary>
    [HttpGet("access")]
    [AIDashboardAuthorize(AIDashboardPermissions.View, auditDenied: false)]
    public ActionResult<AIDashboardAccessDto> GetAccess() => Ok(AIDashboardService.ToAccessDto(Access, _options));

    [HttpGet("overview")]
    [AIDashboardAuthorize(AIDashboardPermissions.View)]
    public async Task<ActionResult<AIDashboardOverviewDto>> Overview([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await _service.OverviewAsync(Access, days, ct));

    [HttpGet("conversations")]
    [AIDashboardAuthorize(AIDashboardPermissions.Conversations)]
    public async Task<ActionResult<PagedResult<AIDashboardConversationDto>>> Conversations(
        [FromQuery] string? search, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => Ok(await _service.ConversationsAsync(Access, search, from, to, page, pageSize, ct));

    [HttpGet("conversations/{id}")]
    [AIDashboardAuthorize(AIDashboardPermissions.Conversations)]
    public async Task<ActionResult<AIDashboardConversationDetailDto>> Conversation(string id, CancellationToken ct)
        => Ok(await _service.ConversationAsync(Access, id, ct));

    [HttpGet("query-logs")]
    [AIDashboardAuthorize(AIDashboardPermissions.QueryLogs)]
    public async Task<ActionResult<PagedResult<AIDashboardQueryLogDto>>> QueryLogs(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] ChatStatus? status, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => Ok(await _service.QueryLogsAsync(Access, new QueryLogFilter
        {
            From = from, To = to, Status = status, Search = search, Page = page, PageSize = pageSize
        }, ct));

    [HttpGet("query-logs/{id}")]
    [AIDashboardAuthorize(AIDashboardPermissions.QueryLogs)]
    public async Task<ActionResult<AIDashboardQueryLogDetailDto>> QueryLog(string id, CancellationToken ct)
        => Ok(await _service.QueryLogAsync(Access, id, ct));

    [HttpGet("activity")]
    [AIDashboardAuthorize(AIDashboardPermissions.Activity)]
    public async Task<ActionResult<PagedResult<AIDashboardActivityDto>>> Activity(
        [FromQuery] string? search, [FromQuery] string? conversationId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? status, [FromQuery] string? requestId, [FromQuery] string? userId, [FromQuery] string? action,
        [FromQuery] string? inputType, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => Ok(await _service.ActivityAsync(Access, new ActivityFilter
        {
            Search = search, ConversationId = conversationId, ActivityId = requestId, From = from, To = to, Status = status,
            UserId = userId, Action = action, InputType = inputType, Page = page, PageSize = pageSize
        }, ct));

    /// <summary>Filter options (users, actions, input types) from this company's own activity.</summary>
    [HttpGet("activity/filters")]
    [AIDashboardAuthorize(AIDashboardPermissions.Activity)]
    public async Task<ActionResult<AIDashboardActivityFiltersDto>> ActivityFilters(CancellationToken ct)
        => Ok(await _insights.ActivityFiltersAsync(Access, ct));

    [HttpGet("activity/{requestId}")]
    [AIDashboardAuthorize(AIDashboardPermissions.Activity)]
    public async Task<ActionResult<AIDashboardActivityDetailDto>> ActivityDetail(string requestId, CancellationToken ct)
        => Ok(await _service.ActivityDetailAsync(Access, requestId, ct));

    [HttpGet("usage")]
    [AIDashboardAuthorize(AIDashboardPermissions.Usage)]
    public async Task<ActionResult<UsageReportDto>> Usage([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await _service.UsageAsync(Access, days, ct));

    [HttpGet("models")]
    [AIDashboardAuthorize(AIDashboardPermissions.Models)]
    public async Task<ActionResult<List<AIDashboardModelDto>>> Models([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await _insights.ModelsAsync(Access, days, ct));

    [HttpGet("agent")]
    [AIDashboardAuthorize(AIDashboardPermissions.Agent)]
    public async Task<ActionResult<AIDashboardAgentDto>> Agent([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await _insights.AgentAsync(Access, days, ct));

    [HttpGet("schema")]
    [AIDashboardAuthorize(AIDashboardPermissions.Schema)]
    public async Task<ActionResult<AIDashboardSchemaDto>> Schema(CancellationToken ct)
        => Ok(await _insights.SchemaAsync(Access, ct));

    [HttpGet("security")]
    [AIDashboardAuthorize(AIDashboardPermissions.Security)]
    public async Task<ActionResult<AIDashboardSecurityDto>> Security([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await _insights.SecurityAsync(Access, days, ct));

    [HttpGet("audit-log")]
    [AIDashboardAuthorize(AIDashboardPermissions.Security)]
    public async Task<ActionResult<PagedResult<AIDashboardAuditDto>>> AuditLog(
        [FromQuery] string? search, [FromQuery] string? action, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => Ok(await _insights.AuditLogAsync(Access, new AuditLogFilter
        {
            Search = search, Action = action, From = from, To = to, Page = page, PageSize = pageSize
        }, ct));

    [HttpGet("providers")]
    [AIDashboardAuthorize(AIDashboardPermissions.Providers)]
    public async Task<ActionResult<List<AIDashboardProviderDto>>> Providers(CancellationToken ct)
        => Ok(await _service.ProvidersAsync(Access, ct));

    [HttpGet("settings")]
    [AIDashboardAuthorize(AIDashboardPermissions.Settings)]
    public async Task<ActionResult<AIDashboardSettingsDto>> Settings(CancellationToken ct)
        => Ok(await _service.SettingsAsync(Access, ct));
}
