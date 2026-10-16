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

    /// <summary>[from, to) in unix seconds for the period containing <paramref name="anchor"/>, in local time. Weeks start Saturday.</summary>
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
                var back = ((int)anchor.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
                start = anchor.Date.AddDays(-back); end = start.AddDays(7);
                label = $"{start.ToString("d MMM", inv)} - {end.AddDays(-1).ToString("d MMM yyyy", inv)}";
                break;
            default:
                start = new DateTime(anchor.Year, anchor.Month, 1); end = start.AddMonths(1);
                label = start.ToString("MMMM yyyy", inv);
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
