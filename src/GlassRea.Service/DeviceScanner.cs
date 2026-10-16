using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GlassRea.Core.Alerts;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;

namespace GlassRea.Service;

/// <summary>
/// Every few minutes, pings the local subnet so every device answers ARP, then records each MAC address seen.
/// A device seen for the first time raises an alert (except on the very first scan, when everything is new).
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

        using var gate = new SemaphoreSlim(64);
        await Task.WhenAll(hosts.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try { using var ping = new Ping(); await ping.SendPingAsync(h, TimeSpan.FromMilliseconds(400), cancellationToken: ct); }
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

        await Parallel.ForEachAsync(seen, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (d, token) =>
        {
            var name = d.Address.Equals(ip) ? Environment.MachineName : await HostName(d.Address, token);
            var mac = string.Join(":", d.Mac.Select(b => b.ToString("X2")));
            var device = new DeviceInfo(mac, d.Address.ToString(), name, (d.Mac[0] & 0x02) != 0, d.Address.Equals(gateway),
                d.Address.Equals(ip), now, now);
            if (db.SaveDevice(device) && !firstScan && state.Flag(SettingKeys.AlertNewDevice))
            {
                var (title, body) = AlertText.NewDevice(name, device.Ip, mac);
                db.AddAlert("new_device", title, body, now);
            }
        });
        Interlocked.Exchange(ref state.LastDeviceScan, now);
    }

    private static (NetworkInterface Nic, IPAddress Ip, IPAddress Mask, IPAddress Gateway)? PickInterface()
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
