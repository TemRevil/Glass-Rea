using System.Runtime.InteropServices;

namespace GlassRea.Core.Paths;

/// <summary>
/// Which copy of Glass-Rea this is: the one setup installs (Program Files, service "GlassRea"), or the Microsoft Store
/// package (service "GlassReaStore"). The two can be on one PC for a while, while the person moves from one to the other.
/// </summary>
public static partial class Packaging
{
    public const string ClassicService = "GlassRea";
    public const string StoreService = "GlassReaStore";

    /// <summary>Runs from the Microsoft Store package (with its package identity).</summary>
    public static bool IsPackaged { get; } = HasPackage();

    /// <summary>The Windows service this copy's recording service runs as.</summary>
    public static string ServiceName => IsPackaged ? StoreService : ClassicService;

    private static unsafe bool HasPackage()
    {
        const int InsufficientBuffer = 122; // its full name doesn't fit in no room: there is a package (15700 means none)
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) == InsufficientBuffer;
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFullName(ref uint length, char* name);
}
