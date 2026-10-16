using System.Runtime.InteropServices;
using GlassRea.Core.Paths;
using GlassRea.Core.Storage;

namespace GlassRea.Service;

/// <summary>
/// Blocks apps through Windows Firewall (inbound and outbound rules in the "Glass-Rea" group), so a block
/// keeps working even when this service is stopped. Windows Firewall is the source of truth for what is blocked.
/// </summary>
public sealed class FirewallManager(Database db, ILogger<FirewallManager> log)
{
    public const string Group = "Glass-Rea";
    private const int DirIn = 1, DirOut = 2, ActionBlock = 0, AnyProtocol = 256, AllProfiles = 0x7FFFFFFF;
    private readonly Lock _gate = new();
    private HashSet<long> _blocked = [];
    private long _syncedAt = long.MinValue;

    public static string RuleName(string appKey, bool outbound) => $"{Group} block {(outbound ? "out" : "in")}: {appKey}";

    public HashSet<long> BlockedAppIds()
    {
        lock (_gate)
        {
            if (Environment.TickCount64 - _syncedAt > 300_000) Sync();
            return [.. _blocked];
        }
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
        var app = db.GetApp(appId) ?? throw new InvalidOperationException("Unknown app.");
        var key = db.AllApps().First(a => a.Id == appId).Key;
        if (app.Path is null) throw new InvalidOperationException($"{app.Name} has no program file, so Windows Firewall cannot target it.");
        if (app.Service is { } s && s.Contains(','))
            throw new InvalidOperationException("This svchost process hosts several services; blocking it would cut them all off.");

        lock (_gate)
        {
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

    private static dynamic Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;

    private static bool Exists(dynamic rules, string name)
    {
        try { _ = rules.Item(name); return true; }
        catch (COMException) { return false; }
        catch (FileNotFoundException) { return false; }
    }
}
