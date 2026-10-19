using System.Diagnostics;
using System.ServiceProcess;
using GlassRea.Core.Paths;
using GlassRea.Core.Storage;
using Microsoft.Win32;

namespace GlassRea.Service;

/// <summary>
/// The Store copy's service while the copy setup installs is still on this PC. That one records and blocks, so this one
/// only watches: it keeps note of which apps the other copy blocks, and once that copy is removed (keeping the
/// history), marks them blocked here and stops, for Windows to start it again as the recording service.
/// </summary>
public sealed class Standby(ILogger<Standby> log) : BackgroundService
{
    public static bool ClassicInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Packaging.ClassicService}");
        return key is not null;
    }

    /// <summary>
    /// Has Windows start this service again whenever it stops unexpectedly, as setup does for its copy. Standing by,
    /// that's also how it comes back to record.
    /// </summary>
    public static void RestartOnFailure()
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "failure", Packaging.StoreService, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            FileLoggerProvider.File.Write(Core.Logs.LogLevel.Warning, "Service", $"Windows wouldn't restart this service on failure: {output.Trim()}");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Glass-Rea from setup is on this PC and records; this copy waits until it's removed");
        var blocked = new List<string>();
        while (!ct.IsCancellationRequested)
        {
            if (!ClassicInstalled())
            {
                TakeOver(blocked);
                return;
            }
            // Its uninstaller stops it before removing its blocks, so a stopped copy's rules may be half gone.
            if (ClassicRunning())
            {
                try { blocked = FirewallManager.ClassicBlockedKeys(); }
                catch (Exception ex) { log.LogWarning(ex, "Could not read the other copy's blocks"); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static bool ClassicRunning()
    {
        try
        {
            using var service = new ServiceController(Packaging.ClassicService);
            return service.Status is ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException) { return false; }
    }

    private void TakeOver(List<string> blocked)
    {
        var file = Path.Combine(ServiceState.DataDirectory, "data.db");
        try
        {
            // Without the history (the person chose to delete it), the blocked apps are gone too.
            if (blocked.Count > 0 && File.Exists(file))
            {
                using var db = Database.OpenFile(file);
                db.SetBlocked(blocked);
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not carry over the other copy's blocks"); }
        log.LogInformation("Glass-Rea from setup was removed; restarting to record, with {Count} blocked apps carried over", blocked.Count);
        // Ends without telling Windows it stopped, so Windows restarts it (RestartOnFailure), this time recording.
        Environment.Exit(1);
    }
}
