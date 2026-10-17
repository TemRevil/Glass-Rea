using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GlassRea.Core.Updates;

namespace GlassRea.App.Support;

/// <summary>A problem with an update, worded for the person reading Settings > About.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Finds newer releases on GitHub, downloads the setup into this user's Glass-Rea folder, checks it is the one
/// GitHub recorded and that Glass-Rea's publisher signed it (<see cref="Releases.AcceptsSigner"/>), then runs it
/// (Windows asks for admin, and the setup updates in place).
/// </summary>
public static partial class Updater
{
    // Before the client: its user agent names this version.
    public static Version Current { get; } = typeof(App).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(1, 0, 0);

    private static readonly HttpClient Http = CreateClient();
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glass-Rea", "Updates");

    /// <summary>The newest release found by the last check, or null when there isn't a newer one.</summary>
    public static Release? Available { get; private set; }

    /// <summary>When the last check finished (local time), or null before the first.</summary>
    public static DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Raised on the thread that checked, after a check finishes.</summary>
    public static event Action? Checked;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Glass-Rea", Current.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Asks GitHub for the latest release. Returns it when it is newer than this copy.</summary>
    public static async Task<Release?> Check(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var (status, release) = await Releases.FindLatest(Get, timeout.Token);
            if (status is 403 or 429) throw new UpdateException("GitHub is limiting requests from this network for now. Try again in an hour.");
            if (status is not (200 or 404)) throw new UpdateException($"GitHub answered {status}. Try again later.");
            Available = release is not null && Releases.IsNewer(release, Current) ? release : null; // 404: nothing released yet
            LastChecked = DateTimeOffset.Now;
            Preferences.Update(p => p.LastUpdateCheck = LastChecked.Value.ToUnixTimeSeconds());
            Checked?.Invoke();
            return Available;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateException("Couldn't reach GitHub. Check the internet connection and try again.");
        }
    }

    private static async Task<(int, string)> Get(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        return ((int)response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "");
    }

    /// <summary>
    /// Downloads the release's setup, or reuses one already downloaded and checked. Refuses a file that isn't
    /// exactly what GitHub recorded or isn't signed by Glass-Rea's publisher.
    /// </summary>
    public static async Task<string> Download(Release release, IProgress<double> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"Glass-Rea Setup {release.Version.ToString(3)}.exe");
        if (File.Exists(path) && Verify(path, release) is null) return path;

        var part = path + ".part";
        try
        {
            using var response = await Http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"The download failed: GitHub answered {(int)response.StatusCode}.");
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(part))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    done += read;
                    if (done > release.SetupSize) throw new UpdateException("The download was bigger than the release says. It was thrown away.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress.Report((double)done / release.SetupSize);
                }
            }
            File.Move(part, path, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            throw new UpdateException("The download stopped. Check the internet connection and try again.");
        }
        finally
        {
            try { File.Delete(part); } catch { /* best effort */ }
        }

        if (Verify(path, release) is { } problem)
        {
            try { File.Delete(path); } catch { /* best effort */ }
            throw new UpdateException(problem);
        }
        return path;
    }

    /// <summary>
    /// Starts the setup. Windows asks for admin (off this thread, so the window keeps drawing while it asks); the setup
    /// then closes this window and opens the new one.
    /// </summary>
    public static async Task Install(string path)
    {
        try { await Task.Run(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new UpdateException("The update needs admin permission to install. Choose Install again and allow it.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 4551)
        {
            throw new UpdateException("Windows Smart App Control blocked the setup, because its certificate isn't one Windows knows. Open the release on GitHub and run the setup from there, or turn Smart App Control off.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UpdateException($"Windows couldn't start the setup: {ex.Message}");
        }
    }

    /// <summary>Removes setups for versions this copy already is or has passed.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.GetFiles(Folder))
            {
                var name = Path.GetFileNameWithoutExtension(file).Replace("Glass-Rea Setup ", "");
                if (file.EndsWith(".part") || Releases.VersionOf(name) is not { } v || v <= Current) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Keeps an eye out for updates while the app runs: once a day when turned on in Settings > About, with one
    /// notification per new version.
    /// </summary>
    public static async Task Watch(Action<Release> announce, CancellationToken ct)
    {
        CleanUp();
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var due = DateTimeOffset.FromUnixTimeSeconds(Preferences.State.LastUpdateCheck).AddDays(1) <= DateTimeOffset.Now;
            if (Preferences.State.CheckForUpdates && due)
            {
                try
                {
                    if (await Check(ct) is { } release && Preferences.State.AnnouncedVersion != release.Tag)
                    {
                        Preferences.Update(p => p.AnnouncedVersion = release.Tag);
                        announce(release);
                    }
                }
                catch (UpdateException) { /* offline: tried again within the hour */ }
                catch (OperationCanceledException) { return; }
            }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Null when the file is the release's setup, signed by Glass-Rea's publisher; otherwise why it isn't.</summary>
    internal static string? Verify(string path, Release release)
    {
        if (new FileInfo(path).Length != release.SetupSize) return "The download isn't the size the release says. It was thrown away.";
        if (release.SetupSha256 is not { } expected)
            return "GitHub has no record of this release's file to check the download against. It was thrown away; get the setup from the release page instead.";
        using (var file = File.OpenRead(path))
        {
            if (!Convert.ToHexStringLower(SHA256.HashData(file)).Equals(expected, StringComparison.Ordinal))
                return "The download doesn't match the file GitHub recorded for this release. It was thrown away.";
        }
        var trust = SignatureStatus(path);
        if (trust is not (0 or Releases.UntrustedRoot)) return "The download isn't properly signed. It was thrown away.";
        var (thumbprint, name, organization) = Signer(path);
        if (!Releases.AcceptsSigner(trust, thumbprint, name, organization, FileVersionInfo.GetVersionInfo(path).ProductName))
            return "The download isn't signed by Glass-Rea's publisher. It was thrown away.";
        return null;
    }

    /// <summary>The signer's certificate: its thumbprint, and the common name and organisation it was issued to.</summary>
    private static (string? Thumbprint, string? Name, string? Organization) Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // X509CertificateLoader can't read a signed file's signer; the signature itself is checked by WinVerifyTrust
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            string? Part(string oid) => cert.SubjectName.EnumerateRelativeDistinguishedNames()
                .FirstOrDefault(n => !n.HasMultipleElements && n.GetSingleElementType().Value == oid)?.GetSingleElementValue();
            return (cert.Thumbprint, Part("2.5.4.3"), Part("2.5.4.10")); // CN, O
        }
        catch (CryptographicException) { return (null, null, null); }
    }

    private static unsafe int SignatureStatus(string path)
    {
        fixed (char* file = path)
        {
            var fileInfo = new WinTrustFileInfo { cbStruct = (uint)sizeof(WinTrustFileInfo), pcwszFilePath = (nint)file };
            var data = new WinTrustData
            {
                cbStruct = (uint)sizeof(WinTrustData),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = (nint)(&fileInfo),
                dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x10 | 0x1000, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(-1, ref action, ref data);
        }
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }
}
