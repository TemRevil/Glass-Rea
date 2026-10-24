using GlassRea.Core.Protocol;

namespace GlassRea.Core.Alerts;

/// <summary>
/// The words of the permission screen, the Permissions card in Settings and the bar a page shows while its feature is
/// off. Each says what the feature does and why it needs the service's system rights.
/// </summary>
public static class PermissionText
{
    public static string Title(Feature f) => f switch
    {
        Feature.Usage => "Usage per app",
        Feature.Blocking => "Blocking apps",
        Feature.History => "Past usage from Windows",
        Feature.Devices => "Device discovery",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    public static string Why(Feature f) => f switch
    {
        Feature.Usage => "Counts each app's internet use from Windows' network events. Only Windows itself can read those, so the service runs as SYSTEM.",
        Feature.Blocking => "Lets Block internet stop an app in Windows' firewall. Only an administrator or SYSTEM can add firewall filters.",
        Feature.History => "Reads once what Windows recorded before Glass-Rea was installed. Windows keeps that file where only SYSTEM can open it.",
        Feature.Devices => "Finds the phones, TVs and computers on your network and listens for their names. This adds a firewall rule for your private networks only.",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    public static string OffTitle(Feature f) => Title(f) + " is off";

    public static string OffBody(Feature f) => f switch
    {
        Feature.Usage => "Nothing is being counted. Turn it on to see what each app uses.",
        Feature.Blocking => "Apps can't be blocked. Turn it on to use Block internet.",
        Feature.History => "Windows' past usage isn't read.",
        Feature.Devices => "Your network isn't scanned. Turn it on to see its devices.",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };
}
