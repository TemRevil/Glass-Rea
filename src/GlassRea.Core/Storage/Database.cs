using GlassRea.Core.Protocol;
using GlassRea.Core.Usage;

namespace GlassRea.Core.Storage;

public enum UsageSource { Live = 0, WindowsHistory = 1 }

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
        Exec("""
            CREATE TABLE IF NOT EXISTS apps(
              id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, name TEXT NOT NULL, path TEXT, service TEXT,
              is_tunnel INTEGER NOT NULL DEFAULT 0, first_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS usage(
              app_id INTEGER NOT NULL REFERENCES apps(id), hour INTEGER NOT NULL, source INTEGER NOT NULL,
              net_in INTEGER NOT NULL DEFAULT 0, net_out INTEGER NOT NULL DEFAULT 0,
              lan_in INTEGER NOT NULL DEFAULT 0, lan_out INTEGER NOT NULL DEFAULT 0,
              tun_in INTEGER NOT NULL DEFAULT 0, tun_out INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(app_id, hour, source)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS usage_by_hour ON usage(hour);
            CREATE TABLE IF NOT EXISTS hosts(
              app_id INTEGER NOT NULL REFERENCES apps(id), day INTEGER NOT NULL, ip TEXT NOT NULL,
              bytes_in INTEGER NOT NULL DEFAULT 0, bytes_out INTEGER NOT NULL DEFAULT 0, last_seen INTEGER NOT NULL,
              PRIMARY KEY(app_id, day, ip)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS dns(ip TEXT PRIMARY KEY, name TEXT NOT NULL, seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS devices(
              mac TEXT PRIMARY KEY, ip TEXT NOT NULL, name TEXT, randomized INTEGER NOT NULL,
              is_gateway INTEGER NOT NULL, is_this_pc INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
              vendor TEXT, model TEXT, kind TEXT, os TEXT, custom_name TEXT, away INTEGER NOT NULL DEFAULT 0, network TEXT);
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
    }

    private void AddMissing(string table, params (string Name, string Type)[] wanted)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = Cmd($"PRAGMA table_info({table})"))
        using (var r = info.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));
        foreach (var (name, type) in wanted)
            if (!columns.Contains(name)) Exec($"ALTER TABLE {table} ADD COLUMN {name} {type}");
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

    public AppUsage? GetApp(long appId) => QueryApps("WHERE a.id=$i", 0, 0, ("$i", appId)).FirstOrDefault();

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

    // ---------- usage ----------

    public void AddUsage(IEnumerable<(long AppId, UsageDelta Delta)> rows, UsageSource source)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = Cmd("""
                INSERT INTO usage(app_id,hour,source,net_in,net_out,lan_in,lan_out,tun_in,tun_out) VALUES($a,$h,$s,$ni,$no,$li,$lo,$ti,$to)
                ON CONFLICT(app_id,hour,source) DO UPDATE SET net_in=net_in+$ni, net_out=net_out+$no, lan_in=lan_in+$li, lan_out=lan_out+$lo,
                  tun_in=tun_in+$ti, tun_out=tun_out+$to
                """, ("$a", 0L), ("$h", 0L), ("$s", (long)source), ("$ni", 0L), ("$no", 0L), ("$li", 0L), ("$lo", 0L), ("$ti", 0L), ("$to", 0L));
            foreach (var (appId, d) in rows)
            {
                c["$a"] = appId; c["$h"] = d.Hour;
                c["$ni"] = d.NetIn; c["$no"] = d.NetOut;
                c["$li"] = d.LanIn; c["$lo"] = d.LanOut;
                c["$ti"] = d.TunIn; c["$to"] = d.TunOut;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Sets how much of already imported Windows history went through a VPN adapter (a one-time pass).</summary>
    public void SetImportedTunnel(IEnumerable<(long AppId, long Hour, long TunIn, long TunOut)> rows)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = Cmd("UPDATE usage SET tun_in=min($ti,net_in), tun_out=min($to,net_out) WHERE app_id=$a AND hour=$h AND source=$s",
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
                INSERT INTO hosts(app_id,day,ip,bytes_in,bytes_out,last_seen) VALUES($a,$d,$ip,$i,$o,$l)
                ON CONFLICT(app_id,day,ip) DO UPDATE SET bytes_in=bytes_in+$i, bytes_out=bytes_out+$o, last_seen=max(last_seen,$l)
                """, ("$a", 0L), ("$d", 0L), ("$ip", ""), ("$i", 0L), ("$o", 0L), ("$l", 0L));
            foreach (var (appId, h) in rows)
            {
                c["$a"] = appId; c["$d"] = h.Day; c["$ip"] = h.Ip;
                c["$i"] = h.In; c["$o"] = h.Out; c["$l"] = h.LastSeen;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Per-app totals, per-local-day totals and per-hour totals for [from, to) in unix seconds.
    /// Tunnel apps (the VPN's encrypted copy of everyone's traffic) are listed but left out of the totals.
    /// </summary>
    public UsageReport Report(long from, long to, ISet<long> blocked)
    {
        var apps = QueryApps("", from, to).Select(a => a with { Blocked = blocked.Contains(a.AppId) }).ToList();
        var hours = new List<HourTotal>();
        lock (_gate)
        {
            using var c = Cmd("""
                SELECT u.hour, sum(u.net_in), sum(u.net_out) FROM usage u JOIN apps a ON a.id=u.app_id
                WHERE a.is_tunnel=0 AND u.hour>=$f AND u.hour<$t GROUP BY u.hour ORDER BY u.hour
                """, ("$f", from / 3600), ("$t", HourCeil(to)));
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
        var (connIn, connOut) = Connection(from, to);
        return new UsageReport(apps.Where(a => a.NetIn + a.NetOut + a.LanIn + a.LanOut > 0).ToList(), days, hours,
            normal.Sum(a => a.NetIn), normal.Sum(a => a.NetOut), tunnel.Sum(a => a.NetIn), tunnel.Sum(a => a.NetOut), firstLive, connIn, connOut);
    }

    /// <summary>
    /// What crossed the internet connection in [from, to): every app's internet bytes, the VPN's included, minus what
    /// apps sent through the VPN (the VPN's own bytes already carry those, wrapped). What an internet provider counts,
    /// and what limits are checked against. Hours recorded before VPN traffic was marked fall back to counting the
    /// apps only, so nothing is counted twice.
    /// </summary>
    public (long In, long Out) Connection(long from, long to)
    {
        var markedFrom = long.TryParse(GetSetting(SettingKeys.TunnelMarkedFrom), out var m) ? m : long.MaxValue;
        var historySplit = GetSetting(SettingKeys.SrumTunnelSplit) == "done" ? 1L : 0L;
        lock (_gate)
        {
            using var c = Cmd("""
                SELECT coalesce(sum(u.net_in-u.tun_in),0), coalesce(sum(u.net_out-u.tun_out),0)
                FROM usage u JOIN apps a ON a.id=u.app_id
                WHERE u.hour>=$f AND u.hour<$t
                  AND NOT (a.is_tunnel=1 AND ((u.source=0 AND u.hour<$marked) OR (u.source=1 AND $split=0)))
                """, ("$f", from / 3600), ("$t", HourCeil(to)), ("$marked", markedFrom), ("$split", historySplit));
            using var r = c.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
        }
    }

    public long ConnectionTotal(long from, long to) { var (i, o) = Connection(from, to); return i + o; }

    private static long HourCeil(long unix) => (unix + 3599) / 3600;

    private List<AppUsage> QueryApps(string where, long from, long to, params (string, object?)[] extra)
    {
        lock (_gate)
        {
            // to == 0 means "all time"
            var args = new List<(string, object?)> { ("$f", from / 3600), ("$t", to == 0 ? long.MaxValue : HourCeil(to)) };
            args.AddRange(extra);
            using var c = Cmd($"""
                SELECT a.id, a.name, a.path, a.service, a.is_tunnel, a.first_seen,
                       coalesce(sum(u.net_in),0), coalesce(sum(u.net_out),0), coalesce(sum(u.lan_in),0), coalesce(sum(u.lan_out),0)
                FROM apps a LEFT JOIN usage u ON u.app_id=a.id AND u.hour>=$f AND u.hour<$t
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

    // ---------- connections ----------

    public List<HostUsage> Hosts(long appId, int fromDay, int toDay)
    {
        lock (_gate)
        {
            using var c = Cmd("""
                SELECT h.ip, d.name, sum(h.bytes_in), sum(h.bytes_out), max(h.last_seen)
                FROM hosts h LEFT JOIN dns d ON d.ip=h.ip
                WHERE h.app_id=$a AND h.day>=$f AND h.day<=$t
                GROUP BY h.ip ORDER BY sum(h.bytes_in)+sum(h.bytes_out) DESC LIMIT 500
                """, ("$a", appId), ("$f", fromDay), ("$t", toDay));
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

    /// <summary>Upserts a device; returns true when the MAC address was never seen before.</summary>
    public bool SaveDevice(DeviceInfo d)
    {
        lock (_gate)
        {
            using var find = Cmd("SELECT 1 FROM devices WHERE mac=$m", ("$m", d.Mac));
            var isNew = find.ExecuteScalar() is null;
            using var c = Cmd("""
                INSERT INTO devices(mac,ip,name,randomized,is_gateway,is_this_pc,first_seen,last_seen,vendor,model,kind,os,network)
                VALUES($m,$ip,$n,$r,$g,$p,$f,$l,$v,$md,$k,$o,$net)
                ON CONFLICT(mac) DO UPDATE SET ip=$ip, name=coalesce($n,name), randomized=$r, is_gateway=$g, is_this_pc=$p, last_seen=$l,
                  network=coalesce($net,network),
                  vendor=coalesce($v,vendor), model=coalesce($md,model),
                  kind=CASE WHEN $k IS NULL OR $k='unknown' THEN coalesce(kind,$k) ELSE $k END, os=coalesce($o,os)
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

    /// <summary>
    /// Remembers what a device said about itself when it joined or announced itself (its DHCP name, Bonjour names
    /// and services), so a scan minutes later still knows. A device can store at most 64 facts.
    /// </summary>
    /// <summary>Your own name for a device; null or blank goes back to what it calls itself. False when the device is unknown.</summary>
    public bool RenameDevice(string mac, string? name)
    {
        lock (_gate)
        {
            using var c = Cmd("UPDATE devices SET custom_name=$n WHERE mac=$m", ("$m", mac), ("$n", string.IsNullOrWhiteSpace(name) ? null : name.Trim()));
            return c.ExecuteNonQuery() > 0;
        }
    }

    public void SetAway(string mac, bool away)
    {
        lock (_gate) { using var c = Cmd("UPDATE devices SET away=$a WHERE mac=$m", ("$m", mac), ("$a", away ? 1 : 0)); c.ExecuteNonQuery(); }
    }

    public const int MaxHints = 5000;

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

    /// <summary>Forgets hints for hardware that never showed up as a device within a day, and anything a month old.</summary>
    public void PruneDeviceHints(long now)
    {
        lock (_gate)
        {
            using var c = Cmd("""
                DELETE FROM device_hints WHERE seen < $month
                   OR (seen < $day AND mac NOT IN (SELECT mac FROM devices))
                """, ("$month", now - 30 * 86400), ("$day", now - 86400));
            c.ExecuteNonQuery();
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
