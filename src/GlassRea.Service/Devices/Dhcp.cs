using System.Text;

namespace GlassRea.Service.Devices;

/// <summary>A device asking for an address: its hardware address and what it says about itself.</summary>
public sealed record DhcpRequest(byte[] Mac, string? HostName, string? VendorClass);

/// <summary>
/// DHCP client messages (RFC 2131). A phone or laptop joining the network broadcasts a DISCOVER and a REQUEST that
/// carry its name ("Omars-iPhone", "Galaxy-S23") and often its system ("android-dhcp-14", "MSFT 5.0"). That name is
/// what routers show in their client list. Packets come from any device on the network, so parsing is strict.
/// </summary>
public static class Dhcp
{
    private const int OptionsStart = 240;

    public static DhcpRequest? ParseRequest(ReadOnlySpan<byte> p)
    {
        // op 1 = from a client, htype 1 = Ethernet, hlen 6, then the magic cookie 99.130.83.99.
        if (p.Length < OptionsStart || p[0] != 1 || p[1] != 1 || p[2] != 6) return null;
        if (p[236] != 99 || p[237] != 130 || p[238] != 83 || p[239] != 99) return null;
        var mac = p.Slice(28, 6).ToArray();
        int? type = null;
        string? host = null, vendor = null, fqdn = null;
        for (var at = OptionsStart; at < p.Length;)
        {
            int code = p[at++];
            if (code == 0) continue;     // pad
            if (code == 255) break;      // end
            if (at >= p.Length) break;
            int len = p[at++];
            if (at + len > p.Length) break;
            var data = p.Slice(at, len);
            switch (code)
            {
                case 53 when len == 1: type = data[0]; break;
                case 12: host = Text(data); break;
                case 60: vendor = Text(data); break;
                case 81 when len > 3: fqdn = Fqdn(data[3..]); break; // flags, two legacy codes, then the name
            }
            at += len;
        }
        if (type is not (1 or 3 or 8)) return null; // DISCOVER, REQUEST, INFORM
        return new DhcpRequest(mac, host ?? fqdn, vendor);
    }

    /// <summary>Printable text only, trimmed and capped; anything else isn't a name worth showing.</summary>
    private static string? Text(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > 64) return null;
        var s = Encoding.UTF8.GetString(data).TrimEnd('\0').Trim();
        return s.Length == 0 || s.Any(char.IsControl) || s.Contains('\uFFFD') ? null : s;
    }

    /// <summary>Option 81 carries the name either as plain text or in DNS label form.</summary>
    private static string? Fqdn(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return null;
        if (data[0] > 0 && data[0] < 64 && data[0] < data.Length && !char.IsAsciiLetterOrDigit((char)data[0]))
            return Text(data.Slice(1, data[0])); // first label of the wire-format name
        return Text(data)?.Split('.')[0];
    }
}
