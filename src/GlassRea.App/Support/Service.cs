using System.Globalization;
using GlassRea.Core.Protocol;

namespace GlassRea.App.Support;

/// <summary>The one connection to the background service.</summary>
public static class Service
{
    public static readonly PipeClient Client = new();

    public static Task<T> Call<T>(string op, long from = 0, long to = 0, long appId = 0, bool value = false, long afterId = 0,
        Dictionary<string, string>? settings = null) =>
        Client.Call<T>(new Request(op, from, to, appId, value, afterId, settings));

    public static Task Do(string op, long appId = 0, bool value = false, Dictionary<string, string>? settings = null) =>
        Client.Call<object?>(new Request(op, AppId: appId, Value: value, Settings: settings));

    /// <summary>True for the failures that mean "the service isn't reachable right now", as opposed to a bug.</summary>
    public static bool IsOffline(Exception e) => e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException;
}

/// <summary>Text for numbers, sizes and dates. Invariant culture throughout, so digits are always 0-9.</summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Bytes(long b) => Core.Alerts.Bytes.Format(b);

    public static string Rate(long bytesPerSec) => bytesPerSec <= 0 ? "0 B/s" : Bytes(bytesPerSec) + "/s";

    public static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("d MMM yyyy", Inv);

    public static string DateTime(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("d MMM yyyy, HH:mm", Inv);

    public static string Ago(long unix)
    {
        var d = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix);
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        return Date(unix);
    }

    public static bool TryParseNumber(string text, out double value) => Core.Text.Numbers.TryParse(text, out value);

    public static string Number(double v) => v.ToString("0.##", Inv);

    public static string Percent(double share) => Math.Round(share * 100).ToString(Inv) + "%";
}

public static class Periods
{
    public enum Kind { Day, Week, Month }

    /// <summary>The day of the month usage starts counting again (Settings > Data), 1 by default.</summary>
    public static int MonthDay { get; private set; } = Core.Usage.Cycle.DefaultMonthDay;
    /// <summary>The first day of the week (Settings > Data), Saturday by default.</summary>
    public static DayOfWeek WeekStart { get; private set; } = Core.Usage.Cycle.DefaultWeekStart;

    /// <summary>Raised when the reset day or week start changed, so period pickers can recompute.</summary>
    public static event Action? Changed;

    /// <summary>Takes the reset settings from the service's settings.</summary>
    public static void Use(IReadOnlyDictionary<string, string> settings)
    {
        var day = Core.Usage.Cycle.ParseMonthDay(settings.GetValueOrDefault(SettingKeys.MonthStartDay));
        var week = Core.Usage.Cycle.ParseWeekStart(settings.GetValueOrDefault(SettingKeys.WeekStart));
        if (day == MonthDay && week == WeekStart) return;
        MonthDay = day;
        WeekStart = week;
        Changed?.Invoke();
    }

    /// <summary>[from, to) in unix seconds for the period containing <paramref name="anchor"/>, in local time.</summary>
    public static (long From, long To, string Label) Range(Kind kind, DateTime anchor)
    {
        var inv = CultureInfo.InvariantCulture;
        DateTime start, end;
        string label;
        switch (kind)
        {
            case Kind.Day:
                start = anchor.Date; end = start.AddDays(1);
                label = start == System.DateTime.Today ? "Today, " + start.ToString("dddd d MMMM", inv) : start.ToString("dddd, d MMM yyyy", inv);
                break;
            case Kind.Week:
                (start, end) = Core.Usage.Cycle.Week(anchor, WeekStart);
                label = $"{start.ToString("d MMM", inv)} - {end.AddDays(-1).ToString("d MMM yyyy", inv)}";
                break;
            default:
                (start, end) = Core.Usage.Cycle.Month(anchor, MonthDay);
                label = MonthDay == 1 ? start.ToString("MMMM yyyy", inv)
                    : $"{start.ToString("d MMM", inv)} - {end.AddDays(-1).ToString("d MMM yyyy", inv)}";
                break;
        }
        return (new DateTimeOffset(start).ToUnixTimeSeconds(), new DateTimeOffset(end).ToUnixTimeSeconds(), label);
    }

    public static DateTime Step(Kind kind, DateTime anchor, int direction) => kind switch
    {
        Kind.Day => anchor.AddDays(direction),
        Kind.Week => anchor.AddDays(7 * direction),
        _ => anchor.AddMonths(direction),
    };
}
