using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using GlassRea.Core.Paths;
using GlassRea.Core.Usage;

namespace GlassRea.Service.Capture;

/// <summary>
/// Turns a process id into a stable app identity. Process-start events from ETW fill the cache early,
/// so even programs that exit within a second are still named. svchost is split by the services it hosts.
/// </summary>
public sealed class ProcessResolver(ServiceState state, ILogger<ProcessResolver> log)
{
    private sealed record Entry(AppIdentity App, long CheckedAt);

    private readonly ConcurrentDictionary<int, Entry> _byPid = new();
    private readonly ConcurrentDictionary<int, string> _startedImages = new();
    private readonly ConcurrentDictionary<string, AppIdentity> _byKey = new();
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, (string Name, string Display)[]> _services = [];
    private long _servicesAt;
    private DevicePaths _paths = new(Native.DeviceToDriveMap());
    private string[] _tunnels = state.TunnelNames();

    public DevicePaths Paths => _paths;

    public void RefreshSettings()
    {
        _tunnels = state.TunnelNames();
        _byPid.Clear();
    }

    public AppIdentity? ByKey(string key) => _byKey.GetValueOrDefault(key);

    public void Remember(AppIdentity app) => _byKey[app.Key] = app;

    public void OnProcessStart(int pid, string imageName)
    {
        _byPid.TryRemove(pid, out _); // a new process now owns this id
        if (string.IsNullOrWhiteSpace(imageName)) _startedImages.TryRemove(pid, out _);
        else _startedImages[pid] = _paths.Normalize(imageName);
    }

    public void OnProcessStop(int pid)
    {
        // Keep the identity briefly so packets still in flight are attributed, then let the id be reused.
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => { _byPid.TryRemove(pid, out var _); _startedImages.TryRemove(pid, out var _); });
    }

    public AppIdentity Resolve(int pid)
    {
        if (pid is 0 or 4) return Keep(AppIdentity.System);
        var now = Environment.TickCount64;
        if (_byPid.TryGetValue(pid, out var e) && now - e.CheckedAt < 60_000) return e.App;

        var path = Native.ProcessPath(pid) ?? _startedImages.GetValueOrDefault(pid);
        var app = string.IsNullOrWhiteSpace(path) ? AppIdentity.Unknown : Identify(pid, path);
        _byPid[pid] = new Entry(app, now);
        return Keep(app);
    }

    private AppIdentity Keep(AppIdentity app) { _byKey.TryAdd(app.Key, app); return app; }

    private AppIdentity Identify(int pid, string path)
    {
        var tunnel = TunnelProcesses.IsTunnel(path, _tunnels);
        if (string.Equals(Path.GetFileName(path), "svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var services = ServicesFor(pid);
            if (services.Length > 0)
            {
                var service = string.Join(",", services.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase));
                var display = string.Join(", ", services.Select(s => s.Display).Order(StringComparer.OrdinalIgnoreCase));
                return new AppIdentity(AppIdentity.KeyFor(path, service), display, path, service, tunnel);
            }
        }
        return new AppIdentity(AppIdentity.KeyFor(path, null), FriendlyName(path), path, null, tunnel);
    }

    public string FriendlyName(string path) => _names.GetOrAdd(path, p =>
    {
        try
        {
            if (File.Exists(p))
            {
                var info = FileVersionInfo.GetVersionInfo(p);
                var name = info.FileDescription?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length < 3) name = info.ProductName?.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch { /* unreadable file: fall back to its name */ }
        return Path.GetFileNameWithoutExtension(p);
    });

    private (string Name, string Display)[] ServicesFor(int pid)
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _servicesAt) > 30_000 || !_services.ContainsKey(pid))
            RefreshServices();
        return _services.GetValueOrDefault(pid) ?? [];
    }

    private void RefreshServices()
    {
        try
        {
            var map = new Dictionary<int, List<(string, string)>>();
            using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, ProcessId FROM Win32_Service WHERE ProcessId <> 0");
            foreach (var o in searcher.Get())
            {
                var pid = Convert.ToInt32(o["ProcessId"]);
                if (!map.TryGetValue(pid, out var list)) map[pid] = list = [];
                list.Add(((string)o["Name"], (string?)o["DisplayName"] ?? (string)o["Name"]));
            }
            _services = map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not list services"); }
        Interlocked.Exchange(ref _servicesAt, Environment.TickCount64);
    }

    public void RefreshDrives() => _paths = new DevicePaths(Native.DeviceToDriveMap());
}
