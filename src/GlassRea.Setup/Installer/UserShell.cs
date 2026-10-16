using System.Runtime.InteropServices;

namespace GlassRea.Setup.Installer;

/// <summary>
/// Starts a program or opens a link as the signed-in person, not with setup's admin rights. It asks the desktop's
/// own Explorer window to do the launch (Shell.Application's ShellExecute on the desktop view), so whatever opens
/// inherits Explorer's ordinary rights. Running "explorer.exe &lt;path&gt;" from an elevated process silently does
/// nothing on some PCs, which is why this goes through the running shell instead.
/// </summary>
public static class UserShell
{
    public static void Open(string fileOrUrl)
    {
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidShellWindows)!)!;
        object desktop = 0 /* CSIDL_DESKTOP */, empty = null!; // an empty VARIANT
        var found = windows.FindWindowSW(ref desktop, ref empty, SwcDesktop, out _, SwfoNeedDispatch)
                    ?? throw new InvalidOperationException("The Windows desktop isn't running.");
        var services = (IServiceProvider)found;
        var sidBrowser = SidTopLevelBrowser;
        var iidBrowser = typeof(IShellBrowser).GUID;
        services.QueryService(ref sidBrowser, ref iidBrowser, out var browser);
        browser.QueryActiveShellView(out var view);
        var iidDispatch = IidDispatch;
        view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iidDispatch, out var folderView);
        dynamic shell = ((dynamic)folderView).Application;
        shell.ShellExecute(fileOrUrl, "", "", "open", 1 /* SW_SHOWNORMAL */);
    }

    private const int SwcDesktop = 8, SwfoNeedDispatch = 1;
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");

    // Methods in vtable order up to the one used; the earlier ones are never called.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
        void RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass, out int plCookie);
        void Revoke(int lCookie);
        void OnNavigate(int lCookie, ref object pvarLoc);
        void OnActivated(int lCookie, bool fActive);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        void QueryService(ref Guid guidService, ref Guid riid, out IShellBrowser ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out nint phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB(nint hmenuShared, nint lpMenuWidths);
        void SetMenuSB(nint hmenuShared, nint holemenuRes, nint hwndActiveObject);
        void RemoveMenusSB(nint hmenuShared);
        void SetStatusTextSB(nint pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(nint pmsg, ushort wID);
        void BrowseObject(nint pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out nint ppStrm);
        void GetControlWindow(uint id, out nint phwnd);
        void SendControlMsg(uint id, uint uMsg, nint wParam, nint lParam, out nint pret);
        void QueryActiveShellView(out IShellView ppshv);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out nint phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void TranslateAccelerator(nint pmsg);
        void EnableModeless(bool fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(nint psvPrevious, nint pfs, nint psb, nint prcView, out nint phWnd);
        void DestroyViewWindow();
        void GetCurrentInfo(nint pfs);
        void AddPropertySheetPages(uint dwReserved, nint pfn, nint lparam);
        void SaveViewState();
        void SelectItem(nint pidlItem, uint uFlags);
        void GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }
}
