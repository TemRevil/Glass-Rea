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
    private static partial uint GetIpNetTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

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
