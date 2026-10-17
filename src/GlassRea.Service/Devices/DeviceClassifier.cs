using System.Text.RegularExpressions;
using GlassRea.Core.Protocol;

namespace GlassRea.Service.Devices;

/// <summary>Everything the scan learned about one address.</summary>
public sealed class DeviceEvidence
{
    public HashSet<string> HostNames { get; } = new(StringComparer.OrdinalIgnoreCase);   // Bonjour, NetBIOS, DNS
    public List<string> InstanceNames { get; } = [];                                        // "Tem's iPhone", from Bonjour services
    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);     // "_googlecast._tcp"
    public Dictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase); // model=, md=, fn=, ty=
    public string? NetBiosName { get; set; }
    public string? DhcpName { get; set; }    // what the device sends when it joins the network: "Tems-iPhone"
    public string? DhcpVendor { get; set; }  // DHCP option 60: "android-dhcp-14", "MSFT 5.0"
    public HashSet<int> OpenPorts { get; } = [];
    public UpnpDescription? Upnp { get; set; }
    public string? UpnpServer { get; set; }  // the SSDP SERVER header: "Linux/4.9 UPnP/1.0 ..."
    public string? ReverseDns { get; set; }
    public int? Ttl { get; set; }            // from the ping reply: 128 Windows, 64 Unix-like, 255 network gear
    public string? Vendor { get; set; }
    public bool Randomized { get; set; }
    public bool IsGateway { get; set; }
    public bool IsThisPc { get; set; }

    /// <summary>What the passive listener heard, flattened to (key, value) pairs so it can be stored per device.</summary>
    public IEnumerable<(string Key, string Value)> Hints()
    {
        if (DhcpName is not null) yield return ("dhcp_name", DhcpName);
        if (DhcpVendor is not null) yield return ("dhcp_vendor", DhcpVendor);
        foreach (var h in HostNames) yield return ("host:" + h, "");
        foreach (var n in InstanceNames) yield return ("name:" + n, "");
        foreach (var s in Services) yield return ("svc:" + s, "");
        foreach (var (k, v) in Txt) yield return ("txt:" + k, v);
    }

    public void AddHints(IEnumerable<(string Key, string Value)> hints)
    {
        foreach (var (key, value) in hints)
        {
            var colon = key.IndexOf(':');
            var rest = colon < 0 ? "" : key[(colon + 1)..];
            switch (colon < 0 ? key : key[..colon])
            {
                case "dhcp_name": DhcpName ??= value; break;
                case "dhcp_vendor": DhcpVendor ??= value; break;
                case "host": HostNames.Add(rest); break;
                case "name": if (!InstanceNames.Contains(rest)) InstanceNames.Add(rest); break;
                case "svc": Services.Add(rest); break;
                case "txt": Txt.TryAdd(rest, value); break;
            }
        }
    }
}

public sealed record DeviceIdentity(string? Name, string? Model, string Kind, string? Os);

/// <summary>
/// Turns scan evidence into a name, a kind (phone, TV, printer...) and an operating system. The strongest evidence
/// wins: what a device says it is (Bonjour model, UPnP type, printer services), then its name, then the ports it
/// listens on, then who made it.
/// </summary>
public static partial class DeviceClassifier
{
    public static DeviceIdentity Classify(DeviceEvidence e)
    {
        var (kind, os) = KindAndOs(e);
        return new DeviceIdentity(BestName(e), Model(e), kind, os);
    }

    // ---------- name ----------


    public static string? BestName(DeviceEvidence e)
    {
        // A name someone gave it ("Living Room TV", "Tem's iPhone") beats a host name.
        var given = new[] { e.Txt.GetValueOrDefault("fn"), e.Upnp?.FriendlyName }.Concat(e.InstanceNames)
            .Select(Tidy).FirstOrDefault(n => n is not null && !LooksLikeId(n));
        if (given is not null) return given;
        var host = new[] { e.DhcpName, e.NetBiosName }.Concat(e.HostNames).Append(e.ReverseDns)
            .Select(HostLabel).FirstOrDefault(n => n is not null && !GenericHost().IsMatch(n) && !LooksLikeId(n));
        return host;
    }

    /// <summary>"Tems-iPhone.local" → "Tems-iPhone"; "galaxy-s23.lan" → "galaxy-s23"; drops router-made names like "192-168-1-5".</summary>
    public static string? HostLabel(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var first = host.Trim().TrimEnd('.').Split('.')[0];
        if (first.Length == 0 || IpLike().IsMatch(first)) return null;
        return first;
    }

    private static string? Tidy(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        // Bonjour instance names can carry a MAC prefix ("A1B2C3D4E5F6@Living Room") or a trailing " (2)".
        var at = n.IndexOf('@');
        if (at >= 0 && at < n.Length - 1) n = n[(at + 1)..];
        n = Regex.Replace(n, @"\s\(\d+\)$", "");
        n = Regex.Replace(n, @"[-_][0-9a-f]{12,}$", "", RegexOptions.IgnoreCase); // "Google-Nest-Mini-3f2a9c...": the tail is an id
        return n.Length == 0 ? null : n;
    }

    /// <summary>Hex strings, UUIDs and MAC-like tokens aren't names.</summary>
    private static bool LooksLikeId(string n) => IdLike().IsMatch(n);

    /// <summary>Names a system makes up rather than one a person chose: "Android_HQJB9FND", "android-3f2a9c", "ESP_1A2B3C".</summary>
    [GeneratedRegex(@"^(localhost|unknown|device|espressif|esp|android|host)([-_][0-9a-z]{4,})?$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericHost();

    [GeneratedRegex(@"^(\d{1,3}[-.]){3}\d{1,3}$|^ip-")]
    private static partial Regex IpLike();

    [GeneratedRegex(@"^([0-9a-f]{2}[:-]?){6}$|^[0-9a-f]{8}-[0-9a-f]{4}-|^[0-9a-f]{12,}$", RegexOptions.IgnoreCase)]
    private static partial Regex IdLike();

    // ---------- model ----------

    private static string? Model(DeviceEvidence e)
    {
        var apple = AppleModel(e);
        if (apple is not null) return AppleFamily(apple) ?? apple;
        var md = e.Txt.GetValueOrDefault("md") ?? e.Txt.GetValueOrDefault("usb_MDL") ?? e.Txt.GetValueOrDefault("ty") ?? e.Upnp?.ModelName;
        return string.IsNullOrWhiteSpace(md) ? null : md.Trim();
    }

    /// <summary>Apple's model identifier from Bonjour: "iPhone15,2", "MacBookPro18,3", "AppleTV11,1", "AudioAccessory5,1".</summary>
    private static string? AppleModel(DeviceEvidence e) =>
        new[] { e.Txt.GetValueOrDefault("model"), e.Txt.GetValueOrDefault("rpMd"), e.Txt.GetValueOrDefault("am") }
            .FirstOrDefault(m => m is not null && AppleId().IsMatch(m));

    [GeneratedRegex(@"^(iPhone|iPad|iPod|Mac|MacBook|MacBookPro|MacBookAir|iMac|MacPro|Macmini|MacStudio|AppleTV|AudioAccessory|Watch)\d*,?\d*", RegexOptions.IgnoreCase)]
    private static partial Regex AppleId();

    private static string? AppleFamily(string id)
    {
        var l = id.ToLowerInvariant();
        return l.StartsWith("iphone") ? "iPhone" : l.StartsWith("ipad") ? "iPad" : l.StartsWith("ipod") ? "iPod touch"
            : l.StartsWith("macbookair") ? "MacBook Air" : l.StartsWith("macbookpro") ? "MacBook Pro" : l.StartsWith("macbook") ? "MacBook"
            : l.StartsWith("imac") ? "iMac" : l.StartsWith("macmini") ? "Mac mini" : l.StartsWith("macstudio") ? "Mac Studio"
            : l.StartsWith("macpro") ? "Mac Pro" : l.StartsWith("mac") ? "Mac" : l.StartsWith("appletv") ? "Apple TV"
            : l.StartsWith("audioaccessory") ? "HomePod" : l.StartsWith("watch") ? "Apple Watch" : null;
    }

    // ---------- kind and OS ----------

    private static readonly string[] PrinterServices = ["_ipp._tcp", "_ipps._tcp", "_printer._tcp", "_pdl-datastream._tcp", "_uscan._tcp", "_scanner._tcp"];
    private static readonly string[] PhoneMakers = ["Samsung", "OPPO", "Xiaomi", "Huawei", "Honor", "vivo", "OnePlus", "realme", "Motorola", "Nokia", "Infinix", "Tecno", "itel", "Transsion", "Meizu", "ZTE"];
    private static readonly string[] PrinterMakers = ["Epson", "Brother", "Canon", "Kyocera", "Lexmark", "Xerox", "Ricoh"];
    private static readonly string[] NetworkMakers = ["TP-Link", "Netgear", "Ubiquiti", "Cisco", "D-Link", "Tenda", "Mercusys", "Zyxel", "MikroTik", "Arcadyan", "Sagemcom", "Technicolor", "FiberHome"];
    private static readonly string[] SmartHomeMakers = ["Espressif", "Tuya", "Shelly", "Philips Hue"];
    private static readonly string[] CameraMakers = ["Hikvision", "Dahua", "EZVIZ"];
    private static readonly string[] TvMakers = ["Samsung", "LG", "Sony", "Hisense", "TCL", "Philips", "Vizio", "Panasonic", "Sharp", "Toshiba", "Roku"];
    private static readonly string[] PcCardMakers = ["Intel", "Realtek", "AzureWave", "Lite-On", "Foxconn", "Murata", "MediaTek", "Qualcomm", "Dell", "Lenovo", "ASUS", "Acer", "MSI", "Gigabyte"];

    public static (string Kind, string? Os) KindAndOs(DeviceEvidence e)
    {
        if (e.IsThisPc) return (DeviceKinds.Computer, DeviceOs.Windows);

        // 1. What the device says it is.
        if (AppleModel(e) is { } apple)
        {
            var l = apple.ToLowerInvariant();
            if (l.StartsWith("iphone") || l.StartsWith("ipod")) return (DeviceKinds.Phone, DeviceOs.Ios);
            if (l.StartsWith("ipad")) return (DeviceKinds.Tablet, DeviceOs.IpadOs);
            if (l.StartsWith("appletv")) return (DeviceKinds.Tv, DeviceOs.TvOs);
            if (l.StartsWith("audioaccessory")) return (DeviceKinds.Speaker, null);
            if (l.StartsWith("watch")) return (DeviceKinds.Watch, DeviceOs.WatchOs);
            return (DeviceKinds.Computer, DeviceOs.MacOs);
        }
        if (e.IsGateway) return (DeviceKinds.Router, null);
        if (PrinterServices.Any(e.Services.Contains) || Has(e.Upnp?.DeviceType, "printer")) return (DeviceKinds.Printer, null);
        var upnpType = e.Upnp?.DeviceType ?? "";
        if (Has(upnpType, "InternetGatewayDevice") || Has(upnpType, "WANDevice")) return (DeviceKinds.Router, null);
        var serverOs = Has(e.UpnpServer, "android") ? DeviceOs.Android : Has(e.UpnpServer, "windows") ? DeviceOs.Windows : null;
        if (e.Services.Contains("_androidtvremote2._tcp") || e.Services.Contains("_androidtvremote._tcp")) return (DeviceKinds.Tv, DeviceOs.Android);
        if (e.Services.Contains("_amzn-wplay._tcp")) return (DeviceKinds.Tv, DeviceOs.Android);
        if (e.Services.Contains("_googlecast._tcp"))
        {
            var md = e.Txt.GetValueOrDefault("md") ?? "";
            return Has(md, "Home") || Has(md, "Nest Audio") || Has(md, "Nest Mini") || Has(md, "Speaker") || Has(md, "Audio")
                ? (DeviceKinds.Speaker, null) : (DeviceKinds.Tv, null);
        }
        if (Has(upnpType, "MediaRenderer") || Has(upnpType, "MediaServer") || Has(upnpType, "dial"))
        {
            if (Has(e.Upnp?.FriendlyName, "TV") || Has(e.Upnp?.ModelName, "TV") || TvMakers.Contains(e.Upnp?.Manufacturer is { } m ? Oui.Clean(m) : "")) return (DeviceKinds.Tv, serverOs);
            if (Has(e.Upnp?.ModelDescription, "speaker") || Has(e.Upnp?.Manufacturer, "Sonos") || Has(e.Upnp?.Manufacturer, "Bose")) return (DeviceKinds.Speaker, null);
        }
        if (e.Services.Contains("_spotify-connect._tcp") && !e.Services.Contains("_companion-link._tcp")) return (DeviceKinds.Speaker, null);
        if (e.Services.Contains("_raop._tcp") && !e.Services.Contains("_airplay._tcp")) return (DeviceKinds.Speaker, null);
        if (e.Services.Contains("_hap._tcp") || e.Services.Contains("_homekit._tcp") || e.Services.Contains("_matter._tcp")) return (DeviceKinds.SmartHome, null);

        // 2. What it calls itself, and the model it says it is: "Samsung 7 Series (43)" is a TV, though Samsung makes phones too.
        var names = string.Join(' ', e.HostNames.Append(e.DhcpName ?? "").Append(e.NetBiosName ?? "").Append(e.ReverseDns ?? "").Concat(e.InstanceNames)
            .Append(e.Txt.GetValueOrDefault("fn") ?? "").Append(e.Txt.GetValueOrDefault("md") ?? "")
            .Append(e.Upnp?.FriendlyName ?? "").Append(e.Upnp?.ModelName ?? "")).ToLowerInvariant();
        if (Name(names, "iphone")) return (DeviceKinds.Phone, DeviceOs.Ios);
        if (Name(names, "ipad")) return (DeviceKinds.Tablet, DeviceOs.IpadOs);
        if (Name(names, "macbook", "imac", "mac-mini", "macmini", "mac-studio")) return (DeviceKinds.Computer, DeviceOs.MacOs);
        if (Name(names, "galaxy-tab", "galaxytab", "tab-s", "lenovo-tab", "mipad", "matepad")) return (DeviceKinds.Tablet, DeviceOs.Android);
        if (Name(names, "android", "galaxy", "pixel", "redmi", "poco", "oppo", "vivo", "oneplus", "huawei", "honor", "realme", "xiaomi", "moto", "infinix", "tecno")
            || AndroidModel().IsMatch(names)) return (DeviceKinds.Phone, DeviceOs.Android);
        if (Name(names, "printer", "epson", "brother", "canon", "laserjet", "deskjet", "officejet", "envy") || HpPrinter().IsMatch(names)) return (DeviceKinds.Printer, null);
        if (Name(names, "xbox", "playstation", "ps4", "ps5", "nintendo")) return (DeviceKinds.Console, null);
        if (Name(names, "tv", "bravia", "roku", "firetv", "fire-tv", "chromecast", "shield", "webos", "tizen", "qled", "oled") || SamsungTv().IsMatch(names))
            return (DeviceKinds.Tv, null);
        if (Name(names, "echo", "alexa", "homepod", "sonos", "nest-mini", "nest-audio", "speaker")) return (DeviceKinds.Speaker, null);
        if (Name(names, "cam", "camera", "ipcam", "doorbell")) return (DeviceKinds.Camera, null);
        if (Name(names, "nas", "synology", "diskstation", "qnap", "truenas")) return (DeviceKinds.Storage, DeviceOs.Linux);
        if (Name(names, "esp", "esp32", "esp8266", "tasmota", "shelly", "tuya", "plug", "bulb", "sonoff")) return (DeviceKinds.SmartHome, null);
        if (Name(names, "watch")) return (DeviceKinds.Watch, null);
        if (Name(names, "desktop", "laptop", "pc", "win", "workstation")) return (DeviceKinds.Computer, DeviceOs.Windows);
        if (Name(names, "raspberrypi", "ubuntu", "debian", "fedora")) return (DeviceKinds.Computer, DeviceOs.Linux);
        if (e.DhcpVendor?.StartsWith("android-dhcp", StringComparison.OrdinalIgnoreCase) == true) return (DeviceKinds.Phone, DeviceOs.Android);
        if (e.DhcpVendor?.StartsWith("MSFT", StringComparison.OrdinalIgnoreCase) == true) return (DeviceKinds.Computer, DeviceOs.Windows);
        if (e.NetBiosName is not null) return (DeviceKinds.Computer, DeviceOs.Windows);
        if (e.Services.Contains("_companion-link._tcp") || e.Services.Contains("_apple-mobdev2._tcp")) return (DeviceKinds.Phone, DeviceOs.Ios);
        if (e.Services.Contains("_smb._tcp") || e.Services.Contains("_afpovertcp._tcp")) return (DeviceKinds.Storage, null);
        if (e.Services.Contains("_workstation._tcp") || e.Services.Contains("_ssh._tcp")) return (DeviceKinds.Computer, DeviceOs.Linux);

        // 3. What it listens on.
        var v = e.Vendor;
        var port = e.OpenPorts;
        if (port.Contains(62078)) return (DeviceKinds.Phone, DeviceOs.Ios); // iPhone and iPad sync service; an iPad usually names itself above
        if (port.Contains(9100) || port.Contains(631) || port.Contains(515)) return (DeviceKinds.Printer, null);
        if (port.Contains(8008) || port.Contains(8009) || port.Contains(8060)) return (DeviceKinds.Tv, null); // Chromecast, Google TV, Roku
        if (port.Contains(1400)) return (DeviceKinds.Speaker, null);                                       // Sonos
        if (port.Contains(554)) return (DeviceKinds.Camera, null);                                         // RTSP video
        if (port.Contains(5555)) return (DeviceKinds.Tv, DeviceOs.Android);                                // Android TV boxes
        if (port.Contains(7000)) return v is "Apple" ? (DeviceKinds.Computer, DeviceOs.MacOs) : (DeviceKinds.Tv, null); // AirPlay receiver
        if (port.Contains(445) || port.Contains(139)) return e.Ttl is 128 ? (DeviceKinds.Computer, DeviceOs.Windows) : (DeviceKinds.Storage, null);

        // 4. Who made it, with the ping's TTL as a tie-breaker.
        if (v is "Apple") return (DeviceKinds.Unknown, null); // a Mac, iPad, watch or HomePod: nothing above told them apart
        if (v is "PlayStation" or "Nintendo") return (DeviceKinds.Console, null);
        if (v is "Amazon") return (DeviceKinds.Speaker, null);
        if (v is "Google") return (DeviceKinds.Speaker, null);
        if (v is "Raspberry Pi") return (DeviceKinds.Computer, DeviceOs.Linux);
        if (v is not null && SmartHomeMakers.Contains(v)) return (DeviceKinds.SmartHome, null);
        if (v is not null && CameraMakers.Contains(v)) return (DeviceKinds.Camera, null);
        if (v is not null && PrinterMakers.Contains(v)) return (DeviceKinds.Printer, null);
        if (v is not null && NetworkMakers.Contains(v)) return (DeviceKinds.Network, null);
        if (v is not null && PhoneMakers.Contains(v))
            return e.Ttl is null or 64 ? (DeviceKinds.Phone, DeviceOs.Android) : (DeviceKinds.Unknown, null);
        if (v is not null && PcCardMakers.Contains(v)) return (DeviceKinds.Computer, e.Ttl is 128 ? DeviceOs.Windows : null);
        if (e.Ttl is 128) return (DeviceKinds.Computer, DeviceOs.Windows);
        if (e.Randomized && e.Ttl is 64 or null) return (DeviceKinds.Phone, null); // phones are what hide behind private addresses
        if (e.Ttl is 255) return (DeviceKinds.Network, null);
        return (DeviceKinds.Unknown, serverOs);
    }

    private static bool Has(string? text, string part) => text?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Whole-word match inside host names, where words are split by '-', '_', '.', spaces or digits.</summary>
    private static bool Name(string names, params string[] words) =>
        words.Any(w => Regex.IsMatch(names, $@"(^|[^a-z]){Regex.Escape(w)}([^a-z]|$)"));

    [GeneratedRegex(@"(^|[^a-z])(sm-[a-z]\d{3}|cph\d{4}|rmx\d{4}|m\d{4}[a-z]\d+[a-z]?|v\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex AndroidModel(); // Samsung SM-S911B, OPPO CPH2xxx, realme RMX3xxx, Xiaomi 2201117TG-style, vivo V2xxx

    [GeneratedRegex(@"(^|[^a-z0-9])((ue|un|ua|qe|qn|qa|gq|gu)\d{2}[a-z]{1,3}\d{2,4}|\d{1,2} series([^a-z]|$))", RegexOptions.IgnoreCase)]
    private static partial Regex SamsungTv(); // Samsung TV models (UE43RU7100, QN55Q60T) and the names they come with ("Samsung 7 Series")

    [GeneratedRegex(@"(^|[^a-z])hp[0-9a-f]{6}([^a-z0-9]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex HpPrinter(); // HP printers call themselves HPxxxxxx after their MAC
}
