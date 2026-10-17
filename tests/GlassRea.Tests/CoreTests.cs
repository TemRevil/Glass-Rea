using System.Globalization;
using System.Net;
using GlassRea.Core.Alerts;
using GlassRea.Core.Net;
using GlassRea.Core.Paths;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Text;
using GlassRea.Core.Usage;
using GlassRea.Service;

namespace GlassRea.Tests;

public class AddressScopeTests
{
    [Theory]
    [InlineData("127.0.0.1", Scope.Loopback)]
    [InlineData("::1", Scope.Loopback)]
    [InlineData("0.0.0.0", Scope.Loopback)]
    [InlineData("192.168.1.20", Scope.Lan)]
    [InlineData("10.4.5.6", Scope.Lan)]
    [InlineData("172.16.0.1", Scope.Lan)]
    [InlineData("172.31.255.1", Scope.Lan)]
    [InlineData("169.254.3.3", Scope.Lan)]
    [InlineData("239.255.255.250", Scope.Lan)]
    [InlineData("255.255.255.255", Scope.Lan)]
    [InlineData("fe80::1", Scope.Lan)]
    [InlineData("fd12:3456::1", Scope.Lan)]
    [InlineData("ff02::fb", Scope.Lan)]
    [InlineData("172.32.0.1", Scope.Internet)]
    [InlineData("162.159.192.1", Scope.Internet)]
    [InlineData("2606:4700::1111", Scope.Internet)]
    [InlineData("::ffff:192.168.1.1", Scope.Lan)]
    [InlineData("::ffff:8.8.8.8", Scope.Internet)]
    public void Classifies(string ip, Scope expected) => Assert.Equal(expected, AddressScope.Classify(IPAddress.Parse(ip)));

    [Fact]
    public void ReadsEtwIpv4AsNetworkOrderUint()
    {
        // ETW stores 1.2.3.4 as the bytes 01 02 03 04, which read little-endian is 0x04030201.
        Assert.Equal(IPAddress.Parse("1.2.3.4"), AddressScope.FromPayload(0x04030201u));
        Assert.Equal(IPAddress.Parse("1.2.3.4"), AddressScope.FromPayload(0x04030201));
        Assert.Equal(IPAddress.Parse("2606:4700::1111"), AddressScope.FromPayload(IPAddress.Parse("2606:4700::1111").GetAddressBytes()));
        Assert.Null(AddressScope.FromPayload(null));
    }
}

public class DnsResultsTests
{
    [Fact]
    public void KeepsOnlyAddresses()
    {
        var ips = DnsResults.Parse("type:  5 edge.example.net;142.250.1.1;::ffff:1.2.3.4;2606:4700::1;");
        Assert.Equal(["142.250.1.1", "1.2.3.4", "2606:4700::1"], ips.Select(i => i.ToString()));
    }

    [Fact]
    public void EmptyIsEmpty() => Assert.Empty(DnsResults.Parse(null));
}

public class PathTests
{
    [Fact]
    public void NormalizesDevicePaths()
    {
        var paths = new DevicePaths(new Dictionary<string, string> { [@"\Device\HarddiskVolume3"] = "C:", [@"\Device\HarddiskVolume7"] = "G:" });
        Assert.Equal(@"C:\Windows\System32\svchost.exe", paths.Normalize(@"\Device\HarddiskVolume3\Windows\System32\svchost.exe"));
        Assert.Equal(@"G:\Apps\x.exe", paths.Normalize(@"\Device\HarddiskVolume7\Apps\x.exe"));
        // Volume3 must not match Volume30
        Assert.Equal(@"\Device\HarddiskVolume30\a.exe", paths.Normalize(@"\Device\HarddiskVolume30\a.exe"));
        Assert.Equal(@"C:\x.exe", paths.Normalize(@"\??\C:\x.exe"));
        Assert.Equal(@"C:\already.exe", paths.Normalize(@"C:\already.exe"));
    }

    [Fact]
    public void FindsPackageFamilyName()
    {
        Assert.Equal("SpotifyAB.SpotifyMusic_zpdnekdrzrea0",
            PackagePaths.FamilyName(@"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.248.0.0_x64__zpdnekdrzrea0\Spotify.exe"));
        Assert.Null(PackagePaths.FamilyName(@"C:\Program Files\Google\Chrome\Application\chrome.exe"));
    }
}

public class NumbersTests
{
    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("\u0661\u0662٫\u0665", 12.5)]
    [InlineData("\u06F3\u06F0", 30)]
    [InlineData(" 7,25 ", 7.25)]
    public void ParsesAnyDigits(string text, double expected)
    {
        Assert.True(Numbers.TryParse(text, out var v));
        Assert.Equal(expected, v, 6);
    }

    [Fact]
    public void RejectsText() => Assert.False(Numbers.TryParse("ten", out _));

    [Fact]
    public void FormatsBytesWithLatinDigitsUnderArabicCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal("1.5 GB", Bytes.Format(1610612736));
            Assert.Equal("512 B", Bytes.Format(512));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}

public class LimitTrackerTests
{
    [Fact]
    public void FiresEachThresholdOncePerPeriod()
    {
        var fired = new HashSet<string>();
        Assert.Empty(LimitTracker.Check("d1", "Daily", 70, 100, fired));
        Assert.Equal([80], LimitTracker.Check("d1", "Daily", 85, 100, fired).Select(c => c.Percent));
        Assert.Empty(LimitTracker.Check("d1", "Daily", 90, 100, fired));
        Assert.Equal([100], LimitTracker.Check("d1", "Daily", 120, 100, fired).Select(c => c.Percent));
        Assert.Empty(LimitTracker.Check("d1", "Daily", 200, 100, fired));
        Assert.Equal([80, 100], LimitTracker.Check("d2", "Daily", 200, 100, fired).Select(c => c.Percent));
    }

    [Fact]
    public void NoLimitNoAlerts() => Assert.Empty(LimitTracker.Check("d1", "Daily", long.MaxValue / 200, 0, new HashSet<string>()));
}

public class UsageAccumulatorTests
{
    private static readonly AppIdentity Chrome = new(@"c:\chrome.exe", "Chrome", @"C:\chrome.exe", null, false);
    private static readonly AppIdentity Warp = new(@"c:\warp-svc.exe", "WARP", @"C:\warp-svc.exe", null, true);
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void SplitsInternetAndLanAndDropsLoopback()
    {
        var acc = new UsageAccumulator();
        acc.Add(Chrome, T, 1000, false, IPAddress.Parse("8.8.8.8"));
        acc.Add(Chrome, T, 300, true, IPAddress.Parse("8.8.8.8"));
        acc.Add(Chrome, T, 50, false, IPAddress.Parse("192.168.1.5"));
        acc.Add(Chrome, T, 999, false, IPAddress.Parse("127.0.0.1"));
        var (usage, hosts) = acc.Drain();
        var row = Assert.Single(usage);
        Assert.Equal((1000, 300, 50, 0), (row.NetIn, row.NetOut, row.LanIn, row.LanOut));
        Assert.Equal(T.ToUnixTimeSeconds() / 3600, row.Hour);
        Assert.Equal(2, hosts.Count);
        Assert.Empty(acc.Drain().Usage);
    }

    [Fact]
    public void BucketsByHour()
    {
        var acc = new UsageAccumulator();
        acc.Add(Chrome, T, 10, false, IPAddress.Parse("1.1.1.1"));
        acc.Add(Chrome, T.AddHours(1), 20, false, IPAddress.Parse("1.1.1.1"));
        Assert.Equal(2, acc.Drain().Usage.Count);
    }

    [Fact]
    public void LiveGraphLeavesOutTheTunnel()
    {
        var acc = new UsageAccumulator();
        acc.Add(Chrome, T, 4000, false, IPAddress.Parse("1.1.1.1"));
        acc.Add(Warp, T, 4200, false, IPAddress.Parse("162.159.192.1"));
        var (inn, _) = acc.Live(T, 5);
        Assert.Equal(4000, inn[^1]);
        var top = acc.TopRates(T.AddSeconds(1), 5);
        Assert.Equal([Chrome.Key], top.Select(t => t.AppKey));
    }

    [Fact]
    public void LiveSecondsRollOver()
    {
        var acc = new UsageAccumulator(liveSeconds: 10);
        acc.Add(Chrome, T, 100, false, IPAddress.Parse("1.1.1.1"));
        var (inn, _) = acc.Live(T.AddSeconds(20), 10);
        Assert.All(inn, v => Assert.Equal(0, v)); // stale seconds are cleared, not replayed
    }
}

public class ServiceHelperTests
{
    [Fact]
    public void SubnetIsCappedAt22Bits()
    {
        var hosts = DeviceScanner.SubnetHosts(IPAddress.Parse("10.1.2.3"), IPAddress.Parse("255.0.0.0")).ToList();
        Assert.Equal(1022, hosts.Count);
        Assert.Equal(IPAddress.Parse("10.1.0.1"), hosts[0]);
    }

    [Fact]
    public void Slash24Has254Hosts()
    {
        var hosts = DeviceScanner.SubnetHosts(IPAddress.Parse("192.168.1.40"), IPAddress.Parse("255.255.255.0")).ToList();
        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), hosts[^1]);
    }

    [Fact]
    public void FirewallRuleNamesAreStablePerDirection()
    {
        Assert.Equal("Glass-Rea block out: c:\\x.exe", FirewallManager.RuleName(@"c:\x.exe", true));
        Assert.NotEqual(FirewallManager.RuleName("k", true), FirewallManager.RuleName("k", false));
    }
}

public class ProcessResolverTests
{
    [Fact]
    public void ProcessStartWithoutAnImageNameStaysUnknown()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gwrea-test-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Database(file);
            var resolver = new GlassRea.Service.Capture.ProcessResolver(new ServiceState(db),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GlassRea.Service.Capture.ProcessResolver>.Instance);
            const int pid = 0x7FFF_FFF0; // no such process, so only the start event names it
            resolver.OnProcessStart(pid, "");
            Assert.Same(AppIdentity.Unknown, resolver.Resolve(pid));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(file) + "*")) File.Delete(f);
        }
    }
}

public class AlertTextTests
{
    [Fact]
    public void NewAppNamesTheAppAndSaysWhatToDo()
    {
        var (title, body) = AlertText.NewApp("Spotify", @"C:\Apps\Spotify.exe");
        Assert.Equal("Spotify went online", title);
        Assert.StartsWith("This is the first time it has connected to the internet.", body);
        Assert.Contains("Firewall", body);
        Assert.EndsWith(@"C:\Apps\Spotify.exe", body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NewAppWithoutANameStillReads(string? name)
    {
        var (title, body) = AlertText.NewApp(name, @"C:\x.exe");
        Assert.Equal("An unnamed program went online", title);
        Assert.EndsWith(@"C:\x.exe", body);
    }

    [Fact]
    public void NewDeviceNamesTheDevice()
    {
        var (title, body) = AlertText.NewDevice("Galaxy-S23", "192.168.1.23", "AA:BB:CC:DD:EE:FF");
        Assert.Equal("Galaxy-S23 joined your network", title);
        Assert.Contains("192.168.1.23", body);
        Assert.Contains("AA:BB:CC:DD:EE:FF", body);
        Assert.Equal("A new device joined your network", AlertText.NewDevice(null, "192.168.1.9", "AA").Title);
    }

    [Fact]
    public void LimitsSayHowMuchAndWhenTheCountRestarts()
    {
        var warn = AlertText.Limit("Daily", 80, 8L << 30, 10L << 30);
        Assert.Equal("80% of your daily limit used", warn.Title);
        Assert.Equal("8 GB of 10 GB used today. The count starts again at midnight.", warn.Body);
        var hit = AlertText.Limit("Monthly", 100, 51L << 30, 50L << 30);
        Assert.Equal("Monthly limit reached", hit.Title);
        Assert.Equal("51 GB of 50 GB used this month. Glass-Rea doesn't cut your connection. The count starts again on the 1st.", hit.Body);
    }

    [Fact]
    public void NoAlertTextUsesEmDashes()
    {
        var all = new[]
        {
            AlertText.NewApp("A", "p"), AlertText.NewApp(null, "p"), AlertText.NewDevice("D", "1", "2"),
            AlertText.Limit("Daily", 80, 1, 2), AlertText.Limit("Monthly", 100, 2, 1),
        };
        Assert.DoesNotContain(all, t => (t.Title + t.Body).Contains('\u2014'));
    }
}

public class PriorityTests
{
    [Theory]
    [InlineData("normal", System.Diagnostics.ProcessPriorityClass.Normal)]
    [InlineData("above_normal", System.Diagnostics.ProcessPriorityClass.AboveNormal)]
    [InlineData("high", System.Diagnostics.ProcessPriorityClass.High)]
    [InlineData("realtime", System.Diagnostics.ProcessPriorityClass.Normal)]
    [InlineData(null, System.Diagnostics.ProcessPriorityClass.Normal)]
    public void ParsesOnlyTheOffered(string? value, System.Diagnostics.ProcessPriorityClass expected) =>
        Assert.Equal(expected, Priority.Parse(value));

    [Fact]
    public void AppliesToThisProcess()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var before = self.PriorityClass;
        try
        {
            Assert.True(Priority.ApplyToSelf(Priority.AboveNormal));
            self.Refresh();
            Assert.Equal(System.Diagnostics.ProcessPriorityClass.AboveNormal, self.PriorityClass);
        }
        finally { self.PriorityClass = before; }
    }

    [Fact]
    public void RealtimeIsNeverOffered()
    {
        Assert.False(Priority.IsValid("realtime"));
        Assert.True(Priority.IsValid("high"));
    }
}
