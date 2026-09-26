using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySale.AI.Api.Security;
using MySale.AI.Application.ActivityTracking;
using MySale.AI.Application.Contracts;
using MySale.AI.Domain;

namespace MySale.AI.Api.Controllers;

/// <summary>
/// AYAAN AI activity log — developer/admin only, read-only. There are deliberately no update or delete endpoints:
/// records are removed only by the retention job. Records never contain tokens, keys or connection strings.
/// </summary>
[ApiController]
[Route("api/admin/activity")]
[Authorize(Policy = Policies.ActivityViewer)]
public sealed class ActivityController : ControllerBase
{
    private readonly ActivityQueryService _service;
    public ActivityController(ActivityQueryService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<PagedResult<ActivitySummaryDto>>> Search(
        [FromQuery] string? search, [FromQuery] string? activityId, [FromQuery] string? conversationId, [FromQuery] string? correlationId,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? status, [FromQuery] string? failedStage,
        [FromQuery] string? provider, [FromQuery] string? model, [FromQuery] string? tenantRef,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => Ok(await _service.SearchAsync(new ActivityFilter
        {
            Search = search, ActivityId = activityId, ConversationId = conversationId, CorrelationId = correlationId,
            From = from, To = to, Status = status, FailedStage = failedStage, Provider = provider, Model = model,
            TenantRef = tenantRef, Page = page, PageSize = pageSize
        }, ct));

    [HttpGet("filters")]
    public async Task<ActionResult<ActivityFiltersDto>> Filters(CancellationToken ct) => Ok(await _service.FiltersAsync(ct));

    [HttpGet("stats")]
    public async Task<ActionResult<ActivityStatsDto>> Stats([FromQuery] int days = 7, CancellationToken ct = default)
        => Ok(await _service.StatsAsync(days, ct));

    [HttpGet("conversations")]
    public async Task<ActionResult<PagedResult<AIConversationActivity>>> Conversations(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => Ok(await _service.ConversationsAsync(search, page, pageSize, ct));

    [HttpGet("conversations/{conversationId}")]
    public async Task<ActionResult<ActivityConversationDetailDto>> Conversation(string conversationId, CancellationToken ct)
        => Ok(await _service.ConversationAsync(conversationId, ct));

    /// <summary>Full activity: question → intent → generated MQL → validation → MongoDB → result → answer → timings.</summary>
    [HttpGet("{activityId}")]
    public async Task<ActionResult<AIActivity>> Get(string activityId, CancellationToken ct)
        => Ok(await _service.GetAsync(activityId, ct));
}
