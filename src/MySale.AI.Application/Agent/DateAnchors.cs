using System.Globalization;

namespace MySale.AI.Application.Agent;

public readonly record struct DateRange(DateTime StartUtc, DateTime EndUtc)
{
    public string StartIso => DateAnchors.Iso(StartUtc);
    public string EndIso => DateAnchors.Iso(EndUtc);
}

/// <summary>
/// Pre-computed date ranges in the company's time zone, expressed in UTC. Giving the model exact
/// boundaries is far more reliable (especially for small local models) than asking it to do date math.
/// </summary>
public sealed class DateAnchors
{
    public DateTime NowUtc { get; init; }
    public DateTime LocalNow { get; init; }
    public string TimeZoneId { get; init; } = "UTC";
    /// <summary>
    /// Time zone of the stored day boundaries. Equal to <see cref="TimeZoneId"/> when dates are stored as real instants;
    /// "UTC" when the application stores the local wall-clock time as UTC (MySaleBooks: 10 Sep 21:00 local → 2026-09-10T21:00Z),
    /// so a business day is [date 00:00Z, next date 00:00Z) while "today" still follows the business time zone.
    /// </summary>
    public string BoundaryTimeZoneId { get; init; } = "UTC";
    public bool WallClockStorage { get; init; }
    public DateRange Today { get; init; }
    public DateRange Yesterday { get; init; }
    public DateRange Tomorrow { get; init; }
    public DateRange ThisWeek { get; init; }
    public DateRange LastWeek { get; init; }
    public DateRange ThisMonth { get; init; }
    public DateRange LastMonth { get; init; }
    public DateRange ThisYear { get; init; }
    public DateRange LastYear { get; init; }
    public DateRange Last7Days { get; init; }
    public DateRange Last30Days { get; init; }
    public DateRange Last90Days { get; init; }
    /// <summary>Start of this month until the end of today ("this month until today").</summary>
    public DateRange MonthToDate { get; init; }
    /// <summary>Start of this year until the end of today.</summary>
    public DateRange YearToDate { get; init; }
    /// <summary>Financial year containing today (start month from Business:FinancialYearStartMonth; 1 = calendar year).</summary>
    public DateRange ThisFinancialYear { get; init; }
    public DateRange LastFinancialYear { get; init; }
    public DateRange FinancialYearToDate { get; init; }
    public int FinancialYearStartMonth { get; init; } = 1;
    public DateOnly LocalToday => DateOnly.FromDateTime(LocalNow);

    /// <summary>[start of <paramref name="from"/>, start of the day after <paramref name="to"/>) in the business time zone, as UTC.</summary>
    public static DateRange ForLocalDays(DateOnly from, DateOnly to, string? timeZoneId)
    {
        var tz = ResolveTimeZone(timeZoneId);
        return new DateRange(LocalToUtc(from.ToDateTime(TimeOnly.MinValue), tz), LocalToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), tz));
    }

    /// <summary>
    /// Local wall-clock time in <paramref name="tz"/> → UTC. A time skipped by a daylight-saving change (midnight in some
    /// zones, e.g. America/Santiago) moves forward to the first valid local time instead of throwing.
    /// </summary>
    public static DateTime LocalToUtc(DateTime local, TimeZoneInfo tz)
    {
        var value = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; i < 8 && tz.IsInvalidTime(value); i++) value = value.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(value, tz);
    }

    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    public static DateAnchors Compute(DateTime utcNow, string? timeZoneId, int financialYearStartMonth = 1, bool wallClockStorage = false)
    {
        financialYearStartMonth = Math.Clamp(financialYearStartMonth, 1, 12);
        var tz = ResolveTimeZone(timeZoneId);
        utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var today = local.Date;

        var boundaryTz = wallClockStorage ? TimeZoneInfo.Utc : tz;
        DateTime ToUtc(DateTime localDate) => LocalToUtc(localDate, boundaryTz);

        DateRange Range(DateTime startLocal, DateTime endLocal) => new(ToUtc(startLocal), ToUtc(endLocal));

        int diffToMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-diffToMonday);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);
        var fyStart = new DateTime(today.Month >= financialYearStartMonth ? today.Year : today.Year - 1, financialYearStartMonth, 1);

        return new DateAnchors
        {
            NowUtc = utcNow,
            LocalNow = local,
            TimeZoneId = tz.Id,
            BoundaryTimeZoneId = boundaryTz.Id,
            WallClockStorage = wallClockStorage,
            Today = Range(today, today.AddDays(1)),
            Yesterday = Range(today.AddDays(-1), today),
            Tomorrow = Range(today.AddDays(1), today.AddDays(2)),
            ThisWeek = Range(weekStart, weekStart.AddDays(7)),
            LastWeek = Range(weekStart.AddDays(-7), weekStart),
            ThisMonth = Range(monthStart, monthStart.AddMonths(1)),
            LastMonth = Range(monthStart.AddMonths(-1), monthStart),
            ThisYear = Range(yearStart, yearStart.AddYears(1)),
            LastYear = Range(yearStart.AddYears(-1), yearStart),
            Last7Days = Range(today.AddDays(-6), today.AddDays(1)),
            Last30Days = Range(today.AddDays(-29), today.AddDays(1)),
            Last90Days = Range(today.AddDays(-89), today.AddDays(1)),
            MonthToDate = Range(monthStart, today.AddDays(1)),
            YearToDate = Range(yearStart, today.AddDays(1)),
            ThisFinancialYear = Range(fyStart, fyStart.AddYears(1)),
            LastFinancialYear = Range(fyStart.AddYears(-1), fyStart),
            FinancialYearToDate = Range(fyStart, today.AddDays(1)),
            FinancialYearStartMonth = financialYearStartMonth
        };
    }
}
