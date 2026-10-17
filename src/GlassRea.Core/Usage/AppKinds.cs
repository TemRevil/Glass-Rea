using System.Text.RegularExpressions;
using GlassRea.Core.Protocol;

namespace GlassRea.Core.Usage;

/// <summary>One app's rows added together (see <see cref="AppKinds.Merge"/>): the figures, and the id of every row in it.</summary>
public sealed record MergedApp(AppUsage App, IReadOnlyList<long> Ids);

/// <summary>What sort of thing used the network, as the app lists label it.</summary>
public enum AppKind
{
    /// <summary>A program you install and run yourself.</summary>
    App,
    /// <summary>An app from the Microsoft Store (or any MSIX package), which lives in its own package.</summary>
    StoreApp,
    /// <summary>A Windows service: works in the background for Windows or a program, usually with no window.</summary>
    Service,
    /// <summary>Windows itself: traffic of the system, not of any one program.</summary>
    Windows,
    /// <summary>A VPN's own connection, which carries the other apps' traffic.</summary>
    Tunnel,
    /// <summary>A program that ended before it could be identified.</summary>
    Unknown,
}

/// <summary>
/// Sorts the apps the service reports into kinds, and gathers the rows that are really one app. Windows' usage
/// history names Store apps by full package name (one row per version: "Claude_2.9939.2.0_x64__pzs8sxrjxfjjc")
/// and services by service name ("wuauserv"), while live capture names them by program file, and programs that
/// update into a new folder each time ("...\codex\bin\9614f1d9b40d9c4f\codex.exe") would otherwise appear once
/// per version.
/// </summary>
public static partial class AppKinds
{
    /// <param name="serviceOf">The Windows service a name belongs to, or null when it isn't one (the registry knows; the
    /// caller asks it). A per-user service instance ("CDPUserSvc_2dc4ba") belongs to its service ("CDPUserSvc").</param>
    public static AppKind Classify(AppUsage a, Func<string, string?> serviceOf)
    {
        if (a.IsTunnel) return AppKind.Tunnel;
        if (a.Name == AppIdentity.System.Name || a.Name.StartsWith(AppIdentity.System.Name + "\\", StringComparison.Ordinal)) return AppKind.Windows;
        if (a.Name == AppIdentity.Unknown.Name || (string.IsNullOrEmpty(a.Name) && string.IsNullOrEmpty(a.Path))) return AppKind.Unknown;
        if (a.Service is not null) return AppKind.Service;
        if (PackageFullName(a.Name, a.Path) is not null) return AppKind.StoreApp;
        if (string.IsNullOrEmpty(a.Path)) return serviceOf(a.Name) is not null ? AppKind.Service : AppKind.App;
        return AppKind.App;
    }

    /// <summary>Rows with the same key are one app: the same package, service, VPN program, or program in any version's folder.</summary>
    public static string GroupKey(AppUsage a, Func<string, string?> serviceOf) => Classify(a, serviceOf) switch
    {
        AppKind.Tunnel => "tunnel:" + (a.Path is { Length: > 0 } p ? ProgramName(p) : a.Name).ToLowerInvariant(),
        AppKind.Windows => "windows:" + a.Name.ToLowerInvariant(),
        AppKind.Unknown => "unknown",
        AppKind.Service => "service:" + (serviceOf(a.Service ?? a.Name) ?? a.Service ?? a.Name).ToLowerInvariant(),
        AppKind.StoreApp => "package:" + FamilyOf(PackageFullName(a.Name, a.Path)!)!.ToLowerInvariant(),
        _ => a.Path is { Length: > 0 } path ? "path:" + VersionlessPath(path) : "name:" + a.Name.ToLowerInvariant(),
    };

    /// <summary>
    /// The full package name a row belongs to: its name when that is one (from Windows' history), or the package
    /// folder its program runs from (<c>C:\Program Files\WindowsApps\&lt;full name&gt;\...</c>).
    /// </summary>
    public static string? PackageFullName(string? name, string? path)
    {
        if (name is not null && FullName().IsMatch(name)) return name;
        if (path is null) return null;
        var at = path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = path[(at + @"\WindowsApps\".Length)..];
        var end = rest.IndexOf('\\');
        var folder = end < 0 ? rest : rest[..end];
        return FullName().IsMatch(folder) ? folder : null;
    }

    /// <summary>"Claude_2.42304.0.0_x64__pzs8sxrjxfjjc" gives "Claude_pzs8sxrjxfjjc", the name every version shares.</summary>
    public static string? FamilyOf(string fullName)
    {
        var m = FullName().Match(fullName);
        return m.Success ? $"{m.Groups["name"].Value}_{m.Groups["publisher"].Value}" : null;
    }

    /// <summary>
    /// A program's path with its version folders taken out, lower-case: "...\onedrive\26.168.0830.0006\x.exe" and
    /// "...\onedrive\26.173.0906.0008\x.exe" give the same, as do the hash-named folders some programs update into.
    /// A renamed old copy ("claude.exe.old.1792014796539.20808") counts as the program it was.
    /// </summary>
    public static string VersionlessPath(string path)
    {
        var parts = path.ToLowerInvariant().Split('\\');
        for (var i = 0; i < parts.Length - 1; i++)
            if (VersionFolder().IsMatch(parts[i])) parts[i] = "*";
        parts[^1] = ProgramName(parts[^1]);
        return string.Join('\\', parts);
    }

    /// <summary>
    /// The program as it is now, for a path recorded before it updated: the file itself while it's there, else the
    /// same program in the newest version folder beside the recorded one, or under its name before a self-updater
    /// renamed it ("claude.exe.old.123" is "claude.exe"). Null when there's no such program any more.
    /// </summary>
    public static string? CurrentFile(string path)
    {
        if (File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        var name = ProgramName(path);
        if (dir is null) return null;
        var parts = Path.Combine(dir, name).Split('\\');
        return Find(parts[0] + "\\", 1);

        string? Find(string at, int i)
        {
            if (i == parts.Length - 1) { var file = Path.Combine(at, parts[i]); return File.Exists(file) ? file : null; }
            if (!VersionFolder().IsMatch(parts[i])) return Directory.Exists(Path.Combine(at, parts[i])) ? Find(Path.Combine(at, parts[i]), i + 1) : null;
            try
            {
                return new DirectoryInfo(at).EnumerateDirectories().Where(d => VersionFolder().IsMatch(d.Name))
                    .OrderByDescending(d => d.LastWriteTimeUtc).Select(d => Find(d.FullName, i + 1)).FirstOrDefault(f => f is not null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>The program file's name, with any ".old.123" a self-updater added after ".exe" taken off.</summary>
    public static string ProgramName(string path)
    {
        var name = Path.GetFileName(path);
        var exe = name.IndexOf(".exe.", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? name[..(exe + 4)] : name;
    }

    /// <summary>
    /// One row per app: rows with the same key are added together and shown as the busiest one that still has a
    /// program file (its id is the one other pages open and block). <see cref="MergedApp.Ids"/> keeps every row's
    /// id, lead first, for looking up what each of them talked to.
    /// </summary>
    public static List<MergedApp> Merge(IEnumerable<AppUsage> apps, Func<AppUsage, string> key) =>
        apps.GroupBy(key).Select(g =>
        {
            var rows = g.OrderByDescending(a => !string.IsNullOrEmpty(a.Path)).ThenByDescending(a => a.NetIn + a.NetOut + a.LanIn + a.LanOut).ThenByDescending(a => a.AppId).ToList();
            var lead = rows[0];
            return new MergedApp(rows.Count == 1 ? lead : lead with
            {
                NetIn = rows.Sum(a => a.NetIn),
                NetOut = rows.Sum(a => a.NetOut),
                LanIn = rows.Sum(a => a.LanIn),
                LanOut = rows.Sum(a => a.LanOut),
                FirstSeen = rows.Min(a => a.FirstSeen),
            }, rows.Select(a => a.AppId).ToList());
        }).ToList();

    // Name_Version_Architecture_ResourceId_PublisherId (the resource id is usually empty).
    [GeneratedRegex(@"^(?<name>[A-Za-z0-9][A-Za-z0-9.\-]{1,49})_(?<version>\d+\.\d+\.\d+\.\d+)_(?<arch>x86|x64|arm|arm64|neutral)_(?<resource>[A-Za-z0-9.\-~]*)_(?<publisher>[0-9a-hjkmnp-tv-z]{13})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullName();

    // A version ("155.0.4283.45", "26.168.0830.0006", "2.1.281") or a long hex hash ("9614f1d9b40d9c4f").
    [GeneratedRegex(@"^(\d+(\.\d+){1,3}|[0-9a-f]{12,64})$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolder();
}
