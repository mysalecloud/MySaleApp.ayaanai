using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Attachments;
using MySale.AI.Domain;
using UglyToad.PdfPig;

namespace MySale.AI.Infrastructure.Media;

/// <summary>PDF text extraction (per page) with PdfPig. Scanned PDFs have no text layer and are reported as such.</summary>
public sealed class PdfProcessor : IAttachmentProcessor
{
    private const int MaxPages = 300;

    public bool CanProcess(string kind) => kind == AttachmentKinds.Pdf;

    public Task<ExtractedContent> ProcessAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        using var document = PdfDocument.Open(ms.ToArray());
        var pages = new List<(int? Page, string Text)>();
        var truncated = false;
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            if (page.Number > MaxPages) { truncated = true; break; }
            // Group words into lines by their vertical position so tables stay readable.
            var lines = page.GetWords()
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3))
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            pages.Add((page.Number, string.Join("\n", lines)));
        }
        var (chunks, cut) = TextChunker.Chunk(pages);
        return Task.FromResult(new ExtractedContent { Chunks = chunks, PageCount = document.NumberOfPages, Truncated = truncated || cut });
    }
}

public sealed class TextFileProcessor : IAttachmentProcessor
{
    public bool CanProcess(string kind) => kind == AttachmentKinds.Text;

    public async Task<ExtractedContent> ProcessAsync(Stream content, string fileName, CancellationToken ct)
    {
        var text = await TextReading.ReadAllAsync(content, ct);
        var (chunks, truncated) = TextChunker.Chunk(new[] { ((int?)null, text) });
        return new ExtractedContent { Chunks = chunks, Truncated = truncated };
    }
}

public sealed class CsvProcessor : IAttachmentProcessor
{
    public const int MaxRows = 50_000;

    public bool CanProcess(string kind) => kind == AttachmentKinds.Csv;

    public async Task<ExtractedContent> ProcessAsync(Stream content, string fileName, CancellationToken ct)
    {
        var text = await TextReading.ReadAllAsync(content, ct);
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t', '|' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        var records = ParseCsv(text, delimiter);
        if (records.Count == 0) return new ExtractedContent();

        var table = TableBuilder.Build(records, null, new List<string>(), MaxRows);
        return new ExtractedContent { Table = table, Truncated = table.TotalRows > table.Rows.Count };
    }

    /// <summary>RFC 4180 parser (quoted fields, escaped quotes, embedded newlines).</summary>
    public static List<List<string?>> ParseCsv(string text, char delimiter)
    {
        var rows = new List<List<string?>>();
        var row = new List<string?>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(row);
                row = new List<string?>();
                if (rows.Count > MaxRows + 1) break;
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(row);
        }
        return rows;
    }
}

/// <summary>
/// Minimal XLSX reader (Office Open XML via System.IO.Compression + LINQ to XML): shared strings, inline strings,
/// numbers, booleans and dates for the first non-empty sheet. Formulas are read as their cached values — never evaluated.
/// </summary>
public sealed class XlsxProcessor : IAttachmentProcessor
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly HashSet<int> BuiltInDateFormats = new() { 14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47 };

    public bool CanProcess(string kind) => kind == AttachmentKinds.Excel;

    public Task<ExtractedContent> ProcessAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        var shared = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyles(zip);
        var sheets = ReadSheets(zip);

        foreach (var (name, path) in sheets)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(path);
            if (entry is null) continue;
            var records = ReadSheet(entry, shared, dateStyles);
            if (records.Count == 0) continue;
            var table = TableBuilder.Build(records, name, sheets.Select(s => s.Name).ToList(), CsvProcessor.MaxRows);
            return Task.FromResult(new ExtractedContent { Table = table, Truncated = table.TotalRows > table.Rows.Count });
        }
        return Task.FromResult(new ExtractedContent());
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        return doc.Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList();
    }

    private static HashSet<int> ReadDateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        var entry = zip.GetEntry("xl/styles.xml");
        if (entry is null) return result;
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        var customDate = doc.Root!.Element(S + "numFmts")?.Elements(S + "numFmt")
            .Where(f => IsDateFormat(f.Attribute("formatCode")?.Value))
            .Select(f => int.TryParse(f.Attribute("numFmtId")?.Value, out var id) ? id : -1).ToHashSet() ?? new HashSet<int>();
        var xfs = doc.Root.Element(S + "cellXfs")?.Elements(S + "xf").ToList() ?? new List<XElement>();
        for (int i = 0; i < xfs.Count; i++)
        {
            var id = int.TryParse(xfs[i].Attribute("numFmtId")?.Value, out var n) ? n : 0;
            if (BuiltInDateFormats.Contains(id) || customDate.Contains(id)) result.Add(i);
        }
        return result;
    }

    /// <summary>Date if it has day/year tokens outside [$AED]-style brackets and "quoted" literals.</summary>
    private static bool IsDateFormat(string? formatCode)
    {
        if (string.IsNullOrEmpty(formatCode)) return false;
        var code = System.Text.RegularExpressions.Regex.Replace(formatCode.ToLowerInvariant(), "\\[[^\\]]*\\]|\"[^\"]*\"|\\\\.", string.Empty);
        return code.Contains('d') || code.Contains('y');
    }

    private static List<(string Name, string Path)> ReadSheets(ZipArchive zip)
    {
        using var wbStream = zip.GetEntry("xl/workbook.xml")!.Open();
        var wb = XDocument.Load(wbStream);
        var rels = new Dictionary<string, string>();
        var relEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (relEntry is not null)
        {
            using var rs = relEntry.Open();
            foreach (var rel in XDocument.Load(rs).Root!.Elements(Pr + "Relationship"))
            {
                var target = rel.Attribute("Target")?.Value ?? "";
                target = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                rels[rel.Attribute("Id")?.Value ?? ""] = target;
            }
        }
        return wb.Root!.Element(S + "sheets")?.Elements(S + "sheet")
            .Select((sh, i) => (Name: sh.Attribute("name")?.Value ?? $"Sheet{i + 1}",
                                Path: rels.TryGetValue(sh.Attribute(R + "id")?.Value ?? "", out var p) ? p : $"xl/worksheets/sheet{i + 1}.xml"))
            .ToList() ?? new List<(string, string)>();
    }

    private static List<List<string?>> ReadSheet(ZipArchiveEntry entry, List<string> shared, HashSet<int> dateStyles)
    {
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        var rows = new List<List<string?>>();
        foreach (var row in doc.Descendants(S + "row"))
        {
            var values = new List<string?>();
            foreach (var c in row.Elements(S + "c"))
            {
                var col = ColumnIndex(c.Attribute("r")?.Value);
                while (col >= 0 && values.Count < col) values.Add(null);
                values.Add(CellValue(c, shared, dateStyles));
            }
            if (values.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(values);
            if (rows.Count > CsvProcessor.MaxRows + 1) break;
        }
        return rows;
    }

    private static string? CellValue(XElement c, List<string> shared, HashSet<int> dateStyles)
    {
        var type = c.Attribute("t")?.Value;
        var v = c.Element(S + "v")?.Value;
        switch (type)
        {
            case "s": return int.TryParse(v, out var i) && i < shared.Count ? shared[i] : null;
            case "inlineStr": return string.Concat(c.Descendants(S + "t").Select(t => t.Value));
            case "b": return v == "1" ? "TRUE" : "FALSE";
            case "str":
            case "e": return v;
        }
        if (v is null) return null;
        var style = int.TryParse(c.Attribute("s")?.Value, out var st) ? st : -1;
        if (dateStyles.Contains(style) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var serial) && serial > 0 && serial < 2958466)
            return DateTime.FromOADate(serial).ToString(serial % 1 == 0 ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return v;
    }

    private static int ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return -1;
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsAsciiLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n - 1;
    }
}

internal static class TableBuilder
{
    private const long MaxStoredChars = 6_000_000;

    /// <summary>First non-empty row = header. Blank/duplicate headers are made unique.</summary>
    public static AttachmentTable Build(List<List<string?>> records, string? sheet, List<string> sheetNames, int maxRows)
    {
        var header = records[0];
        var width = records.Max(r => r.Count);
        var columns = new List<string>();
        for (int i = 0; i < width; i++)
        {
            var name = i < header.Count && !string.IsNullOrWhiteSpace(header[i]) ? header[i]!.Trim() : $"Column{i + 1}";
            var unique = name;
            var k = 2;
            while (columns.Contains(unique, StringComparer.OrdinalIgnoreCase)) unique = $"{name}_{k++}";
            columns.Add(unique);
        }
        var body = records.Skip(1).ToList();
        // The table is stored with the attachment record: stay well below MongoDB's 16 MB document limit.
        var rows = new List<List<string?>>();
        long chars = 0;
        foreach (var r in body)
        {
            if (rows.Count >= maxRows || chars > MaxStoredChars) break;
            var row = r.Select(v => v?.Trim()).ToList();
            chars += row.Sum(v => (v?.Length ?? 0) + 8);
            rows.Add(row);
        }
        var table = new AttachmentTable
        {
            Sheet = sheet,
            SheetNames = sheetNames,
            Columns = columns,
            Rows = rows,
            TotalRows = body.Count
        };
        table.ColumnTypes = TableTypes.Infer(table);
        return table;
    }
}

internal static class TextReading
{
    /// <summary>UTF-8 (with BOM detection); falls back to Windows-1252-ish Latin1 for legacy files.</summary>
    public static async Task<string> ReadAllAsync(Stream content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
