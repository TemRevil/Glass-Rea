using GlassRea.App.Support;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

public sealed record DeviceRow(string Name, string Note, string Glyph, string Os, Visibility OsShown, string Ip, string Mac, string First, string Last, double Fade);

/// <summary>Everything seen on the Wi-Fi: the router, this PC, phones, TVs. A new one raises an alert.</summary>
public sealed partial class DevicesPage : UserControl, IPage
{
    public DevicesPage() { InitializeComponent(); }

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
                    d.Ip, d.Mac, $"First seen {Fmt.Date(d.FirstSeen)}", $"Last seen {Fmt.Ago(d.LastSeen)}", d.LastSeen < recent ? 0.55 : 1);
            }).ToList();
        List.ItemsSource = rows;
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
