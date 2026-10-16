using System.Net;
using System.Net.Sockets;

namespace GlassRea.Core.Net;

public enum Scope { Loopback, Lan, Internet }

/// <summary>Decides whether a remote address is this PC, the local network, or the internet.</summary>
public static class AddressScope
{
    public static Scope Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return Scope.Loopback;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 10) return Scope.Lan;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return Scope.Lan;
            if (b[0] == 192 && b[1] == 168) return Scope.Lan;
            if (b[0] == 169 && b[1] == 254) return Scope.Lan;
            if (b[0] >= 224) return Scope.Lan; // multicast and broadcast never leave the local network
            return Scope.Internet;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return Scope.Lan;
        var v6 = address.GetAddressBytes();
        if ((v6[0] & 0xFE) == 0xFC) return Scope.Lan; // unique local fc00::/7
        return Scope.Internet;
    }

    /// <summary>Turns whatever an ETW payload carries for an address into an IPAddress.</summary>
    public static IPAddress? FromPayload(object? value) => value switch
    {
        IPAddress ip => ip,
        uint u => new IPAddress(u),
        int i => new IPAddress(unchecked((uint)i)),
        long l => new IPAddress(l & 0xFFFFFFFF),
        byte[] { Length: 4 or 16 } bytes => new IPAddress(bytes),
        string s when IPAddress.TryParse(s, out var parsed) => parsed,
        _ => null,
    };
}
