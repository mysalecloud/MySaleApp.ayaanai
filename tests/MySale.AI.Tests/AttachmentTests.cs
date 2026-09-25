using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using MySale.AI.Application.Attachments;
using MySale.AI.Domain;
using MySale.AI.Infrastructure.Media;

namespace MySale.AI.Tests;

public class FileTypeDetectorTests
{
    private static (FileTypeInfo? Info, string? Error) Detect(string name, byte[] bytes, string? mime = null)
    {
        using var ms = new MemoryStream(bytes);
        return FileTypeDetector.Detect(name, mime, bytes.AsSpan(0, Math.Min(4096, bytes.Length)), ms);
    }

    [Fact]
    public void Png_with_png_signature_is_accepted()
    {
        var (info, error) = Detect("receipt.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "image/png");
        Assert.Null(error);
        Assert.Equal(AttachmentKinds.Image, info!.Kind);
    }

    [Fact]
    public void Executable_renamed_to_pdf_is_rejected()
    {
        var (info, error) = Detect("invoice.pdf", new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 });
        Assert.Null(info);
        Assert.Contains("Executable", error);
    }

    [Fact]
    public void Disallowed_extension_is_rejected()
        => Assert.Null(Detect("run.exe", Encoding.ASCII.GetBytes("hello")).Info);

    [Fact]
    public void Content_not_matching_extension_is_rejected()
        => Assert.Contains("does not match", Detect("photo.jpg", Encoding.ASCII.GetBytes("not an image")).Error);

    [Fact]
    public void Csv_with_html_mime_is_rejected()
        => Assert.NotNull(Detect("data.csv", Encoding.UTF8.GetBytes("a,b\n1,2"), "text/html").Error);

    [Fact]
    public void Binary_content_in_txt_is_rejected()
        => Assert.NotNull(Detect("notes.txt", new byte[] { 0x41, 0x00, 0x42 }).Error);

    [Fact]
    public void Macro_workbook_renamed_to_xlsx_is_rejected()
    {
        var bytes = Zip(("xl/workbook.xml", "<workbook/>"), ("xl/vbaProject.bin", "x"));
        Assert.NotNull(Detect("book.xlsx", bytes).Error);
    }

    [Fact]
    public void Sanitizer_strips_paths_and_dangerous_characters()
    {
        var name = FileNameSanitizer.Sanitize("../../etc/<pass>wd.csv", ".csv");
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("..", name);
        Assert.DoesNotContain("<", name);
        Assert.EndsWith(".csv", name);
    }

    internal static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }
}

public class TableQueryEngineTests
{
    private static AttachmentTable Sales() => new()
    {
        Columns = new() { "Code", "Region", "Amount" },
        Rows = new()
        {
            new() { "001", "Dubai", "100.50" },
            new() { "002", "Dubai", "200" },
            new() { "003", "Sharjah", "50" },
            new() { "004", "Sharjah", "1,000" }
        },
        TotalRows = 4
    };

    private static double Num(JsonNode? n) => double.Parse(n!.ToString(), CultureInfo.InvariantCulture);

    [Fact]
    public void Group_and_sum_is_exact()
    {
        var spec = JsonNode.Parse("""
            {"groupBy":["Region"],"aggregates":[{"column":"Amount","fn":"sum","as":"total"}],"sort":{"by":"total","dir":"desc"}}
            """)!.AsObject();
        var result = TableQueryEngine.Execute(Sales(), spec);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("Sharjah", result.Rows[0]["Region"]!.ToString());
        Assert.Equal(1050, Num(result.Rows[0]["total"]), 3);
        Assert.Equal(300.5, Num(result.Rows[1]["total"]), 3);
    }

    [Fact]
    public void Filter_and_count()
    {
        var spec = JsonNode.Parse("""
            {"filter":[{"column":"Amount","op":"gte","value":100}],"aggregates":[{"fn":"count","as":"n"}]}
            """)!.AsObject();
        var result = TableQueryEngine.Execute(Sales(), spec);
        Assert.Equal(3, Num(result.Rows[0]["n"]), 0);
    }

    [Fact]
    public void Distinct_keeps_leading_zero_codes_as_text()
    {
        var spec = JsonNode.Parse("""{"distinct":"Code"}""")!.AsObject();
        var result = TableQueryEngine.Execute(Sales(), spec);
        Assert.Equal("001", result.Rows[0]["Code"]!.ToString());
        Assert.Equal(4, result.Rows.Count);
    }

    [Fact]
    public void Unknown_column_is_reported_not_guessed()
    {
        var spec = JsonNode.Parse("""{"aggregates":[{"column":"Profit","fn":"sum"}]}""")!.AsObject();
        var result = TableQueryEngine.Execute(Sales(), spec);
        Assert.False(result.IsValid);
        Assert.Contains("Profit", result.Errors[0]);
    }
}

public class AttachmentPlanTests
{
    [Fact]
    public void Parses_table_plan_and_ignores_think_block()
    {
        var plan = AttachmentPlan.TryParse("<think>hmm {x}</think>{\"type\":\"table\",\"attachments\":[\"A1\"],\"table\":{\"distinct\":\"Code\"}}");
        Assert.NotNull(plan);
        Assert.Equal("table", plan!.Type);
        Assert.Equal("A1", plan.Attachments[0]);
    }

    [Fact]
    public void Mql_output_is_not_mistaken_for_attachment_plan()
        => Assert.Null(AttachmentPlan.TryParse("{\"collection\":\"sales\",\"operation\":\"aggregate\",\"pipeline\":[]}"));

    [Fact]
    public void Inject_values_replaces_placeholder_with_strings_and_numbers()
    {
        var query = JsonNode.Parse("""{"pipeline":[{"$match":{"Code":{"$in":"@attachment.values"}}}]}""")!;
        var count = AttachmentPlan.InjectValues(query, new[] { "001", "25" });
        Assert.Equal(1, count);
        var arr = query["pipeline"]![0]!["$match"]!["Code"]!["$in"]!.AsArray();
        Assert.Equal(3, arr.Count); // "001", "25", 25 — leading-zero code stays text only
    }
}

public class ContentToolTests
{
    [Fact]
    public void Retriever_prefers_chunks_matching_the_question()
    {
        var chunks = new List<AttachmentChunk>
        {
            new() { Index = 0, Page = 1, Text = "Introduction and company overview." },
            new() { Index = 1, Page = 2, Text = "Payment terms: invoice due within 30 days. Late fee 2%." },
            new() { Index = 2, Page = 3, Text = "Appendix with contact details." }
        };
        var relevant = ContentRetriever.Relevant(chunks, "what are the payment terms for the invoice?", 80);
        Assert.Contains(relevant, c => c.Index == 1);
        Assert.DoesNotContain(relevant, c => c.Index == 2);
    }

    [Fact]
    public void Masker_hides_emails_and_card_like_numbers()
    {
        var masked = SensitiveDataMasker.Mask("mail john@example.com card 4111111111111111");
        Assert.DoesNotContain("john@example.com", masked);
        Assert.DoesNotContain("4111111111111111", masked);
        Assert.EndsWith("1111", masked);
    }
}

public class ProcessorTests
{
    [Fact]
    public async Task Csv_processor_handles_quotes_and_semicolons()
    {
        var csv = "Name;Amount\n\"Al Noor; LLC\";10\nBeta;\"2\"\"5\"\n";
        var result = await new CsvProcessor().ProcessAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "x.csv", default);
        Assert.Equal(new[] { "Name", "Amount" }, result.Table!.Columns);
        Assert.Equal("Al Noor; LLC", result.Table.Rows[0][0]);
        Assert.Equal("2\"5", result.Table.Rows[1][1]);
    }

    [Fact]
    public async Task Xlsx_processor_reads_shared_strings_and_numbers()
    {
        var bytes = FileTypeDetectorTests.Zip(
            ("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sales" sheetId="1" r:id="rId1"/></sheets></workbook>"""),
            ("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>"""),
            ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Item</t></si><si><t>Qty</t></si><si><t>Rice</t></si></sst>"""),
            ("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row><row r="2"><c r="A2" t="s"><v>2</v></c><c r="C2"><v>7</v></c></row></sheetData></worksheet>"""));
        var result = await new XlsxProcessor().ProcessAsync(new MemoryStream(bytes), "x.xlsx", default);
        var t = result.Table!;
        Assert.Equal("Sales", t.Sheet);
        Assert.Equal(new[] { "Item", "Qty", "Column3" }, t.Columns);
        Assert.Equal("Rice", t.Rows[0][0]);
        Assert.Null(t.Rows[0][1]);
        Assert.Equal("7", t.Rows[0][2]);
    }
}
