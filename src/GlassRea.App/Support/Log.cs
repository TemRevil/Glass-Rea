using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using GlassRea.Core.Logs;
using Microsoft.UI.Dispatching;

namespace GlassRea.App.Support;

/// <summary>
/// The app's log, %LOCALAPPDATA%\Glass-Rea\Logs\app.log: crashes, errors, moments the window stopped responding,
/// pages that opened slowly and service calls that were slow or failed, plus the crashes Windows itself recorded
/// for Glass-Rea. It stays on this PC; Settings > About > Logs packs it with the service's log to send.
/// </summary>
public static class Log
{
    public static readonly LogFile File = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glass-Rea", "Logs"), "app");

    /// <summary>The service's own log (see the service's FileLoggerProvider), which users may read but not change.</summary>
    public static readonly string ServiceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Glass-Rea", "Logs");

    /// <summary>The page on screen, so a freeze or an error says where it happened.</summary>
    public static volatile string Where = "";

    /// <summary>Where something happened, as words: "on the apps page", or "while starting".</summary>
    public static string Place => Where.Length == 0 ? "while starting" : $"on the {Where} page";

    /// <summary>The window not answering for this long counts as a freeze.</summary>
    public static readonly TimeSpan Freeze = TimeSpan.FromMilliseconds(500);
    /// <summary>A page taking longer than this to open or refresh, or a service call taking longer, counts as slow.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(1.5);

    public static void Info(string source, string message) => File.Write(LogLevel.Info, source, message);
    public static void Warn(string source, string message, Exception? error = null) => File.Write(LogLevel.Warning, source, message, error);
    public static void Error(string source, string message, Exception? error = null) => File.Write(LogLevel.Error, source, message, error);
    public static void Crash(string source, string message, Exception? error = null) => File.Write(LogLevel.Crash, source, message, error);

    /// <summary>Logs <paramref name="what"/> as slow when it took longer than <see cref="Slow"/>.</summary>
    public static void IfSlow(string source, string what, TimeSpan took)
    {
        if (took > Slow) File.Write(LogLevel.Slow, source, $"{what} took {Seconds(took)} ({Place})");
    }

    public static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>Starts logging: what's running, every error nobody caught, and a watch on the window's responsiveness.</summary>
    public static void Start(DispatcherQueue ui)
    {
        Info("App", $"Starting Glass-Rea {typeof(Log).Assembly.GetName().Version?.ToString(3)} on {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash("App", $"Glass-Rea closed on an error it didn't expect ({Place})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Error("App", "A background task failed and nothing was waiting for it", e.Exception);
        new Thread(() => Watch(ui)) { IsBackground = true, Name = "Freeze watch", Priority = ThreadPriority.AboveNormal }.Start();
        _ = Task.Run(ImportWindowsReports);
    }

    /// <summary>
    /// Posts a no-op to the window's thread every tenth of a second and times how long it waits. A wait past
    /// <see cref="Freeze"/> is logged when the window answers; one past five seconds is logged while it still hasn't,
    /// so a hang that ends with Windows closing the app is on record too. Time the PC spent asleep doesn't count.
    /// </summary>
    private static void Watch(DispatcherQueue ui)
    {
        long ping = 0, answered = 0, sentAt = 0;
        var reportedHang = false;
        var last = Stopwatch.GetTimestamp();
        while (true)
        {
            Thread.Sleep(100);
            var now = Stopwatch.GetTimestamp();
            var slept = Stopwatch.GetElapsedTime(last, now) > TimeSpan.FromSeconds(2);
            last = now;
            if (slept) { answered = Interlocked.Read(ref ping); reportedHang = false; continue; } // the PC was asleep, not the window

            if (Interlocked.Read(ref answered) == ping)
            {
                var id = ++ping;
                sentAt = now;
                var sent = now;
                if (!ui.TryEnqueue(() =>
                    {
                        // A ping left over from before the PC slept is dropped: its wait includes the sleep.
                        if (Interlocked.Read(ref answered) >= id) return;
                        var waited = Stopwatch.GetElapsedTime(sent);
                        Interlocked.Exchange(ref answered, id);
                        if (waited >= Freeze) File.Write(LogLevel.Freeze, "Window", $"didn't respond for {Seconds(waited)} ({Place})");
                    })) return; // the window's thread has ended
                reportedHang = false;
            }
            else if (!reportedHang && Stopwatch.GetElapsedTime(sentAt, now) > TimeSpan.FromSeconds(5))
            {
                reportedHang = true;
                File.Write(LogLevel.Freeze, "Window", $"hasn't responded for 5 s and counting ({Place})");
            }
        }
    }

    private static readonly string CheckedFile = Path.Combine(File.Directory, "windows-reports-checked.txt");

    /// <summary>
    /// Copies in what Windows recorded about Glass-Rea since the last look (a week back the first time): the app or
    /// the service crashing, and the app hanging until it was closed. Those happen where this log can't write.
    /// </summary>
    private static void ImportWindowsReports()
    {
        try
        {
            var since = DateTime.UtcNow.AddDays(-7);
            if (System.IO.File.Exists(CheckedFile) && DateTime.TryParse(System.IO.File.ReadAllText(CheckedFile).Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) since = t;
            var checkedAt = DateTime.UtcNow;
            var query = new EventLogQuery("Application", PathType.LogName,
                "*[System[Provider[@Name='Application Error' or @Name='Application Hang' or @Name='.NET Runtime'] and (EventID=1000 or EventID=1002 or EventID=1026)" +
                $" and TimeCreated[@SystemTime>'{since:yyyy-MM-ddTHH:mm:ss.fffZ}']]]");
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                {
                    var text = e.FormatDescription() ?? string.Join(" | ", e.Properties.Select(p => p.Value));
                    // The service runs inside dotnet.exe, so its reports are known by the Glass-Rea code in them.
                    if (!text.Contains("GlassRea", StringComparison.OrdinalIgnoreCase)) continue;
                    var when = e.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "?";
                    var what = e.Id == 1002 ? "stopped responding and was closed" : "crashed";
                    File.Write(LogLevel.Crash, "Windows", $"recorded at {when} that Glass-Rea {what} ({e.ProviderName} {e.Id}):\n{text}");
                }
            Directory.CreateDirectory(File.Directory);
            System.IO.File.WriteAllText(CheckedFile, checkedAt.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) { Warn("App", "Couldn't read Windows' crash reports", ex); }
    }

    /// <summary>Everything to send in one .zip: this log, the service's, and which versions of what were running.</summary>
    public static void Bundle(Stream output, string? serviceVersion)
    {
        var about = string.Join("\n",
            $"Glass-Rea app {typeof(Log).Assembly.GetName().Version?.ToString(3)}",
            $"Glass-Rea service {serviceVersion ?? "not reachable"}",
            $"Windows {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})",
            $".NET {Environment.Version}",
            $"Time zone {TimeZoneInfo.Local.Id}",
            $"Made {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}", "");
        LogBundle.Write(output, [("app", File.Directory), ("service", ServiceFolder)], about);
    }
}
