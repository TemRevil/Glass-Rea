using System.Net;
using System.Net.Sockets;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Service.Devices;

namespace GlassRea.Service;

/// <summary>
/// Listens for devices introducing themselves: the DHCP request a device broadcasts when it joins (carrying the name
/// routers show, like "Omars-iPhone") and the Bonjour announcements phones and Macs multicast when they wake. Nothing
/// is sent. What's heard is stored per hardware address for <see cref="DeviceScanner"/>, and a device joining
/// triggers a scan straight away.
/// </summary>
public sealed class DeviceListener(Database db, DeviceScanner scanner, FirewallManager firewall, Permissions permissions, ILogger<DeviceListener> log) : BackgroundService
{
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    private readonly Dictionary<string, long> _saved = new(StringComparer.Ordinal);
    private Dictionary<IPAddress, string> _arp = [];
    private long _arpAt = long.MinValue, _scanAskedAt = long.MinValue;

    /// <summary>
    /// Listens only while Device discovery is allowed. Turning it off closes both sockets and removes the firewall rule
    /// that let them hear.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken ct) => permissions.WhileOn(Feature.Devices, Listen, ct);

    private async Task Listen(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return; // turned off again before this started
        firewall.AllowDiscovery();
        try
        {
            await Task.WhenAll(ListenDhcp(ct), ListenMdns(ct)).ConfigureAwait(false);
            // A port it couldn't open ends its listener early; wait anyway, so turning it off still removes the rule.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        finally { if (!permissions.Allowed(Feature.Devices)) firewall.RemoveDiscovery(); }
    }

    private async Task ListenDhcp(CancellationToken ct)
    {
        using var socket = Open(67);
        if (socket is null) return;
        var buffer = new byte[1500];
        while (!ct.IsCancellationRequested)
        {
            int length;
            try { length = (await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct)).ReceivedBytes; }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            if (Dhcp.ParseRequest(buffer.AsSpan(0, length)) is not { } request || request.Mac.All(b => b == 0)) continue;
            var heard = new DeviceEvidence { DhcpName = request.HostName, DhcpVendor = request.VendorClass };
            Save(Mac(request.Mac), heard);
            ScanSoon(ct);
        }
    }

    private async Task ListenMdns(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (DeviceScanner.PickInterface() is not { } pick)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            using var socket = Open(5353);
            if (socket is null) return;
            try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MdnsGroup, pick.Ip)); }
            catch (SocketException ex) { log.LogInformation("Can't join Bonjour on {Ip}: {Reason}", pick.Ip, ex.SocketErrorCode); return; }

            // Rejoin now and then, in case the PC moved to another network.
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cycle.CancelAfter(TimeSpan.FromMinutes(10));
            var buffer = new byte[9000];
            while (!cycle.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                try { r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cycle.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                if (r.RemoteEndPoint is not IPEndPoint from || from.Address.Equals(pick.Ip)) continue;
                if (r.ReceivedBytes < 12 || (buffer[2] & 0x80) == 0) continue; // a question, not an answer
                Heard(from.Address.MapToIPv4(), DnsWire.Parse(buffer.AsSpan(0, r.ReceivedBytes)));
            }
        }
    }

    private void Heard(IPAddress sender, List<DnsRecord> records)
    {
        if (records.Count == 0) return;
        foreach (var (ip, heard) in Announced(sender, records))
            if (MacOf(ip) is { } mac) Save(mac, heard);
    }

    /// <summary>
    /// The devices an announcement tells us about, which is only ever the one that sent it. Anything about another
    /// address is dropped: it could be one device trying to rename another.
    /// </summary>
    public static Dictionary<IPAddress, DeviceEvidence> Announced(IPAddress sender, List<DnsRecord> records)
    {
        var found = new Dictionary<IPAddress, DeviceEvidence> { [sender] = new() };
        DeviceProbe.MergeMdns(found, [(sender, records)]);
        return found;
    }

    private void Save(string mac, DeviceEvidence heard)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Devices repeat themselves every few seconds; only write what changed or is ten minutes old.
        List<(string Key, string Value)> fresh;
        lock (_saved)
            fresh = heard.Hints().Where(h =>
            {
                var key = mac + "\n" + h.Key + "\n" + h.Value;
                if (_saved.TryGetValue(key, out var at) && now - at < 600) return false;
                if (_saved.Count > 10_000) _saved.Clear();
                _saved[key] = now;
                return true;
            }).ToList();
        if (fresh.Count > 0) db.SaveDeviceHints(mac, fresh, now);
    }

    private string? MacOf(IPAddress ip)
    {
        if (!_arp.ContainsKey(ip) && Environment.TickCount64 - _arpAt > 5000)
        {
            _arp = Native.ArpTable().Where(a => a.Type is 3 or 4 && a.Mac.Length == 6 && a.Mac.Any(b => b != 0))
                .GroupBy(a => a.Address).ToDictionary(g => g.Key, g => Mac(g.First().Mac));
            _arpAt = Environment.TickCount64;
        }
        return _arp.GetValueOrDefault(ip);
    }

    /// <summary>A joining device gets its address a few seconds after asking, so the scan waits a moment.</summary>
    private void ScanSoon(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _scanAskedAt) < 60_000) return;
        Interlocked.Exchange(ref _scanAskedAt, now);
        _ = Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(t => { if (!t.IsCanceled) scanner.RequestScan(); }, TaskScheduler.Default);
    }

    /// <summary>Shared with Windows' own listeners on the same port, which is why the address is reused.</summary>
    private Socket? Open(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(IPAddress.Any, port));
            return s;
        }
        catch (SocketException ex)
        {
            log.LogInformation("Can't listen on UDP {Port}: {Reason}", port, ex.SocketErrorCode);
            s.Dispose();
            return null;
        }
    }

    private static string Mac(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2")));
}
