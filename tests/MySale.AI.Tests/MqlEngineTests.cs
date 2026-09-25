using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Mql;

namespace MySale.AI.Tests;

public class TenantQueryGuardTests
{
    private readonly TenantQueryGuard _guard = new(new TenantOptions { FieldName = "CompanyId", ValueIsObjectId = true });

    [Fact]
    public void Tenant_filter_is_the_first_stage_and_uses_the_token_company()
    {
        var pipeline = JsonNode.Parse("""[{"$group":{"_id":null,"total":{"$sum":"$NetAmount"}}}]""")!.AsArray();
        var scoped = _guard.Apply(pipeline, TestData.CompanyA, 200);

        var first = scoped[0]!["$match"]!["CompanyId"]!["$oid"]!.GetValue<string>();
        Assert.Equal(TestData.CompanyA, first);
        Assert.Equal(201, scoped[^1]!["$limit"]!.GetValue<int>());
    }

    [Fact]
    public void Lookups_are_scoped_to_the_tenant()
    {
        var pipeline = JsonNode.Parse("""
        [{"$lookup":{"from":"Customers","localField":"CustomerId","foreignField":"_id","as":"c"}},
         {"$facet":{"a":[{"$lookup":{"from":"Items","localField":"ItemId","foreignField":"_id","as":"i"}}]}}]
        """)!.AsArray();
        var scoped = _guard.Apply(pipeline, TestData.CompanyB, 50);

        var lookup = scoped[1]!["$lookup"]!;
        Assert.Equal(TestData.CompanyB, lookup["pipeline"]![0]!["$match"]!["CompanyId"]!["$oid"]!.GetValue<string>());

        var facetLookup = scoped[2]!["$facet"]!["a"]![0]!["$lookup"]!;
        Assert.Equal(TestData.CompanyB, facetLookup["pipeline"]![0]!["$match"]!["CompanyId"]!["$oid"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_tenant_throws()
    {
        Assert.Throws<InvalidOperationException>(() => _guard.Apply(new JsonArray(), "", 10));
    }

    [Fact]
    public void Ai_cannot_escape_tenant_even_with_or_filter()
    {
        // Even if the model tried to widen the filter, the injected $match is ANDed first and CompanyId references are rejected.
        var validator = new MqlValidator();
        var q = TestData.Parse("""{"operation":"find","collection":"Sales","filter":{"$or":[{"CompanyId":{"$exists":true}},{"Status":"Paid"}]}}""");
        var r = validator.Validate(q, TestData.Context());
        Assert.False(r.IsValid);
        Assert.True(r.Blocked);
    }
}

public class MqlParserTests
{
    [Fact]
    public void Parses_json_inside_markdown_and_chatter()
    {
        var raw = "Sure! Here is the query:\n```json\n{\"operation\":\"count\",\"collection\":\"Sales\",\"filter\":{\"Status\":\"Paid\"}}\n```\nLet me know.";
        var r = MqlParser.Parse(raw);
        Assert.True(r.Success, r.Error);
        Assert.Equal("count", r.Query!.Operation);
        Assert.Equal("Sales", r.Query.Collection);
    }

    [Fact]
    public void Unwraps_nested_query_object()
    {
        var r = MqlParser.Parse("""{"query":{"operation":"aggregate","collection":"Sales","pipeline":[]},"explanation":"x"}""");
        Assert.True(r.Success, r.Error);
        Assert.Equal("aggregate", r.Query!.Operation);
        Assert.Equal("x", r.Query.Explanation);
    }

    [Fact]
    public void Infers_aggregate_when_pipeline_present()
    {
        var r = MqlParser.Parse("""{"collection":"Sales","pipeline":[{"$match":{}}]}""");
        Assert.Equal("aggregate", r.Query!.Operation);
    }

    [Fact]
    public void Unsupported_is_recognised()
    {
        var r = MqlParser.Parse("""{"type":"unsupported","reason":"weather is not business data"}""");
        Assert.True(r.Success);
        Assert.True(r.Query!.IsUnsupported);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I cannot help with that")]
    [InlineData("{ not json }")]
    [InlineData("""{"operation":"find"}""")]
    public void Invalid_output_fails(string raw)
    {
        Assert.False(MqlParser.Parse(raw).Success);
    }
}

public class DateCoercerTests
{
    [Fact]
    public void Iso_strings_become_extended_json_dates()
    {
        var node = JsonNode.Parse("""{"$match":{"InvoiceDate":{"$gte":"2026-09-01","$lt":"2026-10-01T00:00:00Z"},"Status":"Paid"}}""");
        MqlDateCoercer.Coerce(node);
        Assert.Equal("2026-09-01T00:00:00.000Z", node!["$match"]!["InvoiceDate"]!["$gte"]!["$date"]!.GetValue<string>());
        Assert.Equal("2026-10-01T00:00:00.000Z", node["$match"]!["InvoiceDate"]!["$lt"]!["$date"]!.GetValue<string>());
        Assert.Equal("Paid", node["$match"]!["Status"]!.GetValue<string>());
    }

    [Fact]
    public void Format_strings_and_field_refs_are_untouched()
    {
        var node = JsonNode.Parse("""{"$dateToString":{"format":"%Y-%m","date":"$InvoiceDate"}}""");
        MqlDateCoercer.Coerce(node);
        Assert.Equal("%Y-%m", node!["$dateToString"]!["format"]!.GetValue<string>());
        Assert.Equal("$InvoiceDate", node["$dateToString"]!["date"]!.GetValue<string>());
    }
}

public class ResultProcessingTests
{
    [Fact]
    public void Grounding_accepts_numbers_from_data_and_flags_invented_ones()
    {
        var rows = new List<JsonObject> { new() { ["totalSales"] = 184250.4, ["invoiceCount"] = 1247 } };

        var ok = GroundingChecker.Check("Your total sales this month are AED 184,250.40 from 1,247 invoices.", rows, "sales this month?");
        Assert.Empty(ok.Warnings);

        var bad = GroundingChecker.Check("Your sales are AED 190,000 which is 12.5% up.", rows, "sales?");
        Assert.Contains(bad.Warnings, w => w.Contains("190,000"));
        Assert.Contains(bad.Warnings, w => w.Contains("12.5"));
    }

    [Fact]
    public void Visualization_picks_kpi_bar_and_line()
    {
        var kpi = VisualizationAdvisor.Decide(new List<JsonObject> { new() { ["total"] = 5.0 } }, new[] { "total" }, null);
        Assert.Equal("kpi", kpi.Type);

        var barRows = new List<JsonObject>
        {
            new() { ["itemName"] = "A", ["qty"] = 5 },
            new() { ["itemName"] = "B", ["qty"] = 3 }
        };
        var bar = VisualizationAdvisor.Decide(barRows, new[] { "itemName", "qty" }, null);
        Assert.Equal("bar", bar.Type);
        Assert.Equal("itemName", bar.XField);

        var lineRows = new List<JsonObject>
        {
            new() { ["date"] = "2026-09-01", ["sales"] = 5 },
            new() { ["date"] = "2026-09-02", ["sales"] = 7 }
        };
        Assert.Equal("line", VisualizationAdvisor.Decide(lineRows, new[] { "date", "sales" }, null).Type);
    }

    [Fact]
    public void Shaper_flattens_group_keys()
    {
        var rows = new List<JsonObject>
        {
            JsonNode.Parse("""{"_id":{"year":2026,"month":9},"total":10}""")!.AsObject(),
            JsonNode.Parse("""{"_id":null,"total":10}""")!.AsObject()
        };
        var (shaped, columns) = ResultShaper.Shape(rows);
        Assert.Equal(new[] { "year", "month", "total" }, columns);
        Assert.False(shaped[1].ContainsKey("_id"));
    }
}
