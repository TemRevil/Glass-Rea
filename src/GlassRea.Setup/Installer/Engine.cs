using System.IO.Compression;

namespace GlassRea.Setup.Installer;

/// <summary>One visible step. <see cref="Run"/> reports finer status text and runs off the UI thread.</summary>
public sealed record Step(string Title, Func<IProgress<string>, CancellationToken, Task> Run);

/// <summary>
/// What installing and removing do, as ordered steps the window shows one by one. Every step can run again safely,
/// so "Try again" after a failure simply starts over.
/// </summary>
public static class Engine
{
    public static bool HasPayload => typeof(Engine).Assembly.GetManifestResourceInfo("payload.zip") is not null;

    /// <param name="startWithWindows">Open in the tray at sign-in.</param>
    /// <param name="previousDir">Where the installed copy lived before, when the person moved it somewhere else.</param>
    public static IReadOnlyList<Step> Install(bool startWithWindows, string? previousDir)
    {
        var legacy = Legacy.IsPresent();
        var moved = previousDir is not null && !string.Equals(Path.GetFullPath(previousDir), Path.GetFullPath(Paths.InstallDir), StringComparison.OrdinalIgnoreCase);
        var steps = new List<Step>
        {
            new("Checking for .NET 10", async (status, ct) =>
            {
                if (Runtime.IsInstalled()) { status.Report("Already on this PC"); return; }
                await Runtime.InstallAsync(status, ct);
            }),
            new("Closing Glass-Rea", (status, _) => Task.Run(() =>
            {
                status.Report("Stopping the recording service");
                ServiceSetup.Stop();
                status.Report("Closing the app");
                AppProcess.CloseAll();
                if (legacy) Legacy.Close();
            })),
        };
        if (legacy)
            steps.Add(new("Bringing over your history", (status, _) => Task.Run(() =>
            {
                Legacy.MoveData(status);
                var n = Legacy.RenameFirewallRules();
                if (n > 0) status.Report(n == 1 ? "1 firewall block kept" : $"{n} firewall blocks kept");
            })));
        steps.Add(new("Copying files", (status, ct) => Task.Run(() =>
        {
            Folders.LockDown(Paths.InstallDir);
            CopyPayload(status, ct);
        }, ct)));
        steps.Add(new("Setting up the recording service", (status, _) => Task.Run(() =>
        {
            ServiceSetup.Register();
            status.Report("Starting it");
            ServiceSetup.Start();
        })));
        steps.Add(new("Adding to Start and Installed apps", (status, _) => Task.Run(() =>
        {
            if (!File.Exists(Paths.Uninstaller)) throw new SetupException("The program files inside setup have no uninstaller. Build it with scripts\\build-setup.ps1.");
            Integration.CreateStartMenuShortcut();
            Integration.SetStartWithWindows(startWithWindows);
            Integration.WriteUninstallEntry(Folders.Size(Paths.InstallDir));
        })));
        if (legacy || moved)
            steps.Add(new("Cleaning up the old copy", (_, _) => Task.Run(() =>
            {
                if (legacy) Legacy.Remove();
                // Only a folder that really held Glass-Rea is deleted.
                if (moved && File.Exists(Path.Combine(previousDir!, "App", Paths.AppExeName))) Folders.Delete(previousDir!);
            })));
        return steps;
    }

    public static IReadOnlyList<Step> Uninstall(bool keepHistory)
    {
        var steps = new List<Step>
        {
            new("Closing Glass-Rea", (status, _) => Task.Run(() =>
            {
                status.Report("Stopping the recording service");
                ServiceSetup.Stop();
                status.Report("Closing the app");
                AppProcess.CloseAll();
            })),
            new("Removing firewall blocks", (status, _) => Task.Run(() =>
            {
                var n = FirewallCleanup.RemoveAll();
                status.Report(n switch { 0 => "No app was blocked", 1 => "1 block removed", _ => $"{n} blocks removed" });
            })),
            new("Removing the recording service", (_, _) => Task.Run(ServiceSetup.Delete)),
            new("Removing files", (_, _) => Task.Run(() =>
            {
                Integration.RemoveStartMenuShortcut();
                Integration.SetStartWithWindows(false);
                Integration.RemoveUninstallEntry();
                Folders.Delete(Paths.InstallDir);
            })),
        };
        if (!keepHistory)
            steps.Add(new("Deleting your history", (_, _) => Task.Run(() =>
            {
                Folders.Delete(Paths.DataDir);
                Folders.Delete(Paths.UserDir);
            })));
        return steps;
    }

    private static void CopyPayload(IProgress<string> status, CancellationToken ct)
    {
        using var stream = typeof(Engine).Assembly.GetManifestResourceStream("payload.zip")
            ?? throw new SetupException("This copy of setup has no program files inside. Build it with scripts\\build-setup.ps1.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        // Replace whole folders so files a newer version dropped don't linger.
        Folders.Delete(Paths.AppDir);
        Folders.Delete(Paths.ServiceDir);
        var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        var root = Path.GetFullPath(Paths.InstallDir) + Path.DirectorySeparatorChar;
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(Paths.InstallDir, files[i].FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new SetupException("The program files inside setup are damaged.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            files[i].ExtractToFile(target, overwrite: true);
            if (i % 25 == 0 || i == files.Count - 1) status.Report($"{i + 1} of {files.Count}");
        }
        Log.Write($"Copied {files.Count} files to {Paths.InstallDir}");
    }
}
