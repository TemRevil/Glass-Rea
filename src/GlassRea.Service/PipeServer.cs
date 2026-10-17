using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;
using GlassRea.Service.Capture;

namespace GlassRea.Service;

/// <summary>Serves the desktop app over a local named pipe: one JSON request per line, one JSON response per line.</summary>
public sealed class PipeServer(Database db, UsageAccumulator usage, ProcessResolver resolver, FirewallManager firewall,
    DeviceScanner devices, ServiceState state, ILogger<PipeServer> log) : BackgroundService
{
    private static readonly HashSet<string> EditableSettings =
        [SettingKeys.DailyLimit, SettingKeys.MonthlyLimit, SettingKeys.AlertNewApp, SettingKeys.AlertNewDevice, SettingKeys.AlertDevicePresence,
         SettingKeys.TunnelProcesses, SettingKeys.Priority,
         SettingKeys.MonthStartDay, SettingKeys.WeekStart];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        while (!ct.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(Pipe.Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
            try { await pipe.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            _ = Task.Run(() => Serve(pipe, ct), ct);
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                Response response;
                try
                {
                    var request = JsonSerializer.Deserialize<Request>(line, Pipe.Json) ?? throw new InvalidOperationException("Empty request.");
                    response = new Response(true, JsonSerializer.SerializeToElement(Handle(request), Pipe.Json));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    response = new Response(false, Error: ex.Message);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Pipe.Json).AsMemory(), ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { /* client went away */ }
        catch (Exception ex) { log.LogWarning(ex, "Pipe client failed"); }
    }

    private object? Handle(Request r)
    {
        var now = DateTimeOffset.UtcNow;
        switch (r.Op)
        {
            case "status":
                return state.Snapshot();
            case "usage":
                return db.Report(r.From, r.To > 0 ? r.To : now.ToUnixTimeSeconds() + 1, firewall.BlockedAppIds());
            case "live":
                var (inn, outt) = usage.Live(now, 120);
                var top = usage.TopRates(now, 8).Select(rate =>
                {
                    var app = resolver.ByKey(rate.AppKey) ?? new AppIdentity(rate.AppKey, rate.AppKey, null, null, false);
                    return new LiveApp(db.EnsureApp(app, now.ToUnixTimeSeconds(), out _), app.Name, app.Path, rate.InPerSec, rate.OutPerSec);
                }).ToList();
                return new LiveSnapshot(now.ToUnixTimeSeconds(), inn, outt, top);
            case "hosts":
                return db.Hosts(r.AppId, UsageAccumulator.LocalDay(DateTimeOffset.FromUnixTimeSeconds(r.From)),
                    UsageAccumulator.LocalDay(DateTimeOffset.FromUnixTimeSeconds(r.To > 0 ? r.To : now.ToUnixTimeSeconds())));
            case "block":
                firewall.SetBlocked(r.AppId, r.Value);
                return null;
            case "devices":
                return db.Devices();
            case "device.rename":
                var mac = r.Settings?.GetValueOrDefault("mac") ?? throw new InvalidOperationException("Which device?");
                if (!DeviceLabel.TryCleanName(r.Settings.GetValueOrDefault("name"), out var name))
                    throw new InvalidOperationException($"A device name can be up to {DeviceLabel.MaxNameLength} characters, with no line breaks.");
                if (!db.RenameDevice(mac, name)) throw new InvalidOperationException("That device isn't in the list any more.");
                return null;
            case "scan":
                devices.RequestScan();
                return null;
            case "alerts":
                return db.Alerts(r.AfterId);
            case "settings":
                var all = new Dictionary<string, string>(ServiceState.Defaults);
                foreach (var (k, v) in db.AllSettings()) if (EditableSettings.Contains(k)) all[k] = v;
                return all;
            case "settings.set":
                foreach (var (k, v) in r.Settings ?? [])
                {
                    if (!EditableSettings.Contains(k)) throw new InvalidOperationException($"Unknown setting: {k}");
                    if (k is SettingKeys.DailyLimit or SettingKeys.MonthlyLimit && (!long.TryParse(v, out var n) || n < 0))
                        throw new InvalidOperationException("Limits must be a whole number of bytes.");
                    if (k == SettingKeys.Priority && !Priority.IsValid(v))
                        throw new InvalidOperationException("Priority must be normal, above_normal or high.");
                    if (k == SettingKeys.MonthStartDay && !Cycle.IsValidMonthDay(v))
                        throw new InvalidOperationException("The monthly reset day must be a whole number from 1 to 31.");
                    if (k == SettingKeys.WeekStart && !Cycle.IsValidWeekStart(v))
                        throw new InvalidOperationException("The week must start on saturday, sunday or monday.");
                    db.SetSetting(k, v);
                }
                if (r.Settings?.ContainsKey(SettingKeys.TunnelProcesses) == true) ApplyTunnelSetting();
                if (r.Settings?.ContainsKey(SettingKeys.Priority) == true && !Priority.ApplyToSelf(state.Setting(SettingKeys.Priority)))
                    log.LogWarning("Windows refused the service priority change");
                return null;
            default:
                throw new InvalidOperationException($"Unknown request: {r.Op}");
        }
    }

    private void ApplyTunnelSetting()
    {
        resolver.RefreshSettings();
        var names = state.TunnelNames();
        foreach (var (id, _, path) in db.AllApps()) db.SetTunnel(id, TunnelProcesses.IsTunnel(path, names));
    }
}
