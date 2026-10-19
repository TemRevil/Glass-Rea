using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace GlassRea.Setup.Installer;

/// <summary>The recording service: registered to run as "dotnet.exe GlassRea.Service.dll" (Smart App Control only
/// lets signed binaries start as services, and dotnet.exe is Microsoft-signed).</summary>
public static class ServiceSetup
{
    private const string About = "Records network use per app, live traffic, connections, Wi-Fi devices and firewall blocks for Glass-Rea.";

    public static bool Exists(string name = Paths.ServiceName) =>
        ServiceController.GetServices().Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void Stop(string name = Paths.ServiceName)
    {
        if (!Exists(name)) return;
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        if (sc.Status is not ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    public static void Register()
    {
        var image = $"\"{Paths.DotnetExe}\" \"{Paths.ServiceDll}\"";
        if (Exists()) Sc("config", Paths.ServiceName, "binPath=", image, "start=", "auto", "DisplayName=", Paths.ProductName);
        else Sc("create", Paths.ServiceName, "binPath=", image, "start=", "auto", "DisplayName=", Paths.ProductName);
        Sc("description", Paths.ServiceName, About);
        Sc("failure", Paths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
        // Its own identity, so a firewall rule can name this service alone (the device discovery rule does).
        Sc("sidtype", Paths.ServiceName, "unrestricted");

        // Event log source the service writes to; same message file .NET uses for its own sources.
        using var key = Registry.LocalMachine.CreateSubKey(Paths.EventLogKey);
        key.SetValue("EventMessageFile", @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll", RegistryValueKind.ExpandString);
    }

    public static void Start()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status is ServiceControllerStatus.Running) return;
        sc.Start();
        try { sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
        catch (System.ServiceProcess.TimeoutException) { throw new SetupException("The recording service didn't start within 30 seconds. Windows' Event Viewer, under Application, has the reason."); }
    }

    public static void Delete()
    {
        if (!Exists()) return;
        Stop();
        Sc("delete", Paths.ServiceName);
        for (var i = 0; i < 50 && Exists(); i++) Thread.Sleep(100);
        // A service that crashed can leave its kernel capture session behind.
        Run("logman.exe", "stop", Paths.CaptureSession, "-ets");
        Registry.LocalMachine.DeleteSubKeyTree(Paths.EventLogKey, throwOnMissingSubKey: false);
    }

    private static void Sc(params string[] args)
    {
        var (code, output) = Run("sc.exe", args);
        if (code != 0) throw new SetupException($"Windows refused to {args[0]} the service: {output.Trim()}");
    }

    public static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Log.Write($"{exe} {string.Join(' ', args)} -> {p.ExitCode}");
        return (p.ExitCode, output);
    }
}

/// <summary>The app's own windows: closed before files are replaced, opened again as the signed-in user.</summary>
public static class AppProcess
{
    public static void CloseAll(string name = "GlassRea")
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                // The Microsoft Store copy runs under the same name; it isn't this one's to close.
                if (IsStoreCopy(p)) continue;
                try { p.Kill(); p.WaitForExit(5000); }
                catch (Exception ex) { Log.Write($"Could not close {name} ({p.Id}): {ex.Message}"); }
            }
        }
    }

    private static bool IsStoreCopy(Process p)
    {
        try { return p.MainModule?.FileName.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>Starts the app as the user, not with this installer's admin rights.</summary>
    public static void OpenAsUser() => UserShell.Open(Paths.AppExe);

    /// <summary>Opens a web page in the person's own browser, unelevated.</summary>
    public static void OpenWebsiteAsUser(string url) => UserShell.Open(url);
}

/// <summary>Start menu, Installed apps, and start-with-Windows entries.</summary>
public static class Integration
{
    public static void WriteUninstallEntry(long sizeBytes)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Paths.UninstallKey);
        key.SetValue("DisplayName", Paths.ProductName);
        key.SetValue("DisplayVersion", Paths.Version);
        key.SetValue("Publisher", Paths.Publisher);
        key.SetValue("DisplayIcon", $"\"{Paths.AppExe}\",0");
        key.SetValue("InstallLocation", Paths.InstallDir);
        key.SetValue("UninstallString", $"\"{Paths.Uninstaller}\" --uninstall");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)(sizeBytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public static string? InstalledVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Paths.UninstallKey);
        if (key?.GetValue("DisplayVersion") is string v) return v;
        // Installed by the old script: no entry, but the files are there.
        return File.Exists(Paths.AppExe) ? FileVersionInfo.GetVersionInfo(Paths.AppExe).ProductVersion?.Split('+')[0] ?? "" : null;
    }

    /// <summary>The folder the installed copy lives in, from its Installed apps entry, or null when not installed.</summary>
    public static string? InstalledLocation()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Paths.UninstallKey);
        return key?.GetValue("InstallLocation") is string dir && dir.Length > 0 && Directory.Exists(dir) ? dir : null;
    }

    public static void RemoveUninstallEntry() => Registry.LocalMachine.DeleteSubKeyTree(Paths.UninstallKey, throwOnMissingSubKey: false);

    public static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Paths.RunKey);
        return key?.GetValue(Paths.ProductName) is not null;
    }

    public static void SetStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Paths.RunKey);
        // Keeps the "start hidden in the tray" choice made in the app's settings; new entries start hidden.
        var hidden = key.GetValue(Paths.ProductName) is not string old || old.Contains("--tray", StringComparison.Ordinal);
        if (on) key.SetValue(Paths.ProductName, $"\"{Paths.AppExe}\"" + (hidden ? " --tray" : ""));
        else key.DeleteValue(Paths.ProductName, throwOnMissingValue: false);
    }

    public static void CreateStartMenuShortcut()
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(Paths.AppExe);
        link.SetWorkingDirectory(Paths.AppDir);
        link.SetDescription("See what every app does with your internet");
        link.SetIconLocation(Paths.AppExe, 0);
        ((IPersistFile)link).Save(Paths.StartMenuShortcut, true);
    }

    public static void RemoveStartMenuShortcut()
    {
        if (File.Exists(Paths.StartMenuShortcut)) File.Delete(Paths.StartMenuShortcut);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(nint pszFile, int cch, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription(nint pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(nint pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(nint pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(nint pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile(out nint ppszFileName);
    }
}

/// <summary>Every block the app made, removed, so no program stays cut off with nothing left to unblock it.</summary>
public static class FirewallCleanup
{
    public static int RemoveAll()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new SetupException("Windows Firewall isn't available.");
        dynamic policy = Activator.CreateInstance(type)!;
        var names = new List<string>();
        foreach (dynamic rule in policy.Rules)
            if (string.Equals((string?)rule.Grouping, Paths.FirewallGroup, StringComparison.OrdinalIgnoreCase)) names.Add((string)rule.Name);
        foreach (var name in names) policy.Rules.Remove(name);
        Log.Write($"Removed {names.Count} firewall rules");
        return names.Count;
    }
}

/// <summary>Deletes a folder, retrying while Windows still holds files from a process that just exited.</summary>
public static class Folders
{
    public static void Delete(string dir)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Moves a folder; across drives it copies then deletes.</summary>
    public static void Move(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) { Directory.Move(from, to); return; }
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        Delete(from);
    }

    /// <summary>
    /// The service runs as SYSTEM from the install folder, so only administrators may change what's in it. Program
    /// Files already works that way; a folder picked elsewhere gets the same rule: admins and SYSTEM change, everyone
    /// else only reads and runs.
    /// </summary>
    public static void LockDown(string dir)
    {
        Directory.CreateDirectory(dir);
        if (Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        void Allow(System.Security.Principal.WellKnownSidType who, System.Security.AccessControl.FileSystemRights rights) =>
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(who, null), rights, inherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        Allow(System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl);
        Allow(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl);
        Allow(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute);
        new DirectoryInfo(dir).SetAccessControl(acl);
        Log.Write($"Locked {dir} to administrators");
    }

    public static long Size(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
}
