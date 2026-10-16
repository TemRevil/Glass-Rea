using System.Globalization;
using System.IO.Pipes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GlassRea.App;

/// <summary>
/// Single instance: a second launch asks the running copy to show its window, then exits before any XAML starts.
/// "--tray" (used at sign-in) starts without opening the window.
/// </summary>
public static class Program
{
    public const string InstanceName = "GlassRea-App";
    private static Mutex? _single;

    [STAThread]
    private static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        _single = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            try { using var c = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out); c.Connect(800); c.WriteByte(1); }
            catch { /* the other copy is still starting */ }
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(startInTray: args.Contains("--tray"));
        });
        GC.KeepAlive(_single);
    }
}
