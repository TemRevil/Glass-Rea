using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace GlassRea.Service.Devices;

/// <summary>One record from a DNS answer. Data is the address, target name, or "key=value" strings for TXT.</summary>
public sealed record DnsRecord(string Name, ushort Type, string Data, IReadOnlyList<string> Txt);

/// <summary>
/// Just enough of the DNS wire format (RFC 1035) for multicast DNS (RFC 6762) device discovery: building a query
/// and reading the A, AAAA, PTR, SRV and TXT records out of the answers. Packets come from other devices, so every
/// read is bounds-checked and a malformed packet simply yields fewer records.
/// </summary>
public static class DnsWire
{
    public const ushort A = 1, Ptr = 12, Txt = 16, Aaaa = 28, Srv = 33;

    /// <summary>A query for each (name, type), asking for unicast replies (the QU bit).</summary>
    public static byte[] Query(IEnumerable<(string Name, ushort Type)> questions)
    {
        var list = questions.ToList();
        var buf = new List<byte>(512) { 0, 0, 0, 0 }; // id 0, standard query
        Add16(buf, (ushort)list.Count);
        Add16(buf, 0); Add16(buf, 0); Add16(buf, 0);
        foreach (var (name, type) in list)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                buf.Add((byte)Math.Min(63, bytes.Length));
                buf.AddRange(bytes.Take(63));
            }
            buf.Add(0);
            Add16(buf, type);
            Add16(buf, 0x8001); // class IN with the unicast-response bit
        }
        return [.. buf];
    }

    /// <summary>The reverse-lookup name for an IPv4 address: 6.1.168.192.in-addr.arpa.</summary>
    public static string ReverseName(IPAddress ip) => string.Join('.', ip.GetAddressBytes().Reverse()) + ".in-addr.arpa";

    /// <summary>Every answer, authority and additional record in a response; empty for anything unreadable.</summary>
    public static List<DnsRecord> Parse(ReadOnlySpan<byte> p)
    {
        var records = new List<DnsRecord>();
        if (p.Length < 12) return records;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        int total = BinaryPrimitives.ReadUInt16BigEndian(p[6..]) + BinaryPrimitives.ReadUInt16BigEndian(p[8..]) + BinaryPrimitives.ReadUInt16BigEndian(p[10..]);
        var at = 12;
        try
        {
            for (var i = 0; i < qd; i++) { ReadName(p, ref at); at += 4; }
            for (var i = 0; i < total && at + 10 <= p.Length; i++)
            {
                var name = ReadName(p, ref at);
                if (at + 10 > p.Length) break;
                var type = BinaryPrimitives.ReadUInt16BigEndian(p[at..]);
                int len = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 8)..]);
                at += 10;
                if (at + len > p.Length) break;
                var rdata = at;
                at += len;
                switch (type)
                {
                    case A when len == 4:
                        records.Add(new(name, type, new IPAddress(p.Slice(rdata, 4)).ToString(), []));
                        break;
                    case Aaaa when len == 16:
                        records.Add(new(name, type, new IPAddress(p.Slice(rdata, 16)).ToString(), []));
                        break;
                    case Ptr:
                        var r = rdata;
                        records.Add(new(name, type, ReadName(p, ref r), []));
                        break;
                    case Srv when len > 6:
                        var s = rdata + 6;
                        records.Add(new(name, type, ReadName(p, ref s), []));
                        break;
                    case Txt:
                        var txt = new List<string>();
                        for (var t = rdata; t < rdata + len;)
                        {
                            int n = p[t++];
                            if (t + n > rdata + len) break;
                            txt.Add(Encoding.UTF8.GetString(p.Slice(t, n)));
                            t += n;
                        }
                        records.Add(new(name, type, "", txt));
                        break;
                }
            }
        }
        catch (IndexOutOfRangeException) { /* truncated packet: keep what was read */ }
        catch (ArgumentOutOfRangeException) { }
        return records;
    }

    /// <summary>Reads a possibly compressed name; follows at most 16 pointers so a loop can't hang it.</summary>
    private static string ReadName(ReadOnlySpan<byte> p, ref int at)
    {
        var labels = new List<string>();
        var pos = at;
        var jumped = false;
        for (var hops = 0; hops < 16; )
        {
            int len = p[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                var target = ((len & 0x3F) << 8) | p[pos + 1];
                if (!jumped) at = pos + 2;
                jumped = true;
                pos = target;
                hops++;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p.Slice(pos + 1, len)));
            pos += len + 1;
        }
        if (!jumped) at = pos;
        return string.Join('.', labels);
    }

    private static void Add16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
}
