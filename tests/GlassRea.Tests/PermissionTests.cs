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
