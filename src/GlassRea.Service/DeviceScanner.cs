using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Management;
using GlassRea.Core.Alerts;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Service.Devices;

namespace GlassRea.Service;

/// <summary>
/// Every few minutes, pings the local subnet so every device answers ARP, records each MAC address seen, and asks the
/// devices what they are (see <see cref="DeviceProbe"/>). A device seen for the first time raises an alert (except on
/// the very first scan, when everything is new). With presence alerts on, a device missing for
/// <see cref="AwayAfter"/> seconds raises a "left" alert and its return a "back" one.
/// </summary>
public sealed partial class DeviceScanner(Database db, ServiceState state, ILogger<DeviceScanner> log) : BackgroundService
{
    private static readonly string[] SkipAdapters = ["cloudflare", "warp", "vpn", "virtual", "hyper-v", "vmware", "virtualbox", "tap-", "wintun", "wireguard", "loopback", "bluetooth"];
    private readonly SemaphoreSlim _scanNow = new(0, 1);

    /// <summary>Three missed scans: a phone asleep on Wi-Fi still answers ARP, so this means it has really gone.</summary>
    public const long AwayAfter = 15 * 60;

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
        var before = db.Devices().ToDictionary(d => d.Mac);
        var firstScan = before.Count == 0;
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
        state.NetworkPublic = IsPublic(index) == true;
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
        var network = seen.Where(d => d.Address.Equals(gateway)).Select(d => MacText(d.Mac)).FirstOrDefault();
        var presence = state.Flag(SettingKeys.AlertDevicePresence);
        var here = seen.Select(d => MacText(d.Mac)).ToHashSet();

        foreach (var d in seen)
        {
            var mac = MacText(d.Mac);
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
                e.IsThisPc, now, now, e.Vendor, id.Model, id.Kind, id.Os, Network: network);
            var was = Previous(device, before.Values, here);
            if (was is not null)
            {
                db.MergeDevice(was.Mac, mac);
                before.Remove(was.Mac); // it didn't leave: it's here under its new address
            }
            // A merge rests on a name the device chose, which anyone can copy, so it's reported like a new device.
            if ((db.SaveDevice(device) || was is not null) && !firstScan && state.Flag(SettingKeys.AlertNewDevice))
            {
                var (title, body) = was is null
                    ? AlertText.NewDevice(DeviceLabel.For(device), device.Ip, mac)
                    : AlertText.DeviceNewAddress(DeviceLabel.For(device), device.Ip, mac);
                db.AddAlert("new_device", title, body, now);
            }
        }

        var present = seen.ToDictionary(d => MacText(d.Mac), d => d.Address.ToString());
        foreach (var change in Presence(before, present, network, now))
        {
            db.SetAway(change.Device.Mac, !change.Back);
            if (!presence || !change.Alert) continue;
            var name = DeviceLabel.For(change.Device);
            var (title, body) = change.Back
                ? AlertText.DeviceBack(name, present[change.Device.Mac], now - change.Device.LastSeen)
                : AlertText.DeviceLeft(name, change.Device.Ip, now - change.Device.LastSeen);
            db.AddAlert(change.Back ? "device_back" : "device_left", title, body, now);
        }
        db.PruneDeviceHints(now);
        Interlocked.Exchange(ref state.LastDeviceScan, now);
    }

    /// <summary>
    /// Which devices have just gone missing and which have come back. Only devices of the network this PC is on now
    /// (<paramref name="network"/>, its router's hardware address) can go missing, never this PC or the router. A device
    /// missing for much longer than <see cref="AwayAfter"/> (this PC was asleep or off) is marked without an alert,
    /// as is one coming back on a different network.
    /// </summary>
    /// <param name="present">This scan's devices: hardware address to IP address.</param>
    public static List<(DeviceInfo Device, bool Back, bool Alert)> Presence(IReadOnlyDictionary<string, DeviceInfo> before,
        IReadOnlyDictionary<string, string> present, string? network, long now)
    {
        var changes = new List<(DeviceInfo, bool, bool)>();
        foreach (var was in before.Values)
        {
            if (present.ContainsKey(was.Mac))
            {
                if (was.Away) changes.Add((was, true, network is not null && was.Network == network));
                continue;
            }
            if (network is null || was.Away || was.IsThisPc || was.IsGateway || was.Network != network) continue;
            var gone = now - was.LastSeen;
            if (gone >= AwayAfter) changes.Add((was, false, gone < AwayAfter + 3600));
        }
        return changes;
    }

    /// <summary>
    /// What a device was before it took a new private address: a device that gave itself the same name, also on a
    /// private address, on the same network, and missing from this scan. Phones change their private address now and
    /// then and would otherwise be listed twice. Two devices that are both here are never taken for one, and neither
    /// are two that only carry their maker's default name ("iPhone"), which many share.
    /// </summary>
    /// <param name="present">The hardware addresses this scan found.</param>
    public static DeviceInfo? Previous(DeviceInfo incoming, IEnumerable<DeviceInfo> known, IReadOnlySet<string> present)
    {
        var name = incoming.Name?.Trim();
        if (!incoming.Randomized || incoming.IsThisPc || string.IsNullOrEmpty(name) || IsDefaultName(name, incoming)) return null;
        return known
            .Where(k => k.Mac != incoming.Mac && k.Randomized && !present.Contains(k.Mac)
                && string.Equals(k.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)
                && (k.Network is null || incoming.Network is null || k.Network == incoming.Network))
            .OrderByDescending(k => k.LastSeen)
            .FirstOrDefault();
    }

    /// <summary>A name a device comes with rather than one someone gave it: "iPhone", "Galaxy S23", or what it is.</summary>
    private static bool IsDefaultName(string name, DeviceInfo d) =>
        string.Equals(name, DeviceLabel.What(d with { Name = null, CustomName = null }), StringComparison.OrdinalIgnoreCase)
        || DefaultNames().IsMatch(name);

    [System.Text.RegularExpressions.GeneratedRegex(@"^(iphone|ipad|ipod|apple ?watch|macbook( (air|pro))?|imac|android|galaxy[- ]?\w{0,6}|pixel[- ]?\w{0,4}|redmi[- ]?\w{0,8}|phone|tablet)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DefaultNames();

    private static string MacText(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2")));

    /// <summary>Whether Windows has the network on this adapter marked Public (0), rather than Private (1) or Domain (2).</summary>
    private bool? IsPublic(int interfaceIndex)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
                $"SELECT NetworkCategory FROM MSFT_NetConnectionProfile WHERE InterfaceIndex = {interfaceIndex}");
            foreach (var o in searcher.Get())
                using (o) return Convert.ToInt32(o["NetworkCategory"], System.Globalization.CultureInfo.InvariantCulture) == 0;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            log.LogDebug(ex, "Couldn't read the network category");
        }
        return null;
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
