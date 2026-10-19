using System.Diagnostics;
using GlassRea.Core.Paths;
using Microsoft.Win32;

namespace GlassRea.App.Support;

/// <summary>
/// The copy setup installs, seen from the Store copy on the same PC. Until it's removed it records, and the Store
/// copy's service waits; removed with the history kept, the Store copy takes over, blocks included.
/// </summary>
public static class OtherCopy
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GlassRea";

    public static bool Present
    {
        get
        {
            if (!Packaging.IsPackaged) return false;
            using var service = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Packaging.ClassicService}");
            return service is not null;
        }
    }

    /// <summary>Starts that copy's own uninstaller, which asks for admin and offers to keep the history.</summary>
    public static async Task Remove()
    {
        using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
        // "C:\Program Files\Glass-Rea\Uninstall Glass-Rea.exe" --uninstall
        if (key?.GetValue("UninstallString") is not string command || command.Split('"') is not [_, var exe, var args, ..] || !File.Exists(exe))
            throw new InvalidOperationException("Its uninstaller isn't where Windows says. Remove it in Windows' Settings > Apps > Installed apps instead.");
        try { await Task.Run(() => Process.Start(new ProcessStartInfo(exe, args.Trim()) { UseShellExecute = true })); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* the person said no to admin */ }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Windows couldn't start its uninstaller ({ex.Message}). Remove it in Windows' Settings > Apps > Installed apps instead.");
        }
    }
}
