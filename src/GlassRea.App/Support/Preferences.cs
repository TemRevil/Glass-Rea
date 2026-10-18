using System.Text.Json;

namespace GlassRea.App.Support;

/// <summary>What Settings > General, Notifications and About keep on this PC, for this Windows user.</summary>
public sealed class PreferencesState
{
    /// <summary>Minimizing hides the window to the tray instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }
    /// <summary>Closing the window keeps the app in the tray. When off, closing quits it.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>New alerts pop up as Windows notifications. They're listed on the Alerts page either way.</summary>
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Look for a newer release on GitHub once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Unix seconds of the last update check.</summary>
    public long LastUpdateCheck { get; set; }
    /// <summary>The release tag already announced in a notification, so each new version is announced once.</summary>
    public string? AnnouncedVersion { get; set; }
    /// <summary>The network the pages count (the floating bar's network switch); null for all of them.</summary>
    public long? Network { get; set; }
}

public static class Preferences
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glass-Rea", "preferences.json");

    public static PreferencesState State { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) State = JsonSerializer.Deserialize<PreferencesState>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { State = new(); }
    }

    public static void Update(Action<PreferencesState> change)
    {
        change(State);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(State));
        }
        catch { /* a read-only profile keeps the choice for this run only */ }
    }
}
