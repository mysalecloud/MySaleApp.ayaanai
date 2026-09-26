using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;

namespace MySale.AI.Infrastructure.BusinessData;

/// <summary>
/// Executes validated, tenant-scoped pipelines against the business DB with a server-side time limit
/// (maxTimeMS), a client-side cancellation deadline and a hard document cap.
/// </summary>
public sealed class MongoQueryExecutor : IQueryExecutor
{
    private readonly IBusinessDatabase _business;
    private readonly ILogger<MongoQueryExecutor> _logger;

    public MongoQueryExecutor(IBusinessDatabase business, ILogger<MongoQueryExecutor> logger)
    {
        _business = business;
        _logger = logger;
    }

    public async Task<QueryExecutionResult> ExecuteAsync(string collection, JsonArray pipeline, QueryExecutionOptions options, CancellationToken ct)
    {
        List<BsonDocument> stages;
        try
        {
            var array = BsonSerializer.Deserialize<BsonArray>(pipeline.ToJsonString());
            stages = array.Select(s => s.AsBsonDocument).ToList();
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or BsonSerializationException)
        {
            throw new QueryExecutionException("The query could not be converted to BSON: " + ex.Message, ex);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.TimeoutMs + 3000);
        var sw = Stopwatch.StartNew();

        try
        {
            var db = await _business.GetDatabaseAsync(deadline.Token);
            var coll = db.GetCollection<BsonDocument>(collection);
            var aggregateOptions = new AggregateOptions
            {
                MaxTime = TimeSpan.FromMilliseconds(options.TimeoutMs),
                AllowDiskUse = false,
                BatchSize = Math.Min(options.MaxDocuments, 1000)
            };
            if (!string.IsNullOrWhiteSpace(options.Comment)) aggregateOptions.Comment = new BsonString(options.Comment);

            var rows = new List<JsonObject>();
            using var cursor = await coll.AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(stages), aggregateOptions, deadline.Token);
            while (rows.Count < options.MaxDocuments && await cursor.MoveNextAsync(deadline.Token))
            {
                foreach (var doc in cursor.Current)
                {
                    rows.Add(BsonJsonConverter.ToJsonObject(doc));
                    if (rows.Count >= options.MaxDocuments) break;
                }
            }
            sw.Stop();
            return new QueryExecutionResult { Rows = rows, ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (MongoExecutionTimeoutException ex)
        {
            throw new QueryTimeoutException($"MongoDB exceeded the {options.TimeoutMs} ms time limit.", ex);
        }
        catch (MongoCommandException ex) when (ex.Code == 50)
        {
            throw new QueryTimeoutException($"MongoDB exceeded the {options.TimeoutMs} ms time limit.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new QueryTimeoutException($"MongoDB did not respond within {options.TimeoutMs} ms.", ex);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Business database unreachable");
            throw new QueryExecutionException("Cannot connect to the business database.", ex);
        }
        catch (MongoCommandException ex)
        {
            _logger.LogInformation("MongoDB rejected query on {Collection}: {Message}", collection, ex.ErrorMessage);
            throw new QueryExecutionException("MongoDB error: " + ex.ErrorMessage, ex);
        }
        catch (MongoException ex)
        {
            _logger.LogWarning(ex, "MongoDB error on {Collection}", collection);
            throw new QueryExecutionException("MongoDB error: " + ex.Message, ex);
        }
    }
}
