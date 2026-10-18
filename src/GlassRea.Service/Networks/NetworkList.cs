using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using GlassRea.Core.Protocol;
using Microsoft.Win32;

namespace GlassRea.Service.Networks;

/// <param name="Key">The id of Windows' profile for it, which stays the same for as long as Windows remembers the network.</param>
/// <param name="Name">What Windows calls it ("Home", "Network 2").</param>
/// <param name="Kind">One of <see cref="NetworkKinds"/>, when known.</param>
public sealed record WindowsNetwork(string Key, string Name, string? Kind);

/// <summary>
/// Windows' own list of networks: the Network List Manager, and the profiles it keeps in the registry. Windows tells
/// networks apart by the router's hardware address and, on Wi-Fi, the network's name, and remembers each one as a
/// profile with the name shown in its Settings. That profile is what use is counted on.
/// </summary>
public static class NetworkList
{
    private const string ProfilesKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";
    private const string SignaturesKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures";
    private const string WlanKey = @"SOFTWARE\Microsoft\WlanSvc\Interfaces";
    private const int Internet = 0x40 | 0x400; // NLM_CONNECTIVITY_IPV4_INTERNET | NLM_CONNECTIVITY_IPV6_INTERNET
    private const int WirelessProfile = 71, WiredProfile = 6, MobileProfile = 243; // a profile's NameType

    private static readonly string WlanProfiles =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Wlansvc\Profiles\Interfaces");

    public static string Key(Guid profile) => profile.ToString("B");

    /// <summary>
    /// The network this PC's traffic goes out on now. Of the Wi-Fi, Ethernet and mobile adapters on a network Windows
    /// has identified, the one that reaches the internet, and of those the one Windows routes through. A VPN's adapter
    /// never counts: a VPN runs over one of these. Null when there's none, or while Windows is still identifying it.
    /// </summary>
    public static WindowsNetwork? Current()
    {
        var own = new Dictionary<Guid, NetworkInterface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            if (nic.OperationalStatus == OperationalStatus.Up && DeviceScanner.IsOwnAdapter(nic, mobile: true) && Guid.TryParse(nic.Id, out var id))
                own[id] = nic;
        if (own.Count == 0) return null;

        var found = new List<(NetworkInterface Nic, Guid Network, string Name, bool Online)>();
        var manager = (INetworkListManager)new NetworkListManager();
        var connections = manager.GetNetworkConnections();
        var one = new INetworkConnection[1];
        while (connections.Next(1, one, out var got) == 0 && got == 1)
        {
            var c = one[0];
            if (!c.IsConnected || !own.TryGetValue(c.GetAdapterId(), out var nic)) continue;
            var network = c.GetNetwork();
            found.Add((nic, network.GetNetworkId(), network.GetName(), (c.GetConnectivity() & Internet) != 0));
        }
        if (found.Count == 0) return null;
        var metrics = found.Count > 1 ? Native.DefaultRouteMetrics() : [];
        var best = found
            .OrderByDescending(f => f.Online)
            .ThenBy(f => metrics.GetValueOrDefault(f.Nic.GetIPProperties().GetIPv4Properties()?.Index ?? -1, int.MaxValue))
            .First();
        // Until Windows has identified a network it has no profile, and what it reports is a stand-in.
        if (Profile(best.Network) is not { } profile) return null;
        var name = string.IsNullOrWhiteSpace(best.Name) ? profile.Name : best.Name.Trim();
        return new WindowsNetwork(Key(best.Network), name, KindOf(profile.NameType) ?? KindOf(best.Nic.NetworkInterfaceType));
    }

    private static (string Name, int NameType)? Profile(Guid id)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ProfilesKey}\{id:B}");
        if (key is null) return null;
        return ((key.GetValue("ProfileName") as string ?? "").Trim(), key.GetValue("NameType") is int type ? type : 0);
    }

    private static string? KindOf(int nameType) => nameType switch
    {
        WirelessProfile => NetworkKinds.WiFi,
        WiredProfile => NetworkKinds.Wired,
        MobileProfile => NetworkKinds.Mobile,
        _ => null,
    };

    private static string? KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => NetworkKinds.WiFi,
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => NetworkKinds.Mobile,
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => NetworkKinds.Wired,
        _ => null,
    };

    /// <summary>
    /// The Windows network of each Wi-Fi profile number in Windows' usage history (its L2ProfileId). The history names
    /// the Wi-Fi profile, which names the network's SSID; that SSID is matched to the one network Windows remembers by it.
    /// </summary>
    public static Dictionary<long, WindowsNetwork> WifiByHistoryProfile()
    {
        var wlan = new List<(long Index, string Ssid)>();
        using (var interfaces = Registry.LocalMachine.OpenSubKey(WlanKey))
        {
            foreach (var adapter in interfaces?.GetSubKeyNames() ?? [])
            {
                using var profiles = interfaces!.OpenSubKey($@"{adapter}\Profiles");
                foreach (var profile in profiles?.GetSubKeyNames() ?? [])
                {
                    using var key = profiles!.OpenSubKey(profile);
                    if (key?.GetValue("ProfileIndex") is not int index) continue;
                    if (Ssid(Path.Combine(WlanProfiles, adapter, profile + ".xml")) is { } ssid) wlan.Add((unchecked((uint)index), ssid));
                }
            }
        }

        var signatures = new List<(string Ssid, Guid Profile)>();
        var profilesById = new Dictionary<Guid, (string Name, int NameType)>();
        foreach (var kind in new[] { "Managed", "Unmanaged" })
        {
            using var group = Registry.LocalMachine.OpenSubKey($@"{SignaturesKey}\{kind}");
            foreach (var name in group?.GetSubKeyNames() ?? [])
            {
                using var signature = group!.OpenSubKey(name);
                if (signature?.GetValue("FirstNetwork") is not string ssid || !Guid.TryParse(signature.GetValue("ProfileGuid") as string, out var id)) continue;
                signatures.Add((ssid, id));
                if (!profilesById.ContainsKey(id) && Profile(id) is { } p) profilesById[id] = p;
            }
        }
        return MatchWifi(wlan, signatures, profilesById);
    }

    /// <summary>
    /// Pairs Wi-Fi profiles with Windows networks by SSID. An SSID Windows remembers as more than one network (the same
    /// name behind another router) is left unpaired, since the history doesn't say which.
    /// </summary>
    public static Dictionary<long, WindowsNetwork> MatchWifi(IEnumerable<(long Index, string Ssid)> wlan, IEnumerable<(string Ssid, Guid Profile)> signatures,
        IReadOnlyDictionary<Guid, (string Name, int NameType)> profiles)
    {
        var bySsid = signatures
            .Where(s => profiles.TryGetValue(s.Profile, out var p) && p.NameType == WirelessProfile)
            .GroupBy(s => s.Ssid, StringComparer.Ordinal)
            .Where(g => g.Select(s => s.Profile).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Profile, StringComparer.Ordinal);
        var map = new Dictionary<long, WindowsNetwork>();
        foreach (var (index, ssid) in wlan)
            if (bySsid.TryGetValue(ssid, out var id))
                map[index] = new WindowsNetwork(Key(id), profiles[id].Name is { Length: > 0 } name ? name : ssid, NetworkKinds.WiFi);
        return map;
    }

    /// <summary>The SSID in a saved Wi-Fi profile; null when the file is missing or doesn't name one.</summary>
    public static string? Ssid(string profileFile)
    {
        try
        {
            if (!File.Exists(profileFile)) return null;
            var ssid = XDocument.Load(profileFile).Descendants().FirstOrDefault(e => e.Name.LocalName == "SSID");
            var name = ssid?.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
            if (!string.IsNullOrEmpty(name)) return name;
            var hex = ssid?.Elements().FirstOrDefault(e => e.Name.LocalName == "hex")?.Value;
            return string.IsNullOrEmpty(hex) ? null : System.Text.Encoding.UTF8.GetString(Convert.FromHexString(hex));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException) { return null; }
    }

    // The Network List Manager's interfaces (netlistmgr.h), in their order, up to the last method used.

    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager;

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworks(int flags);
        INetwork GetNetwork(Guid id);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0), Out] INetworkConnection[] items, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
    }
}
