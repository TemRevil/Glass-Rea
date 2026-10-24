namespace GlassRea.Core.Protocol;

/// <summary>What the service may do with its system rights. Each is off until the person turns it on.</summary>
public enum Feature { Usage, Blocking, History, Devices }

/// <summary>Where each permission is kept in the service's settings ("true" or "false").</summary>
public static class PermissionKeys
{
    public static readonly Feature[] All = [Feature.Usage, Feature.Blocking, Feature.History, Feature.Devices];

    /// <summary>"true" once the person has closed the permission screen with Done or Allow all.</summary>
    public const string Reviewed = "permissions_reviewed";

    public static string For(Feature f) => f switch
    {
        Feature.Usage => "allow_usage",
        Feature.Blocking => "allow_blocking",
        Feature.History => "allow_history",
        Feature.Devices => "allow_devices",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };
}

/// <param name="Reviewed">The person has seen the permission screen; until then the app shows it.</param>
public sealed record PermissionSet(bool Usage, bool Blocking, bool History, bool Devices, bool Reviewed)
{
    public bool Allowed(Feature f) => f switch
    {
        Feature.Usage => Usage,
        Feature.Blocking => Blocking,
        Feature.History => History,
        Feature.Devices => Devices,
        _ => false,
    };
}
