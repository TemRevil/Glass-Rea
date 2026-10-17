namespace GlassRea.Core.Alerts;

/// <summary>
/// The words of every alert. The title says what happened in a few words; the body says it plainly and, where it
/// helps, what you can do. The same text shows as the Windows notification and on the Alerts page.
/// </summary>
public static class AlertText
{
    public static (string Title, string Body) NewApp(string? name, string? path)
    {
        var title = string.IsNullOrWhiteSpace(name) ? "An unnamed program went online" : $"{name.Trim()} went online";
        var body = "This is the first time it has connected to the internet. If you don't recognise it, you can block it on the Firewall page.";
        return (title, string.IsNullOrWhiteSpace(path) ? body : $"{body}\n{path}");
    }

    public static (string Title, string Body) NewDevice(string? name, string ip, string mac) => (
        string.IsNullOrWhiteSpace(name) ? "A new device joined your network" : $"{name.Trim()} joined your network",
        $"Seen for the first time at {ip}, hardware address {mac}. If it isn't yours, someone else may be using your network.");

    /// <param name="period">"Daily" or "Monthly".</param>
    /// <param name="monthDay">The day of the month the monthly count starts again (Settings > Data).</param>
    public static (string Title, string Body) Limit(string period, int percent, long used, long limit, int monthDay = 1)
    {
        var monthly = period.Equals("Monthly", StringComparison.OrdinalIgnoreCase);
        var name = monthly ? "monthly" : "daily";
        var when = monthly ? "this month" : "today";
        var restart = !monthly ? "The count starts again at midnight."
            : monthDay > 28 ? $"The count starts again on the {Usage.Cycle.Ordinal(monthDay)}, or the last day of a shorter month."
            : $"The count starts again on the {Usage.Cycle.Ordinal(monthDay)}.";
        var amount = $"{Bytes.Format(used)} of {Bytes.Format(limit)} used {when}.";
        return percent >= 100
            ? ($"{(monthly ? "Monthly" : "Daily")} limit reached", $"{amount} Glass-Rea doesn't cut your connection. {restart}")
            : ($"{percent}% of your {name} limit used", $"{amount} {restart}");
    }
}
