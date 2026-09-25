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
    public DateRange Today { get; init; }
    public DateRange Yesterday { get; init; }
    public DateRange ThisWeek { get; init; }
    public DateRange LastWeek { get; init; }
    public DateRange ThisMonth { get; init; }
    public DateRange LastMonth { get; init; }
    public DateRange ThisYear { get; init; }
    public DateRange LastYear { get; init; }
    public DateRange Last7Days { get; init; }
    public DateRange Last30Days { get; init; }
    public DateRange Last90Days { get; init; }

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

    public static DateAnchors Compute(DateTime utcNow, string? timeZoneId)
    {
        var tz = ResolveTimeZone(timeZoneId);
        utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var today = local.Date;

        DateTime ToUtc(DateTime localDate) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), tz);

        DateRange Range(DateTime startLocal, DateTime endLocal) => new(ToUtc(startLocal), ToUtc(endLocal));

        int diffToMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-diffToMonday);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);

        return new DateAnchors
        {
            NowUtc = utcNow,
            LocalNow = local,
            TimeZoneId = tz.Id,
            Today = Range(today, today.AddDays(1)),
            Yesterday = Range(today.AddDays(-1), today),
            ThisWeek = Range(weekStart, weekStart.AddDays(7)),
            LastWeek = Range(weekStart.AddDays(-7), weekStart),
            ThisMonth = Range(monthStart, monthStart.AddMonths(1)),
            LastMonth = Range(monthStart.AddMonths(-1), monthStart),
            ThisYear = Range(yearStart, yearStart.AddYears(1)),
            LastYear = Range(yearStart.AddYears(-1), yearStart),
            Last7Days = Range(today.AddDays(-6), today.AddDays(1)),
            Last30Days = Range(today.AddDays(-29), today.AddDays(1)),
            Last90Days = Range(today.AddDays(-89), today.AddDays(1))
        };
    }
}
