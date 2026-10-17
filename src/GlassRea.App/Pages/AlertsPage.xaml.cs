using GlassRea.App.Support;
using GlassRea.Core.Look;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

public sealed record AlertRow(long Id, string Title, string Body, string When, string Glyph);

public sealed partial class AlertsPage : UserControl, IPage
{
    private readonly LiveList<AlertItem, AlertRow> _rows = new(a => a.Id);

    public AlertsPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
    }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(10);

    public static string Glyph(string kind) => kind switch
    {
        "limit" => "",       // gauge
        "new_device" => "",  // wifi
        "new_app" => "",     // apps
        "device_left" => "\uE711",  // cross
        "device_back" => "\uE73E",  // check mark
        _ => "",             // bell
    };

    /// <summary>The alert's title, finished off when the program it names had no name.</summary>
    public static string Title(AlertInfo a) =>
        a.Title.TrimEnd().EndsWith(':') ? a.Title.TrimEnd() + " a program Windows didn't name" : a.Title;

    public async Task Refresh()
    {
        var alerts = await Service.Call<List<AlertInfo>>("alerts", afterId: 0);
        var rows = alerts.OrderByDescending(a => a.Time).Select(a => new AlertRow(a.Id, Title(a), a.Body, Fmt.DateTime(a.Time), Glyph(a.Kind))).ToList();
        _rows.Sync(rows);
        CountText.Text = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("settings");
}
