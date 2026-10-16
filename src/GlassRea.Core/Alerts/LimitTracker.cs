using System.Globalization;

namespace GlassRea.Core.Alerts;

public sealed record LimitCrossing(string PeriodKey, int Percent, string Title, string Body);

/// <summary>
/// Turns "used so far" against a limit into at most one alert per threshold per period.
/// The caller persists the fired keys, so a restart never repeats an alert.
/// </summary>
public static class LimitTracker
{
    public static readonly int[] Thresholds = [80, 100];

    public static List<LimitCrossing> Check(string period, string periodLabel, long used, long limit, ISet<string> fired)
    {
        var result = new List<LimitCrossing>();
        if (limit <= 0) return result;
        foreach (var t in Thresholds)
        {
            var key = $"{period}:{t}";
            if (used * 100 < limit * t || fired.Contains(key)) continue;
            fired.Add(key);
            var (title, body) = AlertText.Limit(periodLabel, t, used, limit);
            result.Add(new LimitCrossing(key, t, title, body));
        }
        return result;
    }
}

public static class Bytes
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        // Invariant culture: never Arabic-Indic digits or a locale decimal separator.
        return u == 0 ? $"{bytes} B" : $"{v.ToString("0.##", CultureInfo.InvariantCulture)} {units[u]}";
    }
}
