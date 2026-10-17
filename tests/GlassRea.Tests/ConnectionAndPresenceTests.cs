using System.Net;
using GlassRea.Core.Alerts;
using GlassRea.Core.Net;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;
using GlassRea.Service;
using GlassRea.Service.Devices;

namespace GlassRea.Tests;

public class ConnectionCountTests
{
    private static readonly AppIdentity Chrome = new(@"c:\chrome.exe", "Chrome", @"C:\chrome.exe", null, false);
    private static readonly AppIdentity Warp = new(@"c:\warp-svc.exe", "WARP", @"C:\warp-svc.exe", null, true);
    private const long Hour = 500_000;

    [Fact]
    public void BytesThroughAVpnAdapterAreMarked()
    {
        var acc = new UsageAccumulator();
        var t = DateTimeOffset.FromUnixTimeSeconds(Hour * 3600 + 10);
        acc.Add(Chrome, t, 1000, false, IPAddress.Parse("104.16.1.1"), viaTunnel: true);
        acc.Add(Chrome, t, 300, true, IPAddress.Parse("104.16.1.1"), viaTunnel: true);
        acc.Add(Chrome, t, 50, false, IPAddress.Parse("142.250.1.1"));
        acc.Add(Chrome, t, 70, false, IPAddress.Parse("192.168.1.9"), viaTunnel: true); // local network: never through the VPN
        acc.AddThroughTunnel(Chrome, t, 5, outbound: true);
        var u = Assert.Single(acc.Drain().Usage);
        Assert.Equal((1050L, 305L, 70L, 1000L, 305L), (u.NetIn, u.NetOut, u.LanIn, u.TunIn, u.TunOut));
    }

    [Fact]
    public void TheConnectionCarriesTheVpnNotTheAppsAgain()
    {
        using var db = Database.InMemory();
        db.SetSetting(SettingKeys.TunnelMarkedFrom, Hour.ToString());
        var chrome = db.EnsureApp(Chrome, 0, out _);
        var warp = db.EnsureApp(Warp, 0, out _);
        // Chrome moved 1000 in, 100 out through WARP and 200 in outside it; WARP sent the 1100 wrapped, plus overhead.
        db.AddUsage([(chrome, new UsageDelta("", Hour, 1200, 100, 0, 0, 1000, 100)), (warp, new UsageDelta("", Hour, 1080, 130, 0, 0))], UsageSource.Live);

        var (i, o) = db.Connection(Hour * 3600, (Hour + 1) * 3600);
        Assert.Equal((1280L, 130L), (i, o));
        Assert.Equal(1410, db.ConnectionTotal(Hour * 3600, (Hour + 1) * 3600));
        var r = db.Report(Hour * 3600, (Hour + 1) * 3600, new HashSet<long>());
        Assert.Equal((1200L, 100L, 1280L, 130L), (r.NetIn, r.NetOut, r.ConnectionIn, r.ConnectionOut));
    }

    [Fact]
    public void HoursFromBeforeTheMarkingCountTheAppsOnly()
    {
        using var db = Database.InMemory();
        db.SetSetting(SettingKeys.TunnelMarkedFrom, (Hour + 1).ToString());
        var chrome = db.EnsureApp(Chrome, 0, out _);
        var warp = db.EnsureApp(Warp, 0, out _);
        db.AddUsage([(chrome, new UsageDelta("", Hour, 1000, 0, 0, 0)), (warp, new UsageDelta("", Hour, 1100, 0, 0, 0))], UsageSource.Live);
        db.AddUsage([(chrome, new UsageDelta("", Hour - 5, 400, 0, 0, 0)), (warp, new UsageDelta("", Hour - 5, 450, 0, 0, 0))], UsageSource.WindowsHistory);
        Assert.Equal(1400, db.ConnectionTotal((Hour - 5) * 3600, (Hour + 1) * 3600)); // WARP left out: its bytes are Chrome's

        // Once Windows history is marked, its hours count WARP instead of the bytes Chrome sent through it.
        db.SetImportedTunnel([(chrome, Hour - 5, 400, 0)]);
        db.SetSetting(SettingKeys.SrumTunnelSplit, "done");
        Assert.Equal(1450, db.ConnectionTotal((Hour - 5) * 3600, (Hour + 1) * 3600));
    }

    [Fact]
    public void MarkingImportedHistoryNeverExceedsWhatWasCounted()
    {
        using var db = Database.InMemory();
        db.SetSetting(SettingKeys.SrumTunnelSplit, "done");
        var chrome = db.EnsureApp(Chrome, 0, out _);
        db.AddUsage([(chrome, new UsageDelta("", Hour, 100, 10, 0, 0))], UsageSource.WindowsHistory);
        db.AddUsage([(chrome, new UsageDelta("", Hour, 50, 5, 0, 0))], UsageSource.Live);
        db.SetImportedTunnel([(chrome, Hour, 999, 999)]);
        // History marked whole (100 + 10 gone), live untouched (55 left).
        Assert.Equal(55, db.ConnectionTotal(Hour * 3600, (Hour + 1) * 3600));
    }

    [Fact]
    public void ServicesAreTheProgramsTheyRun()
    {
        Assert.Equal(@"C:\Program Files\Cloudflare\Cloudflare WARP\warp-svc.exe",
            GlassRea.Service.History.SrumImporter.ProgramOf("\"C:\\Program Files\\Cloudflare\\Cloudflare WARP\\warp-svc.exe\""));
        Assert.Equal(@"C:\WINDOWS\system32\svchost.exe", GlassRea.Service.History.SrumImporter.ProgramOf(@"C:\WINDOWS\system32\svchost.exe -k netsvcs -p"));
        Assert.Equal(@"C:\Tools\vpn.exe", GlassRea.Service.History.SrumImporter.ProgramOf("  \"C:\\Tools\\vpn.exe\" --service"));
        Assert.Null(GlassRea.Service.History.SrumImporter.ProgramOf(@"\SystemRoot\System32\drivers\tcpip.sys"));
        Assert.True(TunnelProcesses.IsTunnel(GlassRea.Service.History.SrumImporter.ProgramOf("\"C:\\Program Files\\Cloudflare\\Cloudflare WARP\\warp-svc.exe\""), TunnelProcesses.Defaults));
    }

    [Fact]
    public void AnAppFoundToBeTheVpnLeavesTheAppsTotal()
    {
        using var db = Database.InMemory();
        var warp = db.EnsureApp(new AppIdentity("srum:cloudflarewarp", "CloudflareWARP", null, null, false), 0, out _);
        var chrome = db.EnsureApp(Chrome, 0, out _);
        db.AddUsage([(warp, new UsageDelta("", Hour, 900, 100, 0, 0)), (chrome, new UsageDelta("", Hour, 800, 80, 0, 0))], UsageSource.WindowsHistory);
        db.SetAppPath(warp, @"C:\Program Files\Cloudflare\Cloudflare WARP\warp-svc.exe", isTunnel: true);
        var r = db.Report(Hour * 3600, (Hour + 1) * 3600, new HashSet<long>());
        Assert.Equal((800L, 1000L), (r.NetIn, r.TunnelIn + r.TunnelOut));
        Assert.Equal(@"C:\Program Files\Cloudflare\Cloudflare WARP\warp-svc.exe", Assert.Single(db.AllApps(), a => a.Id == warp).Path);
    }

    [Fact]
    public void KnowsVpnAdapters()
    {
        Assert.True(TunnelAdapters.IsTunnel("CloudflareWARP", "Cloudflare WARP Interface Tunnel", 53));
        Assert.True(TunnelAdapters.IsTunnel("Ethernet 3", "TAP-Windows Adapter V9", 6));
        Assert.True(TunnelAdapters.IsTunnel("wg0", "WireGuard Tunnel", 53));
        Assert.True(TunnelAdapters.IsTunnel("x", "y", 131));
        Assert.False(TunnelAdapters.IsTunnel("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", 71));
        Assert.False(TunnelAdapters.IsTunnel("Ethernet", "Realtek PCIe GbE Family Controller", 6));
    }
}

public class DeviceRenameTests
{
    [Fact]
    public void YourNameWinsAndSurvivesScans()
    {
        using var db = Database.InMemory();
        var d = new DeviceInfo("AA:BB:CC:DD:EE:FF", "192.168.1.9", "Galaxy-S23", false, false, false, 1, 1, "Samsung", null, DeviceKinds.Phone, DeviceOs.Android);
        db.SaveDevice(d);
        Assert.True(db.RenameDevice(d.Mac, "  Mum's phone "));
        db.SaveDevice(d with { LastSeen = 9 });
        var back = Assert.Single(db.Devices());
        Assert.Equal(("Mum's phone", "Mum's phone"), (back.CustomName, DeviceLabel.For(back)));
        Assert.True(db.RenameDevice(d.Mac, ""));
        Assert.Equal("Galaxy-S23", DeviceLabel.For(Assert.Single(db.Devices())));
        Assert.False(db.RenameDevice("00:00:00:00:00:00", "x"));
    }

    [Fact]
    public void NamesAreCheckedAndDigitsMadePlain()
    {
        var arabic = new string([(char)0x0661, (char)0x0662]); // Arabic-Indic one, two
        Assert.True(DeviceLabel.TryCleanName($" Room {arabic} ", out var clean));
        Assert.Equal("Room 12", clean);
        Assert.True(DeviceLabel.TryCleanName("   ", out var blank));
        Assert.Null(blank);
        Assert.False(DeviceLabel.TryCleanName("two\nlines", out _));
        Assert.False(DeviceLabel.TryCleanName(new string('x', DeviceLabel.MaxNameLength + 1), out _));
        Assert.True(DeviceLabel.TryCleanName(new string('x', DeviceLabel.MaxNameLength), out _));
    }
}

public class PresenceTests
{
    private const string Home = "00:11:22:33:44:55", Cafe = "66:77:88:99:AA:BB";
    private const long Now = 1_800_000_000;

    private static DeviceInfo D(string mac, long lastSeen, bool away = false, string network = Home, bool gateway = false, bool thisPc = false) =>
        new(mac, "192.168.1.20", "Phone " + mac[^1], false, gateway, thisPc, 1, lastSeen, Away: away, Network: network);

    private static Dictionary<string, DeviceInfo> Before(params DeviceInfo[] d) => d.ToDictionary(x => x.Mac);

    [Fact]
    public void MissingFifteenMinutesIsLeftAndReturningIsBack()
    {
        var before = Before(
            D("AA:00:00:00:00:01", Now - DeviceScanner.AwayAfter),        // just gone
            D("AA:00:00:00:00:02", Now - 300),                             // missed one scan only
            D("AA:00:00:00:00:03", Now - 7200, away: true),                // was away, now here
            D("AA:00:00:00:00:04", Now - 3 * 86400),                       // gone for days (this PC was off)
            D(Home, Now - 3600, gateway: true),                            // the router itself: never "left"
            D("AA:00:00:00:00:05", Now - 3600, thisPc: true));
        var present = new Dictionary<string, string> { ["AA:00:00:00:00:03"] = "192.168.1.33" };

        var changes = DeviceScanner.Presence(before, present, Home, Now).ToDictionary(c => c.Device.Mac);
        Assert.Equal(3, changes.Count);
        Assert.Equal((false, true), (changes["AA:00:00:00:00:01"].Back, changes["AA:00:00:00:00:01"].Alert));
        Assert.Equal((true, true), (changes["AA:00:00:00:00:03"].Back, changes["AA:00:00:00:00:03"].Alert));
        Assert.Equal((false, false), (changes["AA:00:00:00:00:04"].Back, changes["AA:00:00:00:00:04"].Alert)); // marked quietly
    }

    [Fact]
    public void AnotherNetworksDevicesAreNeverMissing()
    {
        var before = Before(D("AA:00:00:00:00:01", Now - 3600, network: Home), D("AA:00:00:00:00:02", Now - 3600, away: true, network: Home));
        Assert.Empty(DeviceScanner.Presence(before, new Dictionary<string, string>(), Cafe, Now));
        Assert.Empty(DeviceScanner.Presence(before, new Dictionary<string, string>(), null, Now));
        // Seen again, but behind another router: no longer away, without an alert.
        var back = Assert.Single(DeviceScanner.Presence(before, new Dictionary<string, string> { ["AA:00:00:00:00:02"] = "10.0.0.5" }, Cafe, Now));
        Assert.Equal((true, false), (back.Back, back.Alert));
    }

    [Fact]
    public void AlertsSayHowLong()
    {
        Assert.Equal("1 minute", AlertText.Span(30));
        Assert.Equal("15 minutes", AlertText.Span(15 * 60));
        Assert.Equal("1 hour", AlertText.Span(3600));
        Assert.Equal("2 hours 5 minutes", AlertText.Span(2 * 3600 + 5 * 60));
        Assert.Equal("1 day 3 hours", AlertText.Span(27 * 3600 + 59));
        Assert.Equal("2 days", AlertText.Span(2 * 86400));
        var (title, body) = AlertText.DeviceBack("Tems-iPhone", "192.168.1.13", 3 * 3600);
        Assert.Equal("Tems-iPhone is back on your network", title);
        Assert.Equal("Away for 3 hours, now at 192.168.1.13.", body);
        Assert.Equal("Tems-iPhone left your network", AlertText.DeviceLeft("Tems-iPhone", "192.168.1.13", 900).Title);
    }
}

public class DiscoveryHardeningTests
{
    [Fact]
    public void AnAnnouncementOnlySpeaksForItsSender()
    {
        var sender = IPAddress.Parse("192.168.1.50");
        var records = DnsWire.Parse(Wire.Response(
            ("_device-info._tcp.local", DnsWire.Ptr, Wire.Name("Studio Mac._device-info._tcp.local")),
            ("Studio Mac._device-info._tcp.local", DnsWire.Srv, Wire.Srv("Studio-Mac.local")),
            ("Studio Mac._device-info._tcp.local", DnsWire.Txt, Wire.Txt("model=Macmini9,1")),
            ("Studio-Mac.local", DnsWire.A, [192, 168, 1, 60]),
            ("Living Room._airplay._tcp.local", DnsWire.Txt, Wire.Txt("model=AppleTV14,1")),
            ("_airplay._tcp.local", DnsWire.Ptr, Wire.Name("Living Room._airplay._tcp.local"))));
        var announced = DeviceListener.Announced(sender, records);
        Assert.Equal(sender, Assert.Single(announced.Keys));      // nothing saved for 192.168.1.60
        var e = announced[sender];
        Assert.Contains("Living Room", e.InstanceNames);         // about itself: kept
        Assert.Equal("AppleTV14,1", e.Txt["model"]);
        Assert.DoesNotContain("Studio Mac", e.InstanceNames);     // about 192.168.1.60: dropped
        Assert.DoesNotContain("Studio-Mac.local", e.HostNames);
    }

    [Fact]
    public void OnlyFollowsDescriptionLinks()
    {
        var sender = IPAddress.Parse("192.168.1.9");
        Assert.NotNull(Ssdp.SafeLocation("http://192.168.1.9:49152/rootDesc.xml", sender));
        Assert.NotNull(Ssdp.SafeLocation("http://192.168.1.9:8060/dial/dd.xml", sender));
        Assert.NotNull(Ssdp.SafeLocation("http://192.168.1.9:1400/description", sender));
        Assert.Null(Ssdp.SafeLocation("http://192.168.1.9/desc.xml?reboot=1", sender));
        Assert.Null(Ssdp.SafeLocation("http://192.168.1.9/desc.xml#x", sender));
        Assert.Null(Ssdp.SafeLocation("http://admin:admin@192.168.1.9/desc.xml", sender));
        Assert.Null(Ssdp.SafeLocation("http://192.168.1.9/cgi-bin/reset", sender));
    }

    [Fact]
    public void HintsStayFewWhateverTheNetworkSends()
    {
        using var db = Database.InMemory();
        for (var m = 0; m < 100; m++)
            db.SaveDeviceHints($"AA:BB:CC:DD:{m / 256:X2}:{m % 256:X2}", Enumerable.Range(0, 60).Select(i => ($"host:h{i}.local", "")), 1000 + m);
        var hints = db.DeviceHints();
        Assert.Equal(Database.MaxHints, hints.Values.Sum(h => h.Count));
        Assert.False(hints.ContainsKey("AA:BB:CC:DD:00:00"));    // the oldest went first
        Assert.True(hints.ContainsKey("AA:BB:CC:DD:00:63"));
    }
}
