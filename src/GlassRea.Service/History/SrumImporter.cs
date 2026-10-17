using System.Diagnostics;
using System.Text;
using GlassRea.Core.Net;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;
using GlassRea.Service.Capture;
using Microsoft.Isam.Esent.Interop;

namespace GlassRea.Service.History;

/// <summary>
/// Imports the per-app network history Windows already keeps (SRUM, about 30-60 days, hourly) for the hours
/// before live capture started. SRUM counts per network adapter, so traffic through a VPN adapter is still
/// attributed to the real app, and is marked as having gone through the VPN (the VPN's own row carries it over the
/// connection). It cannot tell local-network from internet traffic, so it all counts as internet.
/// History imported before that marking existed gets it once, in a second pass.
/// </summary>
public sealed class SrumImporter(Database db, ServiceState state, ProcessResolver resolver, ILogger<SrumImporter> log) : BackgroundService
{
    private const string SruFolder = @"C:\Windows\System32\sru";
    private const string NetworkUsageTable = "{973F5D5C-1D90-4944-BE8E-24B94231A174}";

    private record struct Row(long In, long Out, long TunIn, long TunOut);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var imported = (db.GetSetting(SettingKeys.SrumState) ?? "").StartsWith("Imported", StringComparison.Ordinal);
        if (imported && db.GetSetting(SettingKeys.SrumTunnelSplit) == "done") return;
        while (state.FirstLiveHour == 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        if (imported)
        {
            try
            {
                await Task.Run(() => WithCopy(path => Split(Read(path, state.FirstLiveHour, ct))), ct);
                db.SetSetting(SettingKeys.SrumTunnelSplit, "done");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogWarning(ex, "Couldn't mark VPN traffic in imported Windows history"); }
            return;
        }
        try
        {
            db.SetSetting(SettingKeys.SrumState, "Importing Windows history...");
            var summary = await Task.Run(() => WithCopy(path => Save(Read(path, state.FirstLiveHour, ct))), ct);
            db.SetSetting(SettingKeys.SrumState, summary);
            db.SetSetting(SettingKeys.SrumTunnelSplit, "done");
            log.LogInformation("{Summary}", summary);
        }
        catch (OperationCanceledException) { db.SetSetting(SettingKeys.SrumState, "pending"); }
        catch (Exception ex)
        {
            db.SetSetting(SettingKeys.SrumState, $"Failed: {ex.Message}");
            log.LogError(ex, "Windows history import failed");
        }
    }

    private static T WithCopy<T>(Func<string, T> use)
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

            return use(dbPath);
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

    private static Dictionary<(string App, long Hour), Row> Read(string dbPath, long beforeHour, CancellationToken ct)
    {
        var tunnelIndexes = TunnelIndexes();
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

        var rows = new Dictionary<(string, long), Row>();
        var tunnelByLuid = new Dictionary<long, bool>();
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
                    var luid = cols.TryGetValue("InterfaceLuid", out var luidColumn) ? Api.RetrieveColumnAsInt64(session, usage, luidColumn) ?? 0 : 0;
                    if (!tunnelByLuid.TryGetValue(luid, out var viaTunnel))
                        viaTunnel = tunnelByLuid[luid] = luid != 0 && (TunnelAdapters.IsTunnelType((int)((ulong)luid >> 48)) || tunnelIndexes.Contains(Native.InterfaceIndex(luid)));
                    rows.TryGetValue((app, hour), out var acc);
                    rows[(app, hour)] = new Row(acc.In + recv, acc.Out + sent, acc.TunIn + (viaTunnel ? recv : 0), acc.TunOut + (viaTunnel ? sent : 0));
                } while (Api.TryMoveNext(session, usage));
            }
        }
        return rows;
    }

    /// <summary>Indexes of this PC's VPN adapters whose type alone doesn't say so (OpenVPN's TAP reports itself as Ethernet).</summary>
    private static HashSet<int> TunnelIndexes()
    {
        var set = new HashSet<int>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!TunnelAdapters.IsTunnel(nic.Name, nic.Description, (int)nic.NetworkInterfaceType)) continue;
                var props = nic.GetIPProperties();
                if (props.GetIPv4Properties() is { } v4) set.Add(v4.Index);
                if (props.GetIPv6Properties() is { } v6) set.Add(v6.Index);
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return set;
    }

    private string Save(Dictionary<(string App, long Hour), Row> rows)
    {
        if (rows.Count == 0) return "Imported: Windows had no earlier history";
        var ids = new Dictionary<string, long>();
        var deltas = rows.Select(kv => (AppId(ids, kv.Key.App), new UsageDelta("", kv.Key.Hour, kv.Value.In, kv.Value.Out, 0, 0, kv.Value.TunIn, kv.Value.TunOut))).ToList();
        db.AddUsage(deltas, UsageSource.WindowsHistory);
        var first = DateTimeOffset.FromUnixTimeSeconds(rows.Keys.Min(k => k.Hour) * 3600).ToLocalTime();
        return $"Imported: {ids.Count} apps, history from {first:yyyy-MM-dd}";
    }

    /// <summary>Marks which of the already imported hours went through a VPN adapter.</summary>
    private int Split(Dictionary<(string App, long Hour), Row> rows)
    {
        var ids = new Dictionary<string, long>();
        // Every app, not just those with VPN traffic: this is also when a VPN service known only by name is recognised.
        foreach (var app in rows.Keys.Select(k => k.App).Distinct()) AppId(ids, app);
        var marked = rows.Where(kv => kv.Value.TunIn + kv.Value.TunOut > 0)
            .Select(kv => (AppId(ids, kv.Key.App), kv.Key.Hour, kv.Value.TunIn, kv.Value.TunOut)).ToList();
        db.SetImportedTunnel(marked);
        log.LogInformation("Marked {Count} hours of imported history as sent through a VPN", marked.Count);
        return marked.Count;
    }

    private long AppId(Dictionary<string, long> ids, string app)
    {
        if (ids.TryGetValue(app, out var id)) return id;
        var identity = Identify(app, state.TunnelNames());
        id = ids[app] = db.EnsureApp(identity, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out _);
        // A VPN service imported before its program was looked up gets its path and tunnel mark now, so the
        // tunnel setting keeps it marked from then on.
        if (identity.IsTunnel && identity.Path is { } path) db.SetAppPath(id, path, isTunnel: true);
        return id;
    }

    private AppIdentity Identify(string srumName, string[] tunnels)
    {
        if (srumName.Equals("System", StringComparison.OrdinalIgnoreCase)) return AppIdentity.System;
        var path = resolver.Paths.Normalize(srumName);
        if (Path.IsPathFullyQualified(path))
            return new AppIdentity(AppIdentity.KeyFor(path, null), resolver.FriendlyName(path), path, null, TunnelProcesses.IsTunnel(path, tunnels));
        // Store apps and services appear by name only. A service is the program it runs: Cloudflare WARP's
        // traffic is recorded as "CloudflareWARP", the service that runs warp-svc.exe.
        var key = "srum:" + srumName.ToLowerInvariant();
        if (ServiceProgram(srumName) is { } program && TunnelProcesses.IsTunnel(program, tunnels))
            return new AppIdentity(key, srumName, program, srumName, true);
        return new AppIdentity(key, srumName, null, null, false);
    }

    private static string? ServiceProgram(string serviceName)
    {
        if (serviceName.Length is 0 or > 256 || serviceName.IndexOfAny(['\\', '/']) >= 0) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key?.GetValue("ImagePath", null, Microsoft.Win32.RegistryValueOptions.None) is string image ? ProgramOf(image) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>The program in a service's command line: <c>"C:\Program Files\x\a.exe" --run</c> or <c>C:\x\a.exe -k y</c> give the exe.</summary>
    public static string? ProgramOf(string imagePath)
    {
        var text = imagePath.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : null;
    }
}
