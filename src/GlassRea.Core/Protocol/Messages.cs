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
/// <param name="Network">For "usage" and "hosts", only what was used on this network (a <see cref="NetworkInfo.Id"/>, 0 for
/// use on no known network); null for every network. For "network.rename", the network to rename.</param>
public sealed record Request(string Op, long From = 0, long To = 0, long AppId = 0, bool Value = false, long AfterId = 0,
    Dictionary<string, string>? Settings = null, long? Network = null);

public sealed record Response(bool Ok, JsonElement? Data = null, string? Error = null);

public sealed record AppUsage(long AppId, string Name, string? Path, string? Service, bool IsTunnel, bool Blocked,
    long NetIn, long NetOut, long LanIn, long LanOut, long FirstSeen);

public sealed record DayTotal(int Day, long NetIn, long NetOut);
public sealed record HourTotal(long Hour, long NetIn, long NetOut);

/// <param name="ConnectionIn">What crossed the internet connection itself: the apps' traffic outside any VPN plus the
/// VPN's wrapped copy of the rest. This is the number an internet provider counts.</param>
public sealed record UsageReport(List<AppUsage> Apps, List<DayTotal> Days, List<HourTotal> Hours,
    long NetIn, long NetOut, long TunnelIn, long TunnelOut, long ImportedBefore, long ConnectionIn = 0, long ConnectionOut = 0);

public sealed record LiveApp(long AppId, string Name, string? Path, long InPerSec, long OutPerSec);
public sealed record LiveSnapshot(long Now, long[] In, long[] Out, List<LiveApp> Top);

public sealed record HostUsage(string Ip, string? Name, long In, long Out, long LastSeen);

/// <param name="Name">The name the device gives itself (Bonjour, NetBIOS, UPnP or DNS); null when it gave none.</param>
/// <param name="Vendor">Who made its network card, from the address's maker prefix; null for private addresses.</param>
/// <param name="Model">A model the device announced ("iPhone", "SM-S911B", "Bravia KD-55X80K").</param>
/// <param name="Kind">One of <see cref="DeviceKinds"/>.</param>
/// <param name="Os">One of <see cref="DeviceOs"/>, when it could be told.</param>
/// <param name="CustomName">The name you gave it on the Devices page; shown instead of anything else.</param>
/// <param name="Away">It has been missing from scans for a while (see <c>AlertText.DeviceLeft</c>).</param>
/// <param name="Network">The hardware address of the router it was last seen behind, so devices of one network are never
/// reported missing while this PC is on another.</param>
public sealed record DeviceInfo(string Mac, string Ip, string? Name, bool Randomized, bool IsGateway, bool IsThisPc,
    long FirstSeen, long LastSeen, string? Vendor = null, string? Model = null, string? Kind = null, string? Os = null,
    string? CustomName = null, bool Away = false, string? Network = null);

/// <summary>A network this PC has been on, as Windows knows it (its network profile).</summary>
/// <param name="Id">0 is the use that has no known network: recorded before networks were, or Windows' own history of a
/// wired network, which doesn't say which one.</param>
/// <param name="Name">What Windows calls it: the Wi-Fi's name, or "Network 2" for a wired one.</param>
/// <param name="CustomName">The name you gave it in Settings > Data; shown instead.</param>
/// <param name="Kind">One of <see cref="NetworkKinds"/>, when known.</param>
/// <param name="Current">This PC is on it now.</param>
public sealed record NetworkInfo(long Id, string Name, string? CustomName, string? Kind, long FirstSeen, long LastSeen, bool Current = false);

public sealed record AlertInfo(long Id, long Time, string Kind, string Title, string Body);

/// <param name="NetworkPublic">Windows has the scanned network marked Public, so devices can't be heard saying their names.</param>
/// <param name="Permissions">What the person has allowed the service to do; null from a service older than 1.2.8.</param>
public sealed record ServiceStatus(string Version, bool Capturing, string? CaptureError, string HistoryImport,
    long TrackingSince, long LastDeviceScan, bool NetworkPublic = false, PermissionSet? Permissions = null);

public static class SettingKeys
{
    public const string DailyLimit = "daily_limit_bytes";
    public const string MonthlyLimit = "monthly_limit_bytes";
    public const string AlertNewApp = "alert_new_app";
    public const string AlertNewDevice = "alert_new_device";
    public const string AlertDevicePresence = "alert_device_presence";
    public const string TunnelProcesses = "tunnel_processes";
    public const string Priority = "priority";
    public const string MonthStartDay = "month_start_day";
    public const string WeekStart = "week_start";
    public const string FirstEtwHour = "first_etw_hour";
    public const string SrumState = "srum_state";
    public const string SrumTunnelSplit = "srum_tunnel_split";
    /// <summary>
    /// <see cref="SrumNetworksDone"/> once the use recorded on no known network has been put on its networks, where
    /// Windows' history tells. "done" was the first pass, before Wi-Fi was counted by its name; it's run again.
    /// </summary>
    public const string SrumNetworks = "srum_networks";
    public const string SrumNetworksDone = "wifi-by-name";
    /// <summary>The first hour live capture marked what went through a VPN; earlier live hours predate the marking.</summary>
    public const string TunnelMarkedFrom = "tunnel_marked_from";
    /// <summary>
    /// When live capture first ran, in unix seconds. <see cref="FirstEtwHour"/> can be earlier: with Usage per app off, importing
    /// Windows' history sets it to where the import stopped.
    /// </summary>
    public const string CaptureStartedAt = "capture_started_at";
}
