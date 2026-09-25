using System.Text.Json.Nodes;
using MySale.AI.Application.Mql;

namespace MySale.AI.Tests;

public class MqlValidatorTests
{
    private readonly MqlValidator _validator = new();

    private MqlValidationResult Validate(string json, int maxRecords = 200, int maxStages = 12)
        => _validator.Validate(TestData.Parse(json), TestData.Context(maxRecords, maxStages));

    [Fact]
    public void Valid_aggregate_passes()
    {
        var r = Validate("""
        {"operation":"aggregate","collection":"Sales","pipeline":[
          {"$match":{"Status":{"$ne":"Cancelled"},"InvoiceDate":{"$gte":{"$date":"2026-09-01T00:00:00Z"},"$lt":{"$date":"2026-10-01T00:00:00Z"}}}},
          {"$group":{"_id":null,"totalSales":{"$sum":"$NetAmount"},"invoiceCount":{"$sum":1}}},
          {"$project":{"_id":0,"totalSales":{"$round":["$totalSales",2]},"invoiceCount":1}}
        ]}
        """);
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Equal("Sales", r.Collection);
        Assert.Equal(3, r.Pipeline!.Count);
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("updateMany")]
    [InlineData("deleteMany")]
    [InlineData("drop")]
    [InlineData("renameCollection")]
    [InlineData("createIndex")]
    [InlineData("dropIndex")]
    public void Write_and_admin_operations_are_blocked(string operation)
    {
        var r = Validate("""{"operation":"__OP__","collection":"Sales","filter":{}}""".Replace("__OP__", operation));
        Assert.False(r.IsValid);
        Assert.True(r.Blocked);
    }

    [Theory]
    [InlineData("""{"$out":"hacked"}""")]
    [InlineData("""{"$merge":{"into":"Sales"}}""")]
    [InlineData("""{"$unionWith":"Customers"}""")]
    [InlineData("""{"$graphLookup":{"from":"Customers"}}""")]
    [InlineData("""{"$collStats":{}}""")]
    [InlineData("""{"$currentOp":{}}""")]
    public void Dangerous_stages_are_blocked(string stage)
    {
        var r = Validate("""{"operation":"aggregate","collection":"Sales","pipeline":[__STAGE__]}""".Replace("__STAGE__", stage));
        Assert.False(r.IsValid);
        Assert.True(r.Blocked);
    }

    [Fact]
    public void Where_and_javascript_are_blocked()
    {
        var where = Validate("""{"operation":"find","collection":"Sales","filter":{"$where":"this.NetAmount > 0"}}""");
        Assert.True(where.Blocked);

        var fn = Validate("""
        {"operation":"aggregate","collection":"Sales","pipeline":[
          {"$addFields":{"x":{"$function":{"body":"function(){return 1}","args":[],"lang":"js"}}}}]}
        """);
        Assert.True(fn.Blocked);

        var acc = Validate("""
        {"operation":"aggregate","collection":"Sales","pipeline":[
          {"$group":{"_id":null,"x":{"$accumulator":{"init":"function(){}"}}}}]}
        """);
        Assert.True(acc.Blocked);
    }

    [Fact]
    public void Collection_whitelist_is_enforced()
    {
        var r = Validate("""{"operation":"find","collection":"users","filter":{}}""");
        Assert.False(r.IsValid);
        Assert.True(r.Blocked);
        Assert.Contains(r.Errors, e => e.Contains("not allowed"));
    }

    [Fact]
    public void Collection_name_is_canonicalised_case_insensitively()
    {
        var r = Validate("""{"operation":"count","collection":"sales","filter":{"Status":"Paid"}}""");
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Equal("Sales", r.Collection);
    }

    [Fact]
    public void Unknown_fields_are_rejected_with_suggestion()
    {
        var r = Validate("""{"operation":"find","collection":"Sales","filter":{"netAmount":{"$gt":100}}}""");
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("Did you mean 'NetAmount'"));
    }

    [Theory]
    [InlineData("""{"operation":"find","collection":"Sales","filter":{"CompanyId":{"$oid":"66a000000000000000000002"}}}""")]
    [InlineData("""{"operation":"aggregate","collection":"Sales","pipeline":[{"$group":{"_id":"$CompanyId","t":{"$sum":"$NetAmount"}}}]}""")]
    [InlineData("""{"operation":"aggregate","collection":"Sales","pipeline":[{"$addFields":{"CompanyId":"x"}}]}""")]
    [InlineData("""{"operation":"distinct","collection":"Sales","field":"CompanyId"}""")]
    public void Tenant_field_cannot_be_referenced(string json)
    {
        var r = Validate(json);
        Assert.False(r.IsValid);
        Assert.True(r.Blocked);
        Assert.Contains(r.Errors, e => e.Contains("CompanyId"));
    }

    [Fact]
    public void Limit_above_maximum_is_clamped()
    {
        var r = Validate("""{"operation":"aggregate","collection":"Items","pipeline":[{"$sort":{"Stock":1}},{"$limit":100000}]}""", maxRecords: 50);
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Equal(50, r.Pipeline![1]!["$limit"]!.GetValue<int>());
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void Too_many_stages_are_rejected()
    {
        var stages = string.Join(",", Enumerable.Repeat("""{"$match":{"Status":"Paid"}}""", 6));
        var r = Validate("""{"operation":"aggregate","collection":"Sales","pipeline":[__STAGES__]}""".Replace("__STAGES__", stages), maxStages: 5);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("maximum is 5"));
    }

    [Fact]
    public void Group_output_fields_are_tracked()
    {
        var ok = Validate("""
        {"operation":"aggregate","collection":"SaleItems","pipeline":[
          {"$group":{"_id":"$ItemName","qty":{"$sum":"$Quantity"}}},
          {"$sort":{"qty":-1}},{"$limit":10},
          {"$project":{"_id":0,"itemName":"$_id","qty":1}}]}
        """);
        Assert.True(ok.IsValid, string.Join("; ", ok.Errors));

        var bad = Validate("""
        {"operation":"aggregate","collection":"SaleItems","pipeline":[
          {"$group":{"_id":"$ItemName","qty":{"$sum":"$Quantity"}}},
          {"$sort":{"LineTotal":-1}}]}
        """);
        Assert.False(bad.IsValid);
        Assert.Contains(bad.Errors, e => e.Contains("LineTotal"));
    }

    [Fact]
    public void Lookup_simple_form_is_allowed_and_adds_alias()
    {
        var r = Validate("""
        {"operation":"aggregate","collection":"Sales","pipeline":[
          {"$lookup":{"from":"customers","localField":"CustomerId","foreignField":"_id","as":"cust"}},
          {"$unwind":"$cust"},
          {"$group":{"_id":"$cust.City","total":{"$sum":"$NetAmount"}}}]}
        """);
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Equal("Customers", r.Pipeline![0]!["$lookup"]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void Lookup_with_pipeline_or_disallowed_collection_is_blocked()
    {
        var withPipeline = Validate("""
        {"operation":"aggregate","collection":"Sales","pipeline":[
          {"$lookup":{"from":"Customers","let":{"c":"$CustomerId"},"pipeline":[],"as":"x"}}]}
        """);
        Assert.True(withPipeline.Blocked);

        var otherCollection = Validate("""
        {"operation":"aggregate","collection":"Sales","pipeline":[
          {"$lookup":{"from":"Users","localField":"CustomerId","foreignField":"_id","as":"x"}}]}
        """);
        Assert.True(otherCollection.Blocked);
    }

    [Fact]
    public void Find_is_normalised_to_pipeline_with_limit()
    {
        var r = Validate("""
        {"operation":"find","collection":"Items","filter":{"IsActive":true,"$expr":{"$lte":["$Stock","$ReorderLevel"]}},
         "projection":{"_id":0,"ItemName":1,"Stock":1},"sort":{"Stock":1},"limit":20}
        """);
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        var stages = r.Pipeline!.Select(s => ((JsonObject)s!).First().Key).ToList();
        Assert.Equal(new[] { "$match", "$sort", "$limit", "$project" }, stages);
    }

    [Fact]
    public void Distinct_and_count_are_normalised()
    {
        var distinct = Validate("""{"operation":"distinct","collection":"Customers","field":"City"}""");
        Assert.True(distinct.IsValid, string.Join("; ", distinct.Errors));
        Assert.Contains(distinct.Pipeline!, s => ((JsonObject)s!).ContainsKey("$group"));

        var count = Validate("""{"operation":"count","collection":"Customers","filter":{"Balance":{"$gt":0}}}""");
        Assert.True(count.IsValid, string.Join("; ", count.Errors));
        Assert.Equal("count", count.Pipeline![^1]!["$count"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_operators_are_rejected()
    {
        var r = Validate("""{"operation":"find","collection":"Sales","filter":{"NetAmount":{"$foo":1}}}""");
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Operators_inside_literal_values_are_rejected()
    {
        var r = Validate("""{"operation":"find","collection":"Sales","filter":{"Status":{"$in":[{"$where":"1"}]}}}""");
        Assert.False(r.IsValid);
        Assert.True(r.Blocked);
    }

    [Fact]
    public void Dangerous_variables_are_blocked()
    {
        var r = Validate("""{"operation":"aggregate","collection":"Sales","pipeline":[{"$addFields":{"r":"$$USER_ROLES"}}]}""");
        Assert.True(r.Blocked);
    }

    [Fact]
    public void Excessive_regex_is_rejected()
    {
        var pattern = new string('a', 500);
        var r = Validate("""{"operation":"find","collection":"Customers","filter":{"CustomerName":{"$regex":"__P__"}}}""".Replace("__P__", pattern));
        Assert.False(r.IsValid);
    }
}
