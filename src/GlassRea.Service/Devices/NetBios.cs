using System.Buffers.Binary;
using System.Text;

namespace GlassRea.Service.Devices;

/// <summary>
/// NetBIOS node status (RFC 1002, UDP port 137): asks a device for its name table. Windows PCs, Samba servers,
/// many NAS boxes and some printers answer with their computer name, which is what File Explorer shows them as.
/// </summary>
public static class NetBios
{
    /// <summary>A node status request for the wildcard name "*".</summary>
    public static byte[] StatusRequest(ushort id)
    {
        var b = new byte[50];
        BinaryPrimitives.WriteUInt16BigEndian(b, id);
        b[5] = 1; // one question
        b[12] = 0x20;
        // "*" padded with zero bytes to 16, each half-byte encoded as 'A' + nibble.
        var raw = new byte[16];
        raw[0] = (byte)'*';
        for (var i = 0; i < 16; i++)
        {
            b[13 + i * 2] = (byte)('A' + (raw[i] >> 4));
            b[14 + i * 2] = (byte)('A' + (raw[i] & 0x0F));
        }
        b[45] = 0;
        b[47] = 0x21; // NBSTAT
        b[49] = 0x01; // IN
        return b;
    }

    /// <summary>The computer name (the unique name with suffix 0x00) from a status response.</summary>
    public static string? ParseStatus(ReadOnlySpan<byte> p)
    {
        // Header 12, the encoded name 34, type 2, class 2, TTL 4, data length 2, then the name count.
        const int count = 12 + 34 + 10;
        if (p.Length <= count) return null;
        int names = p[count];
        for (var i = 0; i < names; i++)
        {
            var at = count + 1 + i * 18;
            if (at + 18 > p.Length) break;
            var text = Encoding.ASCII.GetString(p.Slice(at, 15)).TrimEnd(' ', '\0');
            var suffix = p[at + 15];
            var isGroup = (p[at + 16] & 0x80) != 0;
            if (suffix != 0x00 || isGroup || text.Length == 0 || text.Any(c => c < 0x20 || c > 0x7E)) continue;
            return text;
        }
        return null;
    }
}
