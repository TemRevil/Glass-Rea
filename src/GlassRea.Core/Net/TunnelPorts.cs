using System.Net;

namespace GlassRea.Core.Net;

/// <summary>
/// The local ports a VPN program listens on (Cloudflare WARP's proxy and DNS, for example). An app talking to one
/// of these over loopback is really talking to the internet through the VPN, so those bytes count as the app's
/// internet use; the VPN's own copy is left out of totals as usual.
/// </summary>
public sealed class TunnelPorts
{
    public static readonly TunnelPorts None = new([]);

    private readonly HashSet<int> _tcp = [], _udp = [];

    public TunnelPorts(IEnumerable<(bool Tcp, int Port)> ports)
    {
        foreach (var (tcp, port) in ports) (tcp ? _tcp : _udp).Add(port);
    }

    public bool IsEmpty => _tcp.Count == 0 && _udp.Count == 0;

    /// <summary>
    /// The ports of sockets owned by VPN processes that another app on this PC could reach: bound to a loopback
    /// address or to every address.
    /// </summary>
    public static TunnelPorts From(IEnumerable<(bool Tcp, IPAddress Address, int Port, int Pid)> sockets, Func<int, bool> isTunnelProcess) =>
        new(sockets
            .Where(s => IPAddress.IsLoopback(s.Address) || s.Address.Equals(IPAddress.Any) || s.Address.Equals(IPAddress.IPv6Any))
            .Where(s => isTunnelProcess(s.Pid))
            .Select(s => (s.Tcp, s.Port)));

    /// <param name="swapped">
    /// Whether the capture reports ports byte-swapped (network order read as a number); null while unknown, which
    /// accepts either form.
    /// </param>
    public bool Has(bool tcp, int port, bool? swapped)
    {
        var set = tcp ? _tcp : _udp;
        return swapped switch
        {
            false => set.Contains(port),
            true => set.Contains(Swap(port)),
            null => set.Contains(port) || set.Contains(Swap(port)),
        };
    }

    /// <summary>
    /// Seen from a VPN process's own packet, one of the two ports is the VPN's: whichever form matches tells the byte
    /// order. Null when the packet doesn't settle it.
    /// </summary>
    public bool? OrderFrom(bool tcp, int sport, int dport)
    {
        var plain = Has(tcp, sport, false) || Has(tcp, dport, false);
        var swapped = Has(tcp, sport, true) || Has(tcp, dport, true);
        return plain == swapped ? null : swapped;
    }

    public static int Swap(int port) => (ushort)((port & 0xFF) << 8 | (port >> 8) & 0xFF);
}
