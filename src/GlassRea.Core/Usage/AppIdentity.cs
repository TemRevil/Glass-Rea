namespace GlassRea.Core.Usage;

/// <summary>One row in the app list. Key is stable across runs: lower-case path, plus "|service" for svchost services.</summary>
public sealed record AppIdentity(string Key, string Name, string? Path, string? Service, bool IsTunnel)
{
    public static readonly AppIdentity System = new("system", "System", null, null, false);
    public static readonly AppIdentity Unknown = new("unknown", "Unknown (ended before it could be identified)", null, null, false);

    public static string KeyFor(string path, string? service) =>
        service is null ? path.ToLowerInvariant() : $"{path.ToLowerInvariant()}|{service.ToLowerInvariant()}";
}

public static class TunnelProcesses
{
    /// <summary>
    /// VPN/tunnel programs whose own traffic is the encrypted copy of every other app's traffic.
    /// Counting both would double the total, so tunnel rows are shown separately and left out of totals.
    /// </summary>
    public static readonly string[] Defaults = ["warp-svc.exe", "wireguard.exe", "openvpn.exe", "ProtonVPN.WireGuardService.exe", "nordvpn-service.exe"];

    public static bool IsTunnel(string? path, IEnumerable<string> names) =>
        path is not null && names.Any(n => string.Equals(System.IO.Path.GetFileName(path), n, StringComparison.OrdinalIgnoreCase));
}
