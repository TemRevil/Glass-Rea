using System.Net;
using System.Runtime.InteropServices;

namespace GlassRea.Service;

internal static unsafe partial class Native
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, char* buffer, ref int size);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDosDevice(string device, char* buffer, uint max);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint ConvertInterfaceLuidToIndex(in long luid, out uint index);

    /// <summary>The interface index of a network adapter's LUID, or -1 when the adapter no longer exists.</summary>
    public static int InterfaceIndex(long luid) => ConvertInterfaceLuidToIndex(luid, out var index) == 0 ? (int)index : -1;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpNetTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpForwardTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    /// <summary>
    /// The lowest metric of each interface's IPv4 default route, by interface index. The metric includes the
    /// interface's own, so the lowest one is the adapter Windows sends internet traffic through.
    /// </summary>
    public static Dictionary<int, int> DefaultRouteMetrics()
    {
        var map = new Dictionary<int, int>();
        var size = 0;
        GetIpForwardTable(0, ref size, false);
        if (size == 0) return map;
        size += 1024; // routes can come and go between the two calls
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpForwardTable(buffer, ref size, false) != 0) return map;
            var count = Marshal.ReadInt32(buffer);
            const int rowSize = 56; // MIB_IPFORWARDROW: dest, mask, policy, next hop, index, type, proto, age, next hop AS, metric 1-5
            for (var i = 0; i < count && 4 + (i + 1) * rowSize <= size; i++)
            {
                var row = buffer + 4 + i * rowSize;
                if (Marshal.ReadInt32(row) != 0 || Marshal.ReadInt32(row, 4) != 0) continue; // 0.0.0.0/0 only
                var index = Marshal.ReadInt32(row, 16);
                var metric = Marshal.ReadInt32(row, 36);
                map[index] = map.TryGetValue(index, out var known) ? Math.Min(known, metric) : metric;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return map;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSidToStringSid(nint sid, out nint text);

    [LibraryImport("advapi32.dll")]
    private static partial nint FreeSid(nint sid);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    /// <summary>Full Win32 path of a running process, or null if it has exited or cannot be opened.</summary>
    public static string? ProcessPath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == 0) return null;
        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024;
            return QueryFullProcessImageName(h, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Maps \Device\HarddiskVolumeN to its drive letter for every mounted drive.</summary>
    public static Dictionary<string, string> DeviceToDriveMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = stackalloc char[1024];
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var letter = drive.TrimEnd('\\');
            var n = QueryDosDevice(letter, buffer, 1024);
            if (n == 0) continue;
            var target = new string(buffer, 0, (int)n).Split('\0')[0];
            if (target.Length > 0) map[target] = letter;
        }
        return map;
    }

    public readonly record struct ArpEntry(int InterfaceIndex, IPAddress Address, byte[] Mac, int Type);

    /// <summary>The IPv4 neighbor (ARP) table.</summary>
    public static List<ArpEntry> ArpTable()
    {
        var size = 0;
        GetIpNetTable(0, ref size, false);
        var list = new List<ArpEntry>();
        if (size == 0) return list;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, true) != 0) return list;
            var count = Marshal.ReadInt32(buffer);
            const int rowSize = 24; // MIB_IPNETROW: index, physLen, phys[8], addr, type
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * rowSize;
                var index = Marshal.ReadInt32(row);
                var macLen = Marshal.ReadInt32(row, 4);
                var mac = new byte[Math.Clamp(macLen, 0, 8)];
                Marshal.Copy(row + 8, mac, 0, mac.Length);
                var addr = new IPAddress(unchecked((uint)Marshal.ReadInt32(row, 16)));
                var type = Marshal.ReadInt32(row, 20);
                list.Add(new ArpEntry(index, addr, mac, type));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    /// <summary>Every listening TCP socket and every UDP socket on this PC, with the process that owns it.</summary>
    public static List<(bool Tcp, IPAddress Address, int Port, int Pid)> LocalSockets()
    {
        const int Inet = 2, Inet6 = 23, TcpOwnerPidListener = 3, UdpOwnerPid = 1;
        var list = new List<(bool, IPAddress, int, int)>();
        // Row layouts: MIB_TCPROW_OWNER_PID (24 bytes), MIB_TCP6ROW_OWNER_PID (56), MIB_UDPROW_OWNER_PID (12), MIB_UDP6ROW_OWNER_PID (28).
        Read(true, Inet, TcpOwnerPidListener, 24, row => (new IPAddress(unchecked((uint)Marshal.ReadInt32(row, 4))), Marshal.ReadInt32(row, 8), Marshal.ReadInt32(row, 20)));
        Read(true, Inet6, TcpOwnerPidListener, 56, row => (Address6(row), Marshal.ReadInt32(row, 20), Marshal.ReadInt32(row, 52)));
        Read(false, Inet, UdpOwnerPid, 12, row => (new IPAddress(unchecked((uint)Marshal.ReadInt32(row, 0))), Marshal.ReadInt32(row, 4), Marshal.ReadInt32(row, 8)));
        Read(false, Inet6, UdpOwnerPid, 28, row => (Address6(row), Marshal.ReadInt32(row, 20), Marshal.ReadInt32(row, 24)));
        return list;

        void Read(bool tcp, int family, int tableClass, int rowSize, Func<nint, (IPAddress Address, int Port, int Pid)> parse)
        {
            var size = 0;
            _ = tcp ? GetExtendedTcpTable(0, ref size, false, family, tableClass, 0) : GetExtendedUdpTable(0, ref size, false, family, tableClass, 0);
            if (size == 0) return;
            size += 4096; // sockets come and go between the two calls
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = tcp ? GetExtendedTcpTable(buffer, ref size, false, family, tableClass, 0) : GetExtendedUdpTable(buffer, ref size, false, family, tableClass, 0);
                if (result != 0) return;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count && 4 + (i + 1) * rowSize <= size; i++)
                {
                    var (address, port, pid) = parse(buffer + 4 + i * rowSize);
                    list.Add((tcp, address, (ushort)IPAddress.NetworkToHostOrder((short)port), pid));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        static IPAddress Address6(nint row)
        {
            var bytes = new byte[16];
            Marshal.Copy(row, bytes, 0, 16);
            return new IPAddress(bytes);
        }
    }

    /// <summary>The AppContainer SID string Windows Firewall uses to target a Store app, e.g. S-1-15-2-...</summary>
    public static string? AppContainerSid(string packageFamilyName)
    {
        if (DeriveAppContainerSidFromAppContainerName(packageFamilyName, out var sid) != 0 || sid == 0) return null;
        try
        {
            if (!ConvertSidToStringSid(sid, out var text)) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { LocalFree(text); }
        }
        finally { FreeSid(sid); }
    }
}
