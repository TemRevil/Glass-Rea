using GlassRea.App.Support;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;

namespace GlassRea.App.Pages;

/// <summary>An app in a usage list.</summary>
public sealed record AppRow(long Id, string Name, string? Path, string Sub, string Down, string Up, string Total, double Share, bool IsTunnel)
{
    public Visibility TunnelTag => IsTunnel ? Visibility.Visible : Visibility.Collapsed;
    public double Dim => IsTunnel ? 0.7 : 1;

    /// <summary>Rows for a report: counted apps by size, then VPN tunnels (shown, never added to totals).</summary>
    public static List<AppRow> From(UsageReport r)
    {
        var max = Math.Max(1, r.Apps.Count == 0 ? 1 : r.Apps.Max(a => a.NetIn + a.NetOut));
        var rows = new List<AppRow>();
        foreach (var a in r.Apps.Where(a => !a.IsTunnel && a.NetIn + a.NetOut > 0).OrderByDescending(a => a.NetIn + a.NetOut))
        {
            var parts = new List<string>();
            if (a.Blocked) parts.Add("Blocked");
            if (!string.IsNullOrEmpty(a.Path)) parts.Add(a.Path);
            else if (a.Service is not null) parts.Add($"Service: {a.Service}");
            if (a.LanIn + a.LanOut > 0) parts.Add($"{(parts.Count > 0 ? "plus " : "")}{Fmt.Bytes(a.LanIn + a.LanOut)} on the local network");
            var sub = string.Join(", ", parts);
            if (sub.Length > 0) sub = char.ToUpperInvariant(sub[0]) + sub[1..];
            rows.Add(new AppRow(a.AppId, NameOf(a.Name, a.Path), a.Path, sub, Fmt.Bytes(a.NetIn), Fmt.Bytes(a.NetOut), Fmt.Bytes(a.NetIn + a.NetOut),
                (double)(a.NetIn + a.NetOut) / max, false));
        }
        foreach (var a in r.Apps.Where(a => a.IsTunnel && a.NetIn + a.NetOut > 0))
            rows.Add(new AppRow(a.AppId, NameOf(a.Name, a.Path), a.Path, "VPN tunnel: the encrypted copy of the traffic above, so it isn't added to the total",
                Fmt.Bytes(a.NetIn), Fmt.Bytes(a.NetOut), Fmt.Bytes(a.NetIn + a.NetOut), (double)(a.NetIn + a.NetOut) / max, true));
        return rows;
    }

    /// <summary>What to call an app when Windows gave no name: its file name, or a plain description.</summary>
    public static string NameOf(string? name, string? path) =>
        !string.IsNullOrWhiteSpace(name) ? name
        : !string.IsNullOrWhiteSpace(path) ? System.IO.Path.GetFileNameWithoutExtension(path)
        : "Unnamed program";
}

/// <summary>Builds chart bars for a report: hours for a day, days for a week or month.</summary>
public static class Bars
{
    public static List<Controls.Bar> For(UsageReport r, Periods.Kind kind, long from, long to)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (kind == Periods.Kind.Day)
        {
            var byHour = r.Hours.ToDictionary(h => h.Hour);
            return Enumerable.Range(0, 24).Select(i =>
            {
                var hour = from / 3600 + i;
                byHour.TryGetValue(hour, out var h);
                var label = DateTimeOffset.FromUnixTimeSeconds(hour * 3600).ToLocalTime().ToString("HH", inv);
                return new Controls.Bar(label + ":00", h?.NetIn ?? 0, h?.NetOut ?? 0, $"{label}:00 to {label}:59");
            }).ToList();
        }
        var byDay = r.Days.ToDictionary(d => d.Day);
        var start = DateTimeOffset.FromUnixTimeSeconds(from).ToLocalTime().Date;
        var days = (int)Math.Round((DateTimeOffset.FromUnixTimeSeconds(to).ToLocalTime().Date - start).TotalDays);
        return Enumerable.Range(0, days).Select(i =>
        {
            var d = start.AddDays(i);
            byDay.TryGetValue(d.Year * 10000 + d.Month * 100 + d.Day, out var t);
            var label = kind == Periods.Kind.Week ? d.ToString("ddd", inv) : d.ToString("d MMM", inv);
            return new Controls.Bar(label, t?.NetIn ?? 0, t?.NetOut ?? 0, d.ToString("dddd d MMM", inv));
        }).ToList();
    }
}
