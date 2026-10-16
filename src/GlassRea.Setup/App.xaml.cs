using GlassRea.App.Support;
using GlassRea.Setup.Installer;
using Microsoft.UI.Xaml;

namespace GlassRea.Setup;

public partial class App : Application
{
    private readonly SetupMode _mode;
    private SetupWindow? _window;

    public App(SetupMode mode)
    {
        _mode = mode;
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write($"Unhandled: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // An update looks like the app the person already set up: same theme and palette.
        Appearance.Load();
        Appearance.Prime();
        _window = new SetupWindow(_mode);
        _window.Activate();
    }
}
