using System.Globalization;
using GlassRea.Core.Protocol;

namespace GlassRea.App.Support;

/// <summary>The one connection to the background service.</summary>
public static class Service
{
    public static readonly PipeClient Client = new();

    private static bool _offline;

    public static Task<T> Call<T>(string op, long from = 0, long to = 0, long appId = 0, bool value = false, long afterId = 0,
        Dictionary<string, string>? settings = null, long? network = null) =>
        Logged<T>(new Request(op, from, to, appId, value, afterId, settings, network));

    public static Task Do(string op, long appId = 0, bool value = false, Dictionary<string, string>? settings = null, long? network = null) =>
        Logged<object?>(new Request(op, AppId: appId, Value: value, Settings: settings, Network: network));

    /// <summary>Every call, timed: slow ones, failures and the service going away or coming back are logged.</summary>
    private static async Task<T> Logged<T>(Request request)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var answer = await Client.Call<T>(request);
            if (_offline) { _offline = false; Log.Info("Service", "reachable again"); }
            Log.IfSlow("Service", $"The {request.Op} request", System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return answer;
        }
        catch (Exception e) when (IsOffline(e))
        {
            if (!_offline) { _offline = true; Log.Warn("Service", $"not reachable ({request.Op}: {e.GetType().Name}: {e.Message})"); }
            throw;
        }
        catch (ServiceException e)
        {
            Log.Info("Service", $"refused {request.Op}: {e.Message}");
            throw;
        }
        catch (Exception e)
        {
            Log.Error("Service", $"The {request.Op} request failed", e);
            throw;
        }
    }

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

/// <summary>
/// The network Overview, Apps and Connections count: every network together (null, the default each time the app
/// starts) or one of them. Limits always count every network.
/// </summary>
public static class Networks
{
    public static long? Selected { get; private set; }
    /// <summary>The networks the service knows, as it last listed them.</summary>
    public static IReadOnlyList<NetworkInfo> Known { get; private set; } = [];

    /// <summary>Raised when another network is picked, so the pages count it.</summary>
    public static event Action? Changed;
    /// <summary>Raised when the list or a name in it changed, so pickers show it.</summary>
    public static event Action? ListChanged;

    /// <summary>Takes the service's list. A picked network that's no longer in it goes back to all networks.</summary>
    public static void Use(List<NetworkInfo> list)
    {
        static string Shape(IEnumerable<NetworkInfo> l) => string.Join("\n", l.Select(n => $"{n.Id}\t{NetworkLabel.For(n)}\t{n.Kind}\t{n.Current}"));
        if (Shape(list) == Shape(Known)) return;
        Known = list;
        ListChanged?.Invoke();
        if (Selected is { } id && list.All(n => n.Id != id)) Select(null);
    }

    public static void Select(long? id)
    {
        if (id == Selected) return;
        Selected = id;
        Changed?.Invoke();
    }

    /// <summary>The picked network for a sentence ("used today on Home"); "" for all networks.</summary>
    public static string On => Selected switch
    {
        null => "",
        NetworkLabel.Unknown => " on an unknown network",
        var id => " on " + (Known.FirstOrDefault(n => n.Id == id) is { } n ? NetworkLabel.For(n) : "this network"),
    };
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
