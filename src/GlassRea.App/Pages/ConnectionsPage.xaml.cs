using GlassRea.App.Controls;
using GlassRea.App.Support;
using GlassRea.Core.Look;
using GlassRea.Core.Net;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

public sealed record ConnApp(long Id, string Name, string? Icon, string Glyph, string Tag, string Sub, string Total, long In, long Out, bool Blocked, bool CanBlock, bool IsTunnel);
public sealed record HostRow(string Name, string Ip, string Down, string Up, string Seen);

/// <summary>Pick an app on the left; on the right, what it moved and who it talked to, and a way to block it.</summary>
public sealed partial class ConnectionsPage : UserControl, IPage
{
    /// <summary>Set by another page to open a particular app.</summary>
    public static long PendingAppId;

    private readonly PeriodPicker _period = new(Periods.Kind.Day);
    private readonly LiveList<ConnItem, ConnApp> _apps = new(a => a.Id);
    private readonly LiveList<HostItem, HostRow> _hosts = new(h => h.Ip);
    /// <summary>The rows each listed app is made of (see <see cref="AppFaces.Merge"/>), whose hosts are shown together.</summary>
    private Dictionary<long, IReadOnlyList<long>> _rowsOf = [];
    private long _selected, _periodTotal, _connectionTotal;
    private bool _updating;

    public ConnectionsPage()
    {
        InitializeComponent();
        Apps.ItemsSource = _apps;
        Hosts.ItemsSource = _hosts;
        // The list is narrow: the network goes under the period.
        PickerHost.Content = new StackPanel { Spacing = 8, Children = { _period, new NetworkPicker { HorizontalAlignment = HorizontalAlignment.Left } } };
        _period.Changed += () => _ = Refresh();
        Networks.Changed += () => { if (IsLoaded) _ = Refresh(); };
    }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(15);

    public async Task Refresh()
    {
        if (PendingAppId != 0) { _selected = PendingAppId; PendingAppId = 0; }
        var to = Math.Min(_period.To, DateTimeOffset.Now.ToUnixTimeSeconds() + 1);
        var report = await Service.Call<UsageReport>("usage", _period.From, to, network: Networks.Selected);
        _periodTotal = report.NetIn + report.NetOut;
        _connectionTotal = report.ConnectionIn + report.ConnectionOut;
        await AppFaces.Prepare(report.Apps);
        var merged = AppFaces.Merge(report.Apps).Where(m => m.App.NetIn + m.App.NetOut + m.App.LanIn + m.App.LanOut > 0).ToList();
        _rowsOf = merged.ToDictionary(m => m.App.AppId, m => m.Ids);
        var apps = merged.Select(m => m.App)
            .OrderByDescending(a => a.NetIn + a.NetOut + a.LanIn + a.LanOut)
            .Select(a =>
            {
                var face = AppFaces.For(a);
                return new ConnApp(a.AppId, face.Name, face.Icon, face.Glyph, face.Tag, face.About, Fmt.Bytes(a.NetIn + a.NetOut + a.LanIn + a.LanOut),
                    a.NetIn, a.NetOut, a.Blocked, a.Path is not null && a.Service?.Contains(',') != true, a.IsTunnel);
            })
            .ToList();
        _updating = true;
        _apps.Sync(apps);
        var pick = _apps.FirstOrDefault(i => i.Row.Id == _selected) ?? _apps.FirstOrDefault();
        if (Apps.SelectedItem != pick) Apps.SelectedItem = pick;
        _updating = false;
        NoApps.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Detail.Visibility = apps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        await LoadDetail(animate: false);
    }

    private void OnApp(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) _ = LoadDetail(animate: true);
    }

    private async Task LoadDetail(bool animate)
    {
        if (Apps.SelectedItem is not ConnItem { Row: var app }) { _hosts.Sync([]); return; }
        var changed = app.Id != _selected;
        _selected = app.Id;
        AppIcon.Glyph = app.Glyph;
        AppIcon.Icon = app.Icon;
        AppTitle.Text = app.Name;
        AppTagText.Text = app.Tag;
        AppTag.Visibility = app.Tag.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppSub.Text = app.Sub;
        ToolTipService.SetToolTip(AppSub, app.Sub);
        KDown.Text = Fmt.Bytes(app.In);
        KUp.Text = Fmt.Bytes(app.Out);
        // A VPN carries the other apps' traffic, so it isn't in the apps' total; its share is of the connection.
        var total = app.IsTunnel ? _connectionTotal : _periodTotal;
        KShareLabel.Text = app.IsTunnel ? "Share of your connection" : "Share of the period";
        KShare.Text = total > 0 ? Fmt.Percent(Math.Min(1, (double)(app.In + app.Out) / total)) : app.IsTunnel ? "-" : "0%";
        BlockButton.Content = app.Blocked ? "Allow internet" : "Block internet";
        BlockButton.IsEnabled = app.CanBlock;
        ToolTipService.SetToolTip(BlockButton, app.CanBlock ? null : "This one has no program file of its own, so it can't be blocked separately.");
        if (animate && changed) foreach (var child in Detail.Children) Motion.Arrive(child, 0, rise: 6);

        var to = Math.Min(_period.To, DateTimeOffset.Now.ToUnixTimeSeconds());
        // Every row of the app (each version, each sign-in's copy of a service), as one list of hosts.
        var hosts = new List<HostUsage>();
        foreach (var id in _rowsOf.GetValueOrDefault(app.Id) ?? [app.Id]) hosts.AddRange(await Service.Call<List<HostUsage>>("hosts", _period.From, to, appId: id, network: Networks.Selected));
        if (Apps.SelectedItem is not ConnItem { Row.Id: var still } || still != app.Id) return; // another app was picked meanwhile
        hosts = hosts.GroupBy(h => h.Ip).Select(g => new HostUsage(g.Key, g.Select(h => h.Name).FirstOrDefault(n => n is not null),
            g.Sum(h => h.In), g.Sum(h => h.Out), g.Max(h => h.LastSeen))).OrderByDescending(h => h.In + h.Out).ToList();
        Scope ScopeOf(HostUsage h) => System.Net.IPAddress.TryParse(h.Ip, out var ip) ? AddressScope.Classify(ip) : Scope.Internet;
        // Devices on the network are named as the Devices page names them.
        var lan = hosts.Any(h => h.Name is null && ScopeOf(h) == Scope.Lan)
            ? (await Service.Call<List<DeviceInfo>>("devices")).GroupBy(d => d.Ip).ToDictionary(g => g.Key, g => DeviceLabel.For(g.MaxBy(d => d.LastSeen)!))
            : [];
        if (changed) _hosts.Sync([]); // another app's hosts: start the list afresh rather than morph it
        _hosts.Sync(hosts.Select(h =>
        {
            var scope = ScopeOf(h);
            var name = h.Name ?? (scope == Scope.Lan ? lan.GetValueOrDefault(h.Ip) ?? "Device on your network" : h.Ip);
            return new HostRow(name, h.Ip, Fmt.Bytes(h.In), Fmt.Bytes(h.Out), Fmt.Ago(h.LastSeen));
        }).ToList());
        NoHosts.Visibility = hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnBlock(object sender, RoutedEventArgs e)
    {
        if (Apps.SelectedItem is not ConnItem { Row: var app }) return;
        var block = !app.Blocked;
        BlockButton.IsEnabled = false;
        try
        {
            await Service.Do("block", app.Id, block);
            BlockError.IsOpen = false;
        }
        catch (Exception ex)
        {
            BlockError.Title = block ? $"Couldn't block {app.Name}" : $"Couldn't allow {app.Name}";
            BlockError.Message = ex.Message;
            BlockError.IsOpen = true;
        }
        await Refresh();
    }
}
