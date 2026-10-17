using GlassRea.App.Support;
using GlassRea.Core.Look;
using GlassRea.Core.Protocol;
using GlassRea.Core.Usage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

public sealed record FirewallRow(long Id, string Name, string? Icon, string Glyph, string Tag, string Sub, string Total, bool Allowed, bool CanBlock)
{
    public Visibility TagShown => Tag.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Every app ever seen, each with one switch: online or blocked.</summary>
public sealed partial class FirewallPage : UserControl, IPage
{
    private List<FirewallRow> _all = [];
    private readonly LiveList<FirewallItem, FirewallRow> _rows = new(r => r.Id);
    private bool _busy;

    public FirewallPage()
    {
        InitializeComponent();
        Apps.ItemsSource = _rows;
    }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(30);

    public async Task Refresh()
    {
        if (_busy) return;
        var report = await Service.Call<UsageReport>("usage", 0, 0);
        await AppFaces.Prepare(report.Apps);
        // A block rule names one program file, so programs stay one row per file here; a Store app is blocked as
        // a whole package and a service by name, so their rows are still gathered.
        _all = AppKinds.Merge(report.Apps, a => AppFaces.KindOf(a) == AppKind.App && a.Path is { } path ? "file:" + path.ToLowerInvariant() : AppFaces.Key(a))
            .Select(m => m.App).OrderByDescending(a => a.NetIn + a.NetOut).Select(a =>
            {
                var face = AppFaces.For(a);
                var canBlock = a.Path is not null && a.Service?.Contains(',') != true;
                var sub = canBlock ? face.About
                    : a.Path is null ? "No program file of its own, so it can't be blocked separately"
                    : "Shares its Windows process with other services, so it can't be blocked on its own";
                return new FirewallRow(a.AppId, face.Name, face.Icon, face.Glyph, face.Tag, sub,
                    Fmt.Bytes(a.NetIn + a.NetOut), !a.Blocked, canBlock);
            }).ToList();
        var blocked = _all.Count(r => !r.Allowed);
        Summary.Text = (blocked == 0 ? "Nothing is blocked. " : $"{blocked} app{(blocked == 1 ? " is" : "s are")} blocked. ")
            + "Turn an app off to block it in Windows Firewall; turn it back on any time.";
        ApplyFilter();
    }

    private void OnSearch(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = Search.Text.Trim();
        var rows = q.Length == 0 ? _all : _all.Where(r =>
            r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Sub.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _busy = true; // new rows' switches raise Toggled as they're filled in; ignore those
        _rows.Sync(rows);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _busy = false);
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        // The switch's own row, as it is now: a recycled or refreshed row sets the switch to that row's state first,
        // so only a click leaves the two different.
        if (_busy || sender is not ToggleSwitch { DataContext: FirewallItem { Row: var row } } box || box.IsOn == row.Allowed) return;
        var block = !box.IsOn;
        box.IsEnabled = false;
        try
        {
            await Service.Do("block", row.Id, block);
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            _busy = true;
            box.IsOn = !box.IsOn;
            _busy = false;
            ErrorBar.Title = block ? $"Couldn't block {row.Name}" : $"Couldn't unblock {row.Name}";
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally { box.IsEnabled = row.CanBlock; }
        await Refresh();
    }
}
