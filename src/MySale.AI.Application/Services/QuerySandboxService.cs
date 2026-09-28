using System.Diagnostics;
using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;

namespace MySale.AI.Application.Services;

/// <summary>Developer endpoints (/api/query/*) to test each step of the NL → MQL pipeline in isolation.</summary>
public sealed class QuerySandboxService
{
    private readonly ProviderService _providers;
    private readonly SettingsService _settings;
    private readonly QueryEngine _engine;
    private readonly PromptBuilder _prompts;
    private readonly IUserContext _user;
    private readonly TimeProvider _time;
    private readonly AuditService _audit;

    public QuerySandboxService(ProviderService providers, SettingsService settings, QueryEngine engine, PromptBuilder prompts,
        IUserContext user, TimeProvider time, AuditService audit)
    {
        _providers = providers;
        _settings = settings;
        _engine = engine;
        _prompts = prompts;
        _user = user;
        _time = time;
        _audit = audit;
    }

    public async Task<GenerateQueryResponse> GenerateAsync(GenerateQueryRequest request, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        var provider = await _providers.ResolveAsync(request.ProviderId, request.Model, settings, ct);
        var schema = await _engine.GetAllowedSchemaAsync(settings, ct);
        var anchors = DateAnchors.Compute(_time.GetUtcNow().UtcDateTime, _user.TimeZone);
        // Same date handling as the chat: typed dates resolved server-side, literals read in the business time zone.
        var questionDates = QuestionDates.Parse(request.Question, anchors.LocalToday);
        var ctx = new PromptContext(_user.CompanyName, _user.Currency, _user.TimeZone, anchors, settings.Query.MaxRecords, _engine.TenantField)
        {
            QuestionDates = questionDates
        };
        var messages = _prompts.BuildQueryMessages(ctx, schema, Array.Empty<ChatMessage>(), request.Question);

        var sw = Stopwatch.StartNew();
        var ai = await provider.Provider.GenerateQueryAsync(new AIChatRequest
        {
            Messages = messages,
            Model = provider.Model,
            Temperature = provider.Config.Temperature,
            MaxTokens = provider.Config.MaxTokens,
            JsonMode = true
        }, ct);
        sw.Stop();

        var response = new GenerateQueryResponse
        {
            RawOutput = ai.Text,
            Provider = new ProviderRefDto(provider.Config.Id, provider.Config.Name, provider.Config.Kind, provider.Model),
            Usage = new UsageDto
            {
                InputTokens = ai.InputTokens,
                OutputTokens = ai.OutputTokens,
                EstimatedCost = provider.EstimateCost(ai.InputTokens, ai.OutputTokens)
            },
            DurationMs = sw.ElapsedMilliseconds,
            Prompt = PromptBuilder.Render(messages)
        };

        var parsed = MqlParser.Parse(ai.Text);
        if (!parsed.Success)
        {
            response.ParseError = parsed.Error;
            return response;
        }
        response.Parsed = parsed.Query!.ToJson();
        if (!parsed.Query.IsUnsupported && !parsed.Query.IsClarification)
            response.Validation = ToDto(_engine.Prepare(parsed.Query, _engine.CreateContext(schema, settings, null,
                new DateCoercionContext
                {
                    TimeZone = DateAnchors.ResolveTimeZone(_user.TimeZone),
                    InclusiveEndDays = questionDates.InclusiveEndDays,
                    MentionedDays = questionDates.MentionedDays
                }, request.Question), settings));
        return response;
    }

    public async Task<ValidationResultDto> ValidateAsync(JsonObject queryJson, CancellationToken ct)
    {
        var (prepared, error) = await PrepareAsync(queryJson, ct);
        return prepared is null ? new ValidationResultDto { IsValid = false, Errors = { error! } } : ToDto(prepared);
    }

    public async Task<ExecuteQueryResponse> ExecuteAsync(JsonObject queryJson, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        var (prepared, error) = await PrepareAsync(queryJson, ct);
        if (prepared is null)
            return new ExecuteQueryResponse { Validation = new ValidationResultDto { IsValid = false, Errors = { error! } } };

        var response = new ExecuteQueryResponse { Validation = ToDto(prepared) };
        if (!prepared.IsExecutable)
        {
            if (prepared.Validation.Blocked)
                await _audit.LogAsync("QueryBlocked", "Sandbox: " + string.Join("; ", prepared.Validation.Errors), CancellationToken.None);
            return response;
        }

        try
        {
            var executed = await _engine.ExecuteAsync(prepared, settings, ct);
            response.Rows = new JsonArray(executed.Rows.Select(r => (JsonNode?)r.DeepClone()).ToArray());
            response.ResultCount = executed.Rows.Count;
            response.Truncated = executed.Truncated;
            response.ExecutionTimeMs = executed.ElapsedMs;
        }
        catch (QueryTimeoutException)
        {
            response.Error = UserMessages.QueryTimeout;
        }
        catch (QueryExecutionException ex)
        {
            response.Error = UserMessages.DatabaseError + " " + ex.Message;
        }
        return response;
    }

    private async Task<(PreparedQuery? Prepared, string? Error)> PrepareAsync(JsonObject queryJson, CancellationToken ct)
    {
        var parsed = MqlParser.Parse(queryJson.ToJsonString());
        if (!parsed.Success) return (null, parsed.Error);
        if (parsed.Query!.IsUnsupported || parsed.Query.IsClarification) return (null, "The query is marked as unsupported.");

        var settings = await _settings.GetAsync(ct);
        var schema = await _engine.GetAllowedSchemaAsync(settings, ct);
        var dates = new DateCoercionContext { TimeZone = DateAnchors.ResolveTimeZone(_user.TimeZone) };
        return (_engine.Prepare(parsed.Query, _engine.CreateContext(schema, settings, null, dates), settings), null);
    }

    private static ValidationResultDto ToDto(PreparedQuery p) => new()
    {
        IsValid = p.Validation.IsValid,
        Errors = p.Validation.Errors.ToList(),
        Warnings = p.Validation.Warnings.ToList(),
        Mql = p.Mql,
        NormalizedPipeline = p.Validation.Pipeline?.DeepClone() as JsonArray,
        ScopedPipeline = p.ScopedPipeline?.DeepClone() as JsonArray
    };
}
