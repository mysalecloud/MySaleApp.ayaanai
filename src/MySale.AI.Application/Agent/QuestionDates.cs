using System.Globalization;
using System.Text.RegularExpressions;

namespace MySale.AI.Application.Agent;

/// <summary>Configuration section "Business": calendar rules of the company.</summary>
public sealed class BusinessCalendarOptions
{
    public const string Section = "Business";

    /// <summary>First month of the financial year (1 = January / calendar year, 4 = April …).</summary>
    public int FinancialYearStartMonth { get; set; } = 1;

    /// <summary>
    /// How numeric dates typed by users are read: "DMY" (01/09/2026 = 1 September, UAE / India), "MDY" (US), "YMD",
    /// or "Auto" (unambiguous dates are read, ambiguous ones such as 01/09/2026 are asked back to the user).
    /// </summary>
    public string DateOrder { get; set; } = "DMY";

    /// <summary>
    /// Business date field per collection when it cannot be recognised from its name, e.g. { "Vouchers": "voucherDate" }.
    /// Period questions must use this field, not createdAt / updatedAt.
    /// </summary>
    public Dictionary<string, string> BusinessDateFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DateMention(string Text, DateOnly Date, int Index, int Length, bool ExplicitYear);

/// <summary>An inclusive range of local days the user asked for ("from 01-09-2026 to 15-09-2026").</summary>
public sealed record DateSpan(string Text, DateOnly From, DateOnly To);

public sealed record AmbiguousDate(string Text, DateOnly DayFirst, DateOnly MonthFirst);

/// <summary>
/// Dates typed in the question, parsed on the server (not by the model) in the business locale:
/// 01/09/2026, 01-09-2026, 2026-09-01, September 1, 2026, 1 September 2026, Sep 1 to Sep 15, from … until today.
/// Every date becomes whole local days; ranges are inclusive of the end day (the query uses $lt the next day).
/// </summary>
public sealed class QuestionDates
{
    public List<DateMention> Dates { get; } = new();
    public List<DateSpan> Ranges { get; } = new();
    /// <summary>Dates that do not exist (31/02/2026) or ranges that end before they start.</summary>
    public List<string> Invalid { get; } = new();
    public List<AmbiguousDate> Ambiguous { get; } = new();

    public bool IsEmpty => Dates.Count == 0 && Ranges.Count == 0 && Invalid.Count == 0 && Ambiguous.Count == 0;

    /// <summary>Single days (not part of a range) and ranges, as inclusive spans.</summary>
    public IEnumerable<DateSpan> Spans
    {
        get
        {
            foreach (var r in Ranges) yield return r;
            foreach (var d in Dates.Where(d => !Ranges.Any(r => r.Text.Contains(d.Text, StringComparison.Ordinal))))
                yield return new DateSpan(d.Text, d.Date, d.Date);
        }
    }

    /// <summary>Last day the user included ("to 10 September" → 10 Sep) — an $lte on this day means the whole day.</summary>
    public IReadOnlySet<DateOnly> InclusiveEndDays => Spans.Select(s => s.To).ToHashSet();
    public IReadOnlySet<DateOnly> MentionedDays => Spans.SelectMany(s => new[] { s.From, s.To }).ToHashSet();

    private static readonly string[] MonthNames =
        { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
    private const string MonthPattern =
        "(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\\.?";

    private static readonly Regex Numeric = new(@"(?<![\w/.-])(\d{1,4})[/.-](\d{1,2})[/.-](\d{4}|\d{2})(?![\w/.-]|-\d)", RegexOptions.Compiled);
    private static readonly Regex DayMonth = new(@"\b(\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?" + MonthPattern + @"(?:,?\s+(\d{4}))?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MonthDay = new(@"\b" + MonthPattern + @"\s+(\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(\d{4}))?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Joiner = new(@"^\s*(?:to|till|until|through|thru|and|up\s+to|-|–|—)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UntilToday = new(@"^\s*(?:to|till|until|through|up\s+to|-|–)\s+(?:today|now|date)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SinceBefore = new(@"\bsince\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static QuestionDates Parse(string? question, DateOnly today, string? dateOrder = "DMY")
    {
        var result = new QuestionDates();
        if (string.IsNullOrWhiteSpace(question)) return result;
        var order = (dateOrder ?? "DMY").Trim().ToUpperInvariant();
        var mentions = new List<DateMention>();
        var taken = new List<(int Start, int End)>();
        bool Free(int start, int length) => !taken.Any(t => start < t.End && start + length > t.Start);

        foreach (Match m in Numeric.Matches(question))
        {
            if (!Free(m.Index, m.Length)) continue;
            var a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var yText = m.Groups[3].Value;
            var y = int.Parse(yText, CultureInfo.InvariantCulture);
            DateOnly? date = null;

            if (m.Groups[1].Value.Length == 4)                     // 2026-09-01 (YMD)
            {
                if (yText.Length == 4) continue;                    // "2026-09-2026" is not a date
                date = Make(a, b, y, result, m.Value);             // y here is the day
            }
            else
            {
                if (m.Groups[1].Value.Length == 3) continue;
                if (yText.Length == 2) y += 2000;
                var dayFirst = Make(y, b, a, null, null);
                var monthFirst = Make(y, a, b, null, null);
                switch (order)
                {
                    case "MDY": date = monthFirst ?? dayFirst ?? AddInvalid(result, m.Value); break; // 25/12/2026 can only be day-first
                    case "AUTO":
                        if (dayFirst is { } df && monthFirst is { } mf && df != mf) { result.Ambiguous.Add(new AmbiguousDate(m.Value, df, mf)); taken.Add((m.Index, m.Index + m.Length)); continue; }
                        date = dayFirst ?? monthFirst ?? AddInvalid(result, m.Value);
                        break;
                    default: date = dayFirst ?? monthFirst ?? AddInvalid(result, m.Value); break; // DMY; 12/25/2026 can only be month-first
                }
            }
            taken.Add((m.Index, m.Index + m.Length));
            if (date is { } d) mentions.Add(new DateMention(m.Value, d, m.Index, m.Length, true));
        }

        foreach (var (regex, dayGroup, monthGroup, yearGroup) in new[] { (DayMonth, 1, 2, 3), (MonthDay, 2, 1, 3) })
            foreach (Match m in regex.Matches(question))
            {
                if (!Free(m.Index, m.Length)) continue;
                var day = int.Parse(m.Groups[dayGroup].Value, CultureInfo.InvariantCulture);
                var month = MonthIndex(m.Groups[monthGroup].Value);
                var explicitYear = m.Groups[yearGroup].Success && m.Groups[yearGroup].Value.Length == 4;
                var year = explicitYear ? int.Parse(m.Groups[yearGroup].Value, CultureInfo.InvariantCulture) : today.Year;
                taken.Add((m.Index, m.Index + m.Length));
                var date = Make(year, month, day, result, m.Value.Trim());
                if (date is { } d) mentions.Add(new DateMention(m.Value.Trim(), d, m.Index, m.Length, explicitYear));
            }

        mentions = mentions.OrderBy(m => m.Index).ToList();
        var inRange = new HashSet<DateMention>();
        for (var i = 0; i < mentions.Count; i++)
        {
            var from = mentions[i];
            var afterFrom = question[(from.Index + from.Length)..];
            if (i + 1 < mentions.Count)
            {
                var to = mentions[i + 1];
                var between = question.Substring(from.Index + from.Length, to.Index - from.Index - from.Length);
                if (Joiner.IsMatch(between))
                {
                    var start = from.Date;
                    var end = to.Date;
                    // "Dec 25 to Jan 5" without years: the start is in the previous year.
                    if (start > end && !from.ExplicitYear && !to.ExplicitYear) start = start.AddYears(-1);
                    // "Sep 1 to Sep 15 2025": a year written only on the end applies to the start too.
                    if (!from.ExplicitYear && to.ExplicitYear) start = new DateOnly(to.Date.Year, start.Month, Math.Min(start.Day, DateTime.DaysInMonth(to.Date.Year, start.Month)));
                    var text = question.Substring(from.Index, to.Index + to.Length - from.Index);
                    if (start > end) result.Invalid.Add($"{text} (the end date is before the start date)");
                    else result.Ranges.Add(new DateSpan(text, start, end));
                    inRange.Add(from);
                    inRange.Add(to);
                    i++;
                    continue;
                }
            }
            if (UntilToday.Match(afterFrom) is { Success: true } until)
            {
                var text = question.Substring(from.Index, from.Length + until.Length);
                if (from.Date > today) result.Invalid.Add($"{text} (the start date is after today)");
                else result.Ranges.Add(new DateSpan(text.Trim(), from.Date, today));
                inRange.Add(from);
                continue;
            }
            if (SinceBefore.IsMatch(question[..from.Index]) && from.Date <= today)
            {
                result.Ranges.Add(new DateSpan("since " + from.Text, from.Date, today));
                inRange.Add(from);
            }
        }
        foreach (var m in mentions) result.Dates.Add(m);
        // Keep the Dates list for single-day mentions only.
        result.Dates.RemoveAll(inRange.Contains);
        return result;
    }

    private static int MonthIndex(string text)
    {
        var t = text.Trim('.').ToLowerInvariant();
        for (var i = 0; i < MonthNames.Length; i++)
            if (MonthNames[i].StartsWith(t[..Math.Min(3, t.Length)], StringComparison.Ordinal)) return i + 1;
        return 0;
    }

    private static DateOnly? Make(int year, int month, int day, QuestionDates? result, string? text)
    {
        if (year is >= 1900 and <= 2200 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            return new DateOnly(year, month, day);
        if (result is not null && text is not null) result.Invalid.Add(text);
        return null;
    }

    private static DateOnly? AddInvalid(QuestionDates result, string text)
    {
        result.Invalid.Add(text);
        return null;
    }

    public static string Describe(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
