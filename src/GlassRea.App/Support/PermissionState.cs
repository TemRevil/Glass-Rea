using GlassRea.Core.Protocol;

namespace GlassRea.App.Support;

/// <summary>
/// What the service may do, as it last said: the permission screen, the Permissions card in Settings and the bar a
/// page shows while its feature is off all read it here. Null from a service older than 1.2.8, which asks nothing.
/// </summary>
public static class PermissionState
{
    public static PermissionSet? Current { get; private set; }

    /// <summary>Raised on the window's thread when the service reports a change.</summary>
    public static event Action? Changed;

    public static void Use(PermissionSet? set)
    {
        if (set == Current) return;
        Current = set;
        Changed?.Invoke();
    }

    /// <summary>The service knows about permissions and <paramref name="f"/> is off.</summary>
    public static bool IsOff(Feature f) => Current is { } c && !c.Allowed(f);

    public static Task Turn(Feature f, bool on) => Set(new() { [PermissionKeys.For(f)] = on ? "true" : "false" });

    /// <summary>Everything on and the screen closed: the permission screen's "Allow all".</summary>
    public static Task AllowAll()
    {
        var all = PermissionKeys.All.ToDictionary(PermissionKeys.For, _ => "true");
        all[PermissionKeys.Reviewed] = "true";
        return Set(all);
    }

    /// <summary>The permission screen's "Done": what its switches show, and the screen closed.</summary>
    public static Task Choose(IReadOnlyDictionary<Feature, bool> chosen)
    {
        var changes = chosen.ToDictionary(c => PermissionKeys.For(c.Key), c => c.Value ? "true" : "false");
        changes[PermissionKeys.Reviewed] = "true";
        return Set(changes);
    }

    private static async Task Set(Dictionary<string, string> changes)
    {
        await Service.Do("permissions.set", settings: changes);
        Use(await Service.Call<PermissionSet>("permissions"));
    }
}
