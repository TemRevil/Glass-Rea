namespace GlassRea.Core.Net;

/// <summary>
/// Network adapters a VPN creates (Cloudflare WARP's "Cloudflare WARP Interface Tunnel", WireGuard's wintun, OpenVPN's
/// TAP). Bytes an app moves through one of these never cross your connection as themselves: the VPN carries them,
/// wrapped, through the real adapter, and that wrapped copy is what your internet provider counts.
/// </summary>
public static class TunnelAdapters
{
    private static readonly string[] Words =
        ["warp", "tunnel", "wintun", "wireguard", "tap-", "tap adapter", "vpn", "nordlynx", "openvpn", "ovpn", "tailscale", "zerotier",
         "proton", "anyconnect", "globalprotect", "pangp", "fortinet", "forticlient"];

    /// <summary>PPP (23), tunnel (131) and the proprietary virtual type (53) VPN adapters such as WARP's report.</summary>
    public static bool IsTunnelType(int interfaceType) => interfaceType is 23 or 131 or 53;

    public static bool IsTunnel(string name, string description, int interfaceType)
    {
        if (IsTunnelType(interfaceType)) return true;
        var text = (name + " " + description).ToLowerInvariant();
        return Words.Any(text.Contains);
    }
}
