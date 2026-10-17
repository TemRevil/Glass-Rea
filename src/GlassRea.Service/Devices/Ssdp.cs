using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GlassRea.Service.Devices;

/// <summary>What a UPnP device says about itself in its description document.</summary>
public sealed record UpnpDescription(string? DeviceType, string? FriendlyName, string? Manufacturer, string? ModelName, string? ModelDescription);

/// <summary>
/// SSDP (UPnP discovery, UDP 239.255.255.250:1900): routers, smart TVs, media boxes, printers and speakers answer
/// an M-SEARCH with the address of an XML file describing them. Both the answers and the XML come from other
/// devices, so parsing is strict: no DTDs, no external entities, size-capped.
/// </summary>
public static class Ssdp
{
    public static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 1900);

    public static byte[] Search() => Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");

    /// <summary>The header lines of an SSDP response, keys upper-cased.</summary>
    public static Dictionary<string, string> Headers(ReadOnlySpan<byte> packet)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = Encoding.UTF8.GetString(packet[..Math.Min(packet.Length, 4096)]);
        var lines = text.Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) && !lines[0].StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase))
            return map;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) map[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return map;
    }

    /// <summary>
    /// Only follow a description link that points back at the device that answered, on the local network:
    /// otherwise a device could make the service fetch arbitrary addresses.
    /// </summary>
    public static Uri? SafeLocation(string? location, IPAddress sender)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return null;
        if (!IPAddress.TryParse(uri.Host, out var host) || !host.Equals(sender)) return null;
        return uri;
    }

    public static UpnpDescription? ParseDescription(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 512 * 1024 };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader);
            var device = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
            if (device is null) return null;
            string? Field(string name) => device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            return new UpnpDescription(Field("deviceType"), Field("friendlyName"), Field("manufacturer"), Field("modelName"), Field("modelDescription"));
        }
        catch (XmlException) { return null; }
    }
}
