using System.Text.Json;

namespace GlassRea.Core.Updates;

/// <param name="SetupSha256">The setup's SHA-256 as GitHub recorded it on upload (lowercase hex), when GitHub gives one.</param>
public sealed record Release(Version Version, string Tag, string Notes, DateTimeOffset Published, string PageUrl,
    string SetupUrl, long SetupSize, string? SetupSha256);

/// <summary>
/// Glass-Rea's releases on GitHub. Each one carries the signed setup; running a newer setup updates in place.
/// </summary>
public static class Releases
{
    public const string Repository = "TemRevil/Glass-Rea";
    /// <summary>The product name in Glass-Rea's file properties (Directory.Build.props), which the signature covers.</summary>
    public const string Product = "Glass-Rea";
    public const string LatestUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>
    /// The latest release's own description, attached to it as "release.json" by the release scripts
    /// (scripts/write-manifest.ps1): the same fields as <see cref="LatestUrl"/> gives, served as an ordinary download.
    /// GitHub allows only 60 API requests an hour from one address without signing in, and VPNs such as Cloudflare
    /// WARP share one address among many people, so the API is asked only when a release has no release.json.
    /// </summary>
    public const string ManifestUrl = $"https://github.com/{Repository}/releases/latest/download/release.json";

    /// <summary>
    /// Finds the latest release: from its release.json, or from the API when there isn't one. <paramref name="get"/>
    /// fetches a URL and gives the HTTP status and body. Status is 200 with the release (null when it isn't one
    /// Glass-Rea can install), 404 when nothing is released, or whatever else the API answered.
    /// </summary>
    public static async Task<(int Status, Release? Release)> FindLatest(
        Func<string, CancellationToken, Task<(int Status, string Body)>> get, CancellationToken ct)
    {
        var manifest = await get(ManifestUrl, ct);
        if (manifest.Status == 200 && TryParse(manifest.Body) is { } fromManifest) return (200, fromManifest);
        var api = await get(LatestUrl, ct);
        return api.Status == 200 ? (200, TryParse(api.Body)) : (api.Status, null);
    }

    private static Release? TryParse(string json)
    {
        try { return Parse(json); }
        catch (JsonException) { return null; }
    }
    public const string PageUrl = $"https://github.com/{Repository}/releases";

    /// <summary>
    /// Tem Revil's own certificate (CN=Tem Revil), which signed releases up to 1.1.1 and still signs builds made on
    /// the owner's PC. It's its own root, so Windows can't vouch for it; it's accepted by its exact thumbprint.
    /// </summary>
    public const string SignerThumbprint = "E6A4B1317EA62EDEAC18FCA3214798EFBF8D068E";

    /// <summary>
    /// Who signs releases built by GitHub Actions: SignPath Foundation, which signs open-source projects for free
    /// (https://signpath.org). Its certificate is issued by a certificate authority Windows trusts, and is renewed
    /// from time to time, so it's accepted by name with a chain Windows fully trusts, not by thumbprint.
    /// </summary>
    public const string SignPathFoundation = "SignPath Foundation";

    /// <summary>WinVerifyTrust's answer for a signature that's intact but whose root Windows doesn't trust.</summary>
    public const int UntrustedRoot = unchecked((int)0x800B0109);

    /// <summary>
    /// Whether a setup with this signature may be installed as an update. <paramref name="trust"/> is what
    /// WinVerifyTrust said (0 is fully trusted); the next three describe the signer's certificate, and
    /// <paramref name="product"/> is the file's own product name. SignPath Foundation signs many open-source
    /// projects, so its signature only counts on a file that calls itself Glass-Rea (SignPath signs Glass-Rea's
    /// files only with that name, and the signature covers it). The file must also be byte for byte what GitHub
    /// recorded for the release, which is checked first and matters most: the signature only says who built it.
    /// </summary>
    public static bool AcceptsSigner(int trust, string? thumbprint, string? commonName, string? organization, string? product) =>
        trust == 0 && commonName == SignPathFoundation && organization == SignPathFoundation && product == Product
        || (trust is 0 or UntrustedRoot) && string.Equals(thumbprint, SignerThumbprint, StringComparison.OrdinalIgnoreCase);

    private const string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";

    /// <summary>
    /// Reads GitHub's answer for the latest release. Null when it isn't one Glass-Rea can install: a draft or
    /// pre-release, a tag that isn't a version, or no setup attached.
    /// </summary>
    public static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name");
        if (tag is null || VersionOf(tag) is not { } version) return null;
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = Str(a, "name") ?? "";
            var url = Str(a, "browser_download_url") ?? "";
            // GitHub turns the spaces in "Glass-Rea Setup.exe" into dots.
            if (!name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase) || !IsOwnDownload(url)) continue;
            if (!a.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes) || bytes <= 0) continue;
            var digest = Str(a, "digest");
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 7 + 64
                ? digest[7..].ToLowerInvariant() : null;
            var published = DateTimeOffset.TryParse(Str(r, "published_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var p) ? p : DateTimeOffset.MinValue;
            return new Release(version, tag, (Str(r, "body") ?? "").Trim(), published, Str(r, "html_url") ?? PageUrl, url, bytes, sha);
        }
        return null;
    }

    /// <summary>"v1.2.0" or "1.2" as a version with three parts; null for anything else.</summary>
    public static Version? VersionOf(string tag)
    {
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out var v) || v.Revision > 0) return null;
        return Plain(v);
    }

    public static bool IsNewer(Release release, Version current) => release.Version > Plain(current);

    /// <summary>Only a setup attached to one of Glass-Rea's own releases is ever downloaded.</summary>
    public static bool IsOwnDownload(string url) =>
        url.StartsWith(DownloadPrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Query.Length == 0;

    /// <summary>The release notes as plain lines: Markdown list marks, headings and bold dropped.</summary>
    public static string PlainNotes(string notes) => string.Join("\n", notes.Replace("\r", "").Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0)
        .Select(l => l.TrimStart('#', ' ').Replace("**", "").Replace("`", ""))
        .Select(l => l.StartsWith("- ") || l.StartsWith("* ") ? "• " + l[2..] : l));

    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
