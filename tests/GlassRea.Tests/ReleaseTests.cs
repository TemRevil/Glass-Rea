using GlassRea.Core.Updates;

namespace GlassRea.Tests;

public class ReleaseTests
{
    private const string Sha = "3f4a9c2b7d1e8f6a5b4c3d2e1f0a9b8c7d6e5f4a3b2c1d0e9f8a7b6c5d4e3f2a";

    private static string Json(string tag = "v1.2.0", bool draft = false, bool prerelease = false, string asset = "Glass-Rea.Setup.exe",
        string url = "https://github.com/TemRevil/Glass-Rea/releases/download/v1.2.0/Glass-Rea.Setup.exe", long size = 158_000_000,
        string digest = "sha256:" + Sha) => $$"""
        {
          "html_url": "https://github.com/TemRevil/Glass-Rea/releases/tag/{{tag}}",
          "tag_name": "{{tag}}", "name": "Glass-Rea 1.2.0", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "published_at": "2026-11-02T09:30:00Z",
          "body": "﻿## What's new\r\n\r\n- **Faster** scans\r\n- Fixed `x`",
          "assets": [
            { "name": "notes.txt", "size": 10, "browser_download_url": "https://github.com/TemRevil/Glass-Rea/releases/download/v1.2.0/notes.txt" },
            { "name": "{{asset}}", "size": {{size}}, "digest": "{{digest}}", "browser_download_url": "{{url}}" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheSetupFromALatestRelease()
    {
        var r = Releases.Parse(Json())!;
        Assert.Equal(new Version(1, 2, 0), r.Version);
        Assert.Equal("https://github.com/TemRevil/Glass-Rea/releases/download/v1.2.0/Glass-Rea.Setup.exe", r.SetupUrl);
        Assert.Equal((158_000_000L, Sha), (r.SetupSize, r.SetupSha256));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero), r.Published);
        Assert.Equal("What's new\n• Faster scans\n• Fixed x", Releases.PlainNotes(r.Notes));
    }

    [Fact]
    public void RefusesWhatCantBeInstalled()
    {
        Assert.Null(Releases.Parse(Json(draft: true)));
        Assert.Null(Releases.Parse(Json(prerelease: true)));
        Assert.Null(Releases.Parse(Json(tag: "nightly")));
        Assert.Null(Releases.Parse(Json(asset: "Glass-Rea.zip")));
        Assert.Null(Releases.Parse(Json(size: 0)));
        Assert.Null(Releases.Parse(Json(url: "https://example.com/TemRevil/Glass-Rea/releases/download/v1.2.0/Glass-Rea.Setup.exe")));
        Assert.Null(Releases.Parse(Json(url: "https://github.com/Someone/Glass-Rea/releases/download/v1.2.0/Glass-Rea.Setup.exe")));
        Assert.Null(Releases.Parse(Json(url: "http://github.com/TemRevil/Glass-Rea/releases/download/v1.2.0/Glass-Rea.Setup.exe")));
        Assert.Null(Releases.Parse(Json(url: "https://github.com/TemRevil/Glass-Rea/releases/download/v1.2.0/Glass-Rea.Setup.exe?x=1")));
        Assert.Null(Releases.Parse("[]"));
        Assert.Null(Releases.Parse(Json(digest: "md5:abc"))?.SetupSha256);
    }

    [Fact]
    public void ComparesVersionsByTheirThreeParts()
    {
        var r = Releases.Parse(Json())!;
        Assert.True(Releases.IsNewer(r, new Version(1, 1, 0, 0)));
        Assert.False(Releases.IsNewer(r, new Version(1, 2, 0, 0)));
        Assert.False(Releases.IsNewer(r, new Version(1, 10, 0)));
        Assert.False(Releases.IsNewer(r, new Version(1, 2)));           // 1.2 is 1.2.0
        Assert.Equal(new Version(1, 2, 0), Releases.VersionOf("1.2"));
        Assert.Equal(new Version(2, 0, 1), Releases.VersionOf("V2.0.1"));
        Assert.Null(Releases.VersionOf("v1.2.0.5"));
    }

    /// <summary>A fake GitHub: answers by URL and records what was asked.</summary>
    private static (Func<string, CancellationToken, Task<(int, string)>> Get, List<string> Asked) GitHub(
        (int, string) manifest, (int, string) api)
    {
        var asked = new List<string>();
        return ((url, _) =>
        {
            asked.Add(url);
            return Task.FromResult(url == Releases.ManifestUrl ? manifest : url == Releases.LatestUrl ? api : (404, ""));
        }, asked);
    }

    [Fact]
    public async Task TheReleasesOwnDescriptionIsReadWithoutAskingTheApi()
    {
        var (get, asked) = GitHub((200, Json()), (403, ""));
        var (status, release) = await Releases.FindLatest(get, default);
        Assert.Equal((200, new Version(1, 2, 0)), (status, release?.Version));
        Assert.Equal([Releases.ManifestUrl], asked);
    }

    [Fact]
    public async Task TheApiIsAskedOnlyWhenTheReleaseHasNoUsableDescription()
    {
        foreach (var manifest in new[] { (404, ""), (200, "<html>not json</html>"), (200, Json(asset: "Glass-Rea.zip")) })
        {
            var (get, asked) = GitHub(manifest, (200, Json(tag: "v1.3.0")));
            var (status, release) = await Releases.FindLatest(get, default);
            Assert.Equal((200, new Version(1, 3, 0)), (status, release?.Version));
            Assert.Equal([Releases.ManifestUrl, Releases.LatestUrl], asked);
        }
        Assert.Equal((403, null), await Releases.FindLatest(GitHub((404, ""), (403, "")).Get, default));
        Assert.Equal((404, null), await Releases.FindLatest(GitHub((404, ""), (404, "")).Get, default));
    }

    private const string TemRevil = Releases.SignerThumbprint;
    private const string SignPath = Releases.SignPathFoundation;
    private const string Glass = Releases.Product;

    [Fact]
    public void SignPathFoundationIsAcceptedOnlyWithAChainWindowsTrusts()
    {
        Assert.True(Releases.AcceptsSigner(0, "0123456789ABCDEF0123456789ABCDEF01234567", SignPath, SignPath, Glass));
        Assert.False(Releases.AcceptsSigner(Releases.UntrustedRoot, "0123456789ABCDEF0123456789ABCDEF01234567", SignPath, SignPath, Glass));
        Assert.False(Releases.AcceptsSigner(unchecked((int)0x80096010), null, SignPath, SignPath, Glass)); // tampered: bad digest
        Assert.False(Releases.AcceptsSigner(0, null, SignPath, "Someone Else", Glass));                   // only the name, not the organisation
        Assert.False(Releases.AcceptsSigner(0, null, "Someone Else", SignPath, Glass));
        // SignPath Foundation signs many projects: another project's file isn't Glass-Rea's setup.
        Assert.False(Releases.AcceptsSigner(0, null, SignPath, SignPath, "Another App"));
        Assert.False(Releases.AcceptsSigner(0, null, SignPath, SignPath, null));
        Assert.False(Releases.AcceptsSigner(0, null, SignPath, SignPath, "glass-rea"));
    }

    [Fact]
    public void TemRevilsOwnCertificateIsAcceptedByThumbprint()
    {
        Assert.True(Releases.AcceptsSigner(Releases.UntrustedRoot, TemRevil, "Tem Revil", null, Glass));
        Assert.True(Releases.AcceptsSigner(0, TemRevil.ToLowerInvariant(), "Tem Revil", null, Glass));
        Assert.False(Releases.AcceptsSigner(Releases.UntrustedRoot, "0123456789ABCDEF0123456789ABCDEF01234567", "Tem Revil", null, Glass));
        Assert.False(Releases.AcceptsSigner(unchecked((int)0x80096010), TemRevil, "Tem Revil", null, Glass));
        Assert.False(Releases.AcceptsSigner(0, null, null, null, Glass)); // unsigned
    }
}
