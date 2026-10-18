using System.Net.NetworkInformation;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;

namespace GlassRea.Service.Networks;

/// <summary>
/// Keeps track of which network this PC is on (see <see cref="NetworkList.Current"/>), so every hour's use is counted on
/// it. Checked at start, whenever the PC's addresses change and a few times over the next minutes while Windows
/// identifies a new network, and every 5 minutes, which also picks up a network renamed in Windows.
/// </summary>
public sealed class NetworkWatcher(Database db, UsageAccumulator usage, ILogger<NetworkWatcher> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] AfterChange = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)];
    private readonly SemaphoreSlim _changed = new(0, 1);
    private bool _failing;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        NetworkChange.NetworkAddressChanged += OnChange;
        NetworkChange.NetworkAvailabilityChanged += OnChange;
        try
        {
            var pending = new Queue<TimeSpan>();
            Check();
            while (!ct.IsCancellationRequested)
            {
                // A change starts the follow-up checks again, so a burst of changes is checked once it settles.
                if (await _changed.WaitAsync(pending.Count > 0 ? pending.Dequeue() : Every, ct).ConfigureAwait(false)) pending = new(AfterChange);
                else Check();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnChange;
            NetworkChange.NetworkAvailabilityChanged -= OnChange;
        }
    }

    private void OnChange(object? sender, EventArgs e)
    {
        if (_changed.CurrentCount == 0) _changed.Release();
    }

    private void Check()
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var network = NetworkList.Current();
            var id = network is null ? 0 : db.EnsureNetwork(network.Key, network.Name, network.Kind, now, now);
            if (id != usage.Network) log.LogInformation("Use now counts on network {Id} ({Kind})", id, network?.Kind ?? "not known");
            usage.Network = id;
            _failing = false;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NetworkInformationException or UnauthorizedAccessException
            or System.Security.SecurityException or IOException)
        {
            // Use keeps counting on the network it was on; the next check tries again.
            if (!_failing) log.LogWarning(ex, "Couldn't tell which network this PC is on");
            _failing = true;
        }
    }
}
