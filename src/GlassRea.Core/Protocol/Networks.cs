namespace GlassRea.Core.Protocol;

/// <summary>What carries a network, as Windows reports it. Stored and sent as these strings.</summary>
public static class NetworkKinds
{
    public const string WiFi = "wifi";
    public const string Wired = "wired";
    public const string Mobile = "mobile";

    public static string? Display(string? kind) => kind switch
    {
        WiFi => "Wi-Fi",
        Wired => "Wired",
        Mobile => "Mobile broadband",
        _ => null,
    };
}

public static class NetworkLabel
{
    /// <summary>The id of the use that has no known network.</summary>
    public const long Unknown = 0;

    public const int MaxNameLength = DeviceLabel.MaxNameLength;

    /// <summary>What to call a network: the name you gave it, or else the one Windows gives it.</summary>
    public static string For(NetworkInfo n)
    {
        if (n.Id == Unknown) return "Unknown network";
        if (!string.IsNullOrWhiteSpace(n.CustomName)) return n.CustomName.Trim();
        return string.IsNullOrWhiteSpace(n.Name) ? "Unnamed network" : n.Name.Trim();
    }

    /// <summary>A name typed in Settings, checked as a device's is (<see cref="DeviceLabel.TryCleanName"/>). Blank means Windows' name.</summary>
    public static bool TryCleanName(string? name, out string? clean) => DeviceLabel.TryCleanName(name, out clean);
}
