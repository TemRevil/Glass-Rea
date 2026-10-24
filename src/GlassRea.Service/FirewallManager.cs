using System.Runtime.InteropServices;
using GlassRea.Core.Alerts;
using GlassRea.Core.Paths;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;
using GlassRea.Service.Blocking;

namespace GlassRea.Service;

/// <summary>
/// Blocks apps. The copy setup installs does it through Windows Firewall (inbound and outbound rules in the "Glass-Rea"
/// group), so a block keeps working even when this service is stopped, and Windows Firewall is the source of truth
/// for what is blocked. The Store copy can't remove rules when it's uninstalled, so it blocks in the firewall engine
/// for as long as it runs instead (<see cref="WfpBlocker"/>), keeping the list in the database. Nothing is blocked while
/// Blocking apps is off (<see cref="Permissions"/>), and turning it off lifts every block (<see cref="LiftAll"/>).
/// </summary>
public sealed class FirewallManager(Database db, Permissions permissions, ILogger<FirewallManager> log) : IDisposable
{
    public const string Group = "Glass-Rea";
    public const string DiscoveryRule = Group + " device discovery";
    private const int DirIn = 1, DirOut = 2, ActionBlock = 0, ActionAllow = 1, AnyProtocol = 256, Udp = 17, AllProfiles = 0x7FFFFFFF, PrivateAndDomain = 1 | 2;
    private readonly Lock _gate = new();
    private HashSet<long> _blocked = [];
    private long _syncedAt = long.MinValue;
    private WfpBlocker? _engine;
    // The Store copy's blocks on programs that aren't there right now, tried again every 30 seconds until they are.
    private readonly Dictionary<long, (string Key, string Path, string? Service, string Name)> _waiting = [];
    private Timer? _retry;

    public static string RuleName(string appKey, bool outbound) => $"{Group} block {(outbound ? "out" : "in")}: {appKey}";

    /// <summary>One of the rules <see cref="SetBlocked"/> makes, as opposed to the discovery rule or anyone else's.</summary>
    public static bool IsBlockRule(string name) => name.StartsWith(Group + " block ", StringComparison.Ordinal);

    internal int WaitingCount { get { lock (_gate) return _waiting.Count; } }
    internal bool Retrying { get { lock (_gate) return _retry is not null; } }
    internal int BlockedCount { get { lock (_gate) return _blocked.Count; } }
    internal bool EngineOpen { get { lock (_gate) return _engine is not null; } }

    public HashSet<long> BlockedAppIds()
    {
        lock (_gate)
        {
            if (!Packaging.IsPackaged && Environment.TickCount64 - _syncedAt > 300_000) Sync();
            return [.. _blocked];
        }
    }

    /// <summary>The Store copy, at start: blocks again what was blocked when it last stopped.</summary>
    public void RestoreBlocks()
    {
        if (!Packaging.IsPackaged || !permissions.Allowed(Feature.Blocking)) return;
        lock (_gate)
        {
            foreach (var id in db.BlockedApps())
            {
                if (db.GetApp(id) is not { Path: { } path } app) continue;
                try
                {
                    var key = db.AllApps().First(a => a.Id == id).Key;
                    if (!BlockInEngine(key, path, app.Service, true)) Wait(id, key, path, app.Service, app.Name);
                    _blocked.Add(id);
                }
                catch (Exception ex) { log.LogWarning(ex, "Could not block {App} again", app.Name); }
            }
        }
        if (_blocked.Count > 0) log.LogInformation("Blocked {Count} apps again", _blocked.Count);
    }

    /// <summary>
    /// The apps the copy setup installs blocks, by key, read from its Windows Firewall rules (the Store copy carries
    /// them over when it takes that copy's place).
    /// </summary>
    public static List<string> ClassicBlockedKeys()
    {
        var prefix = RuleName("", outbound: true);
        var keys = new List<string>();
        foreach (dynamic rule in (System.Collections.IEnumerable)Policy().Rules)
            if (rule.Grouping is string group && group == Group && rule.Name is string name && name.StartsWith(prefix, StringComparison.Ordinal))
                keys.Add(name[prefix.Length..]);
        return keys;
    }

    /// <summary>Re-reads which known apps have our block rules, in case rules were changed in Windows Firewall directly.</summary>
    private void Sync()
    {
        var blocked = new HashSet<long>();
        try
        {
            dynamic rules = Policy().Rules;
            foreach (var (id, key, _) in db.AllApps())
                if (Exists(rules, RuleName(key, true))) blocked.Add(id);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not read firewall rules"); }
        _blocked = blocked;
        _syncedAt = Environment.TickCount64;
    }

    public void SetBlocked(long appId, bool block)
    {
        // Unblocking always works: it only takes a block away, a leftover one included.
        if (block && !permissions.Allowed(Feature.Blocking)) throw new InvalidOperationException(PermissionText.OffBody(Feature.Blocking));
        var app = db.GetApp(appId) ?? throw new InvalidOperationException("Unknown app.");
        var key = db.AllApps().First(a => a.Id == appId).Key;
        if (app.Path is null) throw new InvalidOperationException($"{app.Name} has no program file, so Windows Firewall cannot target it.");
        if (app.Service is { } s && s.Contains(','))
            throw new InvalidOperationException("This svchost process hosts several services; blocking it would cut them all off.");

        lock (_gate)
        {
            // Again under the lock: Blocking apps may have been turned off, and every block lifted, since the check above.
            if (block && !permissions.Allowed(Feature.Blocking)) throw new InvalidOperationException(PermissionText.OffBody(Feature.Blocking));
            if (Packaging.IsPackaged)
            {
                _waiting.Remove(appId);
                if (!BlockInEngine(key, app.Path, app.Service, block)) Wait(appId, key, app.Path, app.Service, app.Name);
                db.SetBlocked(appId, block);
                if (block) _blocked.Add(appId); else _blocked.Remove(appId);
                log.LogInformation("{Action} {App}", block ? "Blocked" : "Unblocked", app.Name);
                return;
            }
            dynamic rules = Policy().Rules;
            foreach (var outbound in new[] { true, false })
            {
                var name = RuleName(key, outbound);
                if (Exists(rules, name)) rules.Remove(name);
                if (!block) continue;

                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
                rule.Name = name;
                rule.Description = $"Created by Glass-Rea for {app.Name}.";
                rule.Grouping = Group;
                rule.Direction = outbound ? DirOut : DirIn;
                rule.Action = ActionBlock;
                rule.Protocol = AnyProtocol;
                rule.Profiles = AllProfiles;
                rule.InterfaceTypes = "All";
                if (PackagePaths.FamilyName(app.Path) is { } family && Native.AppContainerSid(family) is { } sid)
                    rule.LocalAppPackageId = sid; // Store apps are matched by package, not by exe path
                else
                    rule.ApplicationName = app.Path;
                if (app.Service is { } service) rule.ServiceName = service;
                rule.Enabled = true;
                rules.Add(rule);
            }
            if (block) _blocked.Add(appId); else _blocked.Remove(appId);
        }
        log.LogInformation("{Action} {App}", block ? "Blocked" : "Unblocked", app.Name);
    }

    /// <summary>False when the program to block isn't there now (see <see cref="Wait"/>).</summary>
    private bool BlockInEngine(string key, string path, string? service, bool block)
    {
        _engine ??= new WfpBlocker();
        if (!block) { _engine.Unblock(key); return true; }
        var packageSid = PackagePaths.FamilyName(path) is { } family ? Native.AppContainerSid(family) : null;
        return _engine.Block(key, path, service, packageSid);
    }

    /// <summary>
    /// A program on a drive that isn't plugged in can't be named to the firewall engine. It can't run either, so it
    /// counts as blocked, and is blocked within 30 seconds of its file coming back.
    /// </summary>
    internal void Wait(long appId, string key, string path, string? service, string name)
    {
        _waiting[appId] = (key, path, service, name);
        _retry ??= new Timer(_ => RetryWaiting(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        log.LogInformation("{App} isn't at {Path} now; it's blocked once it's back", name, path);
    }

    internal void RetryWaiting()
    {
        lock (_gate)
        {
            // A tick that waited on the lock while Blocking apps was turned off.
            if (_retry is null || !permissions.Allowed(Feature.Blocking)) return;
            foreach (var (id, (key, path, service, name)) in _waiting.ToList())
            {
                try
                {
                    if (!BlockInEngine(key, path, service, true)) continue;
                    _waiting.Remove(id);
                    log.LogInformation("Blocked {App}, now that it's back", name);
                }
                catch (Exception ex) { _waiting.Remove(id); log.LogWarning(ex, "Could not block {App}", name); }
            }
        }
    }

    /// <summary>
    /// Blocking apps was turned off: every block goes, from Windows Firewall (setup's copy) or the firewall engine (the
    /// Store copy), and none is remembered for when it's turned back on.
    /// </summary>
    public void LiftAll()
    {
        // One hold of the lock (it's re-entrant), so no retry tick or block lands between lifting and forgetting.
        var lifted = false;
        lock (_gate)
        {
            try
            {
                if (Packaging.IsPackaged) { lifted = _engine is not null; _engine?.Dispose(); _engine = null; }
                else
                {
                    dynamic rules = Policy().Rules;
                    var ours = new List<string>();
                    foreach (dynamic rule in (System.Collections.IEnumerable)rules)
                        if (rule.Grouping is string group && group == Group && rule.Name is string name && IsBlockRule(name)) ours.Add(name);
                    foreach (var name in ours) rules.Remove(name);
                    lifted = ours.Count > 0;
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Could not remove every block"); }
            lifted |= _blocked.Count > 0 || _waiting.Count > 0;
            ForgetBlocks();
        }
        if (lifted) log.LogInformation("Lifted every block");
    }

    /// <summary>What <see cref="LiftAll"/> forgets: the list, the blocks waiting for their program, and the retry timer.</summary>
    internal void ForgetBlocks()
    {
        lock (_gate)
        {
            _waiting.Clear();
            _retry?.Dispose();
            _retry = null;
            _blocked.Clear();
            _syncedAt = Environment.TickCount64;
        }
        db.ClearBlocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _retry?.Dispose();
            _engine?.Dispose();
        }
    }

    /// <summary>
    /// Lets this service, and only it, hear devices on the local network introduce themselves (DHCP on UDP 67,
    /// Bonjour on UDP 5353), on private and work networks only: on a network marked Public (a café, a hotel) it
    /// stays deaf. Rewritten at every start so it always points at the current program.
    /// </summary>
    public void AllowDiscovery()
    {
        try
        {
            lock (_gate)
            {
                dynamic rules = Policy().Rules;
                if (Exists(rules, DiscoveryRule)) rules.Remove(DiscoveryRule);
                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
                rule.Name = DiscoveryRule;
                rule.Description = "Lets Glass-Rea hear the devices on your network say their names, so the Devices page can show them.";
                rule.Grouping = Group;
                rule.Direction = DirIn;
                rule.Action = ActionAllow;
                rule.Protocol = Udp;
                rule.LocalPorts = "67,5353";
                rule.RemoteAddresses = "LocalSubnet";
                rule.Profiles = PrivateAndDomain;
                rule.ApplicationName = Environment.ProcessPath;
                // The Store package's service exe is its own (and a packaged service's SID type isn't ours to set).
                if (!Packaging.IsPackaged) rule.ServiceName = Packaging.ClassicService;
                rule.Enabled = true;
                rules.Add(rule);
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not add the device discovery firewall rule"); }
    }

    /// <summary>Device discovery was turned off: the service stops listening, so the rule letting it hear goes too.</summary>
    public void RemoveDiscovery()
    {
        try
        {
            lock (_gate)
            {
                dynamic rules = Policy().Rules;
                if (Exists(rules, DiscoveryRule)) rules.Remove(DiscoveryRule);
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not remove the device discovery firewall rule"); }
    }

    private static dynamic Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;

    private static bool Exists(dynamic rules, string name)
    {
        try { _ = rules.Item(name); return true; }
        catch (COMException) { return false; }
        catch (FileNotFoundException) { return false; }
    }
}
