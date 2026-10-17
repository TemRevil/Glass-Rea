using GlassRea.App.Support;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;

namespace GlassRea.App.Pages;

/// <summary>An app in a usage list: every row of it added together (see <see cref="AppFaces.Merge"/>).</summary>
public sealed record AppRow(long Id, string Name, string? Icon, string Glyph, string Tag, string Sub, string Down, string Up, string Total, double Share, bool IsTunnel)
{
    public Visibility TagShown => Tag.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public double Dim => IsTunnel ? 0.7 : 1;

    /// <summary>Rows for a report: counted apps by size, then VPN tunnels (shown, never added to totals).</summary>
    public static async Task<List<AppRow>> From(UsageReport r)
    {
        await AppFaces.Prepare(r.Apps);
        var apps = AppFaces.Merge(r.Apps).Select(m => m.App).Where(a => a.NetIn + a.NetOut > 0).ToList();
        var max = Math.Max(1, apps.Count == 0 ? 1 : apps.Max(a => a.NetIn + a.NetOut));
        return apps.Where(a => !a.IsTunnel).OrderByDescending(a => a.NetIn + a.NetOut)
            .Concat(apps.Where(a => a.IsTunnel))
            .Select(a =>
            {
                var face = AppFaces.For(a);
                var sub = face.About;
                if (a.Blocked) sub = "Blocked, " + sub;
                if (a.LanIn + a.LanOut > 0 && !a.IsTunnel) sub += $", plus {Fmt.Bytes(a.LanIn + a.LanOut)} on the local network";
                return new AppRow(a.AppId, face.Name, face.Icon, face.Glyph, face.Tag, sub, Fmt.Bytes(a.NetIn), Fmt.Bytes(a.NetOut),
                    Fmt.Bytes(a.NetIn + a.NetOut), (double)(a.NetIn + a.NetOut) / max, a.IsTunnel);
            }).ToList();
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
