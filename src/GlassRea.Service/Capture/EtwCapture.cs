using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using GlassRea.Core.Net;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Core.Usage;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace GlassRea.Service.Capture;

/// <summary>
/// Real-time ETW session: Kernel-Network gives bytes per packet with the owning process id, Kernel-Process names
/// processes as they start, and DNS-Client maps the addresses apps connect to back to domain names.
/// Because it sees traffic at the TCP/IP layer, apps behind a VPN like Cloudflare WARP are counted as themselves,
/// including when the VPN works as a local proxy.
/// </summary>
public sealed class EtwCapture(UsageAccumulator usage, ProcessResolver resolver, DnsCache dns, ServiceState state,
    Database db, Permissions permissions, ILogger<EtwCapture> log) : BackgroundService
{
    public const string SessionName = "GlassRea-Capture";
    private const string KernelNetwork = "Microsoft-Windows-Kernel-Network";
    private const string KernelProcess = "Microsoft-Windows-Kernel-Process";
    private const string DnsClient = "Microsoft-Windows-DNS-Client";

    private volatile HashSet<IPAddress> _localAddresses = LocalAddresses(tunnelsOnly: false);
    private volatile HashSet<IPAddress> _tunnelAddresses = LocalAddresses(tunnelsOnly: true);
    private volatile TunnelPorts _tunnelPorts = TunnelPorts.None;
    private long _tunnelPortsAt = long.MinValue;
    private bool? _portsSwapped;
    private static readonly ConcurrentDictionary<int, bool> Outbound = new()
    {
        [10] = true, [26] = true, [42] = true, [58] = true,   // TCP/UDP send, IPv4/IPv6
        [11] = false, [27] = false, [43] = false, [59] = false, // TCP/UDP receive, IPv4/IPv6
    };

    /// <summary>Captures only while Usage per app is allowed; turning it off stops the session (no ETW session is left).</summary>
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            _localAddresses = LocalAddresses(tunnelsOnly: false);
            _tunnelAddresses = LocalAddresses(tunnelsOnly: true);
            resolver.RefreshDrives();
        };
        return permissions.WhileOn(Feature.Usage,
            run => Task.Factory.StartNew(() => Run(run), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default), ct);
    }

    private void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
                using var stop = ct.Register(() => session.Stop());
                session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, 0x10 | 0x20); // IPv4 | IPv6
                session.EnableProvider(KernelProcess, TraceEventLevel.Informational, 0x10);        // process start/stop
                session.EnableProvider(DnsClient, TraceEventLevel.Informational);
                var parser = new RegisteredTraceEventParser(session.Source);
                parser.All += OnEvent;

                var hour = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600).ToString();
                if (state.FirstLiveHour == 0) db.SetSetting(SettingKeys.FirstEtwHour, hour);
                if (state.CaptureFirstHour == 0) db.SetSetting(SettingKeys.CaptureFirstHour, hour);
                // The hour this build started in may hold bytes an older build counted without marking VPN traffic.
                if (db.GetSetting(SettingKeys.TunnelMarkedFrom) is null)
                    db.SetSetting(SettingKeys.TunnelMarkedFrom, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 + 1).ToString());
                state.Capturing = true;
                state.CaptureError = null;
                log.LogInformation("Traffic capture started");
                session.Source.Process(); // blocks until the session stops
            }
            catch (Exception ex)
            {
                state.CaptureError = ex.Message;
                log.LogError(ex, "Traffic capture failed");
            }
            state.Capturing = false;
            if (ct.IsCancellationRequested) state.CaptureError = null;
            else ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    private void OnEvent(TraceEvent e)
    {
        try
        {
            switch (e.ProviderName)
            {
                case KernelNetwork: OnPacket(e); break;
                case KernelProcess when (int)e.ID == 1: resolver.OnProcessStart(Int(e, "ProcessID"), e.PayloadByName("ImageName") as string ?? ""); break;
                case KernelProcess when (int)e.ID == 2: resolver.OnProcessStop(Int(e, "ProcessID")); break;
                case DnsClient when (int)e.ID == 3008 && Int(e, "QueryStatus") == 0:
                    dns.Add(e.PayloadByName("QueryName") as string, e.PayloadByName("QueryResults") as string);
                    break;
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Skipped event {Provider}/{Id}", e.ProviderName, e.ID); }
    }

    private void OnPacket(TraceEvent e)
    {
        if (!Outbound.TryGetValue((int)e.ID, out var outbound)) return;
        var daddr = AddressScope.FromPayload(e.PayloadByName("daddr"));
        var saddr = AddressScope.FromPayload(e.PayloadByName("saddr"));
        if (daddr is null || saddr is null) return;
        // The remote end is whichever address is not one of ours; if both or neither are, use the destination.
        var local = _localAddresses;
        var remote = local.Contains(Plain(daddr)) && !local.Contains(Plain(saddr)) ? saddr : daddr;
        var app = resolver.Resolve(Int(e, "PID"));
        var time = new DateTimeOffset(e.TimeStamp);
        var size = Convert.ToInt64(e.PayloadByName("size"));
        if (AddressScope.Classify(daddr) == Scope.Loopback || AddressScope.Classify(saddr) == Scope.Loopback)
        {
            OnLoopback(e, app, time, size, outbound);
            return;
        }
        var mine = ReferenceEquals(remote, saddr) ? daddr : saddr;
        usage.Add(app, time, size, outbound, remote, _tunnelAddresses.Contains(Plain(mine)));
    }

    /// <summary>
    /// Traffic inside this PC is ignored, except an app talking to a VPN's local proxy or DNS (WARP in proxy mode,
    /// or its DNS on 127.0.2.2): that is the app's internet use, which the VPN then carries as its own.
    /// </summary>
    private void OnLoopback(TraceEvent e, AppIdentity app, DateTimeOffset time, long size, bool outbound)
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _tunnelPortsAt) > 15_000)
        {
            Interlocked.Exchange(ref _tunnelPortsAt, Environment.TickCount64);
            try { _tunnelPorts = TunnelPorts.From(Native.LocalSockets(), pid => resolver.Resolve(pid).IsTunnel); }
            catch (Exception ex) { log.LogDebug(ex, "Could not read the socket tables"); }
        }
        var ports = _tunnelPorts;
        if (ports.IsEmpty) return;
        var tcp = (int)e.ID is 10 or 11 or 26 or 27;
        var sport = Int(e, "sport");
        var dport = Int(e, "dport");
        if (app.IsTunnel)
        {
            _portsSwapped ??= ports.OrderFrom(tcp, sport, dport);
            return;
        }
        if (ports.Has(tcp, sport, _portsSwapped) || ports.Has(tcp, dport, _portsSwapped))
            usage.AddThroughTunnel(app, time, size, outbound);
    }

    private static IPAddress Plain(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private static int Int(TraceEvent e, string field) => Convert.ToInt32(e.PayloadByName(field));

    /// <summary>This PC's addresses; with tunnelsOnly, just those of VPN adapters.</summary>
    private static HashSet<IPAddress> LocalAddresses(bool tunnelsOnly)
    {
        var set = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (tunnelsOnly && !TunnelAdapters.IsTunnel(nic.Name, nic.Description, (int)nic.NetworkInterfaceType)) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    set.Add(a.Address);
            }
        }
        catch { /* keep the empty set; the destination fallback still works */ }
        return set;
    }
}

/// <summary>Remembers which domain each IP address was looked up as, so connections show names instead of numbers.</summary>
public sealed class DnsCache
{
    private readonly ConcurrentDictionary<string, string> _pending = new();

    public void Add(string? name, string? results)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (var ip in DnsResults.Parse(results)) _pending[ip.ToString()] = name.TrimEnd('.');
    }

    public List<(string Ip, string Name)> Drain()
    {
        var list = new List<(string, string)>();
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var name)) list.Add((key, name));
        return list;
    }
}
