using GlassRea.App.Controls;
using GlassRea.App.Support;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

/// <summary>Every app with its download, upload and total for the period. Clicking one opens its connections.</summary>
public sealed partial class AppsPage : UserControl, IPage
{
    private readonly PeriodPicker _period = new(Periods.Kind.Month);
    private string _shown = "";

    public AppsPage()
    {
        InitializeComponent();
        PickerHost.Content = _period;
        _period.Changed += () => _ = Refresh();
    }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(10);

    public async Task Refresh()
    {
        var to = Math.Min(_period.To, DateTimeOffset.Now.ToUnixTimeSeconds() + 1);
        var r = await Service.Call<UsageReport>("usage", _period.From, to);
        PeriodText.Text = _period.Label;
        TotalText.Text = Fmt.Bytes(r.NetIn + r.NetOut);
        DownText.Text = Fmt.Bytes(r.NetIn);
        UpText.Text = Fmt.Bytes(r.NetOut);
        var rows = AppRow.From(r);
        // Keep the scroll position on a plain refresh; a new period replaces the list.
        var key = $"{_period.Kind}:{_period.From}";
        if (key != _shown || List.ItemsSource is not List<AppRow> old || !old.SequenceEqual(rows))
            List.ItemsSource = rows;
        _shown = key;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpen(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AppRow row) return;
        ConnectionsPage.PendingAppId = row.Id;
        MainWindow.Instance?.Navigate("connections");
    }
}
