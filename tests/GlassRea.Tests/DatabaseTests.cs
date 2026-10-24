using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;

namespace GlassRea.Tests;

public class DatabaseTests
{
    private static readonly AppIdentity Chrome = new(@"c:\chrome.exe", "Chrome", @"C:\chrome.exe", null, false);
    private static readonly AppIdentity Steam = new(@"c:\steam.exe", "Steam", @"C:\steam.exe", null, false);
    private static readonly AppIdentity Warp = new(@"c:\warp-svc.exe", "WARP", @"C:\warp-svc.exe", null, true);
    private const long Hour = 500_000;

    private static UsageDelta D(long hour, long netIn, long netOut, long lanIn = 0) => new("", hour, netIn, netOut, lanIn, 0);

    [Fact]
    public void EnsureAppReportsNewOnlyOnce()
    {
        using var db = Database.InMemory();
        var a = db.EnsureApp(Chrome, 1, out var first);
        var b = db.EnsureApp(Chrome, 2, out var second);
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ReportExcludesTunnelFromTotalsButListsIt()
    {
        using var db = Database.InMemory();
        var chrome = db.EnsureApp(Chrome, 0, out _);
        var warp = db.EnsureApp(Warp, 0, out _);
        db.AddUsage([(chrome, D(Hour, 1000, 100)), (warp, D(Hour, 1100, 120))], UsageSource.Live);

        var r = db.Report(Hour * 3600, (Hour + 1) * 3600, new HashSet<long>());
        Assert.Equal(1000, r.NetIn);
        Assert.Equal(100, r.NetOut);
        Assert.Equal(1100, r.TunnelIn);
        Assert.Contains(r.Apps, a => a.IsTunnel && a.Name == "WARP");
        Assert.Equal(1000, Assert.Single(r.Hours).NetIn);
        Assert.Equal(1100, db.ConnectionTotal(Hour * 3600, (Hour + 1) * 3600));
    }

    [Fact]
    public void AddsUpAcrossFlushesAndSources()
    {
        using var db = Database.InMemory();
        var chrome = db.EnsureApp(Chrome, 0, out _);
        db.AddUsage([(chrome, D(Hour, 10, 1))], UsageSource.Live);
        db.AddUsage([(chrome, D(Hour, 15, 2))], UsageSource.Live);
        db.AddUsage([(chrome, D(Hour - 5, 100, 50))], UsageSource.WindowsHistory);
        var r = db.Report((Hour - 10) * 3600, (Hour + 1) * 3600, new HashSet<long>());
        var app = Assert.Single(r.Apps);
        Assert.Equal((125, 53), (app.NetIn, app.NetOut));
    }

    [Fact]
    public void RangeIsHalfOpenByHour()
    {
        using var db = Database.InMemory();
        var chrome = db.EnsureApp(Chrome, 0, out _);
        db.AddUsage([(chrome, D(Hour, 10, 0)), (chrome, D(Hour + 1, 20, 0)), (chrome, D(Hour + 2, 40, 0))], UsageSource.Live);
        Assert.Equal(20, db.Report((Hour + 1) * 3600, (Hour + 2) * 3600, new HashSet<long>()).NetIn);
    }

    [Fact]
    public void SortsAppsByInternetUseAndMarksBlocked()
    {
        using var db = Database.InMemory();
        var chrome = db.EnsureApp(Chrome, 0, out _);
        var steam = db.EnsureApp(Steam, 0, out _);
        db.AddUsage([(chrome, D(Hour, 10, 0)), (steam, D(Hour, 900, 0))], UsageSource.Live);
        var r = db.Report(Hour * 3600, (Hour + 1) * 3600, new HashSet<long> { steam });
        Assert.Equal(["Steam", "Chrome"], r.Apps.Select(a => a.Name));
        Assert.True(r.Apps[0].Blocked);
        Assert.False(r.Apps[1].Blocked);
    }

    [Fact]
    public void LanOnlyAppsStillListedButNotInInternetTotals()
    {
        using var db = Database.InMemory();
        var chrome = db.EnsureApp(Chrome, 0, out _);
        db.AddUsage([(chrome, D(Hour, 0, 0, lanIn: 5000))], UsageSource.Live);
        var r = db.Report(Hour * 3600, (Hour + 1) * 3600, new HashSet<long>());
        Assert.Equal(0, r.NetIn);
        Assert.Equal(5000, Assert.Single(r.Apps).LanIn);
    }

    [Fact]
    public void HostsJoinDnsNames()
    {
        using var db = Database.InMemory();
        var chrome = db.EnsureApp(Chrome, 0, out _);
        db.AddHosts([(chrome, new HostDelta("", 20261016, "142.250.1.1", 700, 70, 99)), (chrome, new HostDelta("", 20261016, "142.250.1.1", 300, 30, 100))]);
        db.SaveDns([("142.250.1.1", "www.google.com")], 0);
        var h = Assert.Single(db.Hosts(chrome, 20261016, 20261016));
        Assert.Equal(("www.google.com", 1000L, 100L, 100L), (h.Name, h.In, h.Out, h.LastSeen));
    }

    [Fact]
    public void DevicesReportNewOnce()
    {
        using var db = Database.InMemory();
        var d = new DeviceInfo("AA:BB:CC:DD:EE:FF", "192.168.1.9", null, false, false, false, 1, 1);
        Assert.True(db.SaveDevice(d));
        Assert.False(db.SaveDevice(d with { Name = "phone", LastSeen = 5 }));
        Assert.Equal("phone", Assert.Single(db.Devices()).Name);
    }

    [Fact]
    public void ADeviceTakenForSomethingElseLosesTheWrongSystem()
    {
        using var db = Database.InMemory();
        var d = new DeviceInfo("AA:00:00:00:00:02", "192.168.1.50", "Samsung 7 Series", false, false, false, 1, 1, "Samsung", Kind: DeviceKinds.Phone, Os: DeviceOs.Android);
        db.SaveDevice(d);
        db.SaveDevice(d with { Kind = DeviceKinds.Tv, Os = null, LastSeen = 2 });
        var tv = Assert.Single(db.Devices());
        Assert.Equal((DeviceKinds.Tv, null), (tv.Kind, tv.Os)); // a TV, not an Android TV

        var phone = d with { Mac = "AA:00:00:00:00:01", Os = DeviceOs.Ios };
        db.SaveDevice(phone);
        db.SaveDevice(phone with { Os = null, LastSeen = 2 }); // a scan that heard less still knows it's an iPhone
        Assert.Equal(DeviceOs.Ios, db.Devices().Single(x => x.Mac == phone.Mac).Os);
    }

    [Fact]
    public void AForgottenDeviceComesBackWhenSeenButNotAsNew()
    {
        using var db = Database.InMemory();
        var d = new DeviceInfo("AA:BB:CC:DD:EE:FF", "192.168.1.9", "Omars-iPhone", true, false, false, 1, 1);
        Assert.True(db.SaveDevice(d));
        db.RenameDevice(d.Mac, "Mum's phone");
        Assert.True(db.ForgetDevice(d.Mac, 10));
        Assert.Empty(db.Devices());
        Assert.False(db.ForgetDevice(d.Mac, 11)); // already gone

        Assert.False(db.SaveDevice(d with { LastSeen = 20 })); // back on the next scan, without a "new device" alert
        var back = Assert.Single(db.Devices());
        Assert.Null(back.CustomName); // under its own name again
        db.ForgetDevice(d.Mac, 30);
        db.PruneDeviceHints(30 + 31 * 86400); // a month later it's forgotten for good
        Assert.True(db.SaveDevice(d));
    }

    [Fact]
    public void MergingKeepsYourNameAndWhenItWasFirstSeen()
    {
        using var db = Database.InMemory();
        db.SaveDevice(new DeviceInfo("AA:00:00:00:00:01", "192.168.1.9", "Omars-iPhone", true, false, false, 100, 200));
        db.RenameDevice("AA:00:00:00:00:01", "Omar's phone");
        db.MergeDevice("AA:00:00:00:00:01", "AA:00:00:00:00:02"); // the new address isn't listed yet: the row moves
        var moved = Assert.Single(db.Devices());
        Assert.Equal(("AA:00:00:00:00:02", "Omar's phone", 100L), (moved.Mac, moved.CustomName, moved.FirstSeen));

        db.SaveDevice(new DeviceInfo("AA:00:00:00:00:03", "192.168.1.10", "Omars-iPhone", true, false, false, 300, 300));
        db.MergeDevice("AA:00:00:00:00:02", "AA:00:00:00:00:03"); // both listed: the old one folds into the new
        var merged = Assert.Single(db.Devices());
        Assert.Equal(("AA:00:00:00:00:03", "Omar's phone", 100L), (merged.Mac, merged.CustomName, merged.FirstSeen));
    }

    [Fact]
    public void AlertsAreNewestFirstAfterId()
    {
        using var db = Database.InMemory();
        var a = db.AddAlert("x", "one", "", 1);
        db.AddAlert("x", "two", "", 2);
        Assert.Equal(["two", "one"], db.Alerts(0).Select(x => x.Title));
        Assert.Equal(["two"], db.Alerts(a).Select(x => x.Title));
    }
}

public class SqliteTests
{
    [Fact]
    public void UncommittedTransactionRollsBack()
    {
        using var db = new SqliteDb(":memory:");
        db.Exec("CREATE TABLE t(v INTEGER)");
        using (db.BeginTransaction()) { using var c = db.Prepare("INSERT INTO t VALUES(1)"); c.ExecuteNonQuery(); }
        using (var tx = db.BeginTransaction()) { using var c = db.Prepare("INSERT INTO t VALUES(2)"); c.ExecuteNonQuery(); tx.Commit(); }
        using var sum = db.Prepare("SELECT sum(v) FROM t");
        Assert.Equal(2L, sum.ExecuteScalar());
    }

    [Fact]
    public void TextRoundTripsUnicodeAndNull()
    {
        using var db = new SqliteDb(":memory:");
        db.Exec("CREATE TABLE t(s TEXT)");
        using var ins = db.Prepare("INSERT INTO t VALUES($s)");
        ins["$s"] = "هاتف أحمد 📱"; ins.ExecuteNonQuery();
        ins["$s"] = null; ins.ExecuteNonQuery();
        using var q = db.Prepare("SELECT s FROM t ORDER BY rowid");
        using var r = q.ExecuteReader();
        Assert.True(r.Read()); Assert.Equal("هاتف أحمد 📱", r.GetString(0));
        Assert.True(r.Read()); Assert.True(r.IsDBNull(0));
        Assert.False(r.Read());
    }

    [Fact]
    public void ErrorsCarrySqliteMessage()
    {
        using var db = new SqliteDb(":memory:");
        db.Exec("CREATE TABLE t(k TEXT PRIMARY KEY)");
        using var ins = db.Prepare("INSERT INTO t VALUES($k)");
        ins["$k"] = "a"; ins.ExecuteNonQuery();
        Assert.Contains("UNIQUE", Assert.Throws<SqliteException>(() => ins.ExecuteNonQuery()).Message);
        Assert.Throws<SqliteException>(() => ins.Bind("$missing", 1));
    }
}

public class DeleteHistoryTests
{
    private static readonly AppIdentity Chrome = new(@"c:\chrome.exe", "Chrome", @"C:\chrome.exe", null, false);
    private static readonly AppIdentity Steam = new(@"c:\steam.exe", "Steam", @"C:\steam.exe", null, false);
    private const long Hour = 500_000;

    private static UsageDelta D(long hour, long netIn, long netOut) => new("", hour, netIn, netOut, 0, 0);

    [Fact]
    public void DeleteHistoryKeepsSettingsBlocksAndNetworks()
    {
        using var db = Database.InMemory();
        var chrome = db.EnsureApp(Chrome, 0, out _);
        db.AddUsage([(chrome, D(Hour, 1000, 100))], UsageSource.Live);
        db.AddHosts([(chrome, new HostDelta("", 20000, "1.1.1.1", 10, 10, 5))]);
        db.SaveDns([("1.1.1.1", "one.one")], 5);
        db.SaveDevice(new DeviceInfo("AA-BB-CC-DD-EE-FF", "192.168.1.5", "Phone", false, false, false, 1, 2));
        db.AddAlert("new_app", "Chrome went online", "First time.", 5);
        db.SetSetting(SettingKeys.DailyLimit, "123");
        db.SetBlocked(chrome, true);
        var home = db.EnsureNetwork("wifi:Home", "Home", NetworkKinds.WiFi, 1, 2);

        db.DeleteHistory();

        Assert.Empty(db.Report(0, (Hour + 1) * 3600, new HashSet<long>()).Apps);
        Assert.Empty(db.Hosts(chrome, 0, int.MaxValue));
        Assert.Empty(db.Devices());
        Assert.Empty(db.Alerts(0));
        Assert.False(db.HasUsage());
        Assert.Equal("123", db.GetSetting(SettingKeys.DailyLimit));
        Assert.Equal([chrome], db.BlockedApps());
        Assert.Contains(db.Networks(0), n => n.Id == home);
    }

    [Fact]
    public void UsageAfterDeleteCountsOnlyNewBytes()
    {
        using var db = Database.InMemory();
        var chrome = db.EnsureApp(Chrome, 0, out _);
        db.AddUsage([(chrome, D(Hour, 1000, 100))], UsageSource.Live);
        db.DeleteHistory();
        db.AddUsage([(chrome, D(Hour, 50, 5))], UsageSource.Live);
        Assert.Equal(50, db.Report(Hour * 3600, (Hour + 1) * 3600, new HashSet<long>()).NetIn);
    }

    [Fact]
    public void ClearBlockedUnblocksAll()
    {
        using var db = Database.InMemory();
        db.SetBlocked(db.EnsureApp(Chrome, 0, out _), true);
        db.SetBlocked(db.EnsureApp(Steam, 0, out _), true);
        db.ClearBlocked();
        Assert.Empty(db.BlockedApps());
    }
}
