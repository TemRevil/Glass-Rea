using System.Diagnostics;
using System.Text;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;
using GlassRea.Service.Capture;
using Microsoft.Isam.Esent.Interop;

namespace GlassRea.Service.History;

/// <summary>
/// Imports the per-app network history Windows already keeps (SRUM, about 30-60 days, hourly) for the hours
/// before live capture started. SRUM counts per network adapter, so traffic through a VPN adapter is still
/// attributed to the real app. It cannot tell local-network from internet traffic, so it all counts as internet.
/// </summary>
public sealed class SrumImporter(Database db, ServiceState state, ProcessResolver resolver, ILogger<SrumImporter> log) : BackgroundService
{
    private const string SruFolder = @"C:\Windows\System32\sru";
    private const string NetworkUsageTable = "{973F5D5C-1D90-4944-BE8E-24B94231A174}";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if ((db.GetSetting(SettingKeys.SrumState) ?? "").StartsWith("Imported", StringComparison.Ordinal)) return;
        while (state.FirstLiveHour == 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        try
        {
            db.SetSetting(SettingKeys.SrumState, "Importing Windows history...");
            var summary = await Task.Run(() => Import(state.FirstLiveHour, ct), ct);
            db.SetSetting(SettingKeys.SrumState, summary);
            log.LogInformation("{Summary}", summary);
        }
        catch (OperationCanceledException) { db.SetSetting(SettingKeys.SrumState, "pending"); }
        catch (Exception ex)
        {
            db.SetSetting(SettingKeys.SrumState, $"Failed: {ex.Message}");
            log.LogError(ex, "Windows history import failed");
        }
    }

    private string Import(long beforeHour, CancellationToken ct)
    {
        var work = Path.Combine(ServiceState.DataDirectory, "srum-copy");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            // The live database is locked by Windows: copy it and its logs from a shadow copy, then replay the logs.
            foreach (var file in Directory.GetFiles(SruFolder))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".dat" or ".log" or ".chk" or ".jrs")) continue;
                Esentutl($"/y \"{file}\" /vss /d \"{Path.Combine(work, Path.GetFileName(file))}\" /o");
            }
            var dbPath = Path.Combine(work, "SRUDB.dat");
            if (!File.Exists(dbPath)) throw new FileNotFoundException("Windows usage database was not found.");
            Esentutl($"/r SRU /l\"{work}\" /s\"{work}\" /d\"{work}\" /i /o", allowFailure: true);

            var rows = Read(dbPath, beforeHour, ct);
            return Save(rows);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* best effort */ }
        }
    }

    private static void Esentutl(string args, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "esentutl.exe"), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5))) { p.Kill(); throw new TimeoutException("esentutl did not finish."); }
        if (p.ExitCode != 0 && !allowFailure) throw new InvalidOperationException($"esentutl {args.Split(' ')[0]} failed: {output.Trim().Split('\n').LastOrDefault()}");
    }

    private Dictionary<(string App, long Hour), (long In, long Out)> Read(string dbPath, long beforeHour, CancellationToken ct)
    {
        Api.JetGetDatabaseFileInfo(dbPath, out int pageSize, JET_DbInfo.PageSize);
        SystemParameters.DatabasePageSize = pageSize;
        var esentDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "esent") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(esentDir);

        using var instance = new Instance("GlassRea-SRUM");
        instance.Parameters.Recovery = false;
        instance.Parameters.CircularLog = true;
        instance.Parameters.NoInformationEvent = true;
        instance.Parameters.CreatePathIfNotExist = true;
        instance.Parameters.SystemDirectory = esentDir;
        instance.Parameters.LogFileDirectory = esentDir;
        instance.Parameters.TempDirectory = esentDir;
        instance.Init();
        using var session = new Session(instance);
        Api.JetAttachDatabase(session, dbPath, AttachDatabaseGrbit.ReadOnly);
        Api.JetOpenDatabase(session, dbPath, null, out var dbid, OpenDatabaseGrbit.ReadOnly);

        var names = new Dictionary<int, string>();
        using (var map = new Table(session, dbid, "SruDbIdMapTable", OpenTableGrbit.ReadOnly))
        {
            var cols = Api.GetColumnDictionary(session, map);
            if (Api.TryMoveFirst(session, map))
            {
                do
                {
                    var type = Api.RetrieveColumnAsByte(session, map, cols["IdType"]) ?? 255;
                    var index = Api.RetrieveColumnAsInt32(session, map, cols["IdIndex"]) ?? -1;
                    var blob = Api.RetrieveColumn(session, map, cols["IdBlob"]);
                    if (type <= 2 && blob is { Length: > 0 }) names[index] = Encoding.Unicode.GetString(blob).TrimEnd('\0');
                } while (Api.TryMoveNext(session, map));
            }
        }

        var rows = new Dictionary<(string, long), (long, long)>();
        using (var usage = new Table(session, dbid, NetworkUsageTable, OpenTableGrbit.ReadOnly))
        {
            var cols = Api.GetColumnDictionary(session, usage);
            if (Api.TryMoveFirst(session, usage))
            {
                do
                {
                    ct.ThrowIfCancellationRequested();
                    if (Api.RetrieveColumnAsDateTime(session, usage, cols["TimeStamp"]) is not { } stamp) continue;
                    var end = new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    var hour = (end - 1) / 3600; // each row covers the interval ending at its timestamp
                    if (hour >= beforeHour) continue;
                    var appId = Api.RetrieveColumnAsInt32(session, usage, cols["AppId"]) ?? -1;
                    var app = names.GetValueOrDefault(appId, "System");
                    var sent = Api.RetrieveColumnAsInt64(session, usage, cols["BytesSent"]) ?? 0;
                    var recv = Api.RetrieveColumnAsInt64(session, usage, cols["BytesRecvd"]) ?? 0;
                    rows.TryGetValue((app, hour), out var acc);
                    rows[(app, hour)] = (acc.Item1 + recv, acc.Item2 + sent);
                } while (Api.TryMoveNext(session, usage));
            }
        }
        return rows;
    }

    private string Save(Dictionary<(string App, long Hour), (long In, long Out)> rows)
    {
        if (rows.Count == 0) return "Imported: Windows had no earlier history";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tunnels = state.TunnelNames();
        var ids = new Dictionary<string, long>();
        var deltas = new List<(long, UsageDelta)>();
        foreach (var ((app, hour), (bytesIn, bytesOut)) in rows)
        {
            if (!ids.TryGetValue(app, out var id))
            {
                id = ids[app] = db.EnsureApp(Identify(app, tunnels), now, out _);
            }
            deltas.Add((id, new UsageDelta("", hour, bytesIn, bytesOut, 0, 0)));
        }
        db.AddUsage(deltas, UsageSource.WindowsHistory);
        var first = DateTimeOffset.FromUnixTimeSeconds(rows.Keys.Min(k => k.Hour) * 3600).ToLocalTime();
        return $"Imported: {ids.Count} apps, history from {first:yyyy-MM-dd}";
    }

    private AppIdentity Identify(string srumName, string[] tunnels)
    {
        if (srumName.Equals("System", StringComparison.OrdinalIgnoreCase)) return AppIdentity.System;
        var path = resolver.Paths.Normalize(srumName);
        if (Path.IsPathFullyQualified(path))
            return new AppIdentity(AppIdentity.KeyFor(path, null), resolver.FriendlyName(path), path, null, TunnelProcesses.IsTunnel(path, tunnels));
        // Store apps and services appear by name only.
        return new AppIdentity("srum:" + srumName.ToLowerInvariant(), srumName, null, null, false);
    }
}
