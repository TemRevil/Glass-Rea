using GlassRea.Core.Alerts;
using GlassRea.Core.Protocol;

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
            Assert.DoesNotContain('—', s);
            Assert.DoesNotContain('–', s);
            Assert.Matches("^[A-Z]", s);
        }
        Assert.Equal(PermissionText.Title(f) + " is off", PermissionText.OffTitle(f));
    }
}
