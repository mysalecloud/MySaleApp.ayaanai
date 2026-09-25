using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Mql;
using MySale.AI.Infrastructure;
using MySale.AI.Infrastructure.BusinessData;
using MySale.AI.Infrastructure.Persistence;
using MySale.AI.Infrastructure.Seeding;

namespace MySale.AI.Tests;

/// <summary>Runs only when MYSALE_AI_TEST_MONGO is set, e.g. mongodb://localhost:27017</summary>
public sealed class MongoFactAttribute : FactAttribute
{
    public MongoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYSALE_AI_TEST_MONGO")))
            Skip = "Set MYSALE_AI_TEST_MONGO to run MongoDB integration tests.";
    }
}

public sealed class MongoFixture : IAsyncLifetime
{
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("MYSALE_AI_TEST_MONGO") ?? "mongodb://localhost:27017";
    public string DatabaseName { get; } = "mysale_ai_it_" + Guid.NewGuid().ToString("N")[..8];
    public IQueryExecutor Executor { get; private set; } = null!;
    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYSALE_AI_TEST_MONGO"))) return;

        Database = BusinessDatabase.CreateClient(ConnectionString).GetDatabase(DatabaseName);
        await new SampleDataSeeder(TimeProvider.System).SeedAsync(Database, reset: true, default);

        var systemDb = new SystemDbContext(Options.Create(new SystemDbOptions { ConnectionString = ConnectionString, DatabaseName = DatabaseName + "_sys" }));
        var business = new BusinessDatabase(new DatabaseConfigStore(systemDb), new PlainSecrets(),
            Options.Create(new BusinessDbOptions { ConnectionString = ConnectionString, DatabaseName = DatabaseName }),
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        Executor = new MongoQueryExecutor(business, NullLogger<MongoQueryExecutor>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (Database is null) return;
        await Database.Client.DropDatabaseAsync(DatabaseName);
        await Database.Client.DropDatabaseAsync(DatabaseName + "_sys");
    }
}

public class MongoIntegrationTests : IClassFixture<MongoFixture>
{
    private readonly MongoFixture _fx;
    private readonly TenantQueryGuard _guard = new(new TenantOptions());

    public MongoIntegrationTests(MongoFixture fx) => _fx = fx;

    private async Task<List<JsonObject>> Run(string companyId, string collection, string pipelineJson, int max = 200, int timeoutMs = 5000)
    {
        var pipeline = JsonNode.Parse(pipelineJson)!.AsArray();
        MqlDateCoercer.Coerce(pipeline);
        var scoped = _guard.Apply(pipeline, companyId, max);
        var result = await _fx.Executor.ExecuteAsync(collection, scoped, new QueryExecutionOptions { MaxDocuments = max + 1, TimeoutMs = timeoutMs }, default);
        return result.Rows;
    }

    [MongoFact]
    public async Task Seeded_data_has_expected_volumes()
    {
        Assert.Equal(1200, await _fx.Database.GetCollection<MongoDB.Bson.BsonDocument>("Sales").CountDocumentsAsync(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty));
        Assert.Equal(500, await _fx.Database.GetCollection<MongoDB.Bson.BsonDocument>("Items").CountDocumentsAsync(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty));
        Assert.Equal(100, await _fx.Database.GetCollection<MongoDB.Bson.BsonDocument>("Customers").CountDocumentsAsync(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty));
    }

    [MongoFact]
    public async Task Tenant_guard_isolates_companies()
    {
        var a = await Run(TestData.CompanyA, "Sales", """[{"$group":{"_id":null,"n":{"$sum":1}}}]""");
        var b = await Run(TestData.CompanyB, "Sales", """[{"$group":{"_id":null,"n":{"$sum":1}}}]""");
        Assert.Equal(500, a[0]["n"]!.GetValue<int>());
        Assert.Equal(400, b[0]["n"]!.GetValue<int>());
    }

    [MongoFact]
    public async Task Lookup_cannot_reach_other_company_documents()
    {
        // Join Company A sales to customers — every joined customer must belong to Company A.
        var rows = await Run(TestData.CompanyA, "Sales", """
            [{"$lookup":{"from":"Customers","localField":"CustomerId","foreignField":"_id","as":"c"}},
             {"$unwind":"$c"},{"$group":{"_id":"$c.CompanyId"}}]
            """);
        Assert.Single(rows);
        Assert.Equal(TestData.CompanyA, rows[0]["_id"]!.GetValue<string>());
    }

    [MongoFact]
    public async Task Iso_dates_match_and_today_has_sales()
    {
        var today = DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd");
        var rows = await Run(TestData.CompanyA, "Sales", """[{"$match":{"InvoiceDate":{"$gte":"__DATE__"}}},{"$count":"n"}]""".Replace("__DATE__", today));
        Assert.True(rows.Count == 1 && rows[0]["n"]!.GetValue<int>() > 0);
    }

    [MongoFact]
    public async Task Result_size_is_capped()
    {
        var rows = await Run(TestData.CompanyA, "SaleItems", """[{"$project":{"_id":0,"ItemName":1}}]""", max: 25);
        Assert.Equal(26, rows.Count); // max + 1 lets the engine detect truncation
    }

    [MongoFact]
    public async Task No_results_returns_empty()
    {
        var rows = await Run(TestData.CompanyA, "Sales", """[{"$match":{"InvoiceNo":"DOES-NOT-EXIST"}}]""");
        Assert.Empty(rows);
    }
}
