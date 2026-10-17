using GlassRea.App.Controls;
using GlassRea.App.Support;
using GlassRea.Core.Net;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

public sealed record ConnApp(long Id, string Name, string? Path, string Total, long In, long Out, bool Blocked, bool CanBlock);
public sealed record HostRow(string Name, string Ip, string Down, string Up, string Seen);

/// <summary>Pick an app on the left; on the right, what it moved and who it talked to, and a way to block it.</summary>
public sealed partial class ConnectionsPage : UserControl, IPage
{
    /// <summary>Set by another page to open a particular app.</summary>
    public static long PendingAppId;

    private readonly PeriodPicker _period = new(Periods.Kind.Day);
    private long _selected, _periodTotal;
    private bool _updating;

    public ConnectionsPage()
    {
        InitializeComponent();
        PickerHost.Content = _period;
        _period.Changed += () => _ = Refresh();
    }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(15);

    public async Task Refresh()
    {
        if (PendingAppId != 0) { _selected = PendingAppId; PendingAppId = 0; }
        var to = Math.Min(_period.To, DateTimeOffset.Now.ToUnixTimeSeconds() + 1);
        var report = await Service.Call<UsageReport>("usage", _period.From, to);
        _periodTotal = report.NetIn + report.NetOut;
        var apps = report.Apps.Where(a => a.NetIn + a.NetOut + a.LanIn + a.LanOut > 0)
            .OrderByDescending(a => a.NetIn + a.NetOut + a.LanIn + a.LanOut)
            .Select(a => new ConnApp(a.AppId, a.Name, a.Path, Fmt.Bytes(a.NetIn + a.NetOut + a.LanIn + a.LanOut),
                a.NetIn, a.NetOut, a.Blocked, a.Path is not null && a.Service?.Contains(',') != true))
            .ToList();
        _updating = true;
        Apps.ItemsSource = apps;
        Apps.SelectedItem = apps.FirstOrDefault(a => a.Id == _selected) ?? apps.FirstOrDefault();
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
        if (Apps.SelectedItem is not ConnApp app) { Hosts.ItemsSource = null; return; }
        var changed = app.Id != _selected;
        _selected = app.Id;
        AppIcon.AppName = app.Name;
        AppIcon.AppPath = app.Path;
        AppTitle.Text = app.Name;
        AppSub.Text = app.Path ?? "No program file";
        KDown.Text = Fmt.Bytes(app.In);
        KUp.Text = Fmt.Bytes(app.Out);
        KShare.Text = _periodTotal > 0 ? Fmt.Percent((double)(app.In + app.Out) / _periodTotal) : "0%";
        BlockButton.Content = app.Blocked ? "Allow internet" : "Block internet";
        BlockButton.IsEnabled = app.CanBlock;
        ToolTipService.SetToolTip(BlockButton, app.CanBlock ? null : "This one has no program file of its own, so it can't be blocked separately.");
        if (animate && changed) foreach (var child in Detail.Children) Motion.Arrive(child, 0, rise: 6);

        var to = Math.Min(_period.To, DateTimeOffset.Now.ToUnixTimeSeconds());
        var hosts = await Service.Call<List<HostUsage>>("hosts", _period.From, to, appId: app.Id);
        Scope ScopeOf(HostUsage h) => System.Net.IPAddress.TryParse(h.Ip, out var ip) ? AddressScope.Classify(ip) : Scope.Internet;
        // Devices on the network are named as the Devices page names them.
        var lan = hosts.Any(h => h.Name is null && ScopeOf(h) == Scope.Lan)
            ? (await Service.Call<List<DeviceInfo>>("devices")).GroupBy(d => d.Ip).ToDictionary(g => g.Key, g => DeviceLabel.For(g.MaxBy(d => d.LastSeen)!))
            : [];
        Hosts.ItemsSource = hosts.Select(h =>
        {
            var scope = ScopeOf(h);
            var name = h.Name ?? (scope == Scope.Lan ? lan.GetValueOrDefault(h.Ip) ?? "Device on your network" : h.Ip);
            return new HostRow(name, h.Ip, Fmt.Bytes(h.In), Fmt.Bytes(h.Out), Fmt.Ago(h.LastSeen));
        }).ToList();
        NoHosts.Visibility = hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnBlock(object sender, RoutedEventArgs e)
    {
        if (Apps.SelectedItem is not ConnApp app) return;
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
