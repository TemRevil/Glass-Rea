using System.Net;
using System.Text;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Service;
using GlassRea.Service.Devices;

namespace GlassRea.Tests;

/// <summary>Builds DNS messages the way devices send them, for the parser tests.</summary>
internal static class Wire
{
    public static byte[] Name(string name)
    {
        var b = new List<byte>();
        foreach (var label in name.Split('.')) { var l = Encoding.UTF8.GetBytes(label); b.Add((byte)l.Length); b.AddRange(l); }
        b.Add(0);
        return [.. b];
    }

    public static byte[] Txt(params string[] items) => items.SelectMany(i => new[] { (byte)i.Length }.Concat(Encoding.UTF8.GetBytes(i))).ToArray();
    public static byte[] Srv(string target) => new byte[] { 0, 0, 0, 0, 0x1F, 0x49 }.Concat(Name(target)).ToArray();

    public static byte[] Response(params (string Name, ushort Type, byte[] Data)[] answers)
    {
        var b = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, (byte)answers.Length, 0, 0, 0, 0 };
        foreach (var (name, type, data) in answers)
        {
            b.AddRange(Name(name));
            b.AddRange([(byte)(type >> 8), (byte)type, 0x80, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length]);
            b.AddRange(data);
        }
        return [.. b];
    }
}

public class DnsWireTests
{
    [Fact]
    public void ReadsTheRecordsDevicesAnnounce()
    {
        var packet = Wire.Response(
            ("_googlecast._tcp.local", DnsWire.Ptr, Wire.Name("Living Room TV._googlecast._tcp.local")),
            ("Living Room TV._googlecast._tcp.local", DnsWire.Srv, Wire.Srv("tv-1.local")),
            ("Living Room TV._googlecast._tcp.local", DnsWire.Txt, Wire.Txt("md=Chromecast", "fn=Living Room TV")),
            ("tv-1.local", DnsWire.A, [192, 168, 1, 40]));
        var r = DnsWire.Parse(packet);
        Assert.Equal(4, r.Count);
        Assert.Equal("Living Room TV._googlecast._tcp.local", r[0].Data);
        Assert.Equal("tv-1.local", r[1].Data);
        Assert.Equal(["md=Chromecast", "fn=Living Room TV"], r[2].Txt);
        Assert.Equal("192.168.1.40", r[3].Data);
    }

    [Fact]
    public void FollowsCompressionAndSurvivesLoops()
    {
        // One A record whose name points back at the question's name (offset 12).
        var q = Wire.Name("phone.local");
        var good = new List<byte> { 0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0 };
        good.AddRange(q); good.AddRange([0, 1, 0, 1]);
        good.AddRange([0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 120, 0, 4, 10, 0, 0, 7]);
        var rec = Assert.Single(DnsWire.Parse(good.ToArray()));
        Assert.Equal(("phone.local", "10.0.0.7"), (rec.Name, rec.Data));

        // A name that points at itself must not hang; the record is simply unreadable.
        var loop = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 120, 0, 4, 1, 2, 3, 4 };
        var looped = DnsWire.Parse(loop.ToArray());
        Assert.True(looped.Count <= 1);
    }

    [Fact]
    public void TruncatedPacketsYieldWhatWasRead()
    {
        var packet = Wire.Response(("a.local", DnsWire.A, [10, 0, 0, 1]), ("b.local", DnsWire.A, [10, 0, 0, 2]));
        Assert.Single(DnsWire.Parse(packet[..^3]));
        Assert.Empty(DnsWire.Parse(packet[..5]));
    }

    [Fact]
    public void QueriesAskForUnicastReplies()
    {
        var q = DnsWire.Query([("_ipp._tcp.local", DnsWire.Ptr)]);
        Assert.Equal(1, q[5]);
        Assert.Equal([0, 12, 0x80, 1], q[^4..]);
        Assert.Equal("5.1.168.192.in-addr.arpa", DnsWire.ReverseName(IPAddress.Parse("192.168.1.5")));
    }
}

public class NetBiosTests
{
    [Fact]
    public void ReadsTheComputerName()
    {
        var p = new byte[57 + 3 * 18];
        p[56] = 3;
        void Entry(int i, string name, byte suffix, bool group)
        {
            var at = 57 + i * 18;
            Encoding.ASCII.GetBytes(name.PadRight(15)).CopyTo(p, at);
            p[at + 15] = suffix;
            p[at + 16] = (byte)(group ? 0x80 : 0);
        }
        Entry(0, "OFFICE-PC", 0x20, false); // file server entry: skipped
        Entry(1, "WORKGROUP", 0x00, true);  // the workgroup: a group name, skipped
        Entry(2, "OFFICE-PC", 0x00, false);
        Assert.Equal("OFFICE-PC", NetBios.ParseStatus(p));
        Assert.Null(NetBios.ParseStatus(p[..40]));
        var req = NetBios.StatusRequest(7);
        Assert.Equal(50, req.Length);
        Assert.Equal((byte)'C', req[13]); // '*' = 0x2A → 'C','K'
        Assert.Equal((byte)'K', req[14]);
    }
}

public class SsdpTests
{
    [Fact]
    public void ReadsHeadersOnlyFromRealAnswers()
    {
        var h = Ssdp.Headers(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.9:8080/desc.xml\r\nSERVER: Linux/4.9 UPnP/1.0\r\nST: upnp:rootdevice\r\n\r\n"));
        Assert.Equal("http://192.168.1.9:8080/desc.xml", h["location"]);
        Assert.Equal("Linux/4.9 UPnP/1.0", h["SERVER"]);
        Assert.Empty(Ssdp.Headers(Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nST: ssdp:all\r\n\r\n")));
    }

    [Fact]
    public void OnlyFollowsLinksBackToTheSender()
    {
        var sender = IPAddress.Parse("192.168.1.9");
        Assert.NotNull(Ssdp.SafeLocation("http://192.168.1.9:8080/desc.xml", sender));
        Assert.Null(Ssdp.SafeLocation("http://192.168.1.1/desc.xml", sender));       // another device
        Assert.Null(Ssdp.SafeLocation("http://example.com/desc.xml", sender));       // a name, not the sender
        Assert.Null(Ssdp.SafeLocation("file:///C:/Windows/win.ini", sender));
        Assert.Null(Ssdp.SafeLocation(null, sender));
    }

    [Fact]
    public void ParsesDescriptionsAndRefusesDtds()
    {
        var xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device>
              <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
              <friendlyName>[TV] Samsung Q60</friendlyName><manufacturer>Samsung Electronics</manufacturer><modelName>QE55Q60</modelName>
            </device></root>
            """;
        var d = Ssdp.ParseDescription(xml)!;
        Assert.Equal(("[TV] Samsung Q60", "QE55Q60"), (d.FriendlyName, d.ModelName));
        Assert.Null(Ssdp.ParseDescription("""<!DOCTYPE r [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><root><device><friendlyName>&x;</friendlyName></device></root>"""));
        Assert.Null(Ssdp.ParseDescription("not xml"));
    }
}

public class DhcpTests
{
    private static byte[] Packet(byte op, params (byte Code, byte[] Data)[] options)
    {
        var p = new List<byte>(new byte[240]);
        p[0] = op; p[1] = 1; p[2] = 6;
        byte[] mac = [0x44, 0x9E, 0x8B, 1, 2, 3];
        for (var i = 0; i < 6; i++) p[28 + i] = mac[i];
        p[236] = 99; p[237] = 130; p[238] = 83; p[239] = 99;
        foreach (var (code, data) in options) { p.Add(code); p.Add((byte)data.Length); p.AddRange(data); }
        p.Add(255);
        return [.. p];
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void ReadsTheNameAPhoneJoinsWith()
    {
        var r = Dhcp.ParseRequest(Packet(1, (53, [3]), (12, Ascii("Tems-iPhone")), (55, [1, 3, 6])))!;
        Assert.Equal("Tems-iPhone", r.HostName);
        Assert.Equal("449E8B010203", Convert.ToHexString(r.Mac));
        var android = Dhcp.ParseRequest(Packet(1, (53, [1]), (60, Ascii("android-dhcp-14")), (12, Ascii("Galaxy-S23"))))!;
        Assert.Equal(("Galaxy-S23", "android-dhcp-14"), (android.HostName, android.VendorClass));
    }

    [Fact]
    public void FallsBackToTheFqdnOption()
    {
        byte[] fqdn = [0, 0, 0, .. Wire.Name("laptop.home")];
        Assert.Equal("laptop", Dhcp.ParseRequest(Packet(1, (53, [3]), (81, fqdn)))!.HostName);
        Assert.Equal("desk", Dhcp.ParseRequest(Packet(1, (53, [3]), (81, [0, 0, 0, .. Ascii("desk.lan")])))!.HostName);
    }

    [Fact]
    public void IgnoresServerRepliesAndJunk()
    {
        Assert.Null(Dhcp.ParseRequest(Packet(2, (53, [5]), (12, Ascii("x")))));    // a server's ACK
        Assert.Null(Dhcp.ParseRequest(Packet(1, (53, [7]), (12, Ascii("x")))));    // RELEASE
        Assert.Null(Dhcp.ParseRequest(Packet(1, (12, Ascii("x")))));               // no message type
        Assert.Null(Dhcp.ParseRequest(new byte[100]));
        var bad = Packet(1, (53, [3]), (12, [0x41, 0x01, 0x42]));                    // control characters
        Assert.Null(Dhcp.ParseRequest(bad)!.HostName);
        var cut = Packet(1, (53, [3]), (12, Ascii("Tems-iPhone")));
        Assert.Null(Dhcp.ParseRequest(cut[..^6])!.HostName);                         // option runs past the end
    }
}

public class OuiTests
{
    [Fact]
    public void NamesTheMaker()
    {
        Assert.Equal("Apple", Oui.Vendor("44:9E:8B:54:60:B3"));
        Assert.Equal("Samsung", Oui.Vendor("5C-10-C5-4F-A5-D4"));
        Assert.Equal("Greenpacket", Oui.Vendor("24:79:EF:2F:C1:70"));
        Assert.Null(Oui.Vendor("2A:67:35:00:BB:BC")); // private, randomized address
    }

    [Theory]
    [InlineData("Greenpacket Berhad, Taiwan", "Greenpacket")]
    [InlineData("SAMSUNG ELECTRO-MECHANICS(THAILAND)", "Samsung")]
    [InlineData("Shenzhen Foo Technology Co., Ltd.", "Shenzhen Foo")]
    [InlineData("GUANGDONG OPPO MOBILE TELECOMMUNICATIONS CORP.,LTD", "OPPO")]
    [InlineData("ACME WIDGETS INC.", "Acme Widgets")]
    public void MakesCompanyNamesReadable(string registered, string shown) => Assert.Equal(shown, Oui.Clean(registered));
}

public class DeviceClassifierTests
{
    private static DeviceIdentity Of(Action<DeviceEvidence> fill)
    {
        var e = new DeviceEvidence();
        fill(e);
        return DeviceClassifier.Classify(e);
    }

    [Fact]
    public void AppleModelsSayExactlyWhatTheyAre()
    {
        var phone = Of(e => { e.Txt["rpMd"] = "iPhone15,2"; e.InstanceNames.Add("Tem's iPhone"); });
        Assert.Equal(new DeviceIdentity("Tem's iPhone", "iPhone", DeviceKinds.Phone, DeviceOs.Ios), phone);
        Assert.Equal((DeviceKinds.Computer, DeviceOs.MacOs, "MacBook Pro"), Of(e => e.Txt["model"] = "MacBookPro18,3") is var m ? (m.Kind, m.Os, m.Model) : default);
        Assert.Equal(DeviceKinds.Tv, Of(e => e.Txt["model"] = "AppleTV11,1").Kind);
    }

    [Fact]
    public void GivenNamesBeatHostNamesAndIdsArentNames()
    {
        Assert.Equal("Living Room", Of(e => { e.Services.Add("_raop._tcp"); e.InstanceNames.Add("A1B2C3D4E5F6@Living Room"); }).Name);
        Assert.Equal("Kitchen speaker", Of(e => { e.Txt["fn"] = "Kitchen speaker"; e.HostNames.Add("abc.local"); }).Name);
        Assert.Null(Of(e => e.InstanceNames.Add("3f2a9c1b7d6e5f40a1b2")).Name);
        Assert.Equal("Google Nest Mini", Of(e => e.InstanceNames.Add("Google Nest Mini-3f2a9c1b7d6e5f40a1b2")).Name);
        Assert.Equal("Tems-iPhone", Of(e => { e.DhcpName = "Tems-iPhone"; e.ReverseDns = "192-168-1-3.lan"; }).Name);
        Assert.Null(Of(e => e.ReverseDns = "192-168-1-3.lan").Name);
    }

    [Fact]
    public void MadeUpHostNamesArentNamesButStillTell()
    {
        var a = Of(e => { e.HostNames.Add("Android_HQJB9FND.local"); e.Vendor = "Samsung"; });
        Assert.Null(a.Name);
        Assert.Equal((DeviceKinds.Phone, DeviceOs.Android), (a.Kind, a.Os));
        Assert.Null(Of(e => e.DhcpName = "android-3f2a9c1b").Name);
    }

    [Fact]
    public void NamesAndDhcpTellTheKind()
    {
        Assert.Equal((DeviceKinds.Phone, DeviceOs.Ios), Of(e => e.DhcpName = "Tems-iPhone") is var i ? (i.Kind, i.Os) : default);
        Assert.Equal(DeviceKinds.Printer, Of(e => e.HostNames.Add("HP3F2A9C.local")).Kind);
        Assert.Equal((DeviceKinds.Phone, DeviceOs.Android), Of(e => e.DhcpVendor = "android-dhcp-14") is var d ? (d.Kind, d.Os) : default);
        Assert.Equal((DeviceKinds.Computer, DeviceOs.Windows), Of(e => e.DhcpVendor = "MSFT 5.0") is var w ? (w.Kind, w.Os) : default);
        Assert.Equal((DeviceKinds.Computer, DeviceOs.Windows), Of(e => e.NetBiosName = "OFFICE-PC") is var n ? (n.Kind, n.Os) : default);
    }

    [Fact]
    public void ServicesAndPortsTellTheKind()
    {
        Assert.Equal(DeviceKinds.Printer, Of(e => e.Services.Add("_ipp._tcp")).Kind);
        Assert.Equal(DeviceKinds.Tv, Of(e => { e.Services.Add("_googlecast._tcp"); e.Txt["md"] = "Chromecast"; }).Kind);
        Assert.Equal(DeviceKinds.Speaker, Of(e => { e.Services.Add("_googlecast._tcp"); e.Txt["md"] = "Google Nest Mini"; }).Kind);
        // An iPhone hiding behind a private address still keeps its sync port open.
        Assert.Equal((DeviceKinds.Phone, DeviceOs.Ios), Of(e => { e.Randomized = true; e.Ttl = 64; e.OpenPorts.Add(62078); }) is var p ? (p.Kind, p.Os) : default);
        Assert.Equal(DeviceKinds.Printer, Of(e => e.OpenPorts.Add(9100)).Kind);
        Assert.Equal((DeviceKinds.Computer, DeviceOs.MacOs), Of(e => { e.Vendor = "Apple"; e.OpenPorts.Add(7000); }) is var mac ? (mac.Kind, mac.Os) : default);
    }

    [Fact]
    public void ASamsungTvIsntTakenForAPhone()
    {
        // Samsung makes phones too, so the maker alone says phone; what the TV calls itself has to win.
        static (string, string?) Tv(Action<DeviceEvidence> fill) => Of(e => { e.Vendor = "Samsung"; e.Ttl = 64; fill(e); }) is var d ? (d.Kind, d.Os) : default;
        Assert.Equal((DeviceKinds.Tv, null), Tv(e => e.Upnp = new UpnpDescription("urn:samsung.com:device:RemoteControlReceiver:1", "Samsung 7 Series (43)", "Samsung Electronics", null, null)));
        Assert.Equal((DeviceKinds.Tv, null), Tv(e => e.InstanceNames.Add("Samsung 7 Series (43)")));
        Assert.Equal((DeviceKinds.Tv, null), Tv(e => e.Txt["fn"] = "[TV] Living Room"));
        Assert.Equal((DeviceKinds.Tv, null), Tv(e => e.Upnp = new UpnpDescription("urn:samsung.com:device:IPControlServer:1", null, "Samsung Electronics", "UE43RU7100", null)));
        Assert.Equal((DeviceKinds.Tv, null), Tv(e => e.Txt["md"] = "QN55Q60TAFXZA"));
        Assert.Equal("Samsung 7 Series", Of(e => e.InstanceNames.Add("Samsung 7 Series (43)")).Name);
        Assert.Equal(DeviceKinds.Console, Of(e => e.HostNames.Add("Xbox-Series-X.local")).Kind);
        Assert.Equal((DeviceKinds.Phone, DeviceOs.Android), Tv(e => e.Txt["md"] = "SM-S911B"));
    }

    [Fact]
    public void MakerAndTtlAreTheLastResort()
    {
        Assert.Equal(DeviceKinds.Router, Of(e => { e.IsGateway = true; e.Vendor = "Greenpacket"; }).Kind);
        Assert.Equal((DeviceKinds.Phone, DeviceOs.Android), Of(e => { e.Vendor = "Samsung"; e.Ttl = 64; }) is var s ? (s.Kind, s.Os) : default);
        Assert.Equal(DeviceKinds.Unknown, Of(e => { e.Vendor = "Apple"; e.Ttl = 64; }).Kind); // a Mac, iPad or watch: can't tell
        Assert.Equal((DeviceKinds.Computer, DeviceOs.Windows), Of(e => e.Ttl = 128) is var w ? (w.Kind, w.Os) : default);
        Assert.Equal(DeviceKinds.Phone, Of(e => { e.Randomized = true; e.Ttl = 64; }).Kind);
        Assert.Equal((DeviceKinds.Computer, DeviceOs.Windows), Of(e => { e.IsThisPc = true; e.Vendor = "TP-Link"; }) is var pc ? (pc.Kind, pc.Os) : default);
    }

    [Fact]
    public void HintsSurviveStorage()
    {
        var e = new DeviceEvidence { DhcpName = "Tems-iPhone", DhcpVendor = "x" };
        e.HostNames.Add("tems.local"); e.InstanceNames.Add("Tem's iPhone"); e.Services.Add("_companion-link._tcp"); e.Txt["rpMd"] = "iPhone15,2";
        var back = new DeviceEvidence();
        back.AddHints(e.Hints());
        Assert.Equal(e.Hints().OrderBy(h => h.Key), back.Hints().OrderBy(h => h.Key));
    }

    [Fact]
    public void PingTtlIsRoundedToWhereItStarted()
    {
        Assert.Equal([64, 64, 128, 128, 255], new[] { 64, 63, 128, 120, 254 }.Select(DeviceScanner.InitialTtl));
    }
}

public class DeviceProbeTests
{
    [Fact]
    public void RecordsGoToTheAddressTheirNameHas()
    {
        var tv = IPAddress.Parse("192.168.1.40");
        var proxy = IPAddress.Parse("192.168.1.50"); // an Apple TV answering for a sleeping Mac
        var mac = IPAddress.Parse("192.168.1.60");
        var found = new[] { tv, proxy, mac }.ToDictionary(a => a, _ => new DeviceEvidence());
        var fromProxy = DnsWire.Parse(Wire.Response(
            ("_device-info._tcp.local", DnsWire.Ptr, Wire.Name("Studio Mac._device-info._tcp.local")),
            ("Studio Mac._device-info._tcp.local", DnsWire.Srv, Wire.Srv("Studio-Mac.local")),
            ("Studio Mac._device-info._tcp.local", DnsWire.Txt, Wire.Txt("model=Macmini9,1")),
            ("Studio-Mac.local", DnsWire.A, [192, 168, 1, 60])));
        var fromTv = DnsWire.Parse(Wire.Response(("40.1.168.192.in-addr.arpa", DnsWire.Ptr, Wire.Name("Bedroom-TV.local"))));
        DeviceProbe.MergeMdns(found, [(proxy, fromProxy), (tv, fromTv)]);

        Assert.Equal("Macmini9,1", found[mac].Txt["model"]);
        Assert.Contains("Studio Mac", found[mac].InstanceNames);
        Assert.Contains("Studio-Mac.local", found[mac].HostNames);
        Assert.Empty(found[proxy].Txt);
        Assert.Contains("Bedroom-TV.local", found[tv].HostNames);
        Assert.Equal(("Studio Mac", "Mac mini"), (DeviceClassifier.Classify(found[mac]).Name, DeviceClassifier.Classify(found[mac]).Model));
    }

    [Fact]
    public void InstanceLabelsAreTheHumanPart()
    {
        Assert.Equal("Living Room TV", DeviceProbe.InstanceLabel("Living Room TV._googlecast._tcp.local", "_googlecast._tcp.local"));
        Assert.Null(DeviceProbe.InstanceLabel("other._ipp._tcp.local", "_googlecast._tcp.local"));
    }
}

public class DeviceLabelTests
{
    private static DeviceInfo D(string? name = null, string? vendor = null, string? kind = null, string? os = null, bool randomized = false, bool gateway = false, string? model = null) =>
        new("AA:BB:CC:DD:EE:FF", "192.168.1.9", name, randomized, gateway, false, 1, 1, vendor, model, kind, os);

    [Fact]
    public void FallsBackFromNameToWhatItIs()
    {
        Assert.Equal("Tems-iPhone", DeviceLabel.For(D("Tems-iPhone", "Apple", DeviceKinds.Phone, DeviceOs.Ios)));
        Assert.Equal("iPhone", DeviceLabel.For(D(kind: DeviceKinds.Phone, os: DeviceOs.Ios, randomized: true)));
        Assert.Equal("Samsung phone", DeviceLabel.For(D(vendor: "Samsung", kind: DeviceKinds.Phone, os: DeviceOs.Android)));
        Assert.Equal("Android phone", DeviceLabel.For(D(kind: DeviceKinds.Phone, os: DeviceOs.Android)));
        Assert.Equal("Greenpacket router", DeviceLabel.For(D(vendor: "Greenpacket", gateway: true)));
        Assert.Equal("Windows PC", DeviceLabel.For(D(kind: DeviceKinds.Computer, os: DeviceOs.Windows)));
        Assert.Equal("Apple device", DeviceLabel.For(D(vendor: "Apple", kind: DeviceKinds.Unknown)));
        Assert.Equal("Phone", DeviceLabel.For(D(kind: DeviceKinds.Phone, randomized: true)));
        Assert.Equal("Private device", DeviceLabel.For(D(randomized: true)));
        Assert.Equal("Unnamed device", DeviceLabel.For(D()));
        Assert.Null(DeviceLabel.What(D()));
    }
}

public class DeviceStorageTests
{
    [Fact]
    public void KeepsWhatWasLearnedWhenAScanKnowsLess()
    {
        using var db = Database.InMemory();
        var d = new DeviceInfo("AA:BB:CC:DD:EE:FF", "192.168.1.9", "Tems-iPhone", false, false, false, 1, 1, "Apple", "iPhone", DeviceKinds.Phone, DeviceOs.Ios);
        db.SaveDevice(d);
        db.SaveDevice(d with { Name = null, Model = null, Kind = DeviceKinds.Unknown, Os = null, LastSeen = 9 });
        var back = Assert.Single(db.Devices());
        Assert.Equal(("Tems-iPhone", "iPhone", DeviceKinds.Phone, DeviceOs.Ios, 9L), (back.Name, back.Model, back.Kind, back.Os, back.LastSeen));
        db.SaveDevice(d with { Kind = DeviceKinds.Tablet, Os = DeviceOs.IpadOs });
        Assert.Equal(DeviceKinds.Tablet, Assert.Single(db.Devices()).Kind);
    }

    [Fact]
    public void HintsAreCappedAndPruned()
    {
        using var db = Database.InMemory();
        db.SaveDeviceHints("AA:BB:CC:DD:EE:01", Enumerable.Range(0, 100).Select(i => ($"host:h{i}.local", "")), 1000);
        db.SaveDeviceHints("AA:BB:CC:DD:EE:01", [("dhcp_name", "Tems-iPhone")], 1000);
        Assert.Equal(64, db.DeviceHints()["AA:BB:CC:DD:EE:01"].Count);
        db.SaveDeviceHints("AA:BB:CC:DD:EE:01", [("host:h0.local", "updated")], 2000);
        Assert.Contains(("host:h0.local", "updated"), db.DeviceHints()["AA:BB:CC:DD:EE:01"]);

        db.SaveDevice(new DeviceInfo("AA:BB:CC:DD:EE:02", "192.168.1.2", null, false, false, false, 1, 1));
        db.SaveDeviceHints("AA:BB:CC:DD:EE:02", [("dhcp_name", "Galaxy-S23")], 1000);
        db.PruneDeviceHints(1000 + 2 * 86400);
        var left = db.DeviceHints();
        Assert.False(left.ContainsKey("AA:BB:CC:DD:EE:01")); // never became a device
        Assert.True(left.ContainsKey("AA:BB:CC:DD:EE:02"));
        db.PruneDeviceHints(1000 + 31 * 86400);
        Assert.Empty(db.DeviceHints());
    }

    [Fact]
    public void OlderDatabasesGainTheNewColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"glassrea-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var old = new SqliteDb(path))
            {
                old.Exec("""
                    CREATE TABLE devices(mac TEXT PRIMARY KEY, ip TEXT NOT NULL, name TEXT, randomized INTEGER NOT NULL,
                      is_gateway INTEGER NOT NULL, is_this_pc INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL);
                    INSERT INTO devices VALUES('AA:BB:CC:DD:EE:FF','192.168.1.9','phone',0,0,0,1,1);
                    """);
            }
            using var db = new Database(path);
            Assert.Equal("phone", Assert.Single(db.Devices()).Name);
            db.SaveDevice(new DeviceInfo("AA:BB:CC:DD:EE:FF", "192.168.1.9", null, false, false, false, 1, 2, "Apple", null, DeviceKinds.Phone, DeviceOs.Ios));
            Assert.Equal(("phone", "Apple", DeviceOs.Ios), Assert.Single(db.Devices()) is var d ? (d.Name, d.Vendor, d.Os) : default);
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
        }
    }
}
