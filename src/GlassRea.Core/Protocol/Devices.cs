namespace GlassRea.Core.Protocol;

/// <summary>What kind of thing a device is, as the scanner judged it. Stored and sent as these strings.</summary>
public static class DeviceKinds
{
    public const string Router = "router";
    public const string Computer = "computer";
    public const string Phone = "phone";
    public const string Tablet = "tablet";
    public const string Tv = "tv";
    public const string Speaker = "speaker";
    public const string Printer = "printer";
    public const string Console = "console";
    public const string Camera = "camera";
    public const string Watch = "watch";
    public const string SmartHome = "smart_home";
    public const string Storage = "storage";
    public const string Network = "network";
    public const string Unknown = "unknown";

    /// <summary>Plain words for a kind, as in "Samsung phone".</summary>
    public static string Noun(string? kind) => kind switch
    {
        Router => "router",
        Computer => "computer",
        Phone => "phone",
        Tablet => "tablet",
        Tv => "TV",
        Speaker => "speaker",
        Printer => "printer",
        Console => "game console",
        Camera => "camera",
        Watch => "watch",
        SmartHome => "smart home device",
        Storage => "network storage",
        Network => "network device",
        _ => "device",
    };
}

/// <summary>Operating systems the scanner can tell apart.</summary>
public static class DeviceOs
{
    public const string Ios = "ios";
    public const string IpadOs = "ipados";
    public const string MacOs = "macos";
    public const string TvOs = "tvos";
    public const string WatchOs = "watchos";
    public const string Android = "android";
    public const string Windows = "windows";
    public const string Linux = "linux";

    public static string? Display(string? os) => os switch
    {
        Ios => "iOS",
        IpadOs => "iPadOS",
        MacOs => "macOS",
        TvOs => "tvOS",
        WatchOs => "watchOS",
        Android => "Android",
        Windows => "Windows",
        Linux => "Linux",
        _ => null,
    };
}

public static class DeviceLabel
{
    /// <summary>
    /// What to call a device: the name it gives itself, or else what it is ("iPhone", "Samsung phone", "Apple device"),
    /// or, for a device hiding behind a private address with nothing else to go on, "Private device".
    /// </summary>
    public static string For(DeviceInfo d)
    {
        if (!string.IsNullOrWhiteSpace(d.Name)) return d.Name.Trim();
        return What(d) ?? (d.Randomized ? "Private device" : "Unnamed device");
    }

    /// <summary>What the device is, without its name: "iPhone", "Samsung phone", "Greenpacket router". Null when unknown.</summary>
    public static string? What(DeviceInfo d)
    {
        if (d.IsGateway) return d.Vendor is null ? "Router" : $"{d.Vendor} router";
        if (!string.IsNullOrWhiteSpace(d.Model)) return d.Model.Trim();
        var apple = d.Os switch
        {
            DeviceOs.Ios => "iPhone",
            DeviceOs.IpadOs => "iPad",
            DeviceOs.MacOs => "Mac",
            DeviceOs.TvOs => "Apple TV",
            DeviceOs.WatchOs => "Apple Watch",
            _ => null,
        };
        if (apple is not null) return apple;
        var noun = DeviceKinds.Noun(d.Kind);
        if (d.Vendor is not null) return $"{d.Vendor} {noun}";
        if (d.Os is DeviceOs.Windows && d.Kind is DeviceKinds.Computer) return "Windows PC";
        if (DeviceOs.Display(d.Os) is { } os && d.Kind is not (null or DeviceKinds.Unknown)) return $"{os} {noun}";
        if (d.Kind is not (null or DeviceKinds.Unknown)) return char.ToUpperInvariant(noun[0]) + noun[1..];
        return null;
    }
}
