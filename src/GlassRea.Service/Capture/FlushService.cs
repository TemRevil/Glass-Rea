using GlassRea.Core.Alerts;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;

namespace GlassRea.Service.Capture;

/// <summary>Writes collected traffic to the database every few seconds and raises "new app" alerts.</summary>
public sealed class FlushService(UsageAccumulator usage, ProcessResolver resolver, DnsCache dns, Database db, ServiceState state,
    ILogger<FlushService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try { while (await timer.WaitForNextTickAsync(ct)) Flush(); }
        catch (OperationCanceledException) { }
        Flush(); // keep the last seconds on shutdown
    }

    public void Flush()
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var (rows, hosts) = usage.Drain();
            var ids = new Dictionary<string, long>();
            long Id(string key)
            {
                if (ids.TryGetValue(key, out var id)) return id;
                var app = resolver.ByKey(key) ?? new AppIdentity(key, key, null, null, false);
                id = db.EnsureApp(app, now, out var isNew);
                if (isNew) OnNewApp(app, now);
                return ids[key] = id;
            }
            if (rows.Count > 0) db.AddUsage(rows.Select(r => (Id(r.AppKey), r)).ToList(), UsageSource.Live);
            if (hosts.Count > 0) db.AddHosts(hosts.Select(h => (Id(h.AppKey), h)).ToList());
            var names = dns.Drain();
            if (names.Count > 0) db.SaveDns(names, now);
        }
        catch (Exception ex) { log.LogError(ex, "Could not save traffic"); }
    }

    private void OnNewApp(AppIdentity app, long now)
    {
        if (!Settled(state.CaptureStartedAt, now) || !state.Flag(SettingKeys.AlertNewApp) || app == AppIdentity.System || app == AppIdentity.Unknown) return;
        var (title, body) = AlertText.NewApp(app.Name, app.Path ?? app.Key);
        db.AddAlert("new_app", title, body, now);
    }

    /// <summary>
    /// During the first hour live capture runs every app is "new"; alerting then would only be noise. Counted from
    /// capture's own first hour, which can be long after install when Usage per app was turned on later.
    /// </summary>
    public static bool Settled(long captureStartedAt, long now) => captureStartedAt > 0 && now - captureStartedAt >= 3600;
}
