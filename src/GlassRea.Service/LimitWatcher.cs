using System.Globalization;
using GlassRea.Core.Alerts;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;

namespace GlassRea.Service;

/// <summary>Checks today's and this month's internet use against the limits once a minute.</summary>
public sealed class LimitWatcher(Database db, ServiceState state, ILogger<LimitWatcher> log) : BackgroundService
{
    private const string FiredKey = "limit_alerts_fired";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { Check(DateTimeOffset.Now); }
            catch (Exception ex) { log.LogError(ex, "Limit check failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    public void Check(DateTimeOffset now)
    {
        var day = new DateTimeOffset(now.Date, now.Offset);
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var to = now.ToUnixTimeSeconds() + 1;
        var dayKey = "d" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var monthKey = "m" + now.ToString("yyyyMM", CultureInfo.InvariantCulture);

        // Only keep keys for the current periods so the stored set never grows.
        var fired = (db.GetSetting(FiredKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(k => k.StartsWith(dayKey) || k.StartsWith(monthKey)).ToHashSet();

        var crossings = LimitTracker.Check(dayKey, "Daily", db.InternetTotal(day.ToUnixTimeSeconds(), to), state.Number(SettingKeys.DailyLimit), fired)
            .Concat(LimitTracker.Check(monthKey, "Monthly", db.InternetTotal(month.ToUnixTimeSeconds(), to), state.Number(SettingKeys.MonthlyLimit), fired))
            .ToList();
        foreach (var c in crossings) db.AddAlert("limit", c.Title, c.Body, now.ToUnixTimeSeconds());
        db.SetSetting(FiredKey, string.Join(",", fired));
    }
}
