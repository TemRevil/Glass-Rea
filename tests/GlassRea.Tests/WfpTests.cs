using System.Runtime.InteropServices;
using System.Security.Principal;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;
using GlassRea.Service.Blocking;

namespace GlassRea.Tests;

public class WfpTests
{
    // Sizes and offsets of the x64 structs in the Windows SDK's fwpmtypes.h and fwptypes.h.
    [Fact]
    public void TheFirewallEngineStructsHaveWindowsLayout()
    {
        Assert.Equal(16, Marshal.SizeOf<WfpBlocker.DisplayData>());
        Assert.Equal(16, Marshal.SizeOf<WfpBlocker.ByteBlob>());
        Assert.Equal(16, Marshal.SizeOf<WfpBlocker.Value>());
        Assert.Equal(72, Marshal.SizeOf<WfpBlocker.Session>());
        Assert.Equal(72, Marshal.SizeOf<WfpBlocker.SubLayer>());
        Assert.Equal(40, Marshal.SizeOf<WfpBlocker.Condition>());
        Assert.Equal(200, Marshal.SizeOf<WfpBlocker.Filter>());

        Assert.Equal(48, Offset<WfpBlocker.Session>("Sid"));
        Assert.Equal(64, Offset<WfpBlocker.Session>("KernelMode"));
        Assert.Equal(64, Offset<WfpBlocker.SubLayer>("Weight"));
        Assert.Equal(24, Offset<WfpBlocker.Condition>("Value"));
        Assert.Equal(64, Offset<WfpBlocker.Filter>("Layer"));
        Assert.Equal(80, Offset<WfpBlocker.Filter>("SubLayer"));
        Assert.Equal(96, Offset<WfpBlocker.Filter>("Weight"));
        Assert.Equal(112, Offset<WfpBlocker.Filter>("ConditionCount"));
        Assert.Equal(120, Offset<WfpBlocker.Filter>("Conditions"));
        Assert.Equal(128, Offset<WfpBlocker.Filter>("Action"));
        Assert.Equal(152, Offset<WfpBlocker.Filter>("ProviderContext"));
        Assert.Equal(176, Offset<WfpBlocker.Filter>("Id"));
        Assert.Equal(184, Offset<WfpBlocker.Filter>("EffectiveWeight"));
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    [Fact]
    public void AServiceSidIsTheOneWindowsGivesIt()
    {
        Assert.Equal("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", WfpBlocker.ServiceSid("TrustedInstaller"));
        foreach (var service in new[] { "Dnscache", "wuauserv", "BITS" })
            Assert.Equal(new NTAccount($@"NT SERVICE\{service}").Translate(typeof(SecurityIdentifier)).Value, WfpBlocker.ServiceSid(service));
    }

    [Fact]
    public void TheStoreCopysBlocksAreKeptAndCarriedOverByKey()
    {
        using var db = Database.InMemory();
        var browser = db.EnsureApp(new AppIdentity(@"c:\apps\browser.exe", "Browser", @"C:\Apps\browser.exe", null, false), 1, out _);
        var game = db.EnsureApp(new AppIdentity(@"c:\apps\game.exe", "Game", @"C:\Apps\game.exe", null, false), 1, out _);
        Assert.Empty(db.BlockedApps());

        db.SetBlocked(browser, true);
        Assert.Equal([browser], db.BlockedApps());
        db.SetBlocked(browser, false);
        Assert.Empty(db.BlockedApps());

        db.SetBlocked([@"c:\apps\game.exe", @"c:\apps\gone.exe"]);
        Assert.Equal([game], db.BlockedApps());
    }
}
