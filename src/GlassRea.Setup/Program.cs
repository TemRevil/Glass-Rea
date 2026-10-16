using System.Diagnostics;
using System.Globalization;
using GlassRea.Setup.Installer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GlassRea.Setup;

public enum SetupMode { Install, Uninstall }

/// <summary>
/// "Glass-Rea Setup.exe" installs or updates; "--uninstall" (what Installed apps runs) removes. The uninstaller
/// lives inside the folder it deletes, so it first hands over to a copy of itself in the temp folder.
/// </summary>
public static class Program
{
    private static Mutex? _single;

    [STAThread]
    private static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        var mode = args.Contains("--uninstall") ? SetupMode.Uninstall : SetupMode.Install;
        if (mode == SetupMode.Uninstall && Paths.IsInsideInstallDir(Environment.ProcessPath) && HandOverToTempCopy()) return;

        _single = new Mutex(true, "GlassRea-Setup", out var first);
        if (!first) return;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(mode);
        });
        GC.KeepAlive(_single);
    }

    private static bool HandOverToTempCopy()
    {
        try
        {
            var copy = Path.Combine(Path.GetTempPath(), $"Glass-Rea Uninstall {Guid.NewGuid():N}.exe");
            File.Copy(Environment.ProcessPath!, copy);
            // Already elevated, so the copy starts elevated too without a second prompt.
            Process.Start(new ProcessStartInfo(copy, "--uninstall --temp-copy") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not start the uninstaller from the temp folder, running in place: {ex}");
            return false;
        }
    }
}
