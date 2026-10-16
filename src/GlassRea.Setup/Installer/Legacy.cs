using Microsoft.Win32;

namespace GlassRea.Setup.Installer;

/// <summary>
/// The app used to be called "Glasswire REA". Installing Glass-Rea over it moves the history across and removes
/// every trace of the old name: its service, folder, Start entry, Installed apps entry and sign-in entry. App blocks
/// in Windows Firewall are renamed rather than removed, so nothing that was blocked comes unblocked.
/// </summary>
public static class Legacy
{
    private const string Name = "Glasswire REA";
    private const string ServiceName = "GlasswireREA";
    private const string ProcessName = "GlasswireRea";
    private const string CaptureSession = "GlasswireREA-Capture";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GlasswireREA";
    private const string EventLogKey = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\Glasswire REA";

    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name);
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Name);
    private static readonly string UserDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
    private static readonly string StartMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Name + ".lnk");

    public static bool IsPresent() =>
        ServiceSetup.Exists(ServiceName) || Directory.Exists(InstallDir) || Directory.Exists(DataDir) || Registry.LocalMachine.OpenSubKey(UninstallKey) is not null;

    /// <summary>Whether the old app was set to open at sign-in, so the new one keeps doing so.</summary>
    public static bool StartedWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Paths.RunKey);
        return key?.GetValue(Name) is not null;
    }

    /// <summary>Stops the old service and app so their files and database are free.</summary>
    public static void Close()
    {
        ServiceSetup.Stop(ServiceName);
        AppProcess.CloseAll(ProcessName);
    }

    /// <summary>History and appearance move to the new folders, unless the new ones already hold data.</summary>
    public static void MoveData(IProgress<string> status)
    {
        foreach (var (from, to) in new[] { (DataDir, Paths.DataDir), (UserDir, Paths.UserDir) })
        {
            if (!Directory.Exists(from)) continue;
            if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            {
                Log.Write($"Kept {to}; left the old {from} in place");
                continue;
            }
            status.Report("Moving your history to the new name");
            if (Directory.Exists(to)) Directory.Delete(to);
            Folders.Move(from, to);
            Log.Write($"Moved {from} to {to}");
        }
    }

    /// <summary>Firewall blocks keep blocking, under the new group and names the service looks for.</summary>
    public static int RenameFirewallRules()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (type is null) return 0;
        dynamic policy = Activator.CreateInstance(type)!;
        var renamed = 0;
        foreach (dynamic rule in policy.Rules)
        {
            if (!string.Equals((string?)rule.Grouping, Name, StringComparison.OrdinalIgnoreCase)) continue;
            var oldName = (string)rule.Name;
            rule.Name = oldName.StartsWith(Name, StringComparison.Ordinal) ? Paths.ProductName + oldName[Name.Length..] : oldName;
            rule.Grouping = Paths.FirewallGroup;
            if (rule.Description is string d) rule.Description = d.Replace(Name, Paths.ProductName, StringComparison.Ordinal);
            renamed++;
        }
        Log.Write($"Renamed {renamed} old firewall rules");
        return renamed;
    }

    /// <summary>Everything left under the old name goes. Run after the new copy is in place.</summary>
    public static void Remove()
    {
        if (ServiceSetup.Exists(ServiceName))
        {
            ServiceSetup.Run("sc.exe", "delete", ServiceName);
            for (var i = 0; i < 50 && ServiceSetup.Exists(ServiceName); i++) Thread.Sleep(100);
        }
        ServiceSetup.Run("logman.exe", "stop", CaptureSession, "-ets");
        Registry.LocalMachine.DeleteSubKeyTree(EventLogKey, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        using (var run = Registry.CurrentUser.CreateSubKey(Paths.RunKey)) run.DeleteValue(Name, throwOnMissingValue: false);
        if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut);
        Folders.Delete(InstallDir);
        Log.Write("Removed the old Glasswire REA install");
    }
}
