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

    /// <summary>
    /// A device that came back on a new private address under the name of one of yours, and was taken for it. Anyone
    /// can name a device that way, so it's reported like a new device rather than merged in silence.
    /// </summary>
    public static (string Title, string Body) DeviceNewAddress(string name, string ip, string mac) => (
        $"{name.Trim()} is on a new private address",
        $"Now at {ip}, hardware address {mac}. Phones change their private address now and then, so it's shown as the same device. If it isn't yours, Forget it on the Devices page; someone else may be using your network.");

    public static (string Title, string Body) DeviceLeft(string name, string ip, long goneSeconds) => (
        $"{name.Trim()} left your network",
        $"Not seen for {Span(goneSeconds)}. It was at {ip}. You'll be told when it comes back.");

    public static (string Title, string Body) DeviceBack(string name, string ip, long awaySeconds) => (
        $"{name.Trim()} is back on your network",
        $"Away for {Span(awaySeconds)}, now at {ip}.");

    /// <summary>A length of time in words, to the minute under a day and to the hour above: "1 hour 5 minutes", "2 days 3 hours".</summary>
    public static string Span(long seconds)
    {
        static string Unit(long n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
        var minutes = Math.Max(1, seconds / 60);
        if (minutes < 60) return Unit(minutes, "minute");
        var hours = minutes / 60;
        if (hours < 24) return minutes % 60 == 0 ? Unit(hours, "hour") : $"{Unit(hours, "hour")} {Unit(minutes % 60, "minute")}";
        return hours % 24 == 0 ? Unit(hours / 24, "day") : $"{Unit(hours / 24, "day")} {Unit(hours % 24, "hour")}";
    }

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
