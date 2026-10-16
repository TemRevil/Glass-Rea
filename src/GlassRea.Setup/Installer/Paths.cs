using System.Reflection;

namespace GlassRea.Setup.Installer;

/// <summary>Where everything goes. Matches what the app and service expect.</summary>
public static class Paths
{
    public const string ProductName = "Glass-Rea";
    public const string Publisher = "Tem Revil";
    public const string ServiceName = "GlassRea";
    public const string FirewallGroup = "Glass-Rea";
    public const string CaptureSession = "GlassRea-Capture";

    public static readonly string DefaultInstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

    /// <summary>Where this run installs: the folder already in use when updating, else Program Files, unless the person picks another.</summary>
    public static string InstallDir { get; set; } = Integration.InstalledLocation() ?? DefaultInstallDir;
    public static string AppDir => Path.Combine(InstallDir, "App");
    public static string ServiceDir => Path.Combine(InstallDir, "Service");
    public static string AppExe => Path.Combine(AppDir, AppExeName);
    public static string ServiceDll => Path.Combine(ServiceDir, "GlassRea.Service.dll");
    public static string Uninstaller => Path.Combine(InstallDir, "Uninstall Glass-Rea.exe");
    public const string AppExeName = "GlassRea.exe";
    /// <summary>Opened when setup finishes; a temrevil.com link that redirects and counts the visit as coming from setup.</summary>
    public const string Website = "https://temrevil.com/revil/ge2CozAa";

    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);
    public static readonly string UserDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);
    public static readonly string StartMenuShortcut = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName + ".lnk");

    public static readonly string DotnetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    public static readonly string DotnetExe = Path.Combine(DotnetDir, "dotnet.exe");

    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GlassRea";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string EventLogKey = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\Glass-Rea";

    /// <summary>The version this setup installs, e.g. "1.0.0".</summary>
    public static string Version { get; } =
        typeof(Paths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static bool IsInsideInstallDir(string? path) =>
        path is not null && Path.GetFullPath(path).StartsWith(InstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns the folder someone picked into the install folder: a "Glass-Rea" folder inside it, unless they picked
    /// one already named that or the existing install. Returns null with a reason when the place can't host a service.
    /// </summary>
    public static string? ChooseInstallDir(string picked, out string? problem)
    {
        problem = null;
        var full = Path.GetFullPath(picked.Trim()).TrimEnd(Path.DirectorySeparatorChar);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) { problem = "Pick a folder on this PC. The recording service starts before network drives connect."; return null; }
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType is not DriveType.Fixed) { problem = "Pick a folder on an internal drive. The recording service starts with Windows, before removable drives are ready."; return null; }
        var target = string.Equals(Path.GetFileName(full), ProductName, StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, ProductName);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (target.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { problem = "Pick a folder outside Windows' own folder."; return null; }
        return target;
    }
}

/// <summary>A plain-text log next to the temp files, so a failed install can be diagnosed afterwards.</summary>
public static class Log
{
    public static readonly string FilePath = Path.Combine(Path.GetTempPath(), "Glass-Rea Setup.log");
    private static readonly Lock Gate = new();

    public static void Write(string line)
    {
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
            catch { /* logging must never stop the install */ }
        }
    }
}
