using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Mql;
using MySale.AI.Domain;

namespace MySale.AI.Application.Agent;

public sealed class PreparedQuery
{
    public required MqlQuery Query { get; init; }
    public required MqlValidationResult Validation { get; init; }
    public JsonArray? ScopedPipeline { get; init; }
    public string? Mql { get; init; }
    public bool IsExecutable => Validation.IsValid && ScopedPipeline is not null;
}

public sealed class ExecutedQuery
{
    public List<JsonObject> Rows { get; init; } = new();
    public List<string> Columns { get; init; } = new();
    public bool Truncated { get; init; }
    public long ElapsedMs { get; init; }
}

/// <summary>
/// The MQL pipeline shared by the chat orchestrator and the query sandbox:
/// schema → validation → date coercion → tenant scoping → execution → shaping.
/// </summary>
public sealed class QueryEngine
{
    private readonly ISchemaService _schema;
    private readonly MqlValidator _validator;
    private readonly TenantQueryGuard _guard;
    private readonly TenantOptions _tenant;
    private readonly IQueryExecutor _executor;
    private readonly IUserContext _user;

    public QueryEngine(ISchemaService schema, MqlValidator validator, TenantQueryGuard guard, TenantOptions tenant,
        IQueryExecutor executor, IUserContext user)
    {
        _schema = schema;
        _validator = validator;
        _guard = guard;
        _tenant = tenant;
        _executor = executor;
        _user = user;
    }

    public string TenantField => _tenant.FieldName;

    public async Task<IReadOnlyList<CollectionSchema>> GetAllowedSchemaAsync(AppSettings settings, CancellationToken ct)
    {
        var all = await _schema.GetSchemaAsync(includeDiscovery: true, ct);
        var allowed = settings.Query.AllowedCollections;
        var result = all
            .Where(c => allowed.Count == 0 || allowed.Contains(c.Name, StringComparer.Ordinal))
            .ToList();
        // Hidden fields are neither shown to the AI nor accepted by the validator.
        foreach (var c in result) c.Fields = c.Fields.Where(f => !f.Hidden).ToList();
        return result;
    }

    public MqlValidationContext CreateContext(IReadOnlyList<CollectionSchema> schema, AppSettings settings) => new()
    {
        Collections = schema,
        TenantField = _tenant.FieldName,
        MaxStages = settings.Query.MaxPipelineStages,
        MaxRecords = settings.Query.MaxRecords
    };

    public PreparedQuery Prepare(MqlQuery query, MqlValidationContext context, AppSettings settings)
    {
        var validation = _validator.Validate(query, context);
        if (!validation.IsValid || validation.Pipeline is null || validation.Collection is null)
            return new PreparedQuery { Query = query, Validation = validation };

        var pipeline = (JsonArray)validation.Pipeline.DeepClone();
        var bySchema = context.Collections.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Only convert "2026-09-24"-style values to real dates for fields that are NOT stored as text.
        var textFields = bySchema.TryGetValue(validation.Collection, out var rootSchema)
            ? rootSchema.Fields.Where(f => f.Type is "string").Select(f => f.Name).ToHashSet(StringComparer.Ordinal)
            : null;
        MqlDateCoercer.Coerce(pipeline, textFields);
        TenantBinding? Resolve(string collection)
        {
            if (!bySchema.TryGetValue(collection, out var c)) return _guard.Default;
            if (string.IsNullOrEmpty(c.TenantField)) return null; // shared collection
            var isObjectId = c.TenantValueType switch { "objectId" => true, "string" => false, _ => _tenant.ValueIsObjectId };
            return new TenantBinding(c.TenantField, isObjectId);
        }
        // MySaleBooks JWT users without a company claim: the customer database itself is the tenant boundary
        // (resolved from the JWT "dbName" claim), so no company filter is added — limits still apply.
        var scoped = _user.TenantIsDatabase
            ? _guard.Apply(pipeline, _user.CompanyId, settings.Query.MaxRecords, validation.Collection, _ => null)
            : _guard.Apply(pipeline, _user.CompanyId, settings.Query.MaxRecords, validation.Collection, Resolve);
        return new PreparedQuery
        {
            Query = query,
            Validation = validation,
            ScopedPipeline = scoped,
            Mql = MqlFormatter.ToShell(validation.Collection, scoped)
        };
    }

    public async Task<ExecutedQuery> ExecuteAsync(PreparedQuery prepared, AppSettings settings, CancellationToken ct)
    {
        if (!prepared.IsExecutable)
            throw new InvalidOperationException("The query has not passed validation.");

        var max = settings.Query.MaxRecords;
        var result = await _executor.ExecuteAsync(
            prepared.Validation.Collection!,
            prepared.ScopedPipeline!,
            new QueryExecutionOptions { MaxDocuments = max + 1, TimeoutMs = settings.Query.QueryTimeoutMs },
            ct);

        var truncated = result.Rows.Count > max;
        var (rows, columns) = ResultShaper.Shape(result.Rows.Take(max));
        return new ExecutedQuery { Rows = rows, Columns = columns, Truncated = truncated, ElapsedMs = result.ElapsedMs };
    }
}
