using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GlassRea.Service.Devices;

/// <summary>
/// Asks the devices on the network what they are, the same ways a phone or a PC finds them: Bonjour (mDNS),
/// NetBIOS and UPnP, plus a knock on a few ports that only one kind of device keeps open. All of it runs together
/// and takes about three seconds. Everything received comes from other
/// devices, so it is only parsed, never trusted beyond being shown as a name.
/// </summary>
public static class DeviceProbe
{
    /// <summary>Bonjour services worth asking for: each one says something about what a device is.</summary>
    public static readonly string[] ServiceTypes =
    [
        "_device-info._tcp", "_companion-link._tcp", "_apple-mobdev2._tcp", "_airplay._tcp", "_raop._tcp", "_googlecast._tcp",
        "_androidtvremote2._tcp", "_amzn-wplay._tcp", "_spotify-connect._tcp", "_ipp._tcp", "_ipps._tcp", "_printer._tcp",
        "_pdl-datastream._tcp", "_uscan._tcp", "_scanner._tcp", "_hap._tcp", "_homekit._tcp", "_matter._tcp", "_smb._tcp",
        "_afpovertcp._tcp", "_workstation._tcp", "_ssh._tcp",
    ];

    /// <summary>
    /// Ports that give a device away: 62078 iPhone and iPad, 7000 AirPlay, 9100/631/515 printers, 445/139 file
    /// sharing, 8008/8009 Chromecast, 8060 Roku, 1400 Sonos, 554 cameras, 5555 Android TV. A connect is only tried, never used.
    /// </summary>
    public static readonly int[] TellingPorts = [62078, 7000, 9100, 631, 515, 445, 139, 8008, 8009, 8060, 1400, 554, 5555];

    private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly TimeSpan Listen = TimeSpan.FromSeconds(3);

    public static async Task<Dictionary<IPAddress, DeviceEvidence>> Run(IPAddress local, IReadOnlyCollection<IPAddress> targets, CancellationToken ct)
    {
        var found = targets.ToDictionary(t => t, _ => new DeviceEvidence());
        var mdns = Mdns(local, targets, ct);
        var netbios = NetBiosNames(local, targets, ct);
        var ssdp = SsdpDevices(local, targets.ToHashSet(), ct);
        var ports = OpenPorts(local, targets, ct);
        await Task.WhenAll(mdns, netbios, ssdp, ports).ConfigureAwait(false);

        MergeMdns(found, await mdns);
        foreach (var (ip, name) in await netbios)
            if (found.TryGetValue(ip, out var e)) e.NetBiosName = name;
        foreach (var (ip, (server, upnp)) in await ssdp)
            if (found.TryGetValue(ip, out var e)) { e.UpnpServer = server; e.Upnp = upnp; }
        foreach (var (ip, port) in await ports)
            found[ip].OpenPorts.Add(port);
        return found;
    }

    // ---------- ports ----------

    private static async Task<List<(IPAddress Ip, int Port)>> OpenPorts(IPAddress local, IReadOnlyCollection<IPAddress> targets, CancellationToken ct)
    {
        var open = new List<(IPAddress, int)>();
        using var gate = new SemaphoreSlim(128);
        await Task.WhenAll(targets.SelectMany(ip => TellingPorts.Select(async port =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(local, 0));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(2500)); // a sleeping phone answers its first packet late
                await socket.ConnectAsync(new IPEndPoint(ip, port), timeout.Token).ConfigureAwait(false);
                lock (open) open.Add((ip, port));
            }
            catch (SocketException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            finally { gate.Release(); }
        }))).ConfigureAwait(false);
        return open;
    }

    // ---------- Bonjour ----------

    private static async Task<List<(IPAddress From, List<DnsRecord> Records)>> Mdns(IPAddress local, IReadOnlyCollection<IPAddress> targets, CancellationToken ct)
    {
        var replies = new List<(IPAddress, List<DnsRecord>)>();
        using var socket = Bound(local);
        if (socket is null) return replies;
        try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes()); } catch (SocketException) { }

        // Multicast: every device offering one of these services answers. Asked in small packets, twice, because
        // multicast on Wi-Fi drops packets.
        var queries = ServiceTypes.Select(s => (s + ".local", DnsWire.Ptr)).Chunk(6).Select(DnsWire.Query).ToList();
        // Unicast to each address: "what is your name?" Most phones and Macs answer this even with no services.
        var reverse = targets.Select(t => (To: t, Packet: DnsWire.Query([(DnsWire.ReverseName(t), DnsWire.Ptr)]))).ToList();

        var receive = Collect(socket, Listen, p => DnsWire.Parse(p), ct);
        for (var round = 0; round < 2; round++)
        {
            foreach (var q in queries) await Send(socket, q, MdnsGroup, ct);
            foreach (var (to, packet) in reverse) await Send(socket, packet, new IPEndPoint(to, 5353), ct);
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        foreach (var (from, records) in await receive)
            if (records.Count > 0) replies.Add((from, records));
        return replies;
    }

    /// <summary>
    /// Ties every Bonjour record to an address. A name's own A record wins over who sent it, since one device
    /// (a sleep proxy, usually an Apple TV) can answer for another that's asleep.
    /// </summary>
    public static void MergeMdns(Dictionary<IPAddress, DeviceEvidence> found, IEnumerable<(IPAddress From, List<DnsRecord> Records)> replies)
    {
        var all = replies.SelectMany(r => r.Records.Select(rec => (r.From, Rec: rec))).ToList();
        var hostIp = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, rec) in all)
            if (rec.Type == DnsWire.A && IPAddress.TryParse(rec.Data, out var a)) hostIp[rec.Name] = a;
        var instanceHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, rec) in all)
            if (rec.Type == DnsWire.Srv) instanceHost[rec.Name] = rec.Data;
        var services = ServiceTypes.Select(s => s + ".local").ToHashSet(StringComparer.OrdinalIgnoreCase);

        DeviceEvidence? For(string instance, IPAddress sender)
        {
            var ip = instanceHost.TryGetValue(instance, out var host) && hostIp.TryGetValue(host, out var a) ? a : sender;
            return found.GetValueOrDefault(ip);
        }

        foreach (var (from, rec) in all)
        {
            switch (rec.Type)
            {
                case DnsWire.A when IPAddress.TryParse(rec.Data, out var a):
                    found.GetValueOrDefault(a)?.HostNames.Add(rec.Name);
                    break;
                case DnsWire.Ptr when rec.Name.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase):
                    if (FromReverse(rec.Name) is { } owner) found.GetValueOrDefault(owner)?.HostNames.Add(rec.Data);
                    break;
                case DnsWire.Ptr when services.Contains(rec.Name):
                    // "_googlecast._tcp.local" → "Living Room TV._googlecast._tcp.local"
                    if (For(rec.Data, from) is not { } e) break;
                    var type = rec.Name[..^".local".Length];
                    e.Services.Add(type);
                    var label = InstanceLabel(rec.Data, rec.Name);
                    if (label is not null && type is not "_device-info._tcp" && !e.InstanceNames.Contains(label)) e.InstanceNames.Add(label);
                    break;
                case DnsWire.Txt:
                    if (For(rec.Name, from) is not { } t) break;
                    foreach (var kv in rec.Txt)
                    {
                        var eq = kv.IndexOf('=');
                        if (eq <= 0 || eq == kv.Length - 1) continue;
                        var value = kv[(eq + 1)..].Trim();
                        if (value.Length is > 0 and <= 120) t.Txt.TryAdd(kv[..eq], value);
                    }
                    // _device-info carries no service, but its instance name is the device's own name.
                    if (rec.Name.EndsWith("._device-info._tcp.local", StringComparison.OrdinalIgnoreCase)
                        && InstanceLabel(rec.Name, "_device-info._tcp.local") is { } own && !t.InstanceNames.Contains(own))
                        t.InstanceNames.Add(own);
                    break;
            }
        }
    }

    /// <summary>"Living Room TV._googlecast._tcp.local" with service "_googlecast._tcp.local" → "Living Room TV".</summary>
    public static string? InstanceLabel(string instance, string service)
    {
        if (!instance.EndsWith("." + service, StringComparison.OrdinalIgnoreCase)) return null;
        var label = instance[..^(service.Length + 1)].Trim();
        return label.Length == 0 ? null : label;
    }

    private static IPAddress? FromReverse(string name)
    {
        var parts = name.Split('.');
        if (parts.Length != 6) return null;
        return IPAddress.TryParse(string.Join('.', parts[..4].Reverse()), out var ip) ? ip : null;
    }

    // ---------- NetBIOS ----------

    private static async Task<Dictionary<IPAddress, string>> NetBiosNames(IPAddress local, IReadOnlyCollection<IPAddress> targets, CancellationToken ct)
    {
        var names = new Dictionary<IPAddress, string>();
        using var socket = Bound(local);
        if (socket is null) return names;
        var receive = Collect(socket, Listen, p => NetBios.ParseStatus(p), ct);
        ushort id = 0x4752;
        foreach (var t in targets) await Send(socket, NetBios.StatusRequest(id++), new IPEndPoint(t, 137), ct);
        foreach (var (from, name) in await receive)
            if (name is not null) names.TryAdd(from, name);
        return names;
    }

    // ---------- UPnP ----------

    private static async Task<Dictionary<IPAddress, (string? Server, UpnpDescription? Upnp)>> SsdpDevices(IPAddress local, HashSet<IPAddress> targets, CancellationToken ct)
    {
        var result = new Dictionary<IPAddress, (string?, UpnpDescription?)>();
        using var socket = Bound(local);
        if (socket is null) return result;
        try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes()); } catch (SocketException) { }
        var receive = Collect(socket, Listen, p => Ssdp.Headers(p), ct);
        for (var i = 0; i < 2; i++)
        {
            await Send(socket, Ssdp.Search(), Ssdp.Group, ct);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        var locations = new Dictionary<IPAddress, (string? Server, Uri? Location)>();
        foreach (var (from, headers) in await receive)
        {
            if (!targets.Contains(from) || headers.Count == 0) continue;
            var location = Ssdp.SafeLocation(headers.GetValueOrDefault("LOCATION"), from);
            var server = headers.GetValueOrDefault("SERVER");
            var root = headers.GetValueOrDefault("ST")?.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) == true;
            if (!locations.TryGetValue(from, out var have) || have.Location is null || root && location is not null)
                locations[from] = (server ?? have.Server, location ?? have.Location);
        }

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
        {
            Timeout = TimeSpan.FromSeconds(3),
            MaxResponseContentBufferSize = 512 * 1024,
        };
        await Parallel.ForEachAsync(locations, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (pair, token) =>
        {
            UpnpDescription? upnp = null;
            if (pair.Value.Location is { } uri)
            {
                try { upnp = Ssdp.ParseDescription(await http.GetStringAsync(uri, token)); }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                catch (InvalidOperationException) { }
            }
            lock (result) result[pair.Key] = (pair.Value.Server, upnp);
        });
        return result;
    }

    // ---------- sockets ----------

    private static Socket? Bound(IPAddress local)
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Without this, one "port unreachable" from a device that doesn't listen breaks the next receive.
            const int SioUdpConnReset = -1744830452;
            s.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            s.Bind(new IPEndPoint(local, 0));
            return s;
        }
        catch (SocketException) { return null; }
    }

    private static async Task Send(Socket socket, byte[] packet, IPEndPoint to, CancellationToken ct)
    {
        try { await socket.SendToAsync(packet, SocketFlags.None, to, ct).ConfigureAwait(false); }
        catch (SocketException) { /* unreachable address: nothing comes back, which is fine */ }
    }

    /// <summary>Reads datagrams until the time is up, parsing each as it arrives.</summary>
    private static async Task<List<(IPAddress From, T Value)>> Collect<T>(Socket socket, TimeSpan window, Func<byte[], T> parse, CancellationToken ct)
    {
        var list = new List<(IPAddress, T)>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(window);
        var buffer = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!stop.IsCancellationRequested && list.Count < 4096)
        {
            try
            {
                var r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stop.Token).ConfigureAwait(false);
                if (r.RemoteEndPoint is IPEndPoint from) list.Add((from.Address.MapToIPv4(), parse(buffer[..r.ReceivedBytes])));
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { /* ICMP port unreachable from a host that doesn't listen: keep going */ }
        }
        ct.ThrowIfCancellationRequested();
        return list;
    }
}
