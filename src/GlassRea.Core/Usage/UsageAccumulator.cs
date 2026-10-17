using System.Net;
using GlassRea.Core.Net;

namespace GlassRea.Core.Usage;

public readonly record struct UsageDelta(string AppKey, long Hour, long NetIn, long NetOut, long LanIn, long LanOut);
public readonly record struct HostDelta(string AppKey, int Day, string Ip, long In, long Out, long LastSeen);
public sealed record AppRate(string AppKey, long InPerSec, long OutPerSec);

/// <summary>
/// Collects traffic samples in memory. The service flushes the hourly and per-host buckets to the database
/// every few seconds and serves the per-second ring for the live graph.
/// </summary>
public sealed class UsageAccumulator(int liveSeconds = 300)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, long), long[]> _hours = [];
    private readonly Dictionary<(string, int, string), long[]> _hosts = [];
    private readonly long[] _liveIn = new long[liveSeconds];
    private readonly long[] _liveOut = new long[liveSeconds];
    private readonly Dictionary<string, (long Second, long In, long Out)>[] _liveApps = Enumerable.Range(0, 5).Select(_ => new Dictionary<string, (long, long, long)>()).ToArray();
    private long _liveSecond = -1;

    public void Add(AppIdentity app, DateTimeOffset time, long bytes, bool outbound, IPAddress remote)
    {
        var scope = AddressScope.Classify(remote);
        if (scope == Scope.Loopback) return;
        Count(app, time, bytes, outbound, scope, remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : remote.ToString());
    }

    /// <summary>
    /// Bytes an app sent to or got from a VPN's local proxy or DNS port: internet use, though where it went is
    /// hidden inside the VPN, so no host is recorded.
    /// </summary>
    public void AddThroughTunnel(AppIdentity app, DateTimeOffset time, long bytes, bool outbound) =>
        Count(app, time, bytes, outbound, Scope.Internet, null);

    private void Count(AppIdentity app, DateTimeOffset time, long bytes, bool outbound, Scope scope, string? ip)
    {
        if (bytes <= 0) return;
        var unix = time.ToUnixTimeSeconds();
        var hour = unix / 3600;
        lock (_gate)
        {
            var h = Bucket(_hours, (app.Key, hour), 4);
            var slot = (scope == Scope.Internet ? 0 : 2) + (outbound ? 1 : 0);
            h[slot] += bytes;

            if (ip is not null)
            {
                var host = Bucket(_hosts, (app.Key, LocalDay(time), ip), 3);
                host[outbound ? 1 : 0] += bytes;
                host[2] = unix;
            }

            if (scope == Scope.Internet && !app.IsTunnel) AddLive(app.Key, unix, bytes, outbound);
        }
    }

    public static int LocalDay(DateTimeOffset time)
    {
        var d = time.ToLocalTime();
        return d.Year * 10000 + d.Month * 100 + d.Day;
    }

    private static long[] Bucket<TKey>(Dictionary<TKey, long[]> map, TKey key, int size) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var b)) map[key] = b = new long[size];
        return b;
    }

    private void AddLive(string appKey, long unix, long bytes, bool outbound)
    {
        AdvanceTo(unix);
        var i = (int)(unix % _liveIn.Length);
        if (outbound) _liveOut[i] += bytes; else _liveIn[i] += bytes;
        var apps = _liveApps[unix % _liveApps.Length];
        apps.TryGetValue(appKey, out var cur);
        if (cur.Second != unix) cur = (unix, 0, 0);
        apps[appKey] = outbound ? cur with { Out = cur.Out + bytes } : cur with { In = cur.In + bytes };
    }

    private void AdvanceTo(long unix)
    {
        if (_liveSecond < 0) _liveSecond = unix;
        if (unix <= _liveSecond) return;
        var steps = Math.Min(unix - _liveSecond, _liveIn.Length);
        for (long s = 1; s <= steps; s++)
        {
            var i = (int)((_liveSecond + s) % _liveIn.Length);
            _liveIn[i] = 0; _liveOut[i] = 0;
            var apps = _liveApps[(_liveSecond + s) % _liveApps.Length];
            apps.Clear();
        }
        _liveSecond = unix;
    }

    /// <summary>Takes everything collected since the last flush.</summary>
    public (List<UsageDelta> Usage, List<HostDelta> Hosts) Drain()
    {
        lock (_gate)
        {
            var usage = _hours.Select(kv => new UsageDelta(kv.Key.Item1, kv.Key.Item2, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3])).ToList();
            var hosts = _hosts.Select(kv => new HostDelta(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value[0], kv.Value[1], kv.Value[2])).ToList();
            _hours.Clear();
            _hosts.Clear();
            return (usage, hosts);
        }
    }

    /// <summary>Internet bytes per second for the last <paramref name="seconds"/> seconds ending at <paramref name="now"/>, oldest first.</summary>
    public (long[] In, long[] Out) Live(DateTimeOffset now, int seconds)
    {
        var unix = now.ToUnixTimeSeconds();
        lock (_gate)
        {
            AdvanceTo(unix);
            seconds = Math.Min(seconds, _liveIn.Length);
            var inn = new long[seconds];
            var outt = new long[seconds];
            for (var k = 0; k < seconds; k++)
            {
                var i = (int)((unix - seconds + 1 + k) % _liveIn.Length);
                inn[k] = _liveIn[i];
                outt[k] = _liveOut[i];
            }
            return (inn, outt);
        }
    }

    /// <summary>Average rate per app over the last few complete seconds.</summary>
    public List<AppRate> TopRates(DateTimeOffset now, int count)
    {
        var unix = now.ToUnixTimeSeconds();
        lock (_gate)
        {
            AdvanceTo(unix);
            var window = _liveApps.Length - 1; // skip the second still being filled
            var sums = new Dictionary<string, (long In, long Out)>();
            for (var s = 1; s <= window; s++)
            {
                foreach (var (key, v) in _liveApps[(unix - s) % _liveApps.Length])
                {
                    if (v.Second != unix - s) continue;
                    sums.TryGetValue(key, out var acc);
                    sums[key] = (acc.In + v.In, acc.Out + v.Out);
                }
            }
            return sums.Select(kv => new AppRate(kv.Key, kv.Value.In / window, kv.Value.Out / window))
                .OrderByDescending(r => r.InPerSec + r.OutPerSec).Take(count).ToList();
        }
    }
}
