using GlassRea.Core.Alerts;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;
using GlassRea.Service;

namespace GlassRea.Tests;

public class PermissionTests
{
    public static TheoryData<Feature> Features => [.. PermissionKeys.All];

    [Fact]
    public void KeysAreStable()
    {
        Assert.Equal("allow_usage", PermissionKeys.For(Feature.Usage));
        Assert.Equal("allow_blocking", PermissionKeys.For(Feature.Blocking));
        Assert.Equal("allow_history", PermissionKeys.For(Feature.History));
        Assert.Equal("allow_devices", PermissionKeys.For(Feature.Devices));
        Assert.Equal("permissions_reviewed", PermissionKeys.Reviewed);
        Assert.Equal(4, PermissionKeys.All.Length);
    }

    [Fact]
    public void AllowedReadsEachFlag()
    {
        var p = new PermissionSet(true, false, true, false, false);
        Assert.True(p.Allowed(Feature.Usage));
        Assert.False(p.Allowed(Feature.Blocking));
        Assert.True(p.Allowed(Feature.History));
        Assert.False(p.Allowed(Feature.Devices));
    }

    [Theory, MemberData(nameof(Features))]
    public void WordingIsPlain(Feature f)
    {
        foreach (var s in new[] { PermissionText.Title(f), PermissionText.Why(f), PermissionText.OffTitle(f), PermissionText.OffBody(f) })
        {
            Assert.False(string.IsNullOrWhiteSpace(s));
            Assert.DoesNotContain('\u2014', s);
            Assert.DoesNotContain('\u2013', s);
            Assert.Matches("^[A-Z]", s);
        }
        Assert.Equal(PermissionText.Title(f) + " is off", PermissionText.OffTitle(f));
    }

    private static Permissions Fresh(out Database db)
    {
        db = Database.InMemory();
        var p = new Permissions(db);
        p.Initialize();
        return p;
    }

    [Fact]
    public void FreshDatabaseStartsAllOff()
    {
        var p = Fresh(out var db);
        using var _ = db;
        Assert.Equal(new PermissionSet(false, false, false, false, false), p.Current);
    }

    [Fact]
    public void ExistingInstallKeepsEverythingOn()
    {
        using var db = Database.InMemory();
        db.SetSetting(SettingKeys.FirstEtwHour, "1");
        var p = new Permissions(db);
        p.Initialize();
        Assert.Equal(new PermissionSet(true, true, true, true, false), p.Current);
        Assert.Equal("1", db.GetSetting(SettingKeys.CaptureFirstHour));
    }

    [Fact]
    public void ExistingUsageRowAlsoCountsAsInstalled()
    {
        using var db = Database.InMemory();
        var app = db.EnsureApp(new AppIdentity(@"c:\a.exe", "A", @"C:\a.exe", null, false), 0, out _);
        db.AddUsage([(app, new UsageDelta("", 10, 5, 5, 0, 0))], UsageSource.Live);
        var p = new Permissions(db);
        p.Initialize();
        Assert.True(p.Current.Usage && p.Current.Blocking && p.Current.History && p.Current.Devices);
    }

    [Fact]
    public void InitializeRunsOnce()
    {
        var p = Fresh(out var db);
        using var _ = db;
        p.Set(Feature.Usage, true);
        db.SetSetting(SettingKeys.FirstEtwHour, "1");
        new Permissions(db).Initialize();
        Assert.Equal(new PermissionSet(true, false, false, false, false), new Permissions(db).Current);
    }

    [Fact]
    public async Task WaitAllowedReturnsWhenTurnedOn()
    {
        var p = Fresh(out var db);
        using var _ = db;
        var wait = p.WaitAllowed(Feature.Devices, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        p.Set(Feature.Devices, true);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void WhileAllowedCancelsWhenTurnedOff()
    {
        var p = Fresh(out var db);
        using var _ = db;
        p.Set(Feature.Usage, true);
        var token = p.WhileAllowed(Feature.Usage);
        Assert.False(token.IsCancellationRequested);
        p.Set(Feature.Usage, false);
        Assert.True(token.IsCancellationRequested);
        p.Set(Feature.Usage, true);
        Assert.False(p.WhileAllowed(Feature.Usage).IsCancellationRequested);
    }

    [Fact]
    public void ChangedFiresOnlyOnRealChange()
    {
        var p = Fresh(out var db);
        using var _ = db;
        var fired = 0;
        p.Changed += (_, _) => fired++;
        p.Set(Feature.History, true);
        p.Set(Feature.History, true);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ReviewedIsKept()
    {
        var p = Fresh(out var db);
        using var _ = db;
        p.MarkReviewed();
        Assert.True(new Permissions(db).Current.Reviewed);
    }
}

public class PermissionWorkerTests
{
    [Fact]
    public async Task WhileOnRunsAgainAfterTurnedBackOn()
    {
        using var db = Database.InMemory();
        var p = new Permissions(db);
        p.Initialize();
        using var stop = new CancellationTokenSource();
        var runs = 0;
        var running = p.WhileOn(Feature.Usage, async t => { runs++; await Task.Delay(Timeout.Infinite, t); }, stop.Token);
        await Task.Delay(50);
        Assert.Equal(0, runs);
        p.Set(Feature.Usage, true);
        await Task.Delay(100);
        Assert.Equal(1, runs);
        p.Set(Feature.Usage, false);
        p.Set(Feature.Usage, true);
        await Task.Delay(100);
        Assert.Equal(2, runs);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WhileOnEndsWhenTheWorkIsDone()
    {
        using var db = Database.InMemory();
        var p = new Permissions(db);
        p.Initialize();
        p.Set(Feature.History, true);
        var runs = 0;
        await Task.Run(() => p.WhileOn(Feature.History, _ => { runs++; return Task.CompletedTask; }, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void SrumCutoffUsesNowWhenNothingWasCaptured()
    {
        Assert.Equal(500, GlassRea.Service.History.SrumImporter.Cutoff(0, 500));
        Assert.Equal(400, GlassRea.Service.History.SrumImporter.Cutoff(400, 500));
    }

    [Fact]
    public void NewAppAlertsWaitForTheFirstCaptureHour()
    {
        Assert.False(GlassRea.Service.Capture.FlushService.Settled(0, 9));
        Assert.False(GlassRea.Service.Capture.FlushService.Settled(9, 9));
        Assert.True(GlassRea.Service.Capture.FlushService.Settled(9, 10));
    }
}

public class BlockingPermissionTests
{
    private static FirewallManager Firewall(Database db, Permissions p) =>
        new(db, p, Microsoft.Extensions.Logging.Abstractions.NullLogger<FirewallManager>.Instance);

    [Fact]
    public void OnlyBlockRulesAreLifted()
    {
        Assert.True(FirewallManager.IsBlockRule(FirewallManager.RuleName(@"c:\a.exe", true)));
        Assert.True(FirewallManager.IsBlockRule(FirewallManager.RuleName(@"c:\a.exe", false)));
        Assert.False(FirewallManager.IsBlockRule(FirewallManager.DiscoveryRule));
        Assert.False(FirewallManager.IsBlockRule("Some other app's rule"));
    }

    [Fact]
    public void BlockRefusedWhileBlockingIsOff()
    {
        using var db = Database.InMemory();
        var p = new Permissions(db);
        p.Initialize();
        var id = db.EnsureApp(new AppIdentity(@"c:\a.exe", "A", @"C:\a.exe", null, false), 0, out _);
        using var firewall = Firewall(db, p);
        var refused = Assert.Throws<InvalidOperationException>(() => firewall.SetBlocked(id, true));
        Assert.Equal(PermissionText.OffBody(Feature.Blocking), refused.Message);
        Assert.Empty(db.BlockedApps());
    }

    [Fact]
    public void ForgettingBlocksDropsWaitingOnesToo()
    {
        using var db = Database.InMemory();
        var p = new Permissions(db);
        p.Initialize();
        var id = db.EnsureApp(new AppIdentity(@"e:\game.exe", "Game", @"E:\game.exe", null, false), 0, out _);
        db.SetBlocked(id, true);
        using var firewall = Firewall(db, p);
        firewall.Wait(id, @"e:\game.exe", @"E:\game.exe", null, "Game");
        Assert.Equal(1, firewall.WaitingCount);

        firewall.ForgetBlocks();

        Assert.Equal(0, firewall.WaitingCount);
        Assert.False(firewall.Retrying);
        Assert.Empty(db.BlockedApps());
        Assert.Equal(0, firewall.BlockedCount);
    }
}

public class PermissionRequestTests
{
    [Fact]
    public void ApplySetsEachAndMarksReviewed()
    {
        using var db = Database.InMemory();
        var p = new Permissions(db);
        p.Initialize();
        p.Apply(new Dictionary<string, string> { ["allow_usage"] = "true", ["allow_devices"] = "true", ["permissions_reviewed"] = "true" });
        Assert.Equal(new PermissionSet(true, false, false, true, true), p.Current);
    }

    [Fact]
    public void ApplyRefusesUnknownKeysAndChangesNothing()
    {
        using var db = Database.InMemory();
        var p = new Permissions(db);
        p.Initialize();
        var refused = Assert.Throws<InvalidOperationException>(() =>
            p.Apply(new Dictionary<string, string> { ["allow_usage"] = "true", ["allow_everything"] = "true" }));
        Assert.Equal("Unknown permission: allow_everything", refused.Message);
        Assert.False(p.Current.Usage);
    }

    [Fact]
    public void ApplyRefusesValuesOtherThanTrueOrFalse()
    {
        using var db = Database.InMemory();
        var p = new Permissions(db);
        p.Initialize();
        Assert.Throws<InvalidOperationException>(() => p.Apply(new Dictionary<string, string> { ["allow_usage"] = "yes" }));
        Assert.False(p.Current.Usage);
    }
}
