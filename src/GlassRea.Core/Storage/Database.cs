using GlassRea.Core.Protocol;
using GlassRea.Core.Usage;

namespace GlassRea.Core.Storage;

public enum UsageSource { Live = 0, WindowsHistory = 1 }

/// <summary>
/// At most so much of an app's hour on no known network belongs to <see cref="Network"/> (see <see cref="Database.AssignNetworks"/>);
/// the defaults take the whole row.
/// </summary>
public readonly record struct NetworkMove(long AppId, long Hour, UsageSource Source, long Network,
    long NetIn = long.MaxValue, long NetOut = long.MaxValue, long TunIn = long.MaxValue, long TunOut = long.MaxValue);

/// <summary>SQLite store. One connection, serialized by a lock; the service is the only writer.</summary>
public sealed class Database : IDisposable
{
    private readonly SqliteDb _db;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _appIds = new(StringComparer.Ordinal);

    public Database(string path)
    {
        _db = new SqliteDb(path);
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Exec($"""
            CREATE TABLE IF NOT EXISTS apps(
              id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, name TEXT NOT NULL, path TEXT, service TEXT,
              is_tunnel INTEGER NOT NULL DEFAULT 0, first_seen INTEGER NOT NULL);
            {UsageTable("usage")}
            {HostsTable("hosts")}
            CREATE TABLE IF NOT EXISTS networks(
              id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, name TEXT NOT NULL, custom_name TEXT, kind TEXT,
              first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS dns(ip TEXT PRIMARY KEY, name TEXT NOT NULL, seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS devices(
              mac TEXT PRIMARY KEY, ip TEXT NOT NULL, name TEXT, randomized INTEGER NOT NULL,
              is_gateway INTEGER NOT NULL, is_this_pc INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
              vendor TEXT, model TEXT, kind TEXT, os TEXT, custom_name TEXT, away INTEGER NOT NULL DEFAULT 0, network TEXT);
            CREATE TABLE IF NOT EXISTS forgotten_devices(mac TEXT PRIMARY KEY, at INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS device_hints(
              mac TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, seen INTEGER NOT NULL,
              PRIMARY KEY(mac, key)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS alerts(
              id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
        // Older databases predate these columns.
        AddMissing("devices", ("vendor", "TEXT"), ("model", "TEXT"), ("kind", "TEXT"), ("os", "TEXT"), ("custom_name", "TEXT"),
            ("away", "INTEGER NOT NULL DEFAULT 0"), ("network", "TEXT"));
        AddMissing("usage", ("tun_in", "INTEGER NOT NULL DEFAULT 0"), ("tun_out", "INTEGER NOT NULL DEFAULT 0"));
        AddMissing("apps", ("blocked", "INTEGER NOT NULL DEFAULT 0"));
        KeyByNetwork("usage", UsageTable, "app_id,hour,source,net_in,net_out,lan_in,lan_out,tun_in,tun_out");
        KeyByNetwork("hosts", HostsTable, "app_id,day,ip,bytes_in,bytes_out,last_seen");
        Exec("CREATE INDEX IF NOT EXISTS usage_by_hour ON usage(hour);");
    }

    /// <summary>Hourly totals per app, per source (<see cref="UsageSource"/>) and per network (0 when not known).</summary>
    private static string UsageTable(string name) => $"""
        CREATE TABLE IF NOT EXISTS {name}(
          app_id INTEGER NOT NULL REFERENCES apps(id), hour INTEGER NOT NULL, source INTEGER NOT NULL, network INTEGER NOT NULL DEFAULT 0,
          net_in INTEGER NOT NULL DEFAULT 0, net_out INTEGER NOT NULL DEFAULT 0,
          lan_in INTEGER NOT NULL DEFAULT 0, lan_out INTEGER NOT NULL DEFAULT 0,
          tun_in INTEGER NOT NULL DEFAULT 0, tun_out INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY(app_id, hour, source, network)) WITHOUT ROWID;
        """;

    /// <summary>Who each app talked to, per local day and per network.</summary>
    private static string HostsTable(string name) => $"""
        CREATE TABLE IF NOT EXISTS {name}(
          app_id INTEGER NOT NULL REFERENCES apps(id), day INTEGER NOT NULL, ip TEXT NOT NULL, network INTEGER NOT NULL DEFAULT 0,
          bytes_in INTEGER NOT NULL DEFAULT 0, bytes_out INTEGER NOT NULL DEFAULT 0, last_seen INTEGER NOT NULL,
          PRIMARY KEY(app_id, day, ip, network)) WITHOUT ROWID;
        """;

    private HashSet<string> Columns(string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var info = Cmd($"PRAGMA table_info({table})");
        using var r = info.ExecuteReader();
        while (r.Read()) columns.Add(r.GetString(1));
        return columns;
    }

    private void AddMissing(string table, params (string Name, string Type)[] wanted)
    {
        var columns = Columns(table);
        foreach (var (name, type) in wanted)
            if (!columns.Contains(name)) Exec($"ALTER TABLE {table} ADD COLUMN {name} {type}");
    }

    /// <summary>
    /// Older databases have no network column. It's part of the table's key, which SQLite can't change in place, so the
    /// table is copied into the new shape, every row so far on an unknown network, all in one transaction.
    /// </summary>
    private void KeyByNetwork(string table, Func<string, string> create, string columns)
    {
        if (Columns(table).Contains("network")) return;
        using var tx = _db.BeginTransaction();
        Exec(create(table + "_by_network"));
        Exec($"INSERT INTO {table}_by_network({columns}) SELECT {columns} FROM {table}; DROP TABLE {table}; ALTER TABLE {table}_by_network RENAME TO {table};");
        tx.Commit();
    }

    public static Database OpenFile(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return new Database(path);
    }

    public static Database InMemory() => new(":memory:");

    // ---------- apps ----------

    /// <summary>Returns the app's id, creating the row on first sight. isNew tells the caller to raise a "new app" alert.</summary>
    public long EnsureApp(AppIdentity app, long now, out bool isNew)
    {
        lock (_gate)
        {
            isNew = false;
            if (_appIds.TryGetValue(app.Key, out var cached)) return cached;
            using var find = Cmd("SELECT id FROM apps WHERE key=$k", ("$k", app.Key));
            if (find.ExecuteScalar() is long existing) return _appIds[app.Key] = existing;
            using var ins = Cmd("INSERT INTO apps(key,name,path,service,is_tunnel,first_seen) VALUES($k,$n,$p,$s,$t,$f) RETURNING id",
                ("$k", app.Key), ("$n", app.Name), ("$p", app.Path), ("$s", app.Service), ("$t", app.IsTunnel ? 1 : 0), ("$f", now));
            isNew = true;
            return _appIds[app.Key] = (long)ins.ExecuteScalar()!;
        }
    }

    /// <summary>Gives an app known only by name the program it turned out to be.</summary>
    public void SetAppPath(long appId, string path, bool isTunnel)
    {
        lock (_gate)
        {
            using var c = Cmd("UPDATE apps SET path=$p, is_tunnel=$t WHERE id=$i", ("$p", path), ("$t", isTunnel ? 1 : 0), ("$i", appId));
            c.ExecuteNonQuery();
        }
    }

    public void SetTunnel(long appId, bool isTunnel)
    {
        lock (_gate) { using var c = Cmd("UPDATE apps SET is_tunnel=$t WHERE id=$i", ("$t", isTunnel ? 1 : 0), ("$i", appId)); c.ExecuteNonQuery(); }
    }

    public AppUsage? GetApp(long appId) => QueryApps("WHERE a.id=$i", 0, 0, null, ("$i", appId)).FirstOrDefault();

    public List<(long Id, string Key, string? Path)> AllApps()
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT id,key,path FROM apps");
            using var r = c.ExecuteReader();
            var list = new List<(long, string, string?)>();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            return list;
        }
    }

    /// <summary>
    /// The Store copy's blocked apps, which it blocks again at every start. The copy setup installs keeps its blocks as
    /// Windows Firewall rules instead and leaves this unset.
    /// </summary>
    public HashSet<long> BlockedApps()
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT id FROM apps WHERE blocked=1");
            using var r = c.ExecuteReader();
            var ids = new HashSet<long>();
            while (r.Read()) ids.Add(r.GetInt64(0));
            return ids;
        }
    }

    public void SetBlocked(long appId, bool blocked)
    {
        lock (_gate) { using var c = Cmd("UPDATE apps SET blocked=$b WHERE id=$i", ("$b", blocked ? 1 : 0), ("$i", appId)); c.ExecuteNonQuery(); }
    }

    /// <summary>Marks the apps with these keys blocked (the blocks the copy setup installed had, carried over).</summary>
    public void SetBlocked(IEnumerable<string> appKeys)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = Cmd("UPDATE apps SET blocked=1 WHERE key=$k", ("$k", ""));
            foreach (var key in appKeys)
            {
                c["$k"] = key;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------- usage ----------

    public void AddUsage(IEnumerable<(long AppId, UsageDelta Delta)> rows, UsageSource source)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = AddUsageCommand(source);
            foreach (var (appId, d) in rows)
            {
                c["$a"] = appId; c["$h"] = d.Hour; c["$n"] = d.Network;
                c["$ni"] = d.NetIn; c["$no"] = d.NetOut;
                c["$li"] = d.LanIn; c["$lo"] = d.LanOut;
                c["$ti"] = d.TunIn; c["$to"] = d.TunOut;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private SqliteStatement AddUsageCommand(UsageSource source) => Cmd("""
        INSERT INTO usage(app_id,hour,source,network,net_in,net_out,lan_in,lan_out,tun_in,tun_out) VALUES($a,$h,$s,$n,$ni,$no,$li,$lo,$ti,$to)
        ON CONFLICT(app_id,hour,source,network) DO UPDATE SET net_in=net_in+$ni, net_out=net_out+$no, lan_in=lan_in+$li, lan_out=lan_out+$lo,
          tun_in=tun_in+$ti, tun_out=tun_out+$to
        """, ("$a", 0L), ("$h", 0L), ("$s", (long)source), ("$n", 0L), ("$ni", 0L), ("$no", 0L), ("$li", 0L), ("$lo", 0L), ("$ti", 0L), ("$to", 0L));

    /// <summary>
    /// Sets how much of already imported Windows history went through a VPN adapter (a one-time pass). History imported
    /// before that was marked also predates networks, so it's all on the unknown one.
    /// </summary>
    public void SetImportedTunnel(IEnumerable<(long AppId, long Hour, long TunIn, long TunOut)> rows)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = Cmd("UPDATE usage SET tun_in=min($ti,net_in), tun_out=min($to,net_out) WHERE app_id=$a AND hour=$h AND source=$s AND network=0",
                ("$a", 0L), ("$h", 0L), ("$s", (long)UsageSource.WindowsHistory), ("$ti", 0L), ("$to", 0L));
            foreach (var (appId, hour, tunIn, tunOut) in rows)
            {
                c["$a"] = appId; c["$h"] = hour; c["$ti"] = tunIn; c["$to"] = tunOut;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void AddHosts(IEnumerable<(long AppId, HostDelta Delta)> rows)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = Cmd("""
                INSERT INTO hosts(app_id,day,ip,network,bytes_in,bytes_out,last_seen) VALUES($a,$d,$ip,$n,$i,$o,$l)
                ON CONFLICT(app_id,day,ip,network) DO UPDATE SET bytes_in=bytes_in+$i, bytes_out=bytes_out+$o, last_seen=max(last_seen,$l)
                """, ("$a", 0L), ("$d", 0L), ("$ip", ""), ("$n", 0L), ("$i", 0L), ("$o", 0L), ("$l", 0L));
            foreach (var (appId, h) in rows)
            {
                c["$a"] = appId; c["$d"] = h.Day; c["$ip"] = h.Ip; c["$n"] = h.Network;
                c["$i"] = h.In; c["$o"] = h.Out; c["$l"] = h.LastSeen;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Per-app totals, per-local-day totals and per-hour totals for [from, to) in unix seconds, on one network or (null)
    /// all of them. Tunnel apps (the VPN's encrypted copy of everyone's traffic) are listed but left out of the totals.
    /// </summary>
    public UsageReport Report(long from, long to, ISet<long> blocked, long? network = null)
    {
        var apps = QueryApps("", from, to, network).Select(a => a with { Blocked = blocked.Contains(a.AppId) }).ToList();
        var hours = new List<HourTotal>();
        lock (_gate)
        {
            using var c = Cmd($"""
                SELECT u.hour, sum(u.net_in), sum(u.net_out) FROM usage u JOIN apps a ON a.id=u.app_id
                WHERE a.is_tunnel=0 AND u.hour>=$f AND u.hour<$t AND {OnNetwork("u")} GROUP BY u.hour ORDER BY u.hour
                """, ("$f", from / 3600), ("$t", HourCeil(to)), ("$net", network));
            using var r = c.ExecuteReader();
            while (r.Read()) hours.Add(new HourTotal(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
        }
        var days = hours
            .GroupBy(h => UsageAccumulator.LocalDay(DateTimeOffset.FromUnixTimeSeconds(h.Hour * 3600)))
            .Select(g => new DayTotal(g.Key, g.Sum(h => h.NetIn), g.Sum(h => h.NetOut)))
            .ToList();
        var normal = apps.Where(a => !a.IsTunnel).ToList();
        var tunnel = apps.Where(a => a.IsTunnel).ToList();
        var firstLive = long.TryParse(GetSetting(SettingKeys.FirstEtwHour), out var fh) ? fh * 3600 : 0;
        var (connIn, connOut) = Connection(from, to, network);
        return new UsageReport(apps.Where(a => a.NetIn + a.NetOut + a.LanIn + a.LanOut > 0).ToList(), days, hours,
            normal.Sum(a => a.NetIn), normal.Sum(a => a.NetOut), tunnel.Sum(a => a.NetIn), tunnel.Sum(a => a.NetOut), firstLive, connIn, connOut);
    }

    /// <summary>
    /// What crossed the internet connection in [from, to): every app's internet bytes, the VPN's included, minus what
    /// apps sent through the VPN (the VPN's own bytes already carry those, wrapped). What an internet provider counts,
    /// and what limits are checked against (on every network: <paramref name="network"/> is for showing one). Hours
    /// recorded before VPN traffic was marked fall back to counting the apps only, so nothing is counted twice.
    /// </summary>
    public (long In, long Out) Connection(long from, long to, long? network = null)
    {
        var markedFrom = long.TryParse(GetSetting(SettingKeys.TunnelMarkedFrom), out var m) ? m : long.MaxValue;
        var historySplit = GetSetting(SettingKeys.SrumTunnelSplit) == "done" ? 1L : 0L;
        lock (_gate)
        {
            using var c = Cmd($"""
                SELECT coalesce(sum(u.net_in-u.tun_in),0), coalesce(sum(u.net_out-u.tun_out),0)
                FROM usage u JOIN apps a ON a.id=u.app_id
                WHERE u.hour>=$f AND u.hour<$t AND {OnNetwork("u")}
                  AND NOT (a.is_tunnel=1 AND ((u.source=0 AND u.hour<$marked) OR (u.source=1 AND $split=0)))
                """, ("$f", from / 3600), ("$t", HourCeil(to)), ("$marked", markedFrom), ("$split", historySplit), ("$net", network));
            using var r = c.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
        }
    }

    public long ConnectionTotal(long from, long to) { var (i, o) = Connection(from, to); return i + o; }

    private static long HourCeil(long unix) => (unix + 3599) / 3600;

    /// <summary>Rows of the table <paramref name="alias"/> on the network bound to $net, or on any when it's null.</summary>
    private static string OnNetwork(string alias) => $"($net IS NULL OR {alias}.network=$net)";

    private List<AppUsage> QueryApps(string where, long from, long to, long? network, params (string, object?)[] extra)
    {
        lock (_gate)
        {
            // to == 0 means "all time"
            var args = new List<(string, object?)> { ("$f", from / 3600), ("$t", to == 0 ? long.MaxValue : HourCeil(to)), ("$net", network) };
            args.AddRange(extra);
            using var c = Cmd($"""
                SELECT a.id, a.name, a.path, a.service, a.is_tunnel, a.first_seen,
                       coalesce(sum(u.net_in),0), coalesce(sum(u.net_out),0), coalesce(sum(u.lan_in),0), coalesce(sum(u.lan_out),0)
                FROM apps a LEFT JOIN usage u ON u.app_id=a.id AND u.hour>=$f AND u.hour<$t AND {OnNetwork("u")}
                {where}
                GROUP BY a.id
                """, args.ToArray());
            using var r = c.ExecuteReader();
            var list = new List<AppUsage>();
            while (r.Read())
                list.Add(new AppUsage(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetInt64(4) == 1, false, r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(5)));
            return list.OrderByDescending(a => a.NetIn + a.NetOut).ThenByDescending(a => a.LanIn + a.LanOut).ToList();
        }
    }

    // ---------- networks ----------

    /// <summary>
    /// Returns the id of the Windows network <paramref name="key"/> (its profile's id), adding it the first time it's seen.
    /// Windows' name for it is kept up to date; a name you gave it stays.
    /// </summary>
    public long EnsureNetwork(string key, string name, string? kind, long firstSeen, long lastSeen)
    {
        lock (_gate)
        {
            using var c = Cmd("""
                INSERT INTO networks(key,name,kind,first_seen,last_seen) VALUES($k,$n,$kind,$f,$l)
                ON CONFLICT(key) DO UPDATE SET name=$n, kind=coalesce($kind,kind), first_seen=min(first_seen,$f), last_seen=max(last_seen,$l)
                RETURNING id
                """, ("$k", key), ("$n", name), ("$kind", kind), ("$f", firstSeen), ("$l", lastSeen));
            return (long)c.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Every network this PC has been on, the latest first, and the unknown one (<see cref="NetworkLabel.Unknown"/>) last
    /// while any use is on it. <paramref name="current"/> is the one it's on now.
    /// </summary>
    public List<NetworkInfo> Networks(long current)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT id,name,custom_name,kind,first_seen,last_seen FROM networks ORDER BY last_seen DESC, id");
            var list = new List<NetworkInfo>();
            using (var r = c.ExecuteReader())
                while (r.Read())
                    list.Add(new NetworkInfo(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                        r.GetInt64(4), r.GetInt64(5), r.GetInt64(0) == current));
            using var unknown = Cmd("SELECT 1 FROM usage WHERE network=0 LIMIT 1");
            if (unknown.ExecuteScalar() is not null) list.Add(new NetworkInfo(NetworkLabel.Unknown, "", null, null, 0, 0, current == NetworkLabel.Unknown));
            return list;
        }
    }

    /// <summary>Your own name for a network; null or blank goes back to Windows' name. False when the network is unknown.</summary>
    public bool RenameNetwork(long id, string? name)
    {
        lock (_gate)
        {
            using var c = Cmd("UPDATE networks SET custom_name=$n WHERE id=$i", ("$i", id), ("$n", string.IsNullOrWhiteSpace(name) ? null : name.Trim()));
            return c.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Gives the network listed as <paramref name="oldKey"/> the key <paramref name="key"/> and Windows' name
    /// <paramref name="name"/>. When <paramref name="key"/> is listed already, the two are one network from now on: the
    /// old one's use and hosts join it, keeping every total, and a name you gave either stays. False when
    /// <paramref name="oldKey"/> isn't listed.
    /// </summary>
    public bool Rekey(string oldKey, string key, string name)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var find = Cmd("SELECT id FROM networks WHERE key=$k", ("$k", oldKey));
            if (find.ExecuteScalar() is not long from) return false;
            using var target = Cmd("SELECT id FROM networks WHERE key=$k", ("$k", key));
            if (target.ExecuteScalar() is long into)
            {
                foreach (var sql in new[]
                {
                    """
                    INSERT INTO usage(app_id,hour,source,network,net_in,net_out,lan_in,lan_out,tun_in,tun_out)
                    SELECT app_id,hour,source,$into,net_in,net_out,lan_in,lan_out,tun_in,tun_out FROM usage WHERE network=$from
                    ON CONFLICT(app_id,hour,source,network) DO UPDATE SET net_in=net_in+excluded.net_in, net_out=net_out+excluded.net_out,
                      lan_in=lan_in+excluded.lan_in, lan_out=lan_out+excluded.lan_out, tun_in=tun_in+excluded.tun_in, tun_out=tun_out+excluded.tun_out
                    """,
                    """
                    INSERT INTO hosts(app_id,day,ip,network,bytes_in,bytes_out,last_seen)
                    SELECT app_id,day,ip,$into,bytes_in,bytes_out,last_seen FROM hosts WHERE network=$from
                    ON CONFLICT(app_id,day,ip,network) DO UPDATE SET bytes_in=bytes_in+excluded.bytes_in, bytes_out=bytes_out+excluded.bytes_out,
                      last_seen=max(last_seen,excluded.last_seen)
                    """,
                    """
                    UPDATE networks SET custom_name=coalesce(custom_name,(SELECT custom_name FROM networks WHERE id=$from)),
                      first_seen=min(first_seen,(SELECT first_seen FROM networks WHERE id=$from)),
                      last_seen=max(last_seen,(SELECT last_seen FROM networks WHERE id=$from))
                    WHERE id=$into
                    """,
                    "DELETE FROM usage WHERE network=$from",
                    "DELETE FROM hosts WHERE network=$from",
                    "DELETE FROM networks WHERE id=$from",
                })
                {
                    // Each statement binds the ids it names.
                    using var c = Cmd(sql, new (string, object?)[] { ("$from", from), ("$into", into) }.Where(p => sql.Contains(p.Item1)).ToArray());
                    c.ExecuteNonQuery();
                }
                from = into;
            }
            using var rename = Cmd("UPDATE networks SET key=$k, name=$n WHERE id=$i", ("$k", key), ("$n", name), ("$i", from));
            rename.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// What each app's imported hours already hold on each known network: a later pass over Windows' history puts only
    /// the rest there.
    /// </summary>
    public Dictionary<(long AppId, long Hour, long Network), HistoryTotal> ImportedOnNetworks()
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT app_id,hour,network,net_in,net_out,tun_in,tun_out FROM usage WHERE source=$s AND network<>0",
                ("$s", (long)UsageSource.WindowsHistory));
            using var r = c.ExecuteReader();
            var map = new Dictionary<(long, long, long), HistoryTotal>();
            while (r.Read()) map[(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2))] = new HistoryTotal(r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6));
            return map;
        }
    }

    /// <summary>Each app and hour holding live use on no known network.</summary>
    public List<(long AppId, long Hour)> UnknownLive()
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT app_id, hour FROM usage WHERE source=$s AND network=0", ("$s", (long)UsageSource.Live));
            using var r = c.ExecuteReader();
            var list = new List<(long, long)>();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1)));
            return list;
        }
    }

    /// <summary>
    /// Puts use recorded on no known network on the network it turned out to be on, for the one pass over what was
    /// recorded before networks were. Each move takes at most what the unknown row holds and adds exactly that to the
    /// network's row, so every total for all networks together stays as it was. LAN bytes (only live rows have them)
    /// go with the row. Returns how many rows moved.
    /// </summary>
    public int AssignNetworks(IEnumerable<NetworkMove> moves)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var find = Cmd("SELECT net_in,net_out,lan_in,lan_out,tun_in,tun_out FROM usage WHERE app_id=$a AND hour=$h AND source=$s AND network=0",
                ("$a", 0L), ("$h", 0L), ("$s", 0L));
            using var take = Cmd("""
                UPDATE usage SET net_in=net_in-$ni, net_out=net_out-$no, lan_in=lan_in-$li, lan_out=lan_out-$lo, tun_in=tun_in-$ti, tun_out=tun_out-$to
                WHERE app_id=$a AND hour=$h AND source=$s AND network=0
                """, ("$a", 0L), ("$h", 0L), ("$s", 0L), ("$ni", 0L), ("$no", 0L), ("$li", 0L), ("$lo", 0L), ("$ti", 0L), ("$to", 0L));
            using var empty = Cmd("""
                DELETE FROM usage WHERE app_id=$a AND hour=$h AND source=$s AND network=0
                  AND net_in=0 AND net_out=0 AND lan_in=0 AND lan_out=0 AND tun_in=0 AND tun_out=0
                """, ("$a", 0L), ("$h", 0L), ("$s", 0L));
            using var addLive = AddUsageCommand(UsageSource.Live);
            using var addHistory = AddUsageCommand(UsageSource.WindowsHistory);
            var moved = 0;
            foreach (var m in moves)
            {
                if (m.Network == NetworkLabel.Unknown) continue;
                find["$a"] = m.AppId; find["$h"] = m.Hour; find["$s"] = (long)m.Source;
                long[] row;
                using (var r = find.ExecuteReader())
                {
                    if (!r.Read()) continue;
                    row = [r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)];
                }
                long[] most = [m.NetIn, m.NetOut, long.MaxValue, long.MaxValue, m.TunIn, m.TunOut];
                var part = row.Zip(most, (have, cap) => Math.Max(0, Math.Min(have, cap))).ToArray();
                if (part.All(v => v == 0)) continue;
                // What went through a VPN is part of the internet bytes, on both sides of the move.
                part[4] = WithinTunnel(part[4], row[4], row[0], part[0]);
                part[5] = WithinTunnel(part[5], row[5], row[1], part[1]);
                var add = m.Source == UsageSource.Live ? addLive : addHistory;
                add["$a"] = m.AppId; add["$h"] = m.Hour; add["$n"] = m.Network;
                add["$ni"] = part[0]; add["$no"] = part[1]; add["$li"] = part[2]; add["$lo"] = part[3]; add["$ti"] = part[4]; add["$to"] = part[5];
                add.ExecuteNonQuery();
                take["$a"] = m.AppId; take["$h"] = m.Hour; take["$s"] = (long)m.Source;
                take["$ni"] = part[0]; take["$no"] = part[1]; take["$li"] = part[2]; take["$lo"] = part[3]; take["$ti"] = part[4]; take["$to"] = part[5];
                take.ExecuteNonQuery();
                empty["$a"] = m.AppId; empty["$h"] = m.Hour; empty["$s"] = (long)m.Source;
                empty.ExecuteNonQuery();
                moved++;
            }
            tx.Commit();
            return moved;
        }
    }

    /// <summary>
    /// How much VPN traffic a move takes: as asked, but no more than the internet bytes it takes, and enough that what
    /// stays behind holds no more VPN traffic than internet bytes either.
    /// </summary>
    private static long WithinTunnel(long wanted, long tunnel, long internet, long taken)
    {
        var most = Math.Min(tunnel, taken);
        var least = Math.Min(most, Math.Max(0, tunnel - (internet - taken)));
        return Math.Clamp(wanted, least, most);
    }

    /// <summary>Puts the hosts of each local day, recorded on no known network, on the network the day was spent on.</summary>
    public void AssignHostNetworks(IEnumerable<(int Day, long Network)> days)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var copy = Cmd("""
                INSERT INTO hosts(app_id,day,ip,network,bytes_in,bytes_out,last_seen)
                SELECT app_id,day,ip,$n,bytes_in,bytes_out,last_seen FROM hosts WHERE day=$d AND network=0
                ON CONFLICT(app_id,day,ip,network) DO UPDATE SET bytes_in=bytes_in+excluded.bytes_in, bytes_out=bytes_out+excluded.bytes_out,
                  last_seen=max(last_seen,excluded.last_seen)
                """, ("$d", 0L), ("$n", 0L));
            using var clear = Cmd("DELETE FROM hosts WHERE day=$d AND network=0", ("$d", 0L));
            foreach (var (day, network) in days)
            {
                if (network == NetworkLabel.Unknown) continue;
                copy["$d"] = day; copy["$n"] = network;
                copy.ExecuteNonQuery();
                clear["$d"] = day;
                clear.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------- connections ----------

    /// <summary>Who an app talked to over local days [fromDay, toDay], on one network or (null) all of them.</summary>
    public List<HostUsage> Hosts(long appId, int fromDay, int toDay, long? network = null)
    {
        lock (_gate)
        {
            using var c = Cmd($"""
                SELECT h.ip, d.name, sum(h.bytes_in), sum(h.bytes_out), max(h.last_seen)
                FROM hosts h LEFT JOIN dns d ON d.ip=h.ip
                WHERE h.app_id=$a AND h.day>=$f AND h.day<=$t AND {OnNetwork("h")}
                GROUP BY h.ip ORDER BY sum(h.bytes_in)+sum(h.bytes_out) DESC LIMIT 500
                """, ("$a", appId), ("$f", fromDay), ("$t", toDay), ("$net", network));
            using var r = c.ExecuteReader();
            var list = new List<HostUsage>();
            while (r.Read()) list.Add(new HostUsage(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
            return list;
        }
    }

    public void SaveDns(IEnumerable<(string Ip, string Name)> entries, long now)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = Cmd("INSERT INTO dns(ip,name,seen) VALUES($ip,$n,$s) ON CONFLICT(ip) DO UPDATE SET name=$n, seen=$s",
                ("$ip", ""), ("$n", ""), ("$s", now));
            foreach (var (ip, name) in entries) { c["$ip"] = ip; c["$n"] = name; c.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    // ---------- devices ----------

    /// <summary>
    /// Upserts a device; returns true when the MAC address was never seen before. One you told it to forget comes back
    /// into the list when it's seen again, but doesn't count as new. When a scan finds it's another kind of device than
    /// it was taken for (a TV, not a phone), the system it was said to run goes too.
    /// </summary>
    public bool SaveDevice(DeviceInfo d)
    {
        lock (_gate)
        {
            using var find = Cmd("SELECT 1 FROM devices WHERE mac=$m UNION ALL SELECT 1 FROM forgotten_devices WHERE mac=$m", ("$m", d.Mac));
            var isNew = find.ExecuteScalar() is null;
            using var back = Cmd("DELETE FROM forgotten_devices WHERE mac=$m", ("$m", d.Mac));
            back.ExecuteNonQuery();
            using var c = Cmd("""
                INSERT INTO devices(mac,ip,name,randomized,is_gateway,is_this_pc,first_seen,last_seen,vendor,model,kind,os,network)
                VALUES($m,$ip,$n,$r,$g,$p,$f,$l,$v,$md,$k,$o,$net)
                ON CONFLICT(mac) DO UPDATE SET ip=$ip, name=coalesce($n,name), randomized=$r, is_gateway=$g, is_this_pc=$p, last_seen=$l,
                  network=coalesce($net,network),
                  vendor=coalesce($v,vendor), model=coalesce($md,model),
                  kind=CASE WHEN $k IS NULL OR $k='unknown' THEN coalesce(kind,$k) ELSE $k END,
                  os=CASE WHEN $k IS NOT NULL AND $k<>'unknown' AND $k IS NOT kind THEN $o ELSE coalesce($o,os) END
                """, ("$m", d.Mac), ("$ip", d.Ip), ("$n", d.Name), ("$r", d.Randomized ? 1 : 0), ("$g", d.IsGateway ? 1 : 0),
                ("$p", d.IsThisPc ? 1 : 0), ("$f", d.FirstSeen), ("$l", d.LastSeen), ("$v", d.Vendor), ("$md", d.Model), ("$k", d.Kind), ("$o", d.Os),
                ("$net", d.Network));
            c.ExecuteNonQuery();
            return isNew;
        }
    }

    public List<DeviceInfo> Devices()
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT mac,ip,name,randomized,is_gateway,is_this_pc,first_seen,last_seen,vendor,model,kind,os,custom_name,away,network FROM devices ORDER BY last_seen DESC, ip");
            using var r = c.ExecuteReader();
            var list = new List<DeviceInfo>();
            while (r.Read())
                list.Add(new DeviceInfo(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) == 1,
                    r.GetInt64(4) == 1, r.GetInt64(5) == 1, r.GetInt64(6), r.GetInt64(7),
                    r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetString(12), r.GetInt64(13) == 1, r.IsDBNull(14) ? null : r.GetString(14)));
            return list;
        }
    }

    /// <summary>Your own name for a device; null or blank goes back to what it calls itself. False when the device is unknown.</summary>
    public bool RenameDevice(string mac, string? name)
    {
        lock (_gate)
        {
            using var c = Cmd("UPDATE devices SET custom_name=$n WHERE mac=$m", ("$m", mac), ("$n", string.IsNullOrWhiteSpace(name) ? null : name.Trim()));
            return c.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Takes a device off the list. It comes back, under its own name, the next time a scan finds it on the network.
    /// False when it wasn't on the list.
    /// </summary>
    public bool ForgetDevice(string mac, long now)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var delete = Cmd("DELETE FROM devices WHERE mac=$m", ("$m", mac));
            var gone = delete.ExecuteNonQuery() > 0;
            if (gone)
            {
                using var remember = Cmd("INSERT OR REPLACE INTO forgotten_devices(mac,at) VALUES($m,$t)", ("$m", mac), ("$t", now));
                remember.ExecuteNonQuery();
            }
            tx.Commit();
            return gone;
        }
    }

    /// <summary>
    /// Folds the device at <paramref name="oldMac"/> into the one at <paramref name="newMac"/>: the same phone after it
    /// changed its private address. Your name for it and when it was first seen carry over.
    /// </summary>
    public void MergeDevice(string oldMac, string newMac)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var move = Cmd("UPDATE devices SET mac=$new WHERE mac=$old AND NOT EXISTS (SELECT 1 FROM devices WHERE mac=$new)", ("$old", oldMac), ("$new", newMac));
            if (move.ExecuteNonQuery() == 0)
            {
                using var carry = Cmd("""
                    UPDATE devices SET
                      custom_name=coalesce(custom_name, (SELECT custom_name FROM devices WHERE mac=$old)),
                      first_seen=min(first_seen, coalesce((SELECT first_seen FROM devices WHERE mac=$old), first_seen))
                    WHERE mac=$new
                    """, ("$old", oldMac), ("$new", newMac));
                carry.ExecuteNonQuery();
                using var drop = Cmd("DELETE FROM devices WHERE mac=$old", ("$old", oldMac));
                drop.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void SetAway(string mac, bool away)
    {
        lock (_gate) { using var c = Cmd("UPDATE devices SET away=$a WHERE mac=$m", ("$m", mac), ("$a", away ? 1 : 0)); c.ExecuteNonQuery(); }
    }

    public const int MaxHints = 5000;

    /// <summary>
    /// Remembers what a device said about itself when it joined or announced itself (its DHCP name, Bonjour names
    /// and services), so a scan minutes later still knows. A device can store at most 64 facts.
    /// </summary>
    public void SaveDeviceHints(string mac, IEnumerable<(string Key, string Value)> hints, long now)
    {
        lock (_gate)
        {
            using var count = Cmd("SELECT count(*) FROM device_hints WHERE mac=$m", ("$m", mac));
            var room = 64 - (long)count.ExecuteScalar()!;
            using var tx = _db.BeginTransaction();
            using var update = Cmd("UPDATE device_hints SET value=$v, seen=$s WHERE mac=$m AND key=$k", ("$m", mac), ("$k", ""), ("$v", ""), ("$s", now));
            using var insert = Cmd("INSERT OR IGNORE INTO device_hints(mac,key,value,seen) VALUES($m,$k,$v,$s)", ("$m", mac), ("$k", ""), ("$v", ""), ("$s", now));
            foreach (var (key, value) in hints)
            {
                if (key.Length > 200 || value.Length > 200) continue;
                update["$k"] = key; update["$v"] = value;
                if (update.ExecuteNonQuery() > 0 || room <= 0) continue;
                insert["$k"] = key; insert["$v"] = value;
                room -= insert.ExecuteNonQuery();
            }
            // However many hardware addresses something on the network makes up, the table stays small.
            using var trim = Cmd("""
                DELETE FROM device_hints WHERE (mac, key) IN (
                  SELECT mac, key FROM device_hints ORDER BY seen LIMIT max(0, (SELECT count(*) FROM device_hints) - $max))
                """, ("$max", MaxHints));
            trim.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public Dictionary<string, List<(string Key, string Value)>> DeviceHints()
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT mac,key,value FROM device_hints ORDER BY seen DESC");
            using var r = c.ExecuteReader();
            var map = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                if (!map.TryGetValue(r.GetString(0), out var list)) map[r.GetString(0)] = list = [];
                list.Add((r.GetString(1), r.GetString(2)));
            }
            return map;
        }
    }

    /// <summary>
    /// Forgets hints for hardware that never showed up as a device within a day, and anything a month old. A device you
    /// took off the list a month ago and that hasn't been back counts as new if it ever is.
    /// </summary>
    public void PruneDeviceHints(long now)
    {
        lock (_gate)
        {
            using var c = Cmd("""
                DELETE FROM device_hints WHERE seen < $month
                   OR (seen < $day AND mac NOT IN (SELECT mac FROM devices))
                """, ("$month", now - 30 * 86400), ("$day", now - 86400));
            c.ExecuteNonQuery();
            using var forgotten = Cmd("DELETE FROM forgotten_devices WHERE at < $month", ("$month", now - 30 * 86400));
            forgotten.ExecuteNonQuery();
        }
    }

    // ---------- alerts ----------

    public long AddAlert(string kind, string title, string body, long now)
    {
        lock (_gate)
        {
            using var c = Cmd("INSERT INTO alerts(time,kind,title,body) VALUES($t,$k,$ti,$b) RETURNING id", ("$t", now), ("$k", kind), ("$ti", title), ("$b", body));
            return (long)c.ExecuteScalar()!;
        }
    }

    /// <summary>Newest first; only alerts with id greater than afterId.</summary>
    public List<AlertInfo> Alerts(long afterId, int limit = 200)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT id,time,kind,title,body FROM alerts WHERE id>$a ORDER BY id DESC LIMIT $l", ("$a", afterId), ("$l", limit));
            using var r = c.ExecuteReader();
            var list = new List<AlertInfo>();
            while (r.Read()) list.Add(new AlertInfo(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            return list;
        }
    }

    // ---------- settings ----------

    public string? GetSetting(string key)
    {
        lock (_gate) { using var c = Cmd("SELECT value FROM settings WHERE key=$k", ("$k", key)); return c.ExecuteScalar() as string; }
    }

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var c = Cmd("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));
            c.ExecuteNonQuery();
        }
    }

    public Dictionary<string, string> AllSettings()
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT key,value FROM settings");
            using var r = c.ExecuteReader();
            var map = new Dictionary<string, string>();
            while (r.Read()) map[r.GetString(0)] = r.GetString(1);
            return map;
        }
    }

    // ---------- plumbing ----------

    private void Exec(string sql) => _db.Exec(sql);

    private SqliteStatement Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var c = _db.Prepare(sql);
        try { foreach (var (name, value) in args) c.Bind(name, value); }
        catch { c.Dispose(); throw; }
        return c;
    }

    public void Dispose() => _db.Dispose();
}
