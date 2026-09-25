using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MySale.AI.Domain;

namespace MySale.AI.Application.Attachments;

/// <summary>Splits extracted text into ~1,500 character chunks on paragraph / line boundaries.</summary>
public static class TextChunker
{
    public const int ChunkSize = 1500;
    public const int MaxChunks = 400; // ~600k characters per file

    public static (List<AttachmentChunk> Chunks, bool Truncated) Chunk(IEnumerable<(int? Page, string Text)> pages)
    {
        var chunks = new List<AttachmentChunk>();
        foreach (var (page, text) in pages)
        {
            var clean = Normalize(text);
            var start = 0;
            while (start < clean.Length)
            {
                if (chunks.Count >= MaxChunks) return (chunks, true);
                var len = Math.Min(ChunkSize, clean.Length - start);
                if (start + len < clean.Length)
                {
                    var cut = clean.LastIndexOf('\n', start + len, len);
                    if (cut > start + ChunkSize / 2) len = cut - start;
                }
                var piece = clean.Substring(start, len).Trim();
                if (piece.Length > 0) chunks.Add(new AttachmentChunk { Index = chunks.Count, Page = page, Text = piece });
                start += len;
            }
        }
        return (chunks, false);
    }

    private static string Normalize(string text)
        => Regex.Replace(Regex.Replace(text.Replace("\r\n", "\n"), @"[ \t]{2,}", " "), @"\n{3,}", "\n\n");
}

/// <summary>
/// Picks the chunks most relevant to a question (keyword overlap, lightweight BM25-style), within a character budget.
/// Falls back to the beginning of the document when nothing matches (e.g. a Malayalam question about an English PDF).
/// </summary>
public static class ContentRetriever
{
    private static readonly Regex Word = new(@"[\p{L}\p{N}]{2,}", RegexOptions.Compiled);

    public static List<AttachmentChunk> Relevant(IReadOnlyList<AttachmentChunk> chunks, string question, int budgetChars)
    {
        if (chunks.Count == 0) return new List<AttachmentChunk>();
        var total = chunks.Sum(c => c.Text.Length);
        if (total <= budgetChars) return chunks.ToList();

        var terms = Terms(question);
        var df = new Dictionary<string, int>();
        var chunkTerms = chunks.Select(c => Terms(c.Text)).ToList();
        foreach (var set in chunkTerms)
            foreach (var t in set.Distinct())
                df[t] = df.GetValueOrDefault(t) + 1;

        var scored = chunks.Select((c, i) =>
        {
            double score = 0;
            foreach (var t in terms.Distinct())
            {
                var tf = chunkTerms[i].Count(x => x == t);
                if (tf == 0) continue;
                var idf = Math.Log(1 + (double)chunks.Count / (1 + df.GetValueOrDefault(t)));
                score += idf * (tf * 2.2) / (tf + 1.2);
            }
            // Totals / summaries tend to be what business questions need.
            if (Regex.IsMatch(c.Text, @"(?i)\b(total|grand total|net|amount due|balance|subtotal)\b")) score += 0.5;
            return (Chunk: c, Score: score);
        }).ToList();

        var picked = new List<AttachmentChunk>();
        var used = 0;
        var ordered = scored.Max(s => s.Score) > 0
            ? scored.OrderByDescending(s => s.Score).Select(s => s.Chunk)
            : chunks.AsEnumerable();
        foreach (var c in ordered)
        {
            if (used + c.Text.Length > budgetChars && picked.Count > 0) break;
            picked.Add(c);
            used += c.Text.Length;
        }
        return picked.OrderBy(c => c.Index).ToList();
    }

    private static List<string> Terms(string text)
        => Word.Matches(text.ToLowerInvariant()).Select(m => m.Value.Length > 4 && m.Value.EndsWith('s') ? m.Value[..^1] : m.Value).ToList();
}

/// <summary>Masks e-mails, phone numbers and long digit runs (card / account numbers) in debug output.</summary>
public static class SensitiveDataMasker
{
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"(?<!\w)\+?\d[\d \-]{7,}\d(?!\w)", RegexOptions.Compiled);
    private static readonly Regex LongDigits = new(@"\b\d{12,19}\b", RegexOptions.Compiled);

    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        text = Email.Replace(text, m => m.Value[..Math.Min(2, m.Value.Length)] + "***@***");
        text = LongDigits.Replace(text, m => new string('•', m.Value.Length - 4) + m.Value[^4..]);
        text = Phone.Replace(text, m => m.Value.Length > 4 ? new string('•', m.Value.Length - 3) + m.Value[^3..] : m.Value);
        return text;
    }
}

/// <summary>Column type inference and number parsing for CSV / Excel tables.</summary>
public static class TableTypes
{
    public static List<string> Infer(AttachmentTable t)
    {
        var types = new List<string>();
        for (int c = 0; c < t.Columns.Count; c++)
        {
            var values = t.Rows.Take(500).Select(r => c < r.Count ? r[c] : null).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (values.Count == 0) { types.Add("text"); continue; }
            if (values.All(v => TryNumber(v, out _))) types.Add("number");
            else if (values.All(v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && !TryNumber(v, out _))) types.Add("date");
            else types.Add("text");
        }
        return types;
    }

    public static bool TryNumber(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var clean = s.Trim().Replace(",", string.Empty).Replace(" ", string.Empty);
        clean = Regex.Replace(clean, @"^(AED|SAR|INR|USD|Rs\.?|₹|\$)", string.Empty, RegexOptions.IgnoreCase);
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Markdown preview of the first rows (for prompts / debug).</summary>
    public static string Preview(AttachmentTable t, int rows, int maxChars = 4000)
    {
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", t.Columns)).AppendLine(" |");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", t.Columns.Count))).AppendLine();
        foreach (var r in t.Rows.Take(rows))
        {
            sb.Append("| ").Append(string.Join(" | ", t.Columns.Select((_, i) => (i < r.Count ? r[i] : "")?.Replace("|", "/") ?? ""))).AppendLine(" |");
            if (sb.Length > maxChars) break;
        }
        return sb.ToString();
    }
}
