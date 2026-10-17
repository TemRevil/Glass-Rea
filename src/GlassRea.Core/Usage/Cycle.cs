using System.Globalization;

namespace GlassRea.Core.Usage;

/// <summary>
/// When usage counts start again: the day of the month the monthly count resets (to match a provider's billing
/// cycle) and the first day of the week. Days 29 to 31 fall on the last day of shorter months. Dates are local.
/// </summary>
public static class Cycle
{
    public const int DefaultMonthDay = 1;
    public const DayOfWeek DefaultWeekStart = DayOfWeek.Saturday;
    public static readonly DayOfWeek[] WeekStarts = [DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday];

    /// <summary>The setting's text for a month day, "1".."31"; anything else means the 1st.</summary>
    public static int ParseMonthDay(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 31 ? d : DefaultMonthDay;

    /// <summary>The setting's text for a week start, "saturday", "sunday" or "monday"; anything else means Saturday.</summary>
    public static DayOfWeek ParseWeekStart(string? text) =>
        WeekStarts.FirstOrDefault(d => string.Equals(d.ToString(), text, StringComparison.OrdinalIgnoreCase), DefaultWeekStart);

    public static bool IsValidMonthDay(string? text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 31;
    public static bool IsValidWeekStart(string? text) => WeekStarts.Any(d => string.Equals(d.ToString(), text, StringComparison.OrdinalIgnoreCase));

    public static string WeekStartKey(DayOfWeek d) => d.ToString().ToLowerInvariant();

    /// <summary>The reset day within a given month: <paramref name="day"/>, or the month's last day if it's shorter.</summary>
    public static DateTime ResetIn(int year, int month, int day) => new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    /// <summary>The monthly period containing <paramref name="anchor"/>: [start, end) as local midnights.</summary>
    public static (DateTime Start, DateTime End) Month(DateTime anchor, int day)
    {
        var start = ResetIn(anchor.Year, anchor.Month, day);
        if (anchor.Date < start)
        {
            var prev = anchor.AddMonths(-1);
            start = ResetIn(prev.Year, prev.Month, day);
        }
        var next = start.AddMonths(1);
        return (start, ResetIn(next.Year, next.Month, day));
    }

    /// <summary>The week containing <paramref name="anchor"/>: [start, start + 7 days).</summary>
    public static (DateTime Start, DateTime End) Week(DateTime anchor, DayOfWeek first)
    {
        var start = anchor.Date.AddDays(-(((int)anchor.DayOfWeek - (int)first + 7) % 7));
        return (start, start.AddDays(7));
    }

    /// <summary>"1st", "2nd", "23rd", "31st".</summary>
    public static string Ordinal(int day) => day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}
