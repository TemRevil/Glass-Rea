using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using GlassRea.Core.Paths;
using Microsoft.Win32.SafeHandles;

namespace GlassRea.Service;

/// <summary>
/// Who may use the service's pipe: Glass-Rea's own app, or an elevated administrator (who could act as SYSTEM anyway).
/// The app is told apart by where its program runs from, which only administrators can change: the Store copy's
/// package folder (and its package identity), or setup's install folder. Anything else is answered once and dropped.
/// </summary>
internal static unsafe partial class PipeCallers
{
    public const string AppExe = "GlassRea.exe";

    /// <summary>What the service can tell about the program on the other end of the pipe.</summary>
    /// <param name="Image">Its program file, or null if it couldn't be read (it exited, or it's protected).</param>
    /// <param name="PackageFamily">Its package's family name, when it runs with a package identity.</param>
    /// <param name="PackageFolder">That package's install folder.</param>
    /// <param name="ElevatedAdmin">It runs with the Administrators group enabled (elevated, not a filtered token).</param>
    public readonly record struct Caller(string? Image, string? PackageFamily, string? PackageFolder, bool ElevatedAdmin);

    /// <summary>
    /// Setup's copy of the app, beside this service's own folder (<c>App\GlassRea.exe</c> next to <c>Service\</c>), or
    /// null for the Store copy, whose app is matched by its package instead.
    /// </summary>
    public static string? ClassicApp() =>
        Packaging.IsPackaged ? null : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "App", AppExe));

    public static bool Allowed(Caller c, string? classicApp)
    {
        if (c.ElevatedAdmin) return true;
        if (c.Image is null) return false;
        if (c.PackageFamily is not null)
            return c.PackageFamily == Packaging.StoreFamily && c.PackageFolder is not null
                   && SamePath(c.Image, Path.Combine(c.PackageFolder, AppExe));
        return classicApp is not null && SamePath(c.Image, classicApp);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The program on the other end of <paramref name="pipe"/>; nothing known if Windows won't say.</summary>
    public static Caller Describe(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? Describe((int)pid) : default;

    public static Caller Describe(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0) return default;
        try
        {
            var image = Native.ProcessPath(pid);
            var (family, folder) = Package(process);
            return new Caller(image, family, folder, IsElevatedAdmin(process));
        }
        finally { CloseHandle(process); }
    }

    private static (string? Family, string? Folder) Package(nint process)
    {
        const int Size = 512;
        var buffer = stackalloc char[Size];
        var length = (uint)Size;
        if (GetPackageFamilyName(process, ref length, buffer) != 0) return (null, null); // 15700: no package
        var family = new string(buffer, 0, (int)length - 1);
        length = Size;
        if (GetPackageFullName(process, ref length, buffer) != 0) return (family, null);
        var full = new string(buffer, 0, (int)length - 1);
        var path = stackalloc char[Size];
        length = Size;
        // Where it's staged, which is the same for everyone: the service runs as SYSTEM, and GetPackagePathByFullName
        // finds only packages installed for the calling user, so it never found the app's folder and the Store app
        // was turned away whenever it ran without administrator rights.
        return GetStagedPackagePathByFullName(full, ref length, path) == 0 ? (family, new string(path, 0, (int)length - 1)) : (family, null);
    }

    private static bool IsElevatedAdmin(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery | TokenDuplicate, out var token)) return false;
        try
        {
            using var identity = new WindowsIdentity(token);
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or ArgumentException or SecurityException) { return false; }
        finally { CloseHandle(token); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x8, TokenDuplicate = 0x2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("kernel32.dll")]
    private static partial int GetPackageFamilyName(nint process, ref uint length, char* name);

    [LibraryImport("kernel32.dll")]
    private static partial int GetPackageFullName(nint process, ref uint length, char* name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetStagedPackagePathByFullName(string fullName, ref uint length, char* path);
}
