using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassRea.Core.Protocol;

public static class Pipe
{
    public const string Name = "GlassRea";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>One request per line over the named pipe. Times are unix seconds.</summary>
public sealed record Request(string Op, long From = 0, long To = 0, long AppId = 0, bool Value = false, long AfterId = 0,
    Dictionary<string, string>? Settings = null);

public sealed record Response(bool Ok, JsonElement? Data = null, string? Error = null);

public sealed record AppUsage(long AppId, string Name, string? Path, string? Service, bool IsTunnel, bool Blocked,
    long NetIn, long NetOut, long LanIn, long LanOut, long FirstSeen);

public sealed record DayTotal(int Day, long NetIn, long NetOut);
public sealed record HourTotal(long Hour, long NetIn, long NetOut);

public sealed record UsageReport(List<AppUsage> Apps, List<DayTotal> Days, List<HourTotal> Hours,
    long NetIn, long NetOut, long TunnelIn, long TunnelOut, long ImportedBefore);

public sealed record LiveApp(long AppId, string Name, string? Path, long InPerSec, long OutPerSec);
public sealed record LiveSnapshot(long Now, long[] In, long[] Out, List<LiveApp> Top);

public sealed record HostUsage(string Ip, string? Name, long In, long Out, long LastSeen);

/// <param name="Name">The name the device gives itself (Bonjour, NetBIOS, UPnP or DNS); null when it gave none.</param>
/// <param name="Vendor">Who made its network card, from the address's maker prefix; null for private addresses.</param>
/// <param name="Model">A model the device announced ("iPhone", "SM-S911B", "Bravia KD-55X80K").</param>
/// <param name="Kind">One of <see cref="DeviceKinds"/>.</param>
/// <param name="Os">One of <see cref="DeviceOs"/>, when it could be told.</param>
public sealed record DeviceInfo(string Mac, string Ip, string? Name, bool Randomized, bool IsGateway, bool IsThisPc,
    long FirstSeen, long LastSeen, string? Vendor = null, string? Model = null, string? Kind = null, string? Os = null);

public sealed record AlertInfo(long Id, long Time, string Kind, string Title, string Body);

public sealed record ServiceStatus(string Version, bool Capturing, string? CaptureError, string HistoryImport,
    long TrackingSince, long LastDeviceScan);

public static class SettingKeys
{
    public const string DailyLimit = "daily_limit_bytes";
    public const string MonthlyLimit = "monthly_limit_bytes";
    public const string AlertNewApp = "alert_new_app";
    public const string AlertNewDevice = "alert_new_device";
    public const string TunnelProcesses = "tunnel_processes";
    public const string Priority = "priority";
    public const string MonthStartDay = "month_start_day";
    public const string WeekStart = "week_start";
    public const string FirstEtwHour = "first_etw_hour";
    public const string SrumState = "srum_state";
}
