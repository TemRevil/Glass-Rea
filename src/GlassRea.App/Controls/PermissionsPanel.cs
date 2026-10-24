using GlassRea.App.Support;
using GlassRea.Core.Alerts;
using GlassRea.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GlassRea.App.Controls;

/// <summary>
/// One switch per thing the service may do, each with why it needs system rights (<see cref="PermissionText"/>). Used
/// by the permission screen and by Settings > General; both follow <see cref="PermissionState"/>.
/// </summary>
public sealed partial class PermissionsPanel : UserControl
{
    private readonly Dictionary<Feature, ToggleSwitch> _switches = [];
    private bool _showing;

    public PermissionsPanel()
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (var f in PermissionKeys.All)
        {
            var toggle = new ToggleSwitch { MinWidth = 0, OnContent = "", OffContent = "" };
            AutomationProperties.SetName(toggle, PermissionText.Title(f));
            toggle.Toggled += async (_, _) => { if (!_showing) await Turn(f, toggle.IsOn); };
            _switches[f] = toggle;

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = PermissionText.Title(f) });
            text.Children.Add(new TextBlock { Text = PermissionText.Why(f), Style = Resource("Caption"), TextWrapping = TextWrapping.Wrap });
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(text);
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            rows.Children.Add(new Border { Style = Resource("Row"), Child = row });
        }
        Content = rows;
        Loaded += (_, _) => { PermissionState.Changed += Show; Show(); };
        Unloaded += (_, _) => PermissionState.Changed -= Show;
    }

    private static Style Resource(string key) => (Style)Application.Current.Resources[key];

    private void Show()
    {
        _showing = true;
        foreach (var (f, toggle) in _switches)
        {
            toggle.IsOn = PermissionState.Current?.Allowed(f) ?? false;
            toggle.IsEnabled = PermissionState.Current is not null;
        }
        _showing = false;
    }

    private async Task Turn(Feature f, bool on)
    {
        try { await PermissionState.Turn(f, on); }
        catch (Exception e) when (Service.IsOffline(e) || e is ServiceException)
        {
            Log.Warn("Permissions", $"Couldn't turn {PermissionText.Title(f)} {(on ? "on" : "off")}: {e.Message}");
            Show(); // back to what the service has
        }
    }
}
