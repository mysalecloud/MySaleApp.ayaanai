using System.Text.Json.Nodes;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;
using MySale.AI.Application.Mql;
using MySale.AI.Application.Services;
using MySale.AI.Domain;

namespace MySale.AI.Tests;

// Accuracy audit: date ranges, typed dates, date literals in MQL, business date fields, zero results, data boundaries.

public class DateAnchorBoundaryTests
{
    private const string Dubai = "Asia/Dubai"; // UTC+4, no DST

    private static DateTime Utc(string iso) => DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public void Named_ranges_are_whole_local_days_expressed_in_utc()
    {
        // Monday 28 Sep 2026, 14:00 in Dubai
        var a = DateAnchors.Compute(Utc("2026-09-28T10:00:00Z"), Dubai);
        Assert.Equal(("2026-09-27T20:00:00Z", "2026-09-28T20:00:00Z"), (a.Today.StartIso, a.Today.EndIso));
        Assert.Equal(("2026-09-26T20:00:00Z", "2026-09-27T20:00:00Z"), (a.Yesterday.StartIso, a.Yesterday.EndIso));
        Assert.Equal(("2026-09-28T20:00:00Z", "2026-09-29T20:00:00Z"), (a.Tomorrow.StartIso, a.Tomorrow.EndIso));
        Assert.Equal(("2026-09-27T20:00:00Z", "2026-10-04T20:00:00Z"), (a.ThisWeek.StartIso, a.ThisWeek.EndIso));   // Mon–Sun
        Assert.Equal(("2026-09-20T20:00:00Z", "2026-09-27T20:00:00Z"), (a.LastWeek.StartIso, a.LastWeek.EndIso));
        Assert.Equal(("2026-08-31T20:00:00Z", "2026-09-30T20:00:00Z"), (a.ThisMonth.StartIso, a.ThisMonth.EndIso));
        Assert.Equal(("2026-07-31T20:00:00Z", "2026-08-31T20:00:00Z"), (a.LastMonth.StartIso, a.LastMonth.EndIso));
        Assert.Equal(("2025-12-31T20:00:00Z", "2026-12-31T20:00:00Z"), (a.ThisYear.StartIso, a.ThisYear.EndIso));
        Assert.Equal(("2024-12-31T20:00:00Z", "2025-12-31T20:00:00Z"), (a.LastYear.StartIso, a.LastYear.EndIso));
        Assert.Equal(("2026-08-31T20:00:00Z", "2026-09-28T20:00:00Z"), (a.MonthToDate.StartIso, a.MonthToDate.EndIso)); // until today (included)
        Assert.Equal(("2025-12-31T20:00:00Z", "2026-09-28T20:00:00Z"), (a.YearToDate.StartIso, a.YearToDate.EndIso));
        Assert.Equal(("2026-08-29T20:00:00Z", "2026-09-28T20:00:00Z"), (a.Last30Days.StartIso, a.Last30Days.EndIso));
    }

    [Fact]
    public void Year_boundary_just_after_local_midnight_is_the_new_year()
    {
        // 00:30 on 1 Jan 2026 in Dubai is still 31 Dec 2025 in UTC.
        var a = DateAnchors.Compute(Utc("2025-12-31T20:30:00Z"), Dubai);
        Assert.Equal(new DateOnly(2026, 1, 1), a.LocalToday);
        Assert.Equal("2025-12-31T20:00:00Z", a.Today.StartIso);
        Assert.Equal(("2025-11-30T20:00:00Z", "2025-12-31T20:00:00Z"), (a.LastMonth.StartIso, a.LastMonth.EndIso)); // December 2025
        Assert.Equal("2024-12-31T20:00:00Z", a.LastYear.StartIso);
    }

    [Fact]
    public void Leap_day_and_month_end()
    {
        var leap = DateAnchors.Compute(Utc("2028-02-29T08:00:00Z"), Dubai);
        Assert.Equal(("2028-02-28T20:00:00Z", "2028-02-29T20:00:00Z"), (leap.Today.StartIso, leap.Today.EndIso));
        Assert.Equal("2028-02-29T20:00:00Z", leap.ThisMonth.EndIso); // February 2028 has 29 days

        var monthEnd = DateAnchors.Compute(Utc("2026-01-31T19:59:59Z"), Dubai); // 23:59:59 local on 31 Jan
        Assert.Equal(new DateOnly(2026, 1, 31), monthEnd.LocalToday);
        Assert.Equal("2026-01-31T20:00:00Z", monthEnd.ThisMonth.EndIso);
    }

    [Theory]
    [InlineData("2026-02-15T08:00:00Z", 4, "2025-03-31T20:00:00Z", "2026-03-31T20:00:00Z", "2024-03-31T20:00:00Z")] // Apr 2025 – Mar 2026
    [InlineData("2026-04-01T08:00:00Z", 4, "2026-03-31T20:00:00Z", "2027-03-31T20:00:00Z", "2025-03-31T20:00:00Z")] // first day of FY 2026-27
    [InlineData("2026-09-28T08:00:00Z", 1, "2025-12-31T20:00:00Z", "2026-12-31T20:00:00Z", "2024-12-31T20:00:00Z")] // calendar-year FY
    public void Financial_years(string now, int startMonth, string fyStart, string fyEnd, string previousStart)
    {
        var a = DateAnchors.Compute(Utc(now), Dubai, startMonth);
        Assert.Equal((fyStart, fyEnd), (a.ThisFinancialYear.StartIso, a.ThisFinancialYear.EndIso));
        Assert.Equal((previousStart, fyStart), (a.LastFinancialYear.StartIso, a.LastFinancialYear.EndIso));
    }

    [Fact]
    public void Local_day_ranges_include_the_end_day()
    {
        var r = DateAnchors.ForLocalDays(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 10), Dubai);
        Assert.Equal(("2026-08-31T20:00:00Z", "2026-09-10T20:00:00Z"), (r.StartIso, r.EndIso));
        var same = DateAnchors.ForLocalDays(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 10), Dubai);
        Assert.Equal(("2026-09-09T20:00:00Z", "2026-09-10T20:00:00Z"), (same.StartIso, same.EndIso));
    }
}

public class QuestionDateParsingTests
{
    private static readonly DateOnly Today = new(2026, 9, 28);

    private static (DateOnly From, DateOnly To) Single(string question, string order = "DMY")
    {
        var d = QuestionDates.Parse(question, Today, order);
        Assert.Empty(d.Invalid);
        Assert.Empty(d.Ambiguous);
        var span = Assert.Single(d.Spans);
        return (span.From, span.To);
    }

    [Theory]
    [InlineData("Sales on 01/09/2026", 2026, 9, 1)]
    [InlineData("Sales on 01-09-2026", 2026, 9, 1)]
    [InlineData("Sales on 2026-09-01", 2026, 9, 1)]
    [InlineData("Sales on September 1, 2026", 2026, 9, 1)]
    [InlineData("Sales on 1 September 2026", 2026, 9, 1)]
    [InlineData("Sales on 1st Sep 2026", 2026, 9, 1)]
    [InlineData("Sales on 25/12/2026", 2026, 12, 25)]
    [InlineData("Sales on 12/25/2026", 2026, 12, 25)]  // only month-first is valid
    [InlineData("Sales on 29/02/2028", 2028, 2, 29)]   // leap day
    public void Single_dates_in_common_formats(string question, int y, int m, int d)
    {
        var (from, to) = Single(question);
        Assert.Equal(new DateOnly(y, m, d), from);
        Assert.Equal(from, to);
    }

    [Theory]
    [InlineData("Show sales from 01-09-2026 to 15-09-2026", "2026-09-01", "2026-09-15")]
    [InlineData("Show sales from 15-09-2026 to 30-09-2026", "2026-09-15", "2026-09-30")]
    [InlineData("Show my sales between September 1 and September 10", "2026-09-01", "2026-09-10")]
    [InlineData("Show sales from September 10 to September 10", "2026-09-10", "2026-09-10")]
    [InlineData("Sep 1 to Sep 15", "2026-09-01", "2026-09-15")]
    [InlineData("sales 28/08/2026 - 05/09/2026", "2026-08-28", "2026-09-05")]           // crosses months
    [InlineData("sales from 20/12/2025 to 10/01/2026", "2025-12-20", "2026-01-10")]     // crosses years
    [InlineData("sales from Dec 25 to Jan 5", "2025-12-25", "2026-01-05")]              // no years: start in previous year
    [InlineData("sales from 15/03/2026 to 15/04/2026", "2026-03-15", "2026-04-15")]     // crosses an April financial year
    [InlineData("sales from 1 September 2026 until today", "2026-09-01", "2026-09-28")]
    [InlineData("sales since 01/09/2026", "2026-09-01", "2026-09-28")]
    public void Ranges_include_both_days(string question, string from, string to)
    {
        var (f, t) = Single(question);
        Assert.Equal(DateOnly.Parse(from), f);
        Assert.Equal(DateOnly.Parse(to), t);
    }

    [Fact]
    public void Day_month_order_follows_the_business_locale()
    {
        Assert.Equal(new DateOnly(2026, 9, 1), Single("on 01/09/2026", "DMY").From);
        Assert.Equal(new DateOnly(2026, 1, 9), Single("on 01/09/2026", "MDY").From);
        var auto = QuestionDates.Parse("on 01/09/2026", Today, "Auto");
        var amb = Assert.Single(auto.Ambiguous);
        Assert.Equal(new DateOnly(2026, 9, 1), amb.DayFirst);
        Assert.Equal(new DateOnly(2026, 1, 9), amb.MonthFirst);
        Assert.Empty(QuestionDates.Parse("on 25/09/2026", Today, "Auto").Ambiguous); // 25 cannot be a month
    }

    [Theory]
    [InlineData("sales on 31/02/2026")]
    [InlineData("sales on 29/02/2027")]
    [InlineData("sales from 10/09/2026 to 01/09/2026")]
    public void Invalid_dates_are_reported(string question)
        => Assert.NotEmpty(QuestionDates.Parse(question, Today).Invalid);

    [Theory]
    [InlineData("Show my top 10 products")]
    [InlineData("Status of invoice INV-2026-09-001")]
    [InlineData("How much did I sell yesterday?")]
    [InlineData("Sales for the last 30 days")]
    public void Questions_without_typed_dates(string question)
        => Assert.True(QuestionDates.Parse(question, Today).IsEmpty);
}

public class DateLiteralCoercionTests
{
    private static readonly DateCoercionContext Dubai = new() { TimeZone = DateAnchors.ResolveTimeZone("Asia/Dubai") };

    private static JsonNode Coerce(string json, DateCoercionContext? ctx = null, ISet<string>? text = null)
    {
        var node = JsonNode.Parse(json)!;
        MqlDateCoercer.Coerce(node, text, ctx ?? Dubai);
        return node;
    }

    private static string D(JsonNode? n) => n!["$date"]!.GetValue<string>();

    [Fact]
    public void Inclusive_end_date_includes_the_whole_last_day_in_the_business_time_zone()
    {
        var n = Coerce("""{"$match":{"InvoiceDate":{"$gte":"2026-09-01","$lte":"2026-09-10"}}}""");
        var c = n["$match"]!["InvoiceDate"]!;
        Assert.Equal("2026-08-31T20:00:00.000Z", D(c["$gte"]));       // 1 Sep 00:00 Dubai
        Assert.Equal("2026-09-10T20:00:00.000Z", D(c["$lt"]));        // up to the end of 10 Sep
        Assert.Null(c["$lte"]);
    }

    [Fact]
    public void Utc_midnight_on_a_typed_end_day_is_read_as_the_local_day()
    {
        var ctx = new DateCoercionContext
        {
            TimeZone = Dubai.TimeZone,
            InclusiveEndDays = new HashSet<DateOnly> { new(2026, 9, 10) },
            MentionedDays = new HashSet<DateOnly> { new(2026, 9, 1), new(2026, 9, 10) }
        };
        var n = Coerce("""{"InvoiceDate":{"$gte":{"$date":"2026-09-01T00:00:00Z"},"$lte":{"$date":"2026-09-10T00:00:00Z"}}}""", ctx);
        Assert.Equal("2026-08-31T20:00:00.000Z", D(n["InvoiceDate"]!["$gte"]));
        Assert.Equal("2026-09-10T20:00:00.000Z", D(n["InvoiceDate"]!["$lt"]));
    }

    [Fact]
    public void Utc_midnight_after_a_typed_end_day_becomes_the_local_start_of_that_day()
    {
        // "from 1 Sep to 10 Sep" written by the model as [2026-09-01T00:00Z, 2026-09-11T00:00Z) in a UTC+4 company:
        // the upper bound must be 11 Sep 00:00 Dubai, not 11 Sep 04:00 Dubai.
        var ctx = new DateCoercionContext
        {
            TimeZone = Dubai.TimeZone,
            InclusiveEndDays = new HashSet<DateOnly> { new(2026, 9, 10) },
            MentionedDays = new HashSet<DateOnly> { new(2026, 9, 1), new(2026, 9, 10) }
        };
        var n = Coerce("""{"InvoiceDate":{"$gte":{"$date":"2026-09-01T00:00:00Z"},"$lt":{"$date":"2026-09-11T00:00:00Z"}}}""", ctx);
        Assert.Equal("2026-08-31T20:00:00.000Z", D(n["InvoiceDate"]!["$gte"]));
        Assert.Equal("2026-09-10T20:00:00.000Z", D(n["InvoiceDate"]!["$lt"]));
    }

    [Fact]
    public void Lte_on_an_anchor_boundary_becomes_an_exclusive_bound()
    {
        // "this month" end written with $lte: must not add 1 October.
        var n = Coerce("""{"InvoiceDate":{"$gte":{"$date":"2026-08-31T20:00:00Z"},"$lte":{"$date":"2026-09-30T20:00:00Z"}}}""");
        Assert.Equal("2026-09-30T20:00:00.000Z", D(n["InvoiceDate"]!["$lt"]));
    }

    [Fact]
    public void Equality_and_after_on_a_date()
    {
        var eq = Coerce("""{"InvoiceDate":"2026-09-10"}""");
        Assert.Equal("2026-09-09T20:00:00.000Z", D(eq["InvoiceDate"]!["$gte"]));
        Assert.Equal("2026-09-10T20:00:00.000Z", D(eq["InvoiceDate"]!["$lt"]));

        var after = Coerce("""{"InvoiceDate":{"$gt":"2026-09-10"}}""");
        Assert.Equal("2026-09-10T20:00:00.000Z", D(after["InvoiceDate"]!["$gte"]));
    }

    [Fact]
    public void Dates_stored_as_text_keep_text_and_include_the_end_day()
    {
        var n = Coerce("""{"invoiceDate":{"$gte":"2026-09-01","$lte":"2026-09-10"}}""", text: new HashSet<string> { "invoiceDate" });
        Assert.Equal("2026-09-01", n["invoiceDate"]!["$gte"]!.GetValue<string>());
        Assert.Equal("2026-09-11", n["invoiceDate"]!["$lt"]!.GetValue<string>());
    }

    [Fact]
    public void Utc_business_time_zone_keeps_midnight_values()
    {
        var n = Coerce("""{"InvoiceDate":{"$gte":"2026-09-01","$lt":"2026-10-01T00:00:00Z"}}""", DateCoercionContext.Utc);
        Assert.Equal("2026-09-01T00:00:00.000Z", D(n["InvoiceDate"]!["$gte"]));
        Assert.Equal("2026-10-01T00:00:00.000Z", D(n["InvoiceDate"]!["$lt"]));
    }

    [Fact]
    public void Date_values_in_output_stages_stay_values()
    {
        var n = Coerce("""[{"$match":{"InvoiceDate":"2026-09-10"}},{"$addFields":{"reportDay":"2026-09-10"}}]""");
        Assert.NotNull(n[0]!["$match"]!["InvoiceDate"]!["$gte"]);          // filter: the whole day
        Assert.NotNull(n[1]!["$addFields"]!["reportDay"]!["$date"]);         // output: a single date value, not a range
        Assert.Null(n[1]!["$addFields"]!["reportDay"]!["$gte"]);
    }

    [Fact]
    public void Null_and_non_date_values_are_untouched()
    {
        var n = Coerce("""{"InvoiceDate":null,"Status":"Paid","Amount":{"$gte":100}}""");
        Assert.Null(n["InvoiceDate"]);
        Assert.Equal("Paid", n["Status"]!.GetValue<string>());
        Assert.Equal(100, n["Amount"]!["$gte"]!.GetValue<int>());
    }
}

public class BusinessDateFieldTests
{
    private static FieldSchema F(string name, string type) => new() { Name = name, Type = type };

    private static readonly List<CollectionSchema> Schema = new()
    {
        new() { Name = "Sales", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("invoiceDate", "date"), F("createdAt", "date"), F("netAmount", "double") } },
        new() { Name = "Payments", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("paymentDate", "date"), F("updatedAt", "date"), F("amount", "double") } },
        new() { Name = "Customers", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("customerName", "string"), F("createdAt", "date") } },
        new() { Name = "StockMovements", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("transactionDate", "date"), F("createdAt", "date") } },
        new() { Name = "Vouchers", Fields = { F("_id", "objectId"), F("CompanyId", "objectId"), F("vDate", "date"), F("createdAt", "date") } }
    };

    [Theory]
    [InlineData("Sales", "invoiceDate")]
    [InlineData("Payments", "paymentDate")]
    [InlineData("StockMovements", "transactionDate")]
    [InlineData("Customers", null)]
    [InlineData("Vouchers", null)]
    public void Business_date_field_per_collection(string collection, string? expected)
        => Assert.Equal(expected, QueryEngine.BusinessDateField(Schema.Single(c => c.Name == collection)));

    [Fact]
    public void Configured_business_date_field_wins()
        => Assert.Equal("vDate", QueryEngine.BusinessDateField(Schema.Single(c => c.Name == "Vouchers"), new Dictionary<string, string> { ["Vouchers"] = "vDate" }));

    private static PreparedQuery Prepare(string query, string question)
    {
        var tenant = new TenantOptions();
        var engine = new QueryEngine(new FakeSchema(), new MqlValidator(), new TenantQueryGuard(tenant), tenant, new FakeExecutor(), new FakeUser());
        var context = new MqlValidationContext { Collections = Schema, TenantField = "CompanyId", MaxRecords = 200, MaxStages = 12, Question = question };
        return engine.Prepare(TestData.Parse(query), context, new AppSettings());
    }

    [Fact]
    public void Periods_on_record_timestamps_are_sent_back_for_repair()
    {
        var p = Prepare("""{"type":"query","operation":"aggregate","collection":"Sales","pipeline":[{"$match":{"createdAt":{"$gte":{"$date":"2026-09-01T00:00:00Z"}}}},{"$group":{"_id":null,"t":{"$sum":"$netAmount"}}}]}""",
            "What are my sales this month?");
        Assert.False(p.IsExecutable);
        Assert.Contains(p.Validation.Errors, e => e.Contains("invoiceDate"));
    }

    [Fact]
    public void Record_timestamps_are_fine_when_the_user_asks_about_creation_or_the_collection_has_no_business_date()
    {
        Assert.True(Prepare("""{"type":"query","operation":"find","collection":"Sales","filter":{"createdAt":{"$gte":{"$date":"2026-09-01T00:00:00Z"}}},"limit":10}""",
            "Which invoices were created today?").IsExecutable);
        Assert.True(Prepare("""{"type":"query","operation":"count","collection":"Customers","filter":{"createdAt":{"$gte":{"$date":"2026-09-01T00:00:00Z"}}}}""",
            "How many customers this month?").IsExecutable);
    }
}

public class ZeroResultTests
{
    private const string TotalsPipeline = """
        [{"$match":{"Status":{"$ne":"Cancelled"}}},
         {"$group":{"_id":null,"totalSales":{"$sum":"$NetAmount"},"invoiceCount":{"$sum":1},"avgInvoice":{"$avg":"$NetAmount"}}},
         {"$project":{"_id":0,"totalSales":{"$round":["$totalSales",2]},"invoiceCount":1,"avgInvoice":1}}]
        """;

    private static JsonArray P(string json) => (JsonArray)JsonNode.Parse(json)!;

    [Fact]
    public void Totals_over_no_documents_are_zero_and_averages_are_not_available()
    {
        var row = ZeroResultPolicy.ZeroRowFor(P(TotalsPipeline))!;
        Assert.Equal(0, row["totalSales"]!.GetValue<int>());
        Assert.Equal(0, row["invoiceCount"]!.GetValue<int>());
        Assert.True(row.ContainsKey("avgInvoice"));
        Assert.Null(row["avgInvoice"]); // missing ≠ 0
    }

    [Theory]
    [InlineData("""[{"$group":{"_id":"$CustomerId","total":{"$sum":"$NetAmount"}}}]""")]   // a list: empty is the answer
    [InlineData("""[{"$match":{"Status":"Paid"}}]""")]
    [InlineData("""[{"$group":{"_id":null,"t":{"$sum":1}}},{"$unwind":"$x"}]""")]
    public void Lists_have_no_zero_row(string pipeline) => Assert.Null(ZeroResultPolicy.ZeroRowFor(P(pipeline)));

    [Fact]
    public void Count_stage_gives_zero() => Assert.Equal(0, ZeroResultPolicy.ZeroRowFor(P("""[{"$count":"invoices"}]"""))!["invoices"]!.GetValue<int>());

    [Theory]
    [InlineData("What are today's sales?", true, "Today's sales are AED 0.00. No sales were recorded today.")]
    [InlineData("How much did I sell yesterday?", true, "Yesterday's sales are AED 0.00. No sales were recorded yesterday.")]
    [InlineData("What were my sales last month?", true, "Last month's sales are AED 0.00. No sales were recorded last month.")]
    [InlineData("How many invoices were created today?", false, "No invoices were found today.")]
    [InlineData("Which products sold today?", false, "No products were sold today.")]
    [InlineData("Who purchased today?", false, "No customers made purchases today.")]
    [InlineData("What are today's purchases?", false, "No purchase transactions were recorded today.")]
    [InlineData("Show today's payments", false, "No payments were recorded today.")]
    [InlineData("Show today's returns", false, "No returns were recorded today.")]
    [InlineData("Which products are low in stock?", false, "No products are currently below the configured stock level.")]
    [InlineData("Show items out of stock", false, "No products are currently below the configured stock level.")]
    [InlineData("Show customers with outstanding balances", false, "There are no customers with outstanding balances matching your criteria.")]
    [InlineData("Show customers who have not purchased this month", false, "No customers matched your criteria this month.")]
    [InlineData("Find a customer by phone 0501234567", false, "No customers matched your criteria.")]
    public void Clear_messages_by_topic_and_period(string question, bool totals, string expected)
    {
        var pipeline = totals ? P(TotalsPipeline) : P("""[{"$match":{}}]""");
        Assert.Equal(expected, ZeroResultPolicy.Describe(question, pipeline, "Sales", null, "AED").Message);
    }

    [Fact]
    public void Typed_date_ranges_are_named_in_the_message()
    {
        var dates = QuestionDates.Parse("Show sales between 01/09/2026 and 10/09/2026", new DateOnly(2026, 9, 28));
        var z = ZeroResultPolicy.Describe("Show sales between 01/09/2026 and 10/09/2026", P("""[{"$match":{}}]"""), "Sales", dates, "AED");
        Assert.Equal("No sales were recorded between 1 Sep 2026 and 10 Sep 2026.", z.Message);
    }

    [Fact]
    public void Never_claims_there_are_no_records_at_all()
    {
        foreach (var q in new[] { "Show sales today", "List customers named Zed", "Show products", "Anything for yesterday?" })
            Assert.DoesNotContain("no records at all", ZeroResultPolicy.Describe(q, null, null, null, "AED").Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class AccuracyEndToEndTests
{
    private static string SalesRange(string gte, string lte) => $$$$"""
        {"type":"query","operation":"aggregate","collection":"Sales","pipeline":[
          {"$match":{"Status":{"$ne":"Cancelled"},"InvoiceDate":{"$gte":"{{{{gte}}}}","$lte":"{{{{lte}}}}"}}},
          {"$group":{"_id":null,"totalSales":{"$sum":"$NetAmount"},"invoiceCount":{"$sum":1}}},
          {"$project":{"_id":0,"totalSales":{"$round":["$totalSales",2]},"invoiceCount":1}}],
         "explanation":"Sales in the period","visualization":"kpi"}
        """;

    [Fact]
    public async Task Sales_between_two_dates_include_the_end_day_in_the_business_time_zone()
    {
        var h = new Harness();                           // FakeUser: Asia/Dubai, AED
        h.Provider.QueryResponses.Enqueue(_ => SalesRange("2026-09-01", "2026-09-10"));
        h.Executor.Handler = (_, _) => new List<JsonObject> { new() { ["totalSales"] = 1500.0, ["invoiceCount"] = 12 } };

        await h.Orchestrator.RunAsync(new ChatRequest { Message = "Show my sales between 01-09-2026 and 10-09-2026" }, NullChatEventSink.Instance, default);

        var mql = h.Executor.LastCall!.Value.Pipeline.ToJsonString();
        Assert.Contains("2026-08-31T20:00:00.000Z", mql);   // from 1 Sep 00:00 local
        Assert.Contains("2026-09-10T20:00:00.000Z", mql);   // to the end of 10 Sep local
        Assert.DoesNotContain("$lte", mql);
        var log = Assert.Single(h.Store.Logs);
        Assert.Contains("ISODate(\"2026-09-10T20:00:00.000Z\")", log.FinalMql);   // stored MQL shows the corrected boundary
    }

    [Fact]
    public async Task No_sales_today_is_answered_as_zero_not_as_an_empty_result()
    {
        var h = new Harness();
        h.Provider.QueryResponses.Enqueue(_ => SalesRange("2026-09-28", "2026-09-28"));
        h.Executor.Handler = (_, _) => new List<JsonObject>();

        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "What are today's sales?" }, NullChatEventSink.Instance, default);

        Assert.Equal(ChatStatus.NoResults, r.Status);
        Assert.Equal("Today's sales are AED 0.00. No sales were recorded today.", r.Answer);
        Assert.Single(h.Provider.Requests); // no answer-model call needed
    }

    [Fact]
    public async Task Invalid_typed_dates_are_reported_before_any_query()
    {
        var h = new Harness();
        var r = await h.Orchestrator.RunAsync(new ChatRequest { Message = "Sales on 31/02/2026" }, NullChatEventSink.Instance, default);
        Assert.Contains("isn't a valid date", r.Answer);
        Assert.Empty(h.Provider.Requests);
        Assert.Null(h.Executor.LastCall);
    }

    [Fact]
    public async Task Query_prompt_contains_the_resolved_dates_and_business_date_fields()
    {
        var h = new Harness();
        h.Provider.QueryResponses.Enqueue(_ => SalesRange("2026-09-01", "2026-09-15"));
        await h.Orchestrator.RunAsync(new ChatRequest { Message = "Which customers purchased between September 1 and September 15?" }, NullChatEventSink.Instance, default);
        var system = h.Provider.Requests[0].Messages[0].Content;
        Assert.Contains("Dates in the question", system);
        Assert.Contains("1 Sep 2026 to 15 Sep 2026", system);
        Assert.Contains("business date: InvoiceDate", system);
        Assert.Contains("never list a customer twice", system);
    }
}

public class DataBoundaryTests
{
    [Fact]
    public async Task A_thousand_rows_are_capped_and_marked_truncated()
    {
        var tenant = new TenantOptions();
        var executor = new FakeExecutor { Handler = (_, _) => Enumerable.Range(0, 1000).Select(i => new JsonObject { ["n"] = i }).ToList() };
        var engine = new QueryEngine(new FakeSchema(), new MqlValidator(), new TenantQueryGuard(tenant), tenant, executor, new FakeUser());
        var settings = new AppSettings();
        var prepared = engine.Prepare(TestData.Parse("""{"type":"query","operation":"find","collection":"Sales","filter":{},"limit":200}"""), TestData.Context(), settings);
        var result = await engine.ExecuteAsync(prepared, settings, default);
        Assert.True(result.Truncated);
        Assert.Equal(settings.Query.MaxRecords, result.Rows.Count);
    }

    [Fact]
    public void Null_and_missing_values_survive_result_shaping()
    {
        var (rows, columns) = ResultShaper.Shape(new[]
        {
            new JsonObject { ["InvoiceDate"] = null, ["CustomerName"] = "A", ["NetAmount"] = 10.0 },
            new JsonObject { ["CustomerName"] = "B" }
        });
        Assert.Equal(2, rows.Count);
        Assert.Contains("NetAmount", columns);
        Assert.Null(rows[0]["InvoiceDate"]);
        Assert.False(rows[1].ContainsKey("NetAmount")); // missing stays missing, not 0
    }
}
