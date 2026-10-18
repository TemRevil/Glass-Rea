namespace GlassRea.Core.Usage;

/// <summary>
/// What an app moved in an hour of Windows' usage history over one adapter, and the network that adapter was on: a
/// Windows network's id, or null when the history doesn't say (a wired network, or a Wi-Fi Windows no longer knows).
/// </summary>
/// <param name="Hour">Windows writes its history down about once an hour; a row counts in the hour it was written.</param>
/// <param name="ViaTunnel">The adapter was a VPN's.</param>
public readonly record struct HistoryRow(string App, long Hour, bool ViaTunnel, string? Network, long In, long Out);

/// <param name="TunIn">Of In, what went through a VPN.</param>
public readonly record struct HistoryTotal(long In, long Out, long TunIn, long TunOut);

/// <summary>Which network the hours of Windows' usage history were spent on.</summary>
public static class HistoryNetworks
{
    /// <summary>
    /// Each app's hours per network (null when not known), with what went through a VPN marked. A VPN's adapter isn't a
    /// network of its own: its traffic counts on the network this PC was on in that hour, the one its other adapters
    /// used, and on no known network when they used several or none.
    /// </summary>
    public static Dictionary<(string App, long Hour, string? Network), HistoryTotal> Totals(IReadOnlyCollection<HistoryRow> rows)
    {
        var hours = HourNetworks(rows);
        var totals = new Dictionary<(string, long, string?), HistoryTotal>();
        foreach (var r in rows)
        {
            var key = (r.App, r.Hour, r.ViaTunnel ? hours.GetValueOrDefault(r.Hour) : r.Network);
            totals.TryGetValue(key, out var t);
            totals[key] = r.ViaTunnel
                ? new HistoryTotal(t.In + r.In, t.Out + r.Out, t.TunIn + r.In, t.TunOut + r.Out)
                : t with { In = t.In + r.In, Out = t.Out + r.Out };
        }
        return totals;
    }

    /// <summary>What of an hour's <paramref name="total"/> on a network isn't on it yet, when <paramref name="placed"/> already is.</summary>
    public static HistoryTotal Rest(HistoryTotal total, HistoryTotal placed) => new(
        Math.Max(0, total.In - placed.In), Math.Max(0, total.Out - placed.Out), Math.Max(0, total.TunIn - placed.TunIn), Math.Max(0, total.TunOut - placed.TunOut));

    /// <summary>The network of each history hour in which this PC's own adapters (not a VPN's) used one known network and no other.</summary>
    public static Dictionary<long, string> HourNetworks(IEnumerable<HistoryRow> rows)
    {
        var seen = new Dictionary<long, HashSet<string?>>();
        foreach (var r in rows)
        {
            if (r.ViaTunnel || r.In + r.Out <= 0) continue;
            if (!seen.TryGetValue(r.Hour, out var set)) seen[r.Hour] = set = [];
            set.Add(r.Network);
        }
        return seen.Where(kv => kv.Value.Count == 1 && kv.Value.Single() is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Single()!);
    }

    /// <summary>
    /// The network of each hour of live capture that Windows' history puts on one network. A history hour holds what was
    /// written down during it, which reaches back up to an hour, so live hour h is covered by history hours h and h + 1:
    /// both must show the same network and nothing else.
    /// </summary>
    public static Dictionary<long, string> LiveHours(IReadOnlyDictionary<long, string> hourNetworks) =>
        hourNetworks.Where(kv => hourNetworks.TryGetValue(kv.Key + 1, out var next) && next == kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>
    /// The local days whose hours of live use on no known network all turned out to be on one network: the hosts of
    /// such a day, kept per day, were all on it too. An hour that spans two days (where the time zone is off by half
    /// an hour) belongs to both.
    /// </summary>
    /// <param name="unknownHours">Every hour holding live use on no known network.</param>
    /// <param name="liveNetworks">The network each of those hours turned out to be on, where known.</param>
    public static Dictionary<int, long> WholeDays(IEnumerable<long> unknownHours, IReadOnlyDictionary<long, long> liveNetworks) =>
        unknownHours.Distinct()
            .SelectMany(h => new[] { h * 3600, h * 3600 + 3599 }.Select(t => (Hour: h, Day: UsageAccumulator.LocalDay(DateTimeOffset.FromUnixTimeSeconds(t)))))
            .Distinct()
            .GroupBy(x => x.Day)
            .Select(day => (Day: day.Key, Networks: day.Select(x => liveNetworks.TryGetValue(x.Hour, out var n) ? n : (long?)null).Distinct().ToList()))
            .Where(d => d.Networks is [long])
            .ToDictionary(d => d.Day, d => d.Networks[0]!.Value);
}
