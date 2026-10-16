using GlassRea.App.Support;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Pages;

public sealed record FirewallRow(long Id, string Name, string? Path, string Sub, string Total, bool Allowed, bool CanBlock);

/// <summary>Every app ever seen, each with one switch: online or blocked.</summary>
public sealed partial class FirewallPage : UserControl, IPage
{
    private List<FirewallRow> _all = [];
    private bool _busy;

    public FirewallPage() { InitializeComponent(); }

    public TimeSpan RefreshEvery => TimeSpan.FromSeconds(30);

    public async Task Refresh()
    {
        if (_busy) return;
        var report = await Service.Call<UsageReport>("usage", 0, 0);
        _all = report.Apps.OrderByDescending(a => a.NetIn + a.NetOut).Select(a => new FirewallRow(a.AppId, a.Name, a.Path,
            a.Path ?? "No program file of its own, so it can't be blocked separately", Fmt.Bytes(a.NetIn + a.NetOut),
            !a.Blocked, a.Path is not null && a.Service?.Contains(',') != true)).ToList();
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
        if (Apps.ItemsSource is List<FirewallRow> old && old.SequenceEqual(rows)) return;
        _busy = true; // filling the list raises Toggled; ignore those
        Apps.ItemsSource = rows;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _busy = false);
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not ToggleSwitch { Tag: FirewallRow row } box || box.IsOn == row.Allowed) return;
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
