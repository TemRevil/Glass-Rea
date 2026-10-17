using GlassRea.Core.Protocol;
using GlassRea.Core.Usage;

namespace GlassRea.Tests;

public class AppKindsTests
{
    private static AppUsage App(string name, string? path = null, string? service = null, bool tunnel = false, long id = 1, long netIn = 10, long first = 100) =>
        new(id, name, path, service, tunnel, false, netIn, 1, 0, 0, first);

    private static readonly Func<string, string?> Services = n => n switch
    {
        "wuauserv" or "Dnscache" => n,
        "CDPUserSvc_2dc4ba" or "CDPUserSvc_77edb" => "CDPUserSvc",
        _ => null,
    };

    [Fact]
    public void EachKindIsToldApart()
    {
        Assert.Equal(AppKind.App, AppKinds.Classify(App("Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"), Services));
        Assert.Equal(AppKind.StoreApp, AppKinds.Classify(App("Spotify", @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.305.3058.0_x64__zpdnekdrzrea0\Spotify.exe"), Services));
        Assert.Equal(AppKind.StoreApp, AppKinds.Classify(App("Microsoft.LockApp_10.0.26100.9549_neutral__cw5n1h2txyewy"), Services));
        Assert.Equal(AppKind.Service, AppKinds.Classify(App("Windows Update", @"C:\Windows\System32\svchost.exe", "wuauserv"), Services));
        Assert.Equal(AppKind.Service, AppKinds.Classify(App("wuauserv"), Services));
        Assert.Equal(AppKind.App, AppKinds.Classify(App("SomeOldTool"), Services)); // a bare name that isn't a service
        Assert.Equal(AppKind.Windows, AppKinds.Classify(App("System"), Services));
        Assert.Equal(AppKind.Windows, AppKinds.Classify(App(@"System\IPv6 Control Message"), Services));
        Assert.Equal(AppKind.Unknown, AppKinds.Classify(App(AppIdentity.Unknown.Name), Services));
        Assert.Equal(AppKind.Unknown, AppKinds.Classify(App(""), Services));
        Assert.Equal(AppKind.Tunnel, AppKinds.Classify(App("CloudflareWARP", @"C:\Program Files\Cloudflare\Cloudflare WARP\warp-svc.exe", "CloudflareWARP", tunnel: true), Services));
    }

    [Fact]
    public void PackagesAreKnownByFamilyWhateverTheVersion()
    {
        Assert.Equal("Claude_pzs8sxrjxfjjc", AppKinds.FamilyOf("Claude_2.42304.0.0_x64__pzs8sxrjxfjjc"));
        Assert.Equal("Microsoft.LockApp_cw5n1h2txyewy", AppKinds.FamilyOf("Microsoft.LockApp_10.0.26100.9549_neutral__cw5n1h2txyewy"));
        Assert.Null(AppKinds.FamilyOf("Claude"));
        Assert.Null(AppKinds.PackageFullName("claude", @"C:\Users\me\AppData\Local\Packages\claude_pzs8sxrjxfjjc\LocalCache\claude.exe"));
        Assert.Equal("Claude_2.42304.0.0_x64__pzs8sxrjxfjjc",
            AppKinds.PackageFullName("Claude", @"C:\Program Files\WindowsApps\Claude_2.42304.0.0_x64__pzs8sxrjxfjjc\app\claude.exe"));
    }

    [Fact]
    public void VersionFoldersAndRenamedOldCopiesDontMakeANewApp()
    {
        var a = AppKinds.VersionlessPath(@"C:\Program Files\Microsoft OneDrive\26.168.0830.0006\onedrive.sync.service.exe");
        Assert.Equal(a, AppKinds.VersionlessPath(@"C:\program files\microsoft onedrive\26.173.0906.0008\onedrive.sync.service.exe"));
        Assert.Equal(AppKinds.VersionlessPath(@"C:\Users\me\AppData\Local\OpenAI\Codex\bin\9614f1d9b40d9c4f\codex.exe"),
            AppKinds.VersionlessPath(@"C:\users\me\appdata\local\openai\codex\bin\faa963e871dd422c\codex.exe"));
        Assert.Equal(AppKinds.VersionlessPath(@"C:\Users\me\.local\bin\claude.exe"),
            AppKinds.VersionlessPath(@"C:\Users\me\.local\bin\claude.exe.old.1792014796539.20808"));
        // Different programs stay apart.
        Assert.NotEqual(AppKinds.VersionlessPath(@"C:\Program Files (x86)\Google\GoogleUpdater\152.0.7933.0\updater.exe"),
            AppKinds.VersionlessPath(@"C:\Windows\SystemTemp\google15720_732042239\bin\updater.exe"));
        Assert.NotEqual(AppKinds.VersionlessPath(@"C:\Tools\a\run.exe"), AppKinds.VersionlessPath(@"C:\Tools\b\run.exe"));
    }

    [Fact]
    public void OneAppsRowsAreAddedTogetherUnderItsLiveRow()
    {
        var rows = new[]
        {
            App("Claude_2.9939.2.0_x64__pzs8sxrjxfjjc", id: 65, netIn: 500, first: 50),
            App("Claude", @"C:\Program Files\WindowsApps\Claude_2.42304.0.0_x64__pzs8sxrjxfjjc\app\claude.exe", id: 12, netIn: 100, first: 300),
            App("Claude_2.19675.0.0_x64__pzs8sxrjxfjjc", id: 155, netIn: 200, first: 90),
            App("wuauserv", id: 15, netIn: 40),
            App("Windows Update", @"C:\Windows\System32\svchost.exe", "wuauserv", id: 214, netIn: 60),
            App("Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", id: 3, netIn: 70),
            App("CDPUserSvc_2dc4ba", id: 52, netIn: 5),
            App("CDPUserSvc_77edb", @"C:\Windows\System32\svchost.exe", "CDPUserSvc_77edb", id: 249, netIn: 6),
        };
        var groups = AppKinds.Merge(rows, a => AppKinds.GroupKey(a, Services));
        var merged = groups.ToDictionary(g => g.App.AppId, g => g.App);
        Assert.Equal([12L, 65, 155], groups.Single(g => g.App.AppId == 12).Ids); // the live row first, then by size
        Assert.Equal([3L, 12, 214, 249], merged.Keys.Order());
        Assert.Equal(11L, merged[249].NetIn); // one service, whichever sign-in it ran for
        Assert.Equal((800L, 3L, 50L), (merged[12].NetIn, merged[12].NetOut, merged[12].FirstSeen));
        Assert.Equal("Claude", merged[12].Name); // shown as the live row, whose id the other pages use
        Assert.Equal((100L, "wuauserv"), (merged[214].NetIn, merged[214].Service));
        Assert.Equal(70L, merged[3].NetIn);
    }

    [Fact]
    public void AnUpdatedProgramIsFoundInItsNewestVersionFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "glassrea-current-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Make(string relative, DateTime written)
            {
                var file = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "");
                Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(file)!, written);
                return file;
            }
            var older = Make(@"codex\bin\aaaaaaaaaaaaaaaa\codex.exe", new DateTime(2026, 9, 1));
            var newest = Make(@"codex\bin\bbbbbbbbbbbbbbbb\codex.exe", new DateTime(2026, 10, 1));
            Make(@"codex\bin\notaversion\codex.exe", new DateTime(2026, 11, 1));
            var current = Make(@"local\claude.exe", DateTime.UtcNow);

            Assert.Equal(older, AppKinds.CurrentFile(older)); // still there: itself
            Assert.Equal(newest, AppKinds.CurrentFile(Path.Combine(root, @"codex\bin\9614f1d9b40d9c4f\codex.exe")));
            Assert.Equal(current, AppKinds.CurrentFile(Path.Combine(root, @"local\claude.exe.old.1792014796539.20808")));
            Assert.Null(AppKinds.CurrentFile(Path.Combine(root, @"codex\bin\9614f1d9b40d9c4f\other.exe")));
            Assert.Null(AppKinds.CurrentFile(Path.Combine(root, @"gone\1.2.3\app.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
