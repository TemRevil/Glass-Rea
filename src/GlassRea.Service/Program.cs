using System.Globalization;
using GlassRea.Core.Paths;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;
using GlassRea.Service;
using GlassRea.Service.Capture;
using GlassRea.Service.History;
using GlassRea.Service.Networks;

// Numbers, dates and file names are always written in invariant form (Latin digits, Gregorian calendar).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

FileLoggerProvider.PrepareFolder();
var builder = WithLogging(Host.CreateApplicationBuilder(args));
builder.Services.AddWindowsService(o => o.ServiceName = Packaging.ServiceName);
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    FileLoggerProvider.File.Write(GlassRea.Core.Logs.LogLevel.Crash, "Service", "The service stopped on an error it didn't expect", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) =>
    FileLoggerProvider.File.Write(GlassRea.Core.Logs.LogLevel.Error, "Service", "A background task failed and nobody waited for it", e.Exception);
FileLoggerProvider.File.Write(GlassRea.Core.Logs.LogLevel.Info, "Service",
    $"Starting Glass-Rea service {typeof(FileLoggerProvider).Assembly.GetName().Version?.ToString(3)}{(Packaging.IsPackaged ? " (Microsoft Store)" : "")} on {Environment.OSVersion.VersionString}, .NET {Environment.Version}");

// The Store copy, while the copy setup installed is still here: that one records, since two recording would count
// everything twice. Once it's removed, this process starts recording itself, still as the same Windows service.
if (Packaging.IsPackaged && Standby.ClassicInstalled())
{
    builder.Services.AddHostedService(sp => new Standby(sp.GetRequiredService<ILogger<Standby>>(),
        () => Recording(WithLogging(Host.CreateApplicationBuilder(args)))));
    builder.Build().Run();
    return;
}

using var host = Recording(builder);
host.Run();

static HostApplicationBuilder WithLogging(HostApplicationBuilder builder)
{
    // Setup registers the event log source; the Store package can't, and has no way to remove one when it goes.
    if (!Packaging.IsPackaged) builder.Logging.AddEventLog(o => o.SourceName = "Glass-Rea");
    builder.Logging.AddProvider(new FileLoggerProvider());
    return builder;
}

static IHost Recording(HostApplicationBuilder builder)
{
    builder.Services.AddSingleton(_ => Database.OpenFile(Path.Combine(ServiceState.DataDirectory, "data.db")));
    builder.Services.AddSingleton(_ => new UsageAccumulator());
    builder.Services.AddSingleton<ServiceState>();
    builder.Services.AddSingleton<ProcessResolver>();
    builder.Services.AddSingleton<DnsCache>();
    builder.Services.AddSingleton<FirewallManager>();
    builder.Services.AddSingleton<DeviceScanner>();

    // First, so capture starts out counting on the right network.
    builder.Services.AddHostedService<NetworkWatcher>();
    builder.Services.AddHostedService<EtwCapture>();
    builder.Services.AddHostedService<FlushService>();
    builder.Services.AddHostedService<LimitWatcher>();
    builder.Services.AddHostedService<SrumImporter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceScanner>());
    builder.Services.AddHostedService<DeviceListener>();
    builder.Services.AddHostedService<PipeServer>();

    var host = builder.Build();
    // Asks Windows for the processor share chosen in Settings > General, before capture starts.
    Priority.ApplyToSelf(host.Services.GetRequiredService<ServiceState>().Setting(SettingKeys.Priority));
    host.Services.GetRequiredService<FirewallManager>().RestoreBlocks();
    return host;
}
