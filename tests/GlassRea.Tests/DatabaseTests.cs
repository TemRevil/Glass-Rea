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
        Assert.Equal(1100, db.InternetTotal(Hour * 3600, (Hour + 1) * 3600));
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
    public void LanOnlyAppsStillListedButNotInInternetTotal()
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
