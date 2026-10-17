using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace GlassRea.Service.Devices;

/// <summary>
/// Who made a network card, from the first 24, 28 or 36 bits of its hardware address (the IEEE registries,
/// refreshed by scripts\update-oui.ps1). Private, randomized addresses have no maker.
/// </summary>
public static partial class Oui
{
    private static readonly Lazy<Dictionary<string, string>> Table = new(Load);

    /// <summary>The maker's short name ("Apple", "Samsung", "TP-Link"), or null when unknown or randomized.</summary>
    public static string? Vendor(byte[] mac)
    {
        if (mac.Length < 6 || (mac[0] & 0x02) != 0) return null; // locally administered: no registered maker
        var hex = Convert.ToHexString(mac);
        foreach (var len in new[] { 9, 7, 6 })
            if (Table.Value.TryGetValue(hex[..len], out var name)) return Clean(name);
        return null;
    }

    public static string? Vendor(string mac) =>
        Vendor(mac.Split(':', '-').Where(p => p.Length == 2).Select(p => byte.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray());

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(60_000, StringComparer.Ordinal);
        using var file = typeof(Oui).Assembly.GetManifestResourceStream("GlassRea.Service.Devices.oui.tsv.gz")
                         ?? throw new InvalidOperationException("The vendor table is missing from the build.");
        using var reader = new StreamReader(new GZipStream(file, CompressionMode.Decompress));
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t');
            if (tab > 0) map[line[..tab]] = line[(tab + 1)..];
        }
        return map;
    }

    // Brands people know, matched against the registered company name.
    private static readonly (string Contains, string Brand)[] Brands =
    [
        ("apple", "Apple"), ("samsung", "Samsung"), ("oppo", "OPPO"), ("xiaomi", "Xiaomi"), ("huawei", "Huawei"),
        ("honor device", "Honor"), ("vivo mobile", "vivo"), ("oneplus", "OnePlus"), ("realme", "realme"), ("google", "Google"),
        ("amazon", "Amazon"), ("sony interactive", "PlayStation"), ("sony", "Sony"), ("lg electronics", "LG"), ("lg innotek", "LG"),
        ("intel corporate", "Intel"), ("tp-link", "TP-Link"), ("tp link", "TP-Link"), ("netgear", "Netgear"), ("asustek", "ASUS"),
        ("hewlett packard", "HP"), ("hp inc", "HP"), ("canon", "Canon"), ("seiko epson", "Epson"), ("brother industries", "Brother"),
        ("microsoft", "Microsoft"), ("nintendo", "Nintendo"), ("espressif", "Espressif"), ("tuya", "Tuya"), ("raspberry pi", "Raspberry Pi"),
        ("roku", "Roku"), ("hisense", "Hisense"), ("tcl", "TCL"), ("lenovo", "Lenovo"), ("dell", "Dell"), ("motorola", "Motorola"),
        ("nokia", "Nokia"), ("zte", "ZTE"), ("cisco", "Cisco"), ("ubiquiti", "Ubiquiti"), ("synology", "Synology"), ("qnap", "QNAP"),
        ("sonos", "Sonos"), ("bose", "Bose"), ("signify", "Philips Hue"), ("philips", "Philips"), ("d-link", "D-Link"),
        ("tenda", "Tenda"), ("mercusys", "Mercusys"), ("realtek", "Realtek"), ("mediatek", "MediaTek"), ("qualcomm", "Qualcomm"),
        ("azurewave", "AzureWave"), ("liteon", "Lite-On"), ("lite-on", "Lite-On"), ("hon hai", "Foxconn"), ("murata", "Murata"),
        ("kyocera", "Kyocera"), ("lexmark", "Lexmark"), ("xerox", "Xerox"), ("ricoh", "Ricoh"), ("hikvision", "Hikvision"),
        ("dahua", "Dahua"), ("ezviz", "EZVIZ"), ("shelly", "Shelly"), ("arcadyan", "Arcadyan"), ("sagemcom", "Sagemcom"),
        ("technicolor", "Technicolor"), ("zyxel", "Zyxel"), ("mikrotik", "MikroTik"), ("fiberhome", "FiberHome"), ("garmin", "Garmin"),
        ("fitbit", "Fitbit"), ("nvidia", "NVIDIA"), ("valve", "Valve"), ("vizio", "Vizio"), ("panasonic", "Panasonic"), ("sharp", "Sharp"),
        ("toshiba", "Toshiba"), ("acer", "Acer"), ("msi", "MSI"), ("gigabyte", "Gigabyte"), ("infinix", "Infinix"), ("tecno", "Tecno"),
        ("itel", "itel"), ("transsion", "Transsion"), ("meizu", "Meizu"), ("hmd global", "Nokia"),
    ];

    [GeneratedRegex(@"[\s,\.]+(inc|incorporated|corp|corporation|corporate|co|company|ltd|limited|llc|l\.l\.c|gmbh|s\.a|sa|ag|bv|b\.v|plc|pte|pty|srl|s\.r\.l|kg|oy|ab|as|berhad|bhd|technologies|technology|electronics|international|holdings|group)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Suffix();

    /// <summary>A registered company name made readable: a known brand, or the name without its legal suffixes.</summary>
    public static string Clean(string registered)
    {
        var lower = registered.ToLowerInvariant();
        foreach (var (contains, brand) in Brands)
            if (Regex.IsMatch(lower, $@"(^|[^a-z]){Regex.Escape(contains)}([^a-z]|$)")) return brand;
        // What follows the first comma is a legal form or a place ("Greenpacket Berhad, Taiwan", "Foo Co.,Ltd").
        var comma = registered.IndexOf(',');
        var name = (comma > 0 ? registered[..comma] : registered).Trim();
        for (string prev; (prev = name) != (name = Suffix().Replace(name, "").Trim().TrimEnd(',', '.', ' '));) { }
        if (name.Length == 0) name = registered.Trim();
        // SHOUTING NAMES become Title Case, keeping short or mixed tokens (TP, D-Link, 3M) as they are.
        if (name == name.ToUpperInvariant() && name.Any(char.IsLetter))
            name = string.Join(' ', name.Split(' ').Select(w => w.Length <= 3 || w.Any(c => !char.IsLetter(c)) ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        return name;
    }
}
