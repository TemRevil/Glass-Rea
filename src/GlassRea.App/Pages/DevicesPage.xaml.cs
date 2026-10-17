using GlassRea.App.Support;
using GlassRea.Core.Look;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

/// <param name="Custom">The name you gave it, or "".</param>
/// <param name="Own">What it would be called without your name.</param>
public sealed record DeviceRow(string Name, string Note, string Glyph, string Os, Visibility OsShown, string Ip, string Mac, string First, string Last, double Fade,
    string Custom, string Own);

/// <summary>Everything seen on the Wi-Fi: the router, this PC, phones, TVs. A new one raises an alert.</summary>
public sealed partial class DevicesPage : UserControl, IPage
{
    private readonly LiveList<DeviceItem, DeviceRow> _rows = new(d => (d.Mac, d.Ip));

    public DevicesPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
    }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(20);

    public async Task Refresh()
    {
        var status = await Service.Call<ServiceStatus>("status");
        var devices = await Service.Call<List<DeviceInfo>>("devices");
        var recent = status.LastDeviceScan - 600;
        var online = devices.Count(d => d.LastSeen >= recent);
        Summary.Text = status.LastDeviceScan == 0
            ? "The first scan runs shortly after the service starts."
            : $"{online} device{(online == 1 ? "" : "s")} online at the last scan, {Fmt.Ago(status.LastDeviceScan)}. Scans run every 5 minutes.";
        PublicHint.Visibility = status.NetworkPublic ? Visibility.Visible : Visibility.Collapsed;
        var rows = devices
            .OrderByDescending(d => d.IsGateway).ThenByDescending(d => d.IsThisPc).ThenByDescending(d => d.LastSeen >= recent)
            .ThenBy(DeviceLabel.For, StringComparer.CurrentCultureIgnoreCase)
            .Select(d =>
            {
                var name = DeviceLabel.For(d);
                var notes = new List<string>();
                if (d.IsThisPc) notes.Add("This PC");
                else if (DeviceLabel.What(d) is { } what && !what.Equals(name, StringComparison.CurrentCultureIgnoreCase)) notes.Add(what);
                if (d.Randomized) notes.Add("Private address");
                notes.Add(d.LastSeen < recent ? "Offline" : "Online");
                var os = DeviceOs.Display(d.Os) ?? "";
                return new DeviceRow(name, string.Join(", ", notes), Glyph(d), os, os.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
                    d.Ip, d.Mac, $"First seen {Fmt.Date(d.FirstSeen)}", $"Last seen {Fmt.Ago(d.LastSeen)}", d.LastSeen < recent ? 0.55 : 1,
                    d.CustomName ?? "", DeviceLabel.For(d with { CustomName = null }));
            }).ToList();
        _rows.Sync(rows);
    }

    /// <summary>Segoe Fluent Icons for each kind of device.</summary>
    private static string Glyph(DeviceInfo d) => (d.IsGateway ? DeviceKinds.Router : d.IsThisPc ? DeviceKinds.Computer : d.Kind) switch
    {
        DeviceKinds.Router => "\uE968",
        DeviceKinds.Computer => "\uE7F8",
        DeviceKinds.Phone => "\uE8EA",
        DeviceKinds.Tablet => "\uE70A",
        DeviceKinds.Tv => "\uE7F4",
        DeviceKinds.Speaker => "\uE7F5",
        DeviceKinds.Printer => "\uE749",
        DeviceKinds.Console => "\uE7FC",
        DeviceKinds.Camera => "\uE722",
        DeviceKinds.Watch => "\uE916",
        DeviceKinds.SmartHome => "\uE80F",
        DeviceKinds.Storage => "\uEDA2",
        DeviceKinds.Network => "\uE701",
        _ => "\uE772",
    };

    private async void OnOpenNetworkSettings(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:network-wifi"));

    /// <summary>Your own name for a device, kept by the service; blank goes back to the name it gives itself.</summary>
    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceItem { Row: var row } } button) return;
        var box = new TextBox
        {
            Header = "Name", Text = row.Custom, PlaceholderText = row.Own, MaxLength = DeviceLabel.MaxNameLength, Width = 240,
        };
        var error = new TextBlock { Style = (Style)Application.Current.Resources["Caption"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var save = new Button { Content = "Save", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var reset = new Button { Content = "Use its own name", IsEnabled = row.Custom.Length > 0 };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 12, Width = 240,
                Children = { box, error, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, reset } } },
            },
        };
        async Task Apply(string name)
        {
            save.IsEnabled = reset.IsEnabled = false;
            try
            {
                await Service.Do("device.rename", settings: new() { ["mac"] = row.Mac, ["name"] = name });
                flyout.Hide();
                await Refresh();
            }
            catch (Exception ex) when (Service.IsOffline(ex) || ex is ServiceException)
            {
                error.Text = ex is ServiceException ? ex.Message : "The Glass-Rea service isn't answering. Try again in a moment.";
                error.Visibility = Visibility.Visible;
                save.IsEnabled = true;
                reset.IsEnabled = row.Custom.Length > 0;
            }
        }
        save.Click += async (_, _) => await Apply(box.Text);
        reset.Click += async (_, _) => await Apply("");
        box.KeyDown += async (_, k) => { if (k.Key == Windows.System.VirtualKey.Enter) { k.Handled = true; await Apply(box.Text); } };
        flyout.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        flyout.ShowAt(button);
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanRing.IsActive = true;
        ScanRing.Visibility = Visibility.Visible;
        ScanText.Text = "Scanning";
        try
        {
            await Service.Do("scan");
            await Task.Delay(TimeSpan.FromSeconds(12));
            await Refresh();
        }
        catch (Exception ex) when (Service.IsOffline(ex) || ex is ServiceException) { /* the window shows service problems */ }
        finally
        {
            ScanButton.IsEnabled = true;
            ScanRing.IsActive = false;
            ScanRing.Visibility = Visibility.Collapsed;
            ScanText.Text = "Scan now";
        }
    }
}
