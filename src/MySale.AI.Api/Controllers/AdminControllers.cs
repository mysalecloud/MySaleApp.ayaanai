using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySale.AI.Api.Security;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Api.Controllers;

[ApiController]
[Route("api/ai/providers")]
[Authorize(Policy = Policies.Chat)]
public sealed class ProvidersController : ControllerBase
{
    private readonly ProviderService _service;
    public ProvidersController(ProviderService service) => _service = service;

    [HttpGet]
    public async Task<ActionResult<List<ProviderDto>>> List(CancellationToken ct) => Ok(await _service.ListAsync(ct));

    [HttpGet("kinds")]
    public ActionResult<IReadOnlyList<ProviderKindInfo>> Kinds() => Ok(_service.Kinds);

    [HttpGet("{id}")]
    public async Task<ActionResult<ProviderDto>> Get(string id, CancellationToken ct) => Ok(await _service.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<ProviderDto>> Create([FromBody] ProviderUpsertRequest request, CancellationToken ct)
        => Ok(await _service.CreateAsync(request, ct));

    [HttpPut("{id}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<ProviderDto>> Update(string id, [FromBody] ProviderUpsertRequest request, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, request, ct));

    [HttpDelete("{id}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id}/default")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<ProviderDto>> SetDefault(string id, CancellationToken ct) => Ok(await _service.SetDefaultAsync(id, ct));

    [HttpPost("{id}/test")]
    [Authorize(Policy = Policies.Developer)]
    public async Task<ActionResult<ProviderTestResult>> Test(string id, CancellationToken ct) => Ok(await _service.TestAsync(id, ct));

    [HttpGet("{id}/models")]
    public async Task<ActionResult<IReadOnlyList<AIModel>>> Models(string id, CancellationToken ct) => Ok(await _service.GetModelsAsync(id, ct));

    /// <summary>Well-known models for a provider type (before the provider is saved / tested).</summary>
    [HttpGet("catalog")]
    public ActionResult<IReadOnlyList<AIModel>> Catalog([FromQuery] string kind, [FromQuery] string? baseUrl)
        => Ok(_service.GetCatalog(kind, baseUrl));

    /// <summary>Downloads a model (Ollama). Can take several minutes for large models.</summary>
    [HttpPost("{id}/models/pull")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<object>> Pull(string id, [FromBody] PullModelRequest request, CancellationToken ct)
        => Ok(new { message = await _service.PullModelAsync(id, request.Model, ct) });

    public sealed class PullModelRequest
    {
        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200)]
        public string Model { get; set; } = string.Empty;
    }

    [HttpPost("test-draft")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<ProviderDraftTestResponse>> TestDraft([FromBody] ProviderDraftTestRequest request, CancellationToken ct)
        => Ok(await _service.TestDraftAsync(request, ct));
}

[ApiController]
[Route("api/database")]
[Authorize(Policy = Policies.Chat)]
public sealed class DatabaseController : ControllerBase
{
    private readonly IBusinessDatabaseManager _manager;
    private readonly ISchemaService _schema;
    private readonly SettingsService _settings;
    private readonly IUserContext _user;

    public DatabaseController(IBusinessDatabaseManager manager, ISchemaService schema, SettingsService settings, IUserContext user)
    {
        _manager = manager;
        _schema = schema;
        _settings = settings;
        _user = user;
    }

    [HttpGet("config")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<DatabaseConfigDto>> GetConfig(CancellationToken ct) => Ok(await _manager.GetConfigAsync(ct));

    [HttpPut("config")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<DatabaseConfigDto>> UpdateConfig([FromBody] DatabaseConfigUpdate request, CancellationToken ct)
        => Ok(await _manager.UpdateConfigAsync(request, _user.UserName, ct));

    /// <summary>Tests the saved connection.</summary>
    [HttpPost("test")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<DatabaseTestResult>> Test(CancellationToken ct)
        => Ok(await _manager.TestAsync(null, ct));

    /// <summary>Tests an unsaved connection typed on the Database page.</summary>
    [HttpPost("test-draft")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<DatabaseTestResult>> TestDraft([FromBody] DatabaseConfigUpdate draft, CancellationToken ct)
        => Ok(await _manager.TestAsync(draft, ct));

    [HttpGet("databases")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<List<string>>> Databases(CancellationToken ct) => Ok(await _manager.ListDatabasesAsync(ct));

    [HttpGet("collections")]
    public async Task<ActionResult<List<CollectionInfo>>> Collections(CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        var documented = (await _schema.GetSchemaAsync(true, ct)).Where(s => s.Documented).Select(s => s.Name).ToList();
        return Ok(await _manager.ListCollectionsAsync(settings.Query.AllowedCollections, documented, ct));
    }

    [HttpGet("schema/{collection}")]
    public async Task<ActionResult<SchemaMetadataDto>> GetMetadata(string collection, [FromServices] SchemaMetadataService metadata, CancellationToken ct)
        => Ok(await metadata.GetAsync(collection, ct));

    /// <summary>Drafts descriptions for a collection with the AI from sample documents (not saved).</summary>
    [HttpPost("schema/{collection}/generate")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<GenerateMetadataResponse>> GenerateMetadata(string collection, [FromBody] GenerateMetadataRequest request,
        [FromServices] SchemaMetadataService metadata, CancellationToken ct)
        => Ok(await metadata.GenerateAsync(collection, request, ct));

    [HttpPut("schema/{collection}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<SchemaMetadataDto>> SaveMetadata(string collection, [FromBody] SchemaMetadataDto body,
        [FromServices] SchemaMetadataService metadata, CancellationToken ct)
        => Ok(await metadata.SaveAsync(collection, body, ct));

    [HttpDelete("schema/{collection}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> ResetMetadata(string collection, [FromServices] SchemaMetadataService metadata, CancellationToken ct)
    {
        await metadata.ResetAsync(collection, ct);
        return NoContent();
    }

    /// <summary>Most common tenant values in a collection — used to pick a real company for testing.</summary>
    [HttpGet("tenants")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<List<TenantValueInfo>>> Tenants([FromQuery] string collection, [FromServices] SchemaMetadataService metadata, CancellationToken ct)
        => Ok(await metadata.TenantValuesAsync(collection, ct));

    [HttpGet("schema")]
    public async Task<ActionResult<IReadOnlyList<CollectionSchema>>> Schema(CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        var schema = await _schema.GetSchemaAsync(includeDiscovery: true, ct);
        foreach (var c in schema) c.Allowed = settings.Query.AllowedCollections.Contains(c.Name);
        return Ok(schema.OrderByDescending(c => c.Allowed).ThenBy(c => c.Name).ToList());
    }

    [HttpPost("seed")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<SeedResult>> Seed([FromBody] SeedRequest request, CancellationToken ct)
        => Ok(await _manager.SeedAsync(request.Reset, ct));

    public sealed class SeedRequest
    {
        public bool Reset { get; set; } = true;
    }
}

[ApiController]
[Route("api/query")]
[Authorize(Policy = Policies.Developer)]
public sealed class QueryController : ControllerBase
{
    private readonly QuerySandboxService _sandbox;
    public QueryController(QuerySandboxService sandbox) => _sandbox = sandbox;

    [HttpPost("generate")]
    public async Task<ActionResult<GenerateQueryResponse>> Generate([FromBody] GenerateQueryRequest request, CancellationToken ct)
        => Ok(await _sandbox.GenerateAsync(request, ct));

    [HttpPost("validate")]
    public async Task<ActionResult<ValidationResultDto>> Validate([FromBody] QueryBodyRequest request, CancellationToken ct)
        => Ok(await _sandbox.ValidateAsync(request.Query, ct));

    [HttpPost("execute")]
    public async Task<ActionResult<ExecuteQueryResponse>> Execute([FromBody] QueryBodyRequest request, CancellationToken ct)
        => Ok(await _sandbox.ExecuteAsync(request.Query, ct));
}

[ApiController]
[Route("api")]
[Authorize(Policy = Policies.Chat)]
public sealed class MonitoringController : ControllerBase
{
    private readonly QueryLogService _logs;
    private readonly UsageService _usage;

    public MonitoringController(QueryLogService logs, UsageService usage)
    {
        _logs = logs;
        _usage = usage;
    }

    [HttpGet("logs/queries")]
    public async Task<ActionResult<PagedResult<QueryLogSummaryDto>>> Logs(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? providerId, [FromQuery] string? model,
        [FromQuery] ChatStatus? status, [FromQuery] string? userId, [FromQuery] string? operation, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => Ok(await _logs.SearchAsync(new QueryLogFilter
        {
            From = from, To = to, ProviderId = providerId, Model = model, Status = status, UserId = userId,
            Operation = operation, Search = search, Page = page, PageSize = pageSize
        }, ct));

    [HttpGet("logs/queries/{id}")]
    public async Task<ActionResult<QueryLogDetailDto>> Log(string id, CancellationToken ct) => Ok(await _logs.GetAsync(id, ct));

    [HttpGet("logs/filters")]
    public async Task<ActionResult<LogFiltersDto>> Filters(CancellationToken ct) => Ok(await _logs.GetFiltersAsync(ct));

    [HttpGet("usage")]
    public async Task<ActionResult<UsageReportDto>> Usage([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await _usage.GetReportAsync(days, ct));

    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardDto>> Dashboard(CancellationToken ct) => Ok(await _usage.GetDashboardAsync(ct));
}

[ApiController]
[Route("api/settings")]
[Authorize(Policy = Policies.Chat)]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;
    public SettingsController(SettingsService settings) => _settings = settings;

    [HttpGet]
    public async Task<ActionResult<SettingsDto>> Get(CancellationToken ct) => Ok(await _settings.GetDtoAsync(ct));

    [HttpPut]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<SettingsDto>> Update([FromBody] SettingsDto request, CancellationToken ct)
        => Ok(await _settings.UpdateAsync(request, ct));
}
