using GlassRea.App.Controls;
using GlassRea.App.Support;
using GlassRea.Core.Look;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

/// <summary>Every app with its download, upload and total for the period. Clicking one opens its connections.</summary>
public sealed partial class AppsPage : UserControl, IPage
{
    private readonly PeriodPicker _period = new(Periods.Kind.Month);
    private readonly LiveList<AppItem, AppRow> _rows = new(r => r.Id);
    private string _shown = "";

    public AppsPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        PickerHost.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new NetworkPicker(), _period } };
        _period.Changed += () => _ = Refresh();
        Networks.Changed += () => { if (IsLoaded) _ = Refresh(); };
        foreach (var (kind, title, text) in AppFaces.Kinds)
        {
            var entry = new Grid { ColumnSpacing = 12 };
            entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            entry.ColumnDefinitions.Add(new ColumnDefinition());
            entry.Children.Add(new AppIconView { Glyph = AppFaces.Glyph(kind), VerticalAlignment = VerticalAlignment.Top });
            var words = new StackPanel { Spacing = 2 };
            words.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            words.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["Caption"], TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(words, 1);
            entry.Children.Add(words);
            KindsPanel.Children.Add(entry);
        }
    }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(10);

    public async Task Refresh()
    {
        var to = Math.Min(_period.To, DateTimeOffset.Now.ToUnixTimeSeconds() + 1);
        var network = Networks.Selected;
        var r = await Service.Call<UsageReport>("usage", _period.From, to, network: network);
        PeriodText.Text = _period.Label + Networks.On;
        TotalText.Text = Fmt.Bytes(r.NetIn + r.NetOut);
        DownText.Text = Fmt.Bytes(r.NetIn);
        UpText.Text = Fmt.Bytes(r.NetOut);
        var rows = await AppRow.From(r);
        // A refresh changes only the figures that moved; a new period starts again from the top.
        var key = $"{_period.Kind}:{_period.From}:{network}";
        _rows.Sync(rows);
        if (key != _shown && _rows.Count > 0) List.ScrollIntoView(_rows[0]);
        _shown = key;
        Empty.Text = network is null ? "No app has used the internet in this period." : "No app has used the internet on this network in this period.";
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKinds(object sender, RoutedEventArgs e) => Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);

    private void OnOpen(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AppItem { Row: var row }) return;
        ConnectionsPage.PendingAppId = row.Id;
        MainWindow.Instance?.Navigate("connections");
    }
}
