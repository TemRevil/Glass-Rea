using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using GlassRea.Core.Protocol;
using GlassRea.Core.Usage;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace GlassRea.App.Support;

/// <summary>How an app is shown in the lists: its name, what kind of thing it is, and what its icon is drawn from.</summary>
/// <param name="Icon">What <see cref="AppIcons"/> draws the icon from; null when there's nothing to draw.</param>
/// <param name="Glyph">The symbol shown when there's no icon (Segoe Fluent Icons).</param>
/// <param name="Tag">The kind, as the small label beside the name; empty for an ordinary app.</param>
/// <param name="About">A line on what it is: the program file, who made the Store app, what the service does.</param>
public sealed record AppFace(string Name, AppKind Kind, string? Icon, string Glyph, string Tag, string About);

/// <summary>
/// Names and icons for what the service reports. The service knows a Store app by its package and a service by its
/// short name ("wuauserv"); this looks up what Windows calls them ("Windows Update"), who made the app and which
/// icon Start shows for it. Lookups are cached for as long as the app runs; <see cref="Prepare"/> does them off the
/// UI thread before a page builds its rows.
/// </summary>
public static partial class AppFaces
{
    public sealed record ServiceInfo(string Name, string DisplayName, string? Description, string? Program);
    public sealed record PackageInfo(Package Package, string DisplayName, string? Publisher, string? StartId);

    private static readonly ConcurrentDictionary<string, Lazy<ServiceInfo?>> Services = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<PackageInfo?>> Packages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each kind is, for the explanation beside the Apps list (in the order they're explained).</summary>
    public static readonly (AppKind Kind, string Title, string Text)[] Kinds =
    [
        (AppKind.App, "App", "A program you installed and run yourself, like a browser or a game. Shown with its program file."),
        (AppKind.StoreApp, "Store app", "An app from the Microsoft Store, or installed as an app package. Windows keeps each one in its own package, so all its versions are one app."),
        (AppKind.Service, "Service", "Works in the background for Windows or for a program, usually with no window: updates, sync, notifications. Several can share one Windows process."),
        (AppKind.Windows, "Windows", "Windows itself, for traffic that isn't any one program's, like keeping the connection working."),
        (AppKind.Tunnel, "VPN tunnel", "Your VPN's own connection. It carries every other app's traffic in encrypted form, so it isn't added to the total."),
        (AppKind.Unknown, "Unknown", "A program that closed before Glass-Rea could tell which one it was."),
    ];

    public static string Glyph(AppKind kind) => kind switch
    {
        AppKind.StoreApp => "",
        AppKind.Service => "",
        AppKind.Windows => "",
        AppKind.Tunnel => "",
        AppKind.Unknown => "",
        _ => "",
    };

    public static AppKind KindOf(AppUsage a) => AppKinds.Classify(a, ServiceOf);

    /// <summary>Rows with the same key are one app (see <see cref="AppKinds.GroupKey"/>).</summary>
    public static string Key(AppUsage a) => AppKinds.GroupKey(a, ServiceOf);

    /// <summary>The rows of one app added together: each version, its history rows and its live ones.</summary>
    public static List<MergedApp> Merge(IEnumerable<AppUsage> apps) => AppKinds.Merge(apps, Key);

    /// <summary>Looks up every service and package in <paramref name="apps"/>, off the UI thread.</summary>
    public static Task Prepare(IEnumerable<AppUsage> apps)
    {
        var list = apps.ToList();
        return Task.Run(() =>
        {
            foreach (var a in list)
                switch (KindOf(a))
                {
                    case AppKind.StoreApp: PackageOf(AppKinds.FamilyOf(AppKinds.PackageFullName(a.Name, a.Path)!)!); break;
                    case AppKind.Service: if (ServiceOf(a.Service ?? a.Name) is { } s) Info(s); break;
                    case AppKind.App or AppKind.Tunnel: ProgramTitle(a); Info(a.Name); break;
                }
        });
    }

    public static AppFace For(AppUsage a)
    {
        var kind = KindOf(a);
        var glyph = Glyph(kind);
        switch (kind)
        {
            case AppKind.StoreApp:
            {
                var family = AppKinds.FamilyOf(AppKinds.PackageFullName(a.Name, a.Path)!)!;
                var p = PackageOf(family);
                var name = p?.DisplayName ?? (string.IsNullOrEmpty(a.Path) ? FamilyTitle(family) : a.Name);
                var icon = p?.StartId is { } id ? AppIcons.StartPrefix + id : p is not null ? AppIcons.PackagePrefix + family : a.Path;
                var about = p?.Publisher is { Length: > 0 } by ? $"By {by}" : p is null ? "No longer installed" : "App package";
                return new AppFace(name, kind, icon, glyph, "Store app", about);
            }
            case AppKind.Service:
            {
                var s = Info(ServiceOf(a.Service ?? a.Name) ?? a.Service ?? a.Name);
                var name = s?.DisplayName ?? (a.Path is not null ? a.Name : a.Service ?? a.Name);
                var shortName = s?.Name ?? a.Service ?? a.Name;
                var about = s?.Description is { Length: > 0 } d ? $"{shortName}: {d}" : $"{shortName}: works in the background, with no window";
                return new AppFace(name, kind, s?.Program, glyph, "Service", about);
            }
            case AppKind.Windows:
                return new AppFace(a.Name == AppIdentity.System.Name ? "Windows (System)" : a.Name[(AppIdentity.System.Name.Length + 1)..], kind, null, glyph, "Windows",
                    "Windows itself: traffic that isn't any one program's");
            case AppKind.Unknown:
                return new AppFace("Unidentified programs", kind, null, glyph, "", "Programs that closed before Glass-Rea could tell which they were");
            case AppKind.Tunnel:
                return new AppFace(ProgramTitle(a), kind, Current(a.Path), glyph, "VPN tunnel",
                    "VPN tunnel: the encrypted copy of the other apps' traffic, so it isn't added to the total");
            default:
                return new AppFace(ProgramTitle(a), kind, Current(a.Path), glyph, "", a.Path ?? "Its program file wasn't recorded");
        }
    }

    private static readonly ConcurrentDictionary<string, string?> Titles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A program's name: what Windows recorded, unless that's only a file or service name ("warp-svc",
    /// "CloudflareWARP"), in which case the description in its file properties, or else the name of the Windows
    /// service it runs as ("Cloudflare WARP").
    /// </summary>
    private static string ProgramTitle(AppUsage a)
    {
        var name = Pages.AppRow.NameOf(a.Name, a.Path);
        if (a.Path is null) return name;
        var bare = string.Equals(name, Path.GetFileNameWithoutExtension(AppKinds.ProgramName(a.Path)), StringComparison.OrdinalIgnoreCase);
        if (!bare && Info(name) is null) return name;
        var file = Current(a.Path);
        return (file is null ? null : Titles.GetOrAdd(file, Describe)) ?? ServiceRunning(file ?? a.Path)?.DisplayName ?? Info(name)?.DisplayName ?? name;
    }

    private static readonly Lazy<Dictionary<string, string>> ServicePrograms = new(() =>
    {
        // Every service that runs a program of its own, by that program's path.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(ServicesKey[..^1]);
            foreach (var name in services?.GetSubKeyNames() ?? [])
            {
                using var key = services!.OpenSubKey(name);
                if (key?.GetValue("Type") is int type && (type & OwnProcess) != 0 && ProgramOf(key.GetValue("ImagePath") as string) is { } program)
                    map.TryAdd(program, name);
            }
        }
        catch { /* named by file then */ }
        return map;
    });

    /// <summary>The Windows service that runs this program, when one does.</summary>
    private static ServiceInfo? ServiceRunning(string program) =>
        ServicePrograms.Value.TryGetValue(program, out var name) ? Info(name) : null;

    private static string? Describe(string file)
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
            return new[] { v.FileDescription, v.ProductName }.Select(t => t?.Trim()).FirstOrDefault(t => !string.IsNullOrEmpty(t)
                && !string.Equals(t, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private static readonly ConcurrentDictionary<string, string?> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program file to draw the icon from: the recorded one, or the same program after it updated.</summary>
    private static string? Current(string? path) => path is null ? null : Files.GetOrAdd(path, AppKinds.CurrentFile);

    /// <summary>
    /// The Windows service a name belongs to, or null when it isn't one. A per-sign-in copy of a service
    /// ("CDPUserSvc_2dc4ba") belongs to the service it was made from ("CDPUserSvc"), which outlives it.
    /// </summary>
    public static string? ServiceOf(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains(',')) return null;
        var cut = name.LastIndexOf('_');
        if (cut > 0 && Info(name[..cut]) is { } template && IsUserServiceTemplate(template.Name)) return template.Name;
        return Info(name)?.Name;
    }

    public static ServiceInfo? Info(string name) =>
        Services.GetOrAdd(name, n => new Lazy<ServiceInfo?>(() => ReadService(n))).Value;

    public static PackageInfo? PackageOf(string family) =>
        Packages.GetOrAdd(family, f => new Lazy<PackageInfo?>(() => FindPackage(f))).Value;

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";
    private const int OwnProcess = 0x10, ShareProcess = 0x20, UserService = 0x40;

    private static bool IsUserServiceTemplate(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + name);
        return key?.GetValue("Type") is int type && (type & UserService) != 0;
    }

    private static ServiceInfo? ReadService(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + name);
            if (key?.GetValue("Type") is not int type || (type & (OwnProcess | ShareProcess)) == 0) return null;
            var display = Indirect(key.GetValue("DisplayName") as string);
            var description = Indirect(key.GetValue("Description") as string);
            // A service with a program of its own has that program's icon; one hosted by svchost has none of its own.
            var program = ProgramOf(key.GetValue("ImagePath") as string);
            if (program is not null && string.Equals(Path.GetFileName(program), "svchost.exe", StringComparison.OrdinalIgnoreCase)) program = null;
            return new ServiceInfo(Path.GetFileName(key.Name), string.IsNullOrWhiteSpace(display) ? name : display, description, program);
        }
        catch { return null; }
    }

    /// <summary>The program in a service's command line: quoted or not, with its arguments and %variables% dealt with.</summary>
    private static string? ProgramOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"')) { var end = c.IndexOf('"', 1); return end > 1 ? c[1..end] : null; }
        if (c.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) c = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + c[11..];
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : null;
    }

    /// <summary>Text Windows stores as a pointer into a file ("@%SystemRoot%\system32\wuaueng.dll,-105"), read out.</summary>
    private static unsafe string? Indirect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('@')) return text?.Trim();
        var buffer = stackalloc char[1024];
        return SHLoadIndirectString(text, buffer, 1024, 0) == 0 ? new string(buffer).Trim() : null;
    }

    private static PackageInfo? FindPackage(string family)
    {
        try
        {
            var package = new PackageManager().FindPackagesForUser("", family).MaxBy(p => (p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision));
            if (package is null) return null;
            var entry = package.GetAppListEntries().FirstOrDefault();
            var name = Clean(package.DisplayName) ?? Clean(entry?.DisplayInfo.DisplayName) ?? FamilyTitle(family);
            return new PackageInfo(package, name, Clean(package.PublisherDisplayName), entry?.AppUserModelId);
        }
        catch { return null; }
    }

    // A package with no resources of its own for its name reports the "ms-resource:" pointer itself.
    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) || text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : text.Trim();

    /// <summary>"MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy" gives "WebExperience", for a package that isn't installed.</summary>
    private static string FamilyTitle(string family)
    {
        var name = family[..family.LastIndexOf('_')];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(string source, char* buffer, uint size, nint reserved);
}
