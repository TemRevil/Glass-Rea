using System.ServiceProcess;
using GlassRea.Core.Paths;
using GlassRea.Core.Storage;
using Microsoft.Win32;

namespace GlassRea.Service;

/// <summary>
/// The Store copy's service while the copy setup installs is still on this PC. That one records and blocks, so this one
/// only watches: it keeps note of which apps the other copy blocks, and once that copy is removed (keeping the
/// history), marks them blocked here and starts recording in this same process (<paramref name="startRecording"/>).
/// It can't stop and have Windows start it again: Windows doesn't let a packaged service set its own restart.
/// </summary>
public sealed class Standby(ILogger<Standby> log, Func<IHost> startRecording) : BackgroundService
{
    private IHost? _recording;

    public static bool ClassicInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Packaging.ClassicService}");
        return key is not null;
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
        log.LogInformation("Glass-Rea from setup was removed; recording now, with {Count} blocked apps carried over", blocked.Count);
        try { _recording = startRecording(); _recording.Start(); }
        catch (Exception ex) { log.LogError(ex, "Could not start recording"); }
    }

    /// <summary>Windows stopping the service stops the recording that took over in it.</summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        if (_recording is not { } recording) return;
        await recording.StopAsync(ct);
        recording.Dispose();
    }
}
