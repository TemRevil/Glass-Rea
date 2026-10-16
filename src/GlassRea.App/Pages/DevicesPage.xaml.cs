using GlassRea.App.Support;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

public sealed record DeviceRow(string Name, string Note, string Glyph, string Ip, string Mac, string First, string Last, double Fade);

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
            .OrderByDescending(d => d.IsGateway).ThenByDescending(d => d.IsThisPc).ThenByDescending(d => d.LastSeen >= recent).ThenBy(d => d.Name ?? "~")
            .Select(d =>
            {
                var notes = new List<string>();
                if (d.IsThisPc) notes.Add("This PC");
                if (d.IsGateway) notes.Add("Router");
                if (d.Randomized) notes.Add("Private MAC address");
                notes.Add(d.LastSeen < recent ? "Offline" : "Online");
                var glyph = d.IsGateway ? "" : d.IsThisPc ? "" : d.Randomized ? "" : "";
                return new DeviceRow(d.Name ?? (d.IsGateway ? "Router" : "Unnamed device"), string.Join(", ", notes), glyph, d.Ip, d.Mac,
                    $"First seen {Fmt.Date(d.FirstSeen)}", $"Last seen {Fmt.Ago(d.LastSeen)}", d.LastSeen < recent ? 0.55 : 1);
            }).ToList();
        List.ItemsSource = rows;
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
