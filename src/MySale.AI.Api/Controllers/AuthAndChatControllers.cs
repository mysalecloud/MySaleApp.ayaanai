using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MySale.AI.Api.Infrastructure;
using MySale.AI.Api.Security;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Services;
using MySale.AI.Infrastructure;

namespace MySale.AI.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly IUserContext _user;
    private readonly AuthOptions _options;

    public AuthController(AuthService auth, IUserContext user, IOptions<AuthOptions> options)
    {
        _auth = auth;
        _user = user;
        _options = options.Value;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        if (result is null)
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Login failed", Detail = "Invalid user name or password." });
        return Ok(result);
    }

    [HttpGet("me")]
    [Authorize(Policy = Policies.Chat)]
    public ActionResult<UserDto> Me() => Ok(AuthService.FromContext(_user));

    /// <summary>Prototype: test real data by switching the admin's company. Returns a new token.</summary>
    [HttpPost("switch-company")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<LoginResponse>> SwitchCompany([FromBody] SwitchCompanyRequest request, CancellationToken ct)
        => Ok(await _auth.SwitchCompanyAsync(_user.UserId, request, ct));

    [HttpGet("demo-users")]
    [AllowAnonymous]
    public async Task<ActionResult<List<DemoUserDto>>> DemoUsers(CancellationToken ct)
    {
        if (!_options.ExposeDemoUsers) return NotFound();
        return Ok(await _auth.DemoUsersAsync(ct));
    }
}

[ApiController]
[Route("api/ai")]
[Authorize(Policy = Policies.Chat)]
public sealed class ChatController : ControllerBase
{
    private readonly AIAgentOrchestrator _orchestrator;
    private readonly IUserContext _user;
    private readonly JsonSerializerOptions _json;

    public ChatController(AIAgentOrchestrator orchestrator, IUserContext user, IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> json)
    {
        _orchestrator = orchestrator;
        _user = user;
        _json = json.Value.JsonSerializerOptions;
    }

    /// <summary>
    /// Predefined (quick) questions with their ids, exact messages, categories and canonical intents — the catalogue the
    /// clients' quick-question buttons use (send the id as <c>presetId</c> with the exact message). Used by the audit script.
    /// </summary>
    [HttpGet("presets")]
    public ActionResult<IReadOnlyList<PresetQuestion>> Presets() => Ok(PresetQuestions.All);

    /// <summary>
    /// Entity-mapping diagnostics (ids → names) for data-integrity review: per verified MySaleBooks reference, how many
    /// distinct ids the company database uses, how many resolve to a master record, and the orphans. Admin / tester only.
    /// </summary>
    [HttpGet("diagnostics/entity-references")]
    public async Task<IActionResult> EntityReferences(CancellationToken ct)
    {
        if (TenantProblem() is { } problem) return problem;
        var checks = await _orchestrator.EntityReferenceDiagnosticsAsync(ct);
        if (checks is null) return StatusCode(403, new ProblemDetails { Status = 403, Title = "Not allowed", Detail = "Diagnostics are available to administrators and testers only." });
        return Ok(checks);
    }

    /// <summary>
    /// Full pipeline: question → MQL → validation → MongoDB → answer.
    /// MySaleBooks users: Authorization: Bearer &lt;existing JWT&gt; → validated → "dbName" claim → customer database.
    /// </summary>
    [HttpPost("chat")]
    [EnableRateLimiting("chat")]
    public async Task<ActionResult<ChatResponse>> Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        if (TenantProblem() is { } problem) return problem;
        var response = await _orchestrator.RunAsync(request, NullChatEventSink.Instance, ct);
        AddTenantDebug(response);
        return Ok(response);
    }

    /// <summary>A valid MySaleBooks JWT without a usable "dbName" claim never reaches MongoDB — and never falls back.</summary>
    private ObjectResult? TenantProblem()
    {
        if (!_user.IsMySaleBooksUser || !string.IsNullOrEmpty(_user.DatabaseName)) return null;
        return BadRequest(new ProblemDetails
        {
            Status = 400,
            Title = "Customer database not resolved",
            Detail = TenantResolutionException.UserMessage,
            Extensions = { ["code"] = "TenantDatabaseMissing" }
        });
    }

    /// <summary>Only when the developer trace is returned (Settings → Developer mode). Never the JWT or credentials.</summary>
    private void AddTenantDebug(ChatResponse response)
    {
        if (response.Debug is null || !_user.IsMySaleBooksUser) return;
        response.Tenant = new TenantDebugDto
        {
            JwtValid = true,
            DatabaseResolved = !string.IsNullOrEmpty(_user.DatabaseName),
            DatabaseName = _user.DatabaseName,
            CollectionsUsed = string.IsNullOrEmpty(response.Query.Collection) ? new() : new() { response.Query.Collection! }
        };
    }

    /// <summary>Same pipeline streamed as Server-Sent Events: status, query, token…, done | error.</summary>
    [HttpPost("chat/stream")]
    [EnableRateLimiting("chat")]
    public async Task Stream([FromBody] ChatRequest request, CancellationToken ct)
    {
        if (TenantProblem() is { } problem)
        {
            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(problem.Value, ct);
            return;
        }
        SseChatEventSink.Prepare(Response);
        var sink = new SseChatEventSink(Response, _json);
        try
        {
            var result = await _orchestrator.RunAsync(request, sink, ct);
            AddTenantDebug(result);
            await sink.WriteAsync("done", result, CancellationToken.None);
        }
        catch (ChatConversationException ex)
        {
            await sink.WriteAsync("error", new { code = ex.Code, message = ex.Message }, CancellationToken.None);
        }
        catch (NotFoundException ex)
        {
            await sink.WriteAsync("error", new { code = "NotFound", message = ex.Message }, CancellationToken.None);
        }
        catch (AppValidationException ex)
        {
            await sink.WriteAsync("error", new { code = "Invalid", message = ex.Message }, CancellationToken.None);
        }
        catch (AttachmentException ex)
        {
            await sink.WriteAsync("error", new { code = "Attachment", message = ex.Message }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<ChatController>>().LogError(ex, "Streaming chat failed");
            await sink.WriteAsync("error", new { code = "Error", message = UserMessages.Unexpected }, CancellationToken.None);
        }
    }
}

[ApiController]
[Route("api/test-questions")]
[Authorize(Policy = Policies.Chat)]
public sealed class TestQuestionsController : ControllerBase
{
    [HttpGet]
    public ActionResult<List<TestQuestionGroup>> Get() => Ok(TestQuestionCatalog.Groups);
}

[ApiController]
[Route("api/ai/conversations")]
[Authorize(Policy = Policies.Chat)]
public sealed class ConversationsController : ControllerBase
{
    private readonly ConversationService _service;
    public ConversationsController(ConversationService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<List<ConversationSummaryDto>>> List([FromQuery] string? search, [FromQuery] bool? archived, CancellationToken ct)
        => Ok(await _service.ListAsync(search, archived, ct));

    [HttpPost]
    public async Task<ActionResult<ConversationSummaryDto>> Create([FromBody] CreateConversationRequest request, CancellationToken ct)
        => Ok(await _service.CreateAsync(request.Title, ct));

    /// <summary>Conversation history. Customers get answers/data only — never MQL, collections or internal ids.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<ConversationDetailDto>> Get(string id, [FromServices] IUserContext user,
        [FromServices] MySale.AI.Application.ActivityTracking.ActivityOptions activity, CancellationToken ct)
    {
        var detail = await _service.GetAsync(id, ct);
        if (!TechnicalDetailsPolicy.CanSeeTechnicalDetails(user, activity.AllowMySaleBooksAdmins))
            foreach (var m in detail.Messages) TechnicalDetailsPolicy.ForCustomer(m);
        return Ok(detail);
    }

    /// <summary>
    /// Active intent and open clarification question of the conversation (restores option buttons after a reload).
    /// Owner-scoped: tenant, user and customer database come from the validated token, never from the request.
    /// </summary>
    [HttpGet("{id}/context")]
    public async Task<ActionResult<ConversationContextDto>> Context(string id, CancellationToken ct)
        => Ok(await _service.GetContextAsync(id, ct));

    [HttpPatch("{id}")]
    public async Task<ActionResult<ConversationSummaryDto>> Update(string id, [FromBody] UpdateConversationRequest request, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, request, ct));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpDelete("{id}/messages")]
    public async Task<IActionResult> Clear(string id, CancellationToken ct)
    {
        await _service.ClearAsync(id, ct);
        return NoContent();
    }
}
