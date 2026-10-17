using System.Net;
using GlassRea.Core.Net;
using GlassRea.Core.Usage;

namespace GlassRea.Tests;

public class TunnelPortsTests
{
    private const int WarpPid = 4312, ChromePid = 900;

    private static TunnelPorts Warp() => TunnelPorts.From(
    [
        (true, IPAddress.Parse("127.0.2.2"), 53, WarpPid),   // WARP's DNS
        (false, IPAddress.Parse("127.0.2.2"), 53, WarpPid),
        (true, IPAddress.Loopback, 40000, WarpPid),          // WARP in proxy mode
        (false, IPAddress.Any, 54590, WarpPid),              // its tunnel socket, reachable from loopback too
        (true, IPAddress.Parse("192.168.1.6"), 7680, WarpPid), // bound to the LAN address only: not for local apps
        (true, IPAddress.Loopback, 9222, ChromePid),         // someone else's port
    ], pid => pid == WarpPid);

    [Fact]
    public void KnowsOnlyTheVpnsReachablePorts()
    {
        var p = Warp();
        Assert.True(p.Has(true, 40000, false));
        Assert.True(p.Has(false, 53, false));
        Assert.True(p.Has(false, 54590, false));
        Assert.False(p.Has(true, 54590, false)); // that one is UDP
        Assert.False(p.Has(true, 7680, false));
        Assert.False(p.Has(true, 9222, false));
        Assert.True(TunnelPorts.None.IsEmpty);
    }

    [Fact]
    public void WorksOutTheByteOrderFromTheVpnsOwnPackets()
    {
        var p = Warp();
        var swapped40000 = TunnelPorts.Swap(40000);
        Assert.Equal(40000, TunnelPorts.Swap(swapped40000));
        Assert.True(p.Has(true, swapped40000, null));     // unknown order: either form
        Assert.True(p.Has(true, swapped40000, true));
        Assert.False(p.Has(true, swapped40000, false));
        Assert.Equal(false, p.OrderFrom(true, 40000, 51234)); // plain form matched
        Assert.Equal(true, p.OrderFrom(true, TunnelPorts.Swap(51234), swapped40000));
        Assert.Null(p.OrderFrom(true, 1111, 2222));      // neither matches: nothing learned
    }
}

public class ThroughTunnelTests
{
    private static readonly AppIdentity Chrome = new("chrome", "Chrome", @"C:\chrome.exe", null, false);
    private static readonly AppIdentity Warp = new("warp", "Cloudflare WARP", @"C:\warp-svc.exe", null, true);

    [Fact]
    public void ProxiedBytesAreTheAppsInternetUseWithoutAHost()
    {
        var acc = new UsageAccumulator();
        var t = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        acc.AddThroughTunnel(Chrome, t, 1000, outbound: false);
        acc.AddThroughTunnel(Chrome, t, 200, outbound: true);
        acc.Add(Chrome, t, 50, false, IPAddress.Loopback); // plain loopback still counts for nothing
        var (usage, hosts) = acc.Drain();
        var u = Assert.Single(usage);
        Assert.Equal(("chrome", 1000L, 200L, 0L, 0L), (u.AppKey, u.NetIn, u.NetOut, u.LanIn, u.LanOut));
        Assert.Empty(hosts);
        Assert.Equal(1000, acc.Live(t, 1).In.Sum());
    }

    [Fact]
    public void TheVpnItselfStaysOutOfTheLiveTotal()
    {
        var acc = new UsageAccumulator();
        var t = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        acc.AddThroughTunnel(Warp, t, 5000, outbound: false);
        Assert.Equal(0, acc.Live(t, 1).In.Sum());
    }
}
