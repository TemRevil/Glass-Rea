using System.Net;

namespace GlassRea.Core.Net;

public static class DnsResults
{
    /// <summary>
    /// Parses the QueryResults field of DNS-Client event 3008, e.g. "type:  5 edge.example.net;142.250.1.1;::ffff:1.2.3.4;".
    /// Returns only the IP addresses the name resolved to.
    /// </summary>
    public static IReadOnlyList<IPAddress> Parse(string? results)
    {
        if (string.IsNullOrWhiteSpace(results)) return [];
        var list = new List<IPAddress>();
        foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("type:", StringComparison.OrdinalIgnoreCase)) continue;
            if (IPAddress.TryParse(part, out var ip))
                list.Add(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
        }
        return list;
    }
}
