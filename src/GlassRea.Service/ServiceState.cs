using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;

namespace GlassRea.Service;

/// <summary>Runtime status shared between the background workers and the pipe server.</summary>
public sealed class ServiceState(Database db)
{
    public volatile bool Capturing;
    public volatile string? CaptureError;
    public long LastDeviceScan;

    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Glass-Rea");

    public static readonly Dictionary<string, string> Defaults = new()
    {
        [SettingKeys.DailyLimit] = "0",
        [SettingKeys.MonthlyLimit] = "0",
        [SettingKeys.AlertNewApp] = "true",
        [SettingKeys.AlertNewDevice] = "true",
        [SettingKeys.TunnelProcesses] = string.Join(",", TunnelProcesses.Defaults),
        [SettingKeys.Priority] = Priority.Normal,
        [SettingKeys.MonthStartDay] = Cycle.DefaultMonthDay.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [SettingKeys.WeekStart] = Cycle.WeekStartKey(Cycle.DefaultWeekStart),
    };

    public string Setting(string key) => db.GetSetting(key) ?? Defaults.GetValueOrDefault(key, "");

    public bool Flag(string key) => Setting(key) == "true";

    public long Number(string key) => long.TryParse(Setting(key), out var n) ? n : 0;

    public string[] TunnelNames() =>
        Setting(SettingKeys.TunnelProcesses).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The first hour live capture covered. Windows history is only imported before it, so nothing is counted twice.</summary>
    public long FirstLiveHour => long.TryParse(db.GetSetting(SettingKeys.FirstEtwHour), out var h) ? h : 0;

    public ServiceStatus Snapshot() => new(
        typeof(ServiceState).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        Capturing, CaptureError, db.GetSetting(SettingKeys.SrumState) ?? "pending",
        FirstLiveHour * 3600, Interlocked.Read(ref LastDeviceScan));
}
