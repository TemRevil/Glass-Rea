using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GlassRea.Core.Alerts;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Service.Devices;

namespace GlassRea.Service;

/// <summary>
/// Every few minutes, pings the local subnet so every device answers ARP, records each MAC address seen, and asks the
/// devices what they are (see <see cref="DeviceProbe"/>). A device seen for the first time raises an alert (except on
/// the very first scan, when everything is new).
/// </summary>
public sealed class DeviceScanner(Database db, ServiceState state, ILogger<DeviceScanner> log) : BackgroundService
{
    private static readonly string[] SkipAdapters = ["cloudflare", "warp", "vpn", "virtual", "hyper-v", "vmware", "virtualbox", "tap-", "wintun", "wireguard", "loopback", "bluetooth"];
    private readonly SemaphoreSlim _scanNow = new(0, 1);

    public void RequestScan() { if (_scanNow.CurrentCount == 0) _scanNow.Release(); }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await Scan(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Device scan failed"); }
            try { await _scanNow.WaitAsync(TimeSpan.FromMinutes(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task Scan(CancellationToken ct)
    {
        if (PickInterface() is not { } pick) return;
        var (nic, ip, mask, gateway) = pick;
        var firstScan = db.Devices().Count == 0;
        var hosts = SubnetHosts(ip, mask).ToList();

        // The ping makes every device answer ARP; its reply's TTL hints at the system (128 Windows, 64 phones and Macs).
        var ttls = new System.Collections.Concurrent.ConcurrentDictionary<IPAddress, int>();
        using var gate = new SemaphoreSlim(64);
        await Task.WhenAll(hosts.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(h, TimeSpan.FromMilliseconds(400), cancellationToken: ct);
                if (reply.Status == IPStatus.Success && reply.Options is { } o) ttls[h] = InitialTtl(o.Ttl);
            }
            catch (PingException) { }
            finally { gate.Release(); }
        }));

        var index = nic.GetIPProperties().GetIPv4Properties()?.Index ?? -1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var subnet = hosts.ToHashSet();
        var seen = Native.ArpTable()
            .Where(a => a.InterfaceIndex == index && a.Type is 3 or 4 && a.Mac.Length == 6 && a.Mac.Any(b => b != 0) && subnet.Contains(a.Address))
            .Select(a => (Address: a.Address, Mac: a.Mac))
            .Append((Address: ip, Mac: nic.GetPhysicalAddress().GetAddressBytes()))
            .DistinctBy(d => Convert.ToHexString(d.Mac))
            .ToList();

        var others = seen.Select(d => d.Address).Where(a => !a.Equals(ip)).ToList();
        var probe = DeviceProbe.Run(ip, others, ct);
        var reverse = new System.Collections.Concurrent.ConcurrentDictionary<IPAddress, string>();
        await Parallel.ForEachAsync(others, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (a, token) =>
        {
            if (await HostName(a, token) is { } name) reverse[a] = name;
        });
        var evidence = await probe;
        var hints = db.DeviceHints();

        foreach (var d in seen)
        {
            var mac = string.Join(":", d.Mac.Select(b => b.ToString("X2")));
            var e = evidence.GetValueOrDefault(d.Address) ?? new DeviceEvidence();
            if (hints.TryGetValue(mac, out var heard)) e.AddHints(heard);
            e.ReverseDns = reverse.GetValueOrDefault(d.Address);
            e.Ttl = ttls.TryGetValue(d.Address, out var ttl) ? ttl : null;
            e.Vendor = Oui.Vendor(d.Mac);
            e.Randomized = (d.Mac[0] & 0x02) != 0;
            e.IsGateway = d.Address.Equals(gateway);
            e.IsThisPc = d.Address.Equals(ip);
            var id = DeviceClassifier.Classify(e);
            var device = new DeviceInfo(mac, d.Address.ToString(), e.IsThisPc ? Environment.MachineName : id.Name, e.Randomized, e.IsGateway,
                e.IsThisPc, now, now, e.Vendor, id.Model, id.Kind, id.Os);
            if (db.SaveDevice(device) && !firstScan && state.Flag(SettingKeys.AlertNewDevice))
            {
                var (title, body) = AlertText.NewDevice(DeviceLabel.For(device), device.Ip, mac);
                db.AddAlert("new_device", title, body, now);
            }
        }
        db.PruneDeviceHints(now);
        Interlocked.Exchange(ref state.LastDeviceScan, now);
    }

    /// <summary>Systems start at 64, 128 or 255 and each router hop takes one off; a LAN reply is usually untouched.</summary>
    public static int InitialTtl(int ttl) => ttl <= 64 ? 64 : ttl <= 128 ? 128 : 255;

    /// <summary>The Wi-Fi or Ethernet adapter that reaches the router, skipping VPN and virtual adapters.</summary>
    internal static (NetworkInterface Nic, IPAddress Ip, IPAddress Mask, IPAddress Gateway)? PickInterface()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)) continue;
            var label = (nic.Description + " " + nic.Name).ToLowerInvariant();
            if (SkipAdapters.Any(label.Contains)) continue;
            var props = nic.GetIPProperties();
            var gateway = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            var addr = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (gateway is null || addr is null) continue;
            return (nic, addr.Address, addr.IPv4Mask, gateway);
        }
        return null;
    }

    /// <summary>All host addresses in the subnet, capped at a /22 (1022 hosts) around our own address.</summary>
    public static IEnumerable<IPAddress> SubnetHosts(IPAddress ip, IPAddress mask)
    {
        var prefix = mask.GetAddressBytes().Sum(b => System.Numerics.BitOperations.PopCount(b));
        prefix = Math.Max(prefix, 22);
        var self = ToUint(ip);
        var netMask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = self & netMask;
        var broadcast = network | ~netMask;
        for (var a = network + 1; a < broadcast; a++)
            yield return FromUint(a);
    }

    private static uint ToUint(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    private static IPAddress FromUint(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    private static async Task<string?> HostName(IPAddress ip, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), timeout.Token);
            return string.IsNullOrWhiteSpace(entry.HostName) || entry.HostName == ip.ToString() ? null : entry.HostName;
        }
        catch { return null; }
    }
}
