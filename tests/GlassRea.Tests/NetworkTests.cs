using System.Net;
using System.Text.Json;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;
using GlassRea.Service.Networks;

namespace GlassRea.Tests;

public class NetworkDatabaseTests
{
    private static readonly AppIdentity Browser = new(@"c:\browser.exe", "Browser", @"C:\browser.exe", null, false);
    private static readonly AppIdentity Player = new(@"c:\player.exe", "Player", @"C:\player.exe", null, false);
    private static readonly AppIdentity Vpn = new(@"c:\vpn.exe", "VPN", @"C:\vpn.exe", null, true);
    private const long Hour = 500_000;
    private static readonly long From = Hour * 3600, To = (Hour + 3) * 3600;

    private static UsageDelta D(long hour, long network, long netIn, long netOut, long tunIn = 0, long tunOut = 0, long lanIn = 0) =>
        new("", hour, netIn, netOut, lanIn, 0, tunIn, tunOut, network);

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"glassrea-test-{Guid.NewGuid():N}.db");

    private static void Delete(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
    }

    [Fact]
    public void AnOlderDatabaseKeepsEveryRowOnTheUnknownNetwork()
    {
        var path = TempPath();
        try
        {
            using (var old = new SqliteDb(path))
            {
                // The shape before networks, and before VPN traffic was marked (no tun_ columns either).
                old.Exec("""
                    CREATE TABLE apps(id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, name TEXT NOT NULL, path TEXT, service TEXT,
                      is_tunnel INTEGER NOT NULL DEFAULT 0, first_seen INTEGER NOT NULL);
                    CREATE TABLE usage(app_id INTEGER NOT NULL REFERENCES apps(id), hour INTEGER NOT NULL, source INTEGER NOT NULL,
                      net_in INTEGER NOT NULL DEFAULT 0, net_out INTEGER NOT NULL DEFAULT 0, lan_in INTEGER NOT NULL DEFAULT 0, lan_out INTEGER NOT NULL DEFAULT 0,
                      PRIMARY KEY(app_id, hour, source)) WITHOUT ROWID;
                    CREATE INDEX usage_by_hour ON usage(hour);
                    CREATE TABLE hosts(app_id INTEGER NOT NULL REFERENCES apps(id), day INTEGER NOT NULL, ip TEXT NOT NULL,
                      bytes_in INTEGER NOT NULL DEFAULT 0, bytes_out INTEGER NOT NULL DEFAULT 0, last_seen INTEGER NOT NULL,
                      PRIMARY KEY(app_id, day, ip)) WITHOUT ROWID;
                    INSERT INTO apps VALUES(1,'c:\browser.exe','Browser','C:\browser.exe',NULL,0,1);
                    INSERT INTO usage VALUES(1,500000,0,1000,100,50,5);
                    INSERT INTO usage VALUES(1,500001,1,2000,200,0,0);
                    INSERT INTO hosts VALUES(1,20261016,'142.250.1.1',700,70,99);
                    """);
            }
            using (var db = new Database(path))
            {
                var all = db.Report(From, To, new HashSet<long>());
                Assert.Equal((3000L, 300L), (all.NetIn, all.NetOut));
                Assert.Equal(50, Assert.Single(all.Apps).LanIn);
                var unknown = db.Report(From, To, new HashSet<long>(), NetworkLabel.Unknown);
                Assert.Equal((3000L, 300L), (unknown.NetIn, unknown.NetOut));
                Assert.Equal(NetworkLabel.Unknown, Assert.Single(db.Networks(0)).Id);
                Assert.Equal((700L, 70L), Assert.Single(db.Hosts(1, 20261016, 20261016, NetworkLabel.Unknown)) is var h ? (h.In, h.Out) : default);

                // New use goes on its network beside the old rows.
                var home = db.EnsureNetwork("{home}", "Home", NetworkKinds.WiFi, 1, 1);
                db.AddUsage([(1, D(Hour, home, 10, 1))], UsageSource.Live);
                Assert.Equal(10, db.Report(From, To, new HashSet<long>(), home).NetIn);
                Assert.Equal(3010, db.Report(From, To, new HashSet<long>()).NetIn);
            }
            using (var again = new Database(path)) // opening it again changes nothing
                Assert.Equal(3010, again.Report(From, To, new HashSet<long>()).NetIn);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void EachNetworksShareAddsUpToAllNetworks()
    {
        using var db = Database.InMemory();
        var browser = db.EnsureApp(Browser, 0, out _);
        var player = db.EnsureApp(Player, 0, out _);
        var vpn = db.EnsureApp(Vpn, 0, out _);
        var home = db.EnsureNetwork("{home}", "Home", NetworkKinds.WiFi, 1, 1);
        var cafe = db.EnsureNetwork("{cafe}", "Cafe", NetworkKinds.WiFi, 1, 1);
        db.SetSetting(SettingKeys.TunnelMarkedFrom, "0");
        db.AddUsage([
            (browser, D(Hour, home, 1000, 100, tunIn: 400, tunOut: 40)), (vpn, D(Hour, home, 450, 50)),
            (browser, D(Hour + 1, cafe, 300, 30)), (player, D(Hour + 1, cafe, 5000, 70, lanIn: 9)),
            (player, D(Hour + 2, NetworkLabel.Unknown, 20, 2)),
        ], UsageSource.Live);
        db.AddUsage([(player, D(Hour + 2, cafe, 600, 6))], UsageSource.WindowsHistory);

        var all = db.Report(From, To, new HashSet<long>());
        var parts = new long?[] { home, cafe, NetworkLabel.Unknown }.Select(n => db.Report(From, To, new HashSet<long>(), n)).ToList();
        Assert.Equal(all.NetIn, parts.Sum(p => p.NetIn));
        Assert.Equal(all.NetOut, parts.Sum(p => p.NetOut));
        Assert.Equal(all.TunnelIn, parts.Sum(p => p.TunnelIn));
        Assert.Equal(all.ConnectionIn, parts.Sum(p => p.ConnectionIn));
        Assert.Equal(all.ConnectionOut, parts.Sum(p => p.ConnectionOut));
        Assert.Equal(all.Hours.Sum(h => h.NetIn), parts.Sum(p => p.Hours.Sum(h => h.NetIn)));
        foreach (var app in all.Apps)
            Assert.Equal(app.NetIn + app.LanIn, parts.Sum(p => p.Apps.Where(a => a.AppId == app.AppId).Sum(a => a.NetIn + a.LanIn)));

        // Only that network's figures: Home holds the VPN, whose wrapped copy replaces what went through it.
        var atHome = parts[0];
        Assert.Equal((1000L, 100L), (atHome.NetIn, atHome.NetOut));
        Assert.Equal((1000L - 400 + 450, 100L - 40 + 50), (atHome.ConnectionIn, atHome.ConnectionOut));
        Assert.DoesNotContain(atHome.Apps, a => a.AppId == player);
        Assert.Equal([Hour], atHome.Hours.Select(h => h.Hour));
        // Limits count every network.
        Assert.Equal(all.ConnectionIn + all.ConnectionOut, db.ConnectionTotal(From, To));
    }

    [Fact]
    public void HostsAreShownPerNetwork()
    {
        using var db = Database.InMemory();
        var browser = db.EnsureApp(Browser, 0, out _);
        var home = db.EnsureNetwork("{home}", "Home", NetworkKinds.WiFi, 1, 1);
        var cafe = db.EnsureNetwork("{cafe}", "Cafe", NetworkKinds.WiFi, 1, 1);
        db.AddHosts([
            (browser, new HostDelta("", 20261016, "142.250.1.1", 700, 70, 99, home)),
            (browser, new HostDelta("", 20261016, "142.250.1.1", 300, 30, 100, cafe)),
            (browser, new HostDelta("", 20261016, "151.101.1.1", 50, 5, 101, cafe)),
        ]);
        Assert.Equal((1000L, 100L), Assert.Single(db.Hosts(browser, 20261016, 20261016), h => h.Ip == "142.250.1.1") is var all ? (all.In, all.Out) : default);
        Assert.Equal(["142.250.1.1"], db.Hosts(browser, 20261016, 20261016, home).Select(h => h.Ip));
        Assert.Equal(["142.250.1.1", "151.101.1.1"], db.Hosts(browser, 20261016, 20261016, cafe).Select(h => h.Ip));
        Assert.Equal(300, db.Hosts(browser, 20261016, 20261016, cafe)[0].In);
    }

    [Fact]
    public void NetworksKeepWindowsNameUpToDateAndYourNameAsGiven()
    {
        using var db = Database.InMemory();
        var home = db.EnsureNetwork("{home}", "Network 2", NetworkKinds.Wired, 100, 100);
        Assert.Equal(home, db.EnsureNetwork("{home}", "Network 2", null, 50, 200)); // the same profile, seen again
        var cafe = db.EnsureNetwork("{cafe}", "Cafe", NetworkKinds.WiFi, 300, 300);
        Assert.NotEqual(home, cafe);

        Assert.True(db.RenameNetwork(home, "  Home  "));
        db.EnsureNetwork("{home}", "Network 3", null, 400, 400); // renamed in Windows
        var listed = db.Networks(cafe);
        Assert.Equal([home, cafe], listed.Select(n => n.Id)); // the latest first
        var h = listed[0];
        Assert.Equal(("Network 3", "Home", NetworkKinds.Wired, 50L, 400L, false), (h.Name, h.CustomName, h.Kind, h.FirstSeen, h.LastSeen, h.Current));
        Assert.True(listed[1].Current);
        Assert.Equal("Home", NetworkLabel.For(h));

        Assert.True(db.RenameNetwork(home, " "));
        Assert.Equal("Network 3", NetworkLabel.For(db.Networks(0).Single(n => n.Id == home)));
        Assert.False(db.RenameNetwork(999, "Nowhere"));
        Assert.DoesNotContain(db.Networks(0), n => n.Id == NetworkLabel.Unknown); // no use on an unknown network
    }

    [Fact]
    public void PlacingEarlierUseOnNetworksNeverChangesTheTotal()
    {
        using var db = Database.InMemory();
        var browser = db.EnsureApp(Browser, 0, out _);
        var player = db.EnsureApp(Player, 0, out _);
        var home = db.EnsureNetwork("{home}", "Home", NetworkKinds.WiFi, 1, 1);
        var cafe = db.EnsureNetwork("{cafe}", "Cafe", NetworkKinds.WiFi, 1, 1);
        db.AddUsage([(browser, D(Hour, 0, 1000, 100, tunIn: 300, tunOut: 30))], UsageSource.WindowsHistory);
        db.AddUsage([(player, D(Hour + 1, 0, 500, 50, lanIn: 7)), (player, D(Hour + 1, cafe, 5, 1))], UsageSource.Live);
        var before = db.Report(From, To, new HashSet<long>());

        var moved = db.AssignNetworks([
            new NetworkMove(browser, Hour, UsageSource.WindowsHistory, home, 600, 60, 300, 30),
            new NetworkMove(browser, Hour, UsageSource.WindowsHistory, cafe, 900, 90, 0, 0), // more than is left: only the rest moves
            new NetworkMove(player, Hour + 1, UsageSource.Live, cafe), // the whole live row, onto the row already there
            new NetworkMove(player, Hour + 5, UsageSource.Live, cafe), // nothing there
        ]);
        Assert.Equal(3, moved);

        var after = db.Report(From, To, new HashSet<long>());
        Assert.Equal((before.NetIn, before.NetOut, before.ConnectionIn, before.ConnectionOut), (after.NetIn, after.NetOut, after.ConnectionIn, after.ConnectionOut));
        var atHome = db.Report(From, To, new HashSet<long>(), home);
        Assert.Equal((600L, 60L), (atHome.NetIn, atHome.NetOut));
        var atCafe = db.Report(From, To, new HashSet<long>(), cafe);
        Assert.Equal((400L + 505, 40L + 51), (atCafe.NetIn, atCafe.NetOut));
        Assert.Equal(7, atCafe.Apps.Single(a => a.AppId == player).LanIn);
        Assert.DoesNotContain(db.Networks(0), n => n.Id == NetworkLabel.Unknown); // emptied rows are gone
        // VPN traffic stays within each side's internet bytes: Home took all 300, Cafe's 400 none.
        Assert.Equal((600L - 300 + 400, 60L - 30 + 40), (atHome.ConnectionIn + atCafe.ConnectionIn - 505, atHome.ConnectionOut + atCafe.ConnectionOut - 51));
    }

    [Fact]
    public void AMoveLeavesNoMoreVpnTrafficBehindThanInternetBytes()
    {
        using var db = Database.InMemory();
        var browser = db.EnsureApp(Browser, 0, out _);
        var home = db.EnsureNetwork("{home}", "Home", NetworkKinds.WiFi, 1, 1);
        db.AddUsage([(browser, D(Hour, 0, 100, 10, tunIn: 100, tunOut: 10))], UsageSource.WindowsHistory);
        db.AssignNetworks([new NetworkMove(browser, Hour, UsageSource.WindowsHistory, home, 60, 6, 0, 0)]);
        var left = db.Report(From, To, new HashSet<long>(), NetworkLabel.Unknown);
        var moved = db.Report(From, To, new HashSet<long>(), home);
        Assert.Equal((40L, 4L), (left.NetIn, left.NetOut));
        Assert.True(left.ConnectionIn >= 0 && moved.ConnectionIn >= 0);
        Assert.Equal((0L, 0L), (left.ConnectionIn + moved.ConnectionIn, left.ConnectionOut + moved.ConnectionOut));
    }

    [Fact]
    public void TheProfilesOfOneWifiBecomeOneNetwork()
    {
        using var db = Database.InMemory();
        var browser = db.EnsureApp(Browser, 0, out _);
        var home = db.EnsureNetwork("{home}", "Home", NetworkKinds.WiFi, 100, 200);
        var again = db.EnsureNetwork("{home-2}", "Home 2", NetworkKinds.WiFi, 50, 300);
        var cafe = db.EnsureNetwork("{cafe}", "Cafe", NetworkKinds.WiFi, 1, 1);
        Assert.True(db.RenameNetwork(again, "Flat"));
        db.AddUsage([(browser, D(Hour, home, 100, 10)), (browser, D(Hour, again, 50, 5, lanIn: 3)), (browser, D(Hour + 1, again, 7, 1))], UsageSource.Live);
        db.AddUsage([(browser, D(Hour, cafe, 1000, 100))], UsageSource.Live);
        db.AddHosts([(browser, new HostDelta("", 20261016, "142.250.1.1", 10, 1, 99, home)), (browser, new HostDelta("", 20261016, "142.250.1.1", 20, 2, 120, again))]);
        var before = db.Report(From, To, new HashSet<long>());

        Assert.True(db.Rekey("{home}", "wifi:Home", "Home")); // the first profile just takes the Wi-Fi's key
        Assert.True(db.Rekey("{home-2}", "wifi:Home", "Home")); // the second joins it
        Assert.False(db.Rekey("{gone}", "wifi:Gone", "Gone"));

        Assert.Equal(home, db.EnsureNetwork("wifi:Home", "Home", NetworkKinds.WiFi, 400, 400));
        var listed = db.Networks(0);
        Assert.Equal([home, cafe], listed.Select(n => n.Id).Order());
        var h = listed.Single(n => n.Id == home);
        Assert.Equal(("Home", "Flat", 50L, 400L), (h.Name, h.CustomName, h.FirstSeen, h.LastSeen));
        var after = db.Report(From, To, new HashSet<long>());
        Assert.Equal((before.NetIn, before.NetOut, before.Apps.Sum(a => a.LanIn)), (after.NetIn, after.NetOut, after.Apps.Sum(a => a.LanIn)));
        var atHome = db.Report(From, To, new HashSet<long>(), home);
        Assert.Equal((157L, 16L, 3L), (atHome.NetIn, atHome.NetOut, atHome.Apps.Single().LanIn));
        Assert.Equal(0, db.Report(From, To, new HashSet<long>(), again).NetIn);
        var host = Assert.Single(db.Hosts(browser, 20261016, 20261016, home));
        Assert.Equal((30L, 3L, 120L), (host.In, host.Out, host.LastSeen));
    }

    [Fact]
    public void ASecondPassOverWindowsHistoryPlacesOnlyWhatIsntPlacedYet()
    {
        using var db = Database.InMemory();
        var browser = db.EnsureApp(Browser, 0, out _);
        var home = db.EnsureNetwork("wifi:Home", "Home", NetworkKinds.WiFi, 1, 1);
        var cafe = db.EnsureNetwork("wifi:Cafe", "Cafe", NetworkKinds.WiFi, 1, 1);
        // The hour was 100 on Home and 50 on Cafe; the first pass knew only Home.
        db.AddUsage([(browser, D(Hour, 0, 150, 15))], UsageSource.WindowsHistory);
        db.AssignNetworks([new NetworkMove(browser, Hour, UsageSource.WindowsHistory, home, 100, 10, 0, 0)]);

        var placed = db.ImportedOnNetworks();
        Assert.Equal(new HistoryTotal(100, 10, 0, 0), placed[(browser, Hour, home)]);
        var history = new[] { (Network: home, Total: new HistoryTotal(100, 10, 0, 0)), (Network: cafe, Total: new HistoryTotal(50, 5, 0, 0)) };
        var moves = history
            .Select(x => (x.Network, Left: HistoryNetworks.Rest(x.Total, placed.GetValueOrDefault((browser, Hour, x.Network)))))
            .Where(x => x.Left.In + x.Left.Out > 0)
            .Select(x => new NetworkMove(browser, Hour, UsageSource.WindowsHistory, x.Network, x.Left.In, x.Left.Out, x.Left.TunIn, x.Left.TunOut));
        db.AssignNetworks(moves);

        Assert.Equal((100L, 10L), Totals(db, home));
        Assert.Equal((50L, 5L), Totals(db, cafe));
        Assert.Equal((0L, 0L), Totals(db, NetworkLabel.Unknown));

        static (long, long) Totals(Database db, long network)
        {
            var r = db.Report(From, To, new HashSet<long>(), network);
            return (r.NetIn, r.NetOut);
        }
    }

    [Fact]
    public void HostsOfADayMoveOntoItsNetwork()
    {
        using var db = Database.InMemory();
        var browser = db.EnsureApp(Browser, 0, out _);
        var home = db.EnsureNetwork("{home}", "Home", NetworkKinds.WiFi, 1, 1);
        db.AddHosts([
            (browser, new HostDelta("", 20261016, "142.250.1.1", 700, 70, 99)),
            (browser, new HostDelta("", 20261016, "142.250.1.1", 300, 30, 120, home)),
            (browser, new HostDelta("", 20261017, "142.250.1.1", 1, 1, 200)),
        ]);
        db.AssignHostNetworks([(20261016, home)]);
        var h = Assert.Single(db.Hosts(browser, 20261016, 20261016, home));
        Assert.Equal((1000L, 100L, 120L), (h.In, h.Out, h.LastSeen));
        Assert.Empty(db.Hosts(browser, 20261016, 20261016, NetworkLabel.Unknown));
        Assert.Single(db.Hosts(browser, 20261017, 20261017, NetworkLabel.Unknown)); // another day stays
    }
}

public class NetworkCountingTests
{
    private static readonly AppIdentity Browser = new(@"c:\browser.exe", "Browser", @"C:\browser.exe", null, false);
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void UseCountsOnTheNetworkThePcIsOnAtTheTime()
    {
        var acc = new UsageAccumulator();
        acc.Add(Browser, T, 100, false, IPAddress.Parse("8.8.8.8"));
        acc.Network = 3;
        acc.Add(Browser, T.AddSeconds(1), 200, false, IPAddress.Parse("8.8.8.8"));
        acc.AddThroughTunnel(Browser, T.AddSeconds(2), 50, true);
        var (usage, hosts) = acc.Drain();
        Assert.Equal([(0L, 100L, 0L), (3L, 200L, 50L)], usage.OrderBy(u => u.Network).Select(u => (u.Network, u.NetIn, u.TunOut)));
        Assert.All(usage, u => Assert.Equal(T.ToUnixTimeSeconds() / 3600, u.Hour));
        Assert.Equal([(0L, 100L), (3L, 200L)], hosts.OrderBy(h => h.Network).Select(h => (h.Network, h.In)));
    }

    [Fact]
    public void ARequestForAllNetworksLooksAsItDidBefore()
    {
        Assert.DoesNotContain("network", JsonSerializer.Serialize(new Request("usage", 1, 2), Pipe.Json));
        Assert.Contains("\"network\":0", JsonSerializer.Serialize(new Request("usage", 1, 2, Network: 0), Pipe.Json));
        Assert.Equal(5, JsonSerializer.Deserialize<Request>("""{"op":"usage","network":5}""", Pipe.Json)!.Network);
        Assert.Null(JsonSerializer.Deserialize<Request>("""{"op":"usage"}""", Pipe.Json)!.Network);
    }

    [Fact]
    public void NetworksAreNamedPlainly()
    {
        Assert.Equal("Unknown network", NetworkLabel.For(new NetworkInfo(0, "", null, null, 0, 0)));
        Assert.Equal("Home", NetworkLabel.For(new NetworkInfo(1, "Network 2", " Home ", NetworkKinds.Wired, 0, 0)));
        Assert.Equal("Cafe", NetworkLabel.For(new NetworkInfo(2, "Cafe", null, NetworkKinds.WiFi, 0, 0)));
        Assert.Equal("Unnamed network", NetworkLabel.For(new NetworkInfo(3, " ", null, null, 0, 0)));
        Assert.True(NetworkLabel.TryCleanName("Cafe \u0662", out var clean));
        Assert.Equal("Cafe 2", clean);
        Assert.False(NetworkLabel.TryCleanName(new string('x', NetworkLabel.MaxNameLength + 1), out _));
    }
}

public class HistoryNetworkTests
{
    private const string Home = "{home}", Cafe = "{cafe}";
    private const long H = 500_000;

    [Fact]
    public void VpnTrafficCountsOnTheNetworkUnderneath()
    {
        var rows = new List<HistoryRow>
        {
            new("browser", H, false, Home, 1000, 100),
            new("browser", H, true, null, 400, 40),     // through the VPN's adapter, in an hour spent at home
            new("vpn", H, false, Home, 450, 50),
            new("browser", H + 1, false, Home, 10, 1),
            new("player", H + 1, false, null, 20, 2),   // a wired network too: the hour isn't on one network
            new("browser", H + 1, true, null, 30, 3),
        };
        var totals = HistoryNetworks.Totals(rows);
        Assert.Equal(new HistoryTotal(1400, 140, 400, 40), totals[("browser", H, Home)]);
        Assert.Equal(new HistoryTotal(450, 50, 0, 0), totals[("vpn", H, Home)]);
        Assert.Equal(new HistoryTotal(10, 1, 0, 0), totals[("browser", H + 1, Home)]);
        Assert.Equal(new HistoryTotal(30, 3, 30, 3), totals[("browser", H + 1, null)]);
        Assert.Equal(rows.Sum(r => r.In), totals.Values.Sum(t => t.In)); // nothing added or lost
    }

    [Fact]
    public void ALiveHourIsOnANetworkOnlyWhenBothHistoryHoursCoveringItAre()
    {
        var hours = HistoryNetworks.HourNetworks([
            new("browser", H, false, Home, 1, 1),
            new("browser", H + 1, false, Home, 1, 1),
            new("browser", H + 2, false, Cafe, 1, 1),
            new("browser", H + 3, false, Cafe, 1, 1),
            new("player", H + 3, false, Home, 1, 1),   // two networks in one hour
            new("browser", H + 4, true, null, 1, 1),   // only a VPN: says nothing
        ]);
        Assert.Equal(new Dictionary<long, string> { [H] = Home, [H + 1] = Home, [H + 2] = Cafe }, hours);
        Assert.Equal(new Dictionary<long, string> { [H] = Home }, HistoryNetworks.LiveHours(hours));
    }

    [Fact]
    public void ADaysHostsMoveOnlyWhenAllItsHoursWereOnOneNetwork()
    {
        long HourAt(int hour) => new DateTimeOffset(2026, 10, 16, hour, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 16))).ToUnixTimeSeconds() / 3600;
        var unknown = new[] { HourAt(9), HourAt(10), HourAt(9), HourAt(11) };
        var whole = HistoryNetworks.WholeDays(unknown, new Dictionary<long, long> { [HourAt(9)] = 4, [HourAt(10)] = 4, [HourAt(11)] = 4 });
        Assert.Equal(new Dictionary<int, long> { [20261016] = 4 }, whole);
        Assert.Empty(HistoryNetworks.WholeDays(unknown, new Dictionary<long, long> { [HourAt(9)] = 4, [HourAt(10)] = 4 }));
        Assert.Empty(HistoryNetworks.WholeDays(unknown, new Dictionary<long, long> { [HourAt(9)] = 4, [HourAt(10)] = 4, [HourAt(11)] = 5 }));
    }

    [Fact]
    public void AWifiIsCountedByItsName()
    {
        // Windows' "Home" and "Home 2" profiles are the one Wi-Fi.
        Assert.Equal(NetworkList.Wifi("Home"), NetworkList.Wifi("Home"));
        Assert.Equal(new WindowsNetwork("wifi:Home", "Home", NetworkKinds.WiFi), NetworkList.Wifi("Home"));
        Assert.NotEqual(NetworkList.Wifi("Home").Key, NetworkList.Wifi("home").Key);
        Assert.NotEqual(NetworkList.Key(Guid.Empty), NetworkList.Wifi(NetworkList.Key(Guid.Empty)).Key);
    }

    [Fact]
    public void ReadsTheSsidOfASavedWifiProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"glassrea-test-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, """
                <?xml version="1.0"?>
                <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                  <name>Cafe profile</name>
                  <SSIDConfig><SSID><hex>43616665</hex><name>Cafe</name></SSID></SSIDConfig>
                </WLANProfile>
                """);
            Assert.Equal("Cafe", NetworkList.Ssid(path));
            File.WriteAllText(path, """<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1"><SSIDConfig><SSID><hex>486F6D65</hex></SSID></SSIDConfig></WLANProfile>""");
            Assert.Equal("Home", NetworkList.Ssid(path));
            File.WriteAllText(path, """
                <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                  <SSIDConfig><SSID><hex>466C6174</hex><name>Flat</name></SSID><nonBroadcast>true</nonBroadcast></SSIDConfig>
                </WLANProfile>
                """);
            Assert.Equal("Flat", NetworkList.Ssid(path)); // a hidden Wi-Fi
            File.WriteAllText(path, "not xml");
            Assert.Null(NetworkList.Ssid(path));
        }
        finally { File.Delete(path); }
        Assert.Null(NetworkList.Ssid(path));
    }
}
