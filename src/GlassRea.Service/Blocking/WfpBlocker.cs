using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GlassRea.Service.Blocking;

/// <summary>
/// The Store copy's blocks: filters in Windows' firewall engine (WFP) that this service holds in a dynamic session.
/// They last as long as the service runs and Windows drops them when it stops, so removing the Store package, which
/// can't run anything on the way out, leaves no block behind. The service adds them again at every start
/// (<see cref="FirewallManager.RestoreBlocks"/>). The copy setup installs uses Windows Firewall rules instead.
/// </summary>
internal sealed unsafe partial class WfpBlocker : IDisposable
{
    private static readonly Guid SubLayerKey = new("9f0e4c2a-6d3b-4f57-a1c8-3e5b7d9a2c41");
    // Every connection an app makes and every one it accepts, over IPv4 and IPv6: what a Windows Firewall rule for
    // "any protocol, in and out" covers.
    private static readonly Guid[] Layers =
    [
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), // FWPM_LAYER_ALE_AUTH_CONNECT_V4
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), // FWPM_LAYER_ALE_AUTH_CONNECT_V6
        new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), // FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4
        new("a3b42c97-9f04-4672-b87e-cee9c483257f"), // FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6
    ];
    private static readonly Guid AppIdField = new("d78e1e87-8644-4ea5-9437-d809ecefc971");     // FWPM_CONDITION_ALE_APP_ID
    private static readonly Guid UserIdField = new("af043a0a-b34d-4f86-979c-c90371af6e66");    // FWPM_CONDITION_ALE_USER_ID
    private static readonly Guid PackageIdField = new("71bc78fa-f17c-4997-a602-6abb261f351c"); // FWPM_CONDITION_ALE_PACKAGE_ID

    private const uint SessionDynamic = 0x1, AuthnWinNt = 10, ActionBlock = 0x1001, MatchEqual = 0;
    internal const uint TypeByteBlob = 12, TypeSid = 13, TypeSecurityDescriptor = 14;

    private readonly nint _engine;
    private readonly Dictionary<string, List<ulong>> _filters = new(StringComparer.Ordinal);

    public WfpBlocker()
    {
        fixed (char* name = "Glass-Rea")
        {
            var session = new Session { Display = new DisplayData { Name = name }, Flags = SessionDynamic };
            nint engine;
            Check(FwpmEngineOpen0(null, AuthnWinNt, 0, &session, &engine), "open Windows' firewall engine");
            _engine = engine;
        }
        fixed (char* name = "Glass-Rea blocks")
        {
            var subLayer = new SubLayer { Key = SubLayerKey, Display = new DisplayData { Name = name }, Weight = 0x100 };
            try { Check(FwpmSubLayerAdd0(_engine, &subLayer, 0), "add Glass-Rea's place in the firewall engine"); }
            catch { FwpmEngineClose0(_engine); throw; }
        }
    }

    public bool IsBlocked(string key) => _filters.ContainsKey(key);

    /// <summary>
    /// Blocks the program at <paramref name="path"/>; with <paramref name="service"/>, only that service inside it
    /// (svchost.exe runs many). A Store app is also matched by its package (<paramref name="packageSid"/>), which
    /// keeps working after an update moves its program to a new folder.
    /// </summary>
    public void Block(string key, string path, string? service, string? packageSid)
    {
        if (_filters.ContainsKey(key)) return;
        var added = new List<ulong>();
        nint appId = 0, descriptor = 0, sid = 0;
        try
        {
            Check(FwpmGetAppIdFromFileName0(path, &appId), $"find {path}");
            var conditions = stackalloc Condition[2];
            conditions[0] = new Condition { Field = AppIdField, MatchType = MatchEqual, Value = new Value { Type = TypeByteBlob, Data = appId } };
            var count = 1u;
            ByteBlob serviceOnly = default;
            if (service is not null)
            {
                // The connection's user must be the service's own identity: an access check against a descriptor that
                // grants only that SID the right to match (CC, FWP_ACTRL_MATCH_FILTER).
                uint size;
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:(A;;CC;;;{ServiceSid(service)})", 1, &descriptor, &size))
                    Check((uint)Marshal.GetLastPInvokeError(), $"describe the {service} service");
                serviceOnly = new ByteBlob { Size = size, Data = (byte*)descriptor };
                conditions[count++] = new Condition { Field = UserIdField, MatchType = MatchEqual, Value = new Value { Type = TypeSecurityDescriptor, Data = (nint)(&serviceOnly) } };
            }
            AddAll(conditions, count, added);

            if (packageSid is not null && ConvertStringSidToSid(packageSid, &sid))
            {
                var package = new Condition { Field = PackageIdField, MatchType = MatchEqual, Value = new Value { Type = TypeSid, Data = sid } };
                AddAll(&package, 1, added);
            }
            _filters[key] = added;
        }
        catch
        {
            foreach (var id in added) FwpmFilterDeleteById0(_engine, id);
            throw;
        }
        finally
        {
            if (appId != 0) FwpmFreeMemory0(&appId);
            if (descriptor != 0) LocalFree(descriptor);
            if (sid != 0) LocalFree(sid);
        }
    }

    public void Unblock(string key)
    {
        if (!_filters.Remove(key, out var ids)) return;
        foreach (var id in ids) FwpmFilterDeleteById0(_engine, id);
    }

    private void AddAll(Condition* conditions, uint count, List<ulong> added)
    {
        fixed (char* name = "Glass-Rea block")
        {
            foreach (var layer in Layers)
            {
                var filter = new Filter
                {
                    Display = new DisplayData { Name = name }, Layer = layer, SubLayer = SubLayerKey,
                    ConditionCount = count, Conditions = conditions, Action = new FilterAction { Type = ActionBlock },
                };
                ulong id;
                Check(FwpmFilterAdd0(_engine, &filter, 0, &id), "add the block to the firewall engine");
                added.Add(id);
            }
        }
    }

    /// <summary>Closing the session is what removes every filter it added.</summary>
    public void Dispose() => FwpmEngineClose0(_engine);

    /// <summary>A service's own SID, NT SERVICE\name: S-1-5-80 and the SHA-1 of its name in capitals, as five numbers.</summary>
    public static string ServiceSid(string service)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(service.ToUpperInvariant()));
        return "S-1-5-80-" + string.Join('-', Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt32(hash, i * 4)));
    }

    private static void Check(uint result, string what)
    {
        if (result != 0) throw new InvalidOperationException($"Couldn't {what} (error 0x{result:X8}).");
    }

    // ---------- fwpuclnt.dll, x64 layouts (WfpTests checks them against the Windows SDK's) ----------

    [StructLayout(LayoutKind.Sequential)] internal struct DisplayData { public char* Name; public char* Description; }
    [StructLayout(LayoutKind.Sequential)] internal struct ByteBlob { public uint Size; public byte* Data; }
    /// <summary>FWP_VALUE0 and FWP_CONDITION_VALUE0: a type and the value, or a pointer to it.</summary>
    [StructLayout(LayoutKind.Sequential)] internal struct Value { public uint Type; public nint Data; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Session
    {
        public Guid Key; public DisplayData Display; public uint Flags; public uint TransactionWait; public uint ProcessId;
        public nint Sid; public char* UserName; public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SubLayer
    {
        public Guid Key; public DisplayData Display; public uint Flags; public Guid* ProviderKey; public ByteBlob ProviderData; public ushort Weight;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Condition { public Guid Field; public uint MatchType; public Value Value; }
    [StructLayout(LayoutKind.Sequential)] internal struct FilterAction { public uint Type; public Guid FilterType; }
    /// <summary>The union of a UINT64 and a GUID, which C aligns to 8 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)] internal struct Context { public ulong Low; public ulong High; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Filter
    {
        public Guid Key; public DisplayData Display; public uint Flags; public Guid* ProviderKey; public ByteBlob ProviderData;
        public Guid Layer; public Guid SubLayer; public Value Weight; public uint ConditionCount; public Condition* Conditions;
        public FilterAction Action; public Context ProviderContext; public Guid* Reserved; public ulong Id; public Value EffectiveWeight;
    }

    [LibraryImport("fwpuclnt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint FwpmEngineOpen0(string? server, uint authn, nint identity, Session* session, nint* engine);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineClose0(nint engine);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmSubLayerAdd0(nint engine, SubLayer* subLayer, nint descriptor);

    [LibraryImport("fwpuclnt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint FwpmGetAppIdFromFileName0(string path, nint* appId);

    [LibraryImport("fwpuclnt.dll")]
    private static partial void FwpmFreeMemory0(nint* memory);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterAdd0(nint engine, Filter* filter, nint descriptor, ulong* id);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterDeleteById0(nint engine, ulong id);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, nint* descriptor, uint* size);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSid(string text, nint* sid);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
