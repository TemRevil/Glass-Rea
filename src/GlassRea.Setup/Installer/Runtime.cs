using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace GlassRea.Setup.Installer;

/// <summary>
/// The app and service run on the shared .NET 10 runtime (Microsoft Update keeps it patched). When it's missing,
/// Microsoft's own runtime installer is downloaded, checked to be signed by Microsoft, and run silently.
/// </summary>
public static partial class Runtime
{
    private const string DownloadUrl = "https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe";

    public static bool IsInstalled()
    {
        var shared = Path.Combine(Paths.DotnetDir, "shared", "Microsoft.NETCore.App");
        return File.Exists(Paths.DotnetExe) && Directory.Exists(shared) &&
               Directory.EnumerateDirectories(shared, "10.*").Any();
    }

    public static async Task InstallAsync(IProgress<string> status, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"dotnet-runtime-10-{Guid.NewGuid():N}.exe");
        try
        {
            status.Report("Downloading .NET 10 from Microsoft");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var from = await response.Content.ReadAsStreamAsync(ct);
                await using var to = File.Create(file);
                var buffer = new byte[81920];
                long done = 0, lastReport = 0;
                int n;
                while ((n = await from.ReadAsync(buffer, ct)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0 && done - lastReport > 512 * 1024)
                    {
                        lastReport = done;
                        status.Report($"Downloading .NET 10 from Microsoft, {done / 1048576} of {total / 1048576} MB");
                    }
                }
            }

            status.Report("Checking the download is signed by Microsoft");
            if (!IsSignedByMicrosoft(file)) throw new SetupException("The .NET download isn't signed by Microsoft, so it wasn't run.");

            status.Report("Installing .NET 10");
            using var p = Process.Start(new ProcessStartInfo(file, "/install /quiet /norestart") { UseShellExecute = false })!;
            await p.WaitForExitAsync(ct);
            Log.Write($".NET runtime installer exit code {p.ExitCode}");
            if (p.ExitCode is not (0 or 3010 or 1641)) throw new SetupException($".NET 10 didn't install (code {p.ExitCode}).");
            if (!IsInstalled()) throw new SetupException(".NET 10 reported success but isn't in Program Files.");
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp folder, cleaned by Windows */ }
        }
    }

    /// <summary>A valid Authenticode signature (WinVerifyTrust) whose signer is Microsoft Corporation.</summary>
    public static bool IsSignedByMicrosoft(string file)
    {
        if (!VerifyTrust(file)) return false;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static unsafe bool VerifyTrust(string file)
    {
        fixed (char* path = file)
        {
            var info = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = path };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = 2,           // WTD_UI_NONE
                fdwRevocationChecks = 0,  // WTD_REVOKE_NONE
                dwUnionChoice = 1,        // WTD_CHOICE_FILE
                pFile = &info,
                dwStateAction = 0,
                dwProvFlags = 0x80,       // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(-1, &action, &data) == 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public nint hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public nint hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static unsafe partial int WinVerifyTrust(nint hwnd, Guid* action, WINTRUST_DATA* data);
}

/// <summary>A failure with a message meant for the person installing.</summary>
public sealed class SetupException(string message) : Exception(message);
