using System.Globalization;
using GlassRea.App.Controls;
using GlassRea.App.Support;
using GlassRea.Core.Look;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

/// <summary>Answers "where did my data go" first: one sentence, the chart, the top apps, then now / month / alerts.</summary>
public sealed partial class OverviewPage : UserControl, IPage
{
    private readonly LiveList<AppItem, AppRow> _topApps = new(r => r.Id);
    private readonly PeriodPicker _period = new(Periods.Kind.Day);
    private string _shownKey = "";
    private bool _counted;

    public OverviewPage()
    {
        InitializeComponent();
        PickerHost.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new NetworkPicker(), _period } };
        _period.Changed += () => _ = Refresh();
        Networks.Changed += () => { if (IsLoaded) _ = Refresh(); };
    }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(10);

    public async Task Refresh()
    {
        var from = _period.From;
        var to = Math.Min(_period.To, DateTimeOffset.Now.ToUnixTimeSeconds() + 1);
        var network = Networks.Selected;
        var r = await Service.Call<UsageReport>("usage", from, to, network: network);
        var total = r.NetIn + r.NetOut;
        var phrase = Phrase() + Networks.On;
        PeriodText.Text = _period.Label;

        var appRows = await AppRow.From(r);
        var top = AppFaces.Merge(r.Apps).Select(m => m.App).Where(a => !a.IsTunnel).MaxBy(a => a.NetIn + a.NetOut);
        if (total == 0)
        {
            HeadA.Text = $"Nothing has used the internet {phrase} yet.";
            HeadTotal.Text = HeadB.Text = "";
        }
        else
        {
            HeadA.Text = "You used ";
            HeadB.Text = $" {phrase}." + (top is null ? "" : $" {AppFaces.For(top).Name} took {Fmt.Percent((double)(top.NetIn + top.NetOut) / total)} of it.");
            if (!_counted) { _counted = true; Motion.CountUp(DispatcherQueue, total, Fmt.Bytes, s => HeadTotal.Text = s); }
            else HeadTotal.Text = Fmt.Bytes(total);
        }

        var key = $"{_period.Kind}:{from}:{network}";
        Chart.SetBars(Bars.For(r, _period.Kind, _period.From, _period.To), animate: key != _shownKey);
        _shownKey = key;
        DownText.Text = $"Download {Fmt.Bytes(r.NetIn)}";
        UpText.Text = $"Upload {Fmt.Bytes(r.NetOut)}";
        var tunnel = r.Apps.FirstOrDefault(a => a.IsTunnel && a.NetIn + a.NetOut > 0);
        TunnelText.Text = TunnelLine(r, tunnel);
        if (r.ImportedBefore > from)
        {
            HistoryNote.Text = $"Before {Fmt.DateTime(r.ImportedBefore)} the figures come from the usage history Windows keeps, which records whole hours and counts local network traffic as internet.";
            HistoryNote.Visibility = Visibility.Visible;
        }
        else HistoryNote.Visibility = Visibility.Collapsed;

        TopTitle.Text = _period.Kind switch { Periods.Kind.Day => "Top apps today", Periods.Kind.Week => "Top apps this week", _ => "Top apps this month" };
        if (!IsCurrent()) TopTitle.Text = "Top apps";
        var rows = appRows.Take(7).ToList();
        TopApps.ItemsSource ??= _topApps;
        _topApps.Sync(rows);
        NoApps.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowNow();
        await ShowMonth(r, network);
        await ShowAlerts();
    }

    private bool IsCurrent() => _period.From <= DateTimeOffset.Now.ToUnixTimeSeconds() && DateTimeOffset.Now.ToUnixTimeSeconds() < _period.To;

    private string Phrase()
    {
        var inv = CultureInfo.InvariantCulture;
        var start = DateTimeOffset.FromUnixTimeSeconds(_period.From).ToLocalTime();
        if (IsCurrent()) return _period.Kind switch { Periods.Kind.Day => "today", Periods.Kind.Week => "this week", _ => "this month" };
        return _period.Kind switch
        {
            Periods.Kind.Day => "on " + start.ToString("dddd d MMM", inv),
            Periods.Kind.Week => "in the week of " + start.ToString("d MMM", inv),
            _ => "in " + start.ToString("MMMM yyyy", inv),
        };
    }

    private void ShowNow()
    {
        var live = MainWindow.Live;
        var busy = live?.Top.Where(a => a.InPerSec + a.OutPerSec > 0).ToList() ?? [];
        if (busy.Count == 0)
        {
            NowText.Text = "Nothing is using the internet right now.";
            NowMore.Text = "";
            return;
        }
        var first = busy[0];
        NowText.Text = first.InPerSec >= first.OutPerSec
            ? $"{first.Name} is downloading at {Fmt.Rate(first.InPerSec)}."
            : $"{first.Name} is uploading at {Fmt.Rate(first.OutPerSec)}.";
        NowMore.Text = busy.Count > 1 ? "Also active: " + string.Join(", ", busy.Skip(1).Take(3).Select(a => a.Name)) : "";
    }

    /// <summary>
    /// With a VPN on, what crossed the connection (what the internet provider counts, and what limits use) is more than
    /// what the apps moved: the VPN's headers and encryption ride along.
    /// </summary>
    private static string TunnelLine(UsageReport r, AppUsage? tunnel)
    {
        if (tunnel is null) return "";
        var connection = r.ConnectionIn + r.ConnectionOut;
        var extra = connection - (r.NetIn + r.NetOut);
        return extra > 0
            ? $"Your connection carried {Fmt.Bytes(connection)}, {Fmt.Bytes(extra)} of it {tunnel.Name}'s overhead"
            : $"{tunnel.Name} tunnel {Fmt.Bytes(r.TunnelIn + r.TunnelOut)}, not counted twice";
    }

    /// <summary>This month on every network, whichever one the page shows: that's what the monthly limit counts.</summary>
    private async Task ShowMonth(UsageReport current, long? network)
    {
        var (from, to, _) = Periods.Range(Periods.Kind.Month, DateTime.Today);
        var month = network is null && _period.Kind == Periods.Kind.Month && _period.From == from
            ? current
            : await Service.Call<UsageReport>("usage", from, DateTimeOffset.Now.ToUnixTimeSeconds() + 1);
        var used = month.NetIn + month.NetOut;
        MonthText.Text = Fmt.Bytes(used);
        var settings = await Service.Call<Dictionary<string, string>>("settings");
        long.TryParse(settings.GetValueOrDefault(SettingKeys.MonthlyLimit), out var limit);
        var daysLeft = (int)Math.Ceiling((DateTimeOffset.FromUnixTimeSeconds(to) - DateTimeOffset.Now).TotalDays);
        if (limit > 0)
        {
            LimitText.Text = $"limit {Fmt.Bytes(limit)}";
            MonthBar.Visibility = Visibility.Visible;
            MonthBar.Value = Math.Min(1, (double)used / limit);
            LeftText.Text = used < limit
                ? $"{Fmt.Bytes(limit - used)} left, {daysLeft} day{(daysLeft == 1 ? "" : "s")} to go"
                : $"Over by {Fmt.Bytes(used - limit)}";
            MonthNote.Text = network is null ? "You'll be told at 80% and 100%." : "All networks together, as the limit counts. You'll be told at 80% and 100%.";
        }
        else
        {
            LimitText.Text = "";
            LeftText.Text = $"{daysLeft} day{(daysLeft == 1 ? "" : "s")} to go";
            MonthBar.Visibility = Visibility.Collapsed;
            MonthNote.Text = (network is null ? "" : "All networks together. ") + "No monthly limit. Set one in Settings to get a warning before you run out.";
        }
    }

    private async Task ShowAlerts()
    {
        var alerts = await Service.Call<List<AlertInfo>>("alerts", afterId: 0);
        AlertList.Children.Clear();
        foreach (var a in alerts.OrderByDescending(a => a.Time).Take(2))
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new FontIcon { Glyph = AlertsPage.Glyph(a.Kind), FontSize = 14, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = AlertsPage.Title(a), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = Fmt.Ago(a.Time), Style = (Style)Application.Current.Resources["Caption"] });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            AlertList.Children.Add(row);
        }
        NoAlerts.Visibility = alerts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSeeAll(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("apps");
}
