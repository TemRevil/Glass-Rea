using System.Runtime.InteropServices;

namespace GlassRea.App.Support;

/// <summary>
/// The notification-area icon, written against Shell_NotifyIcon directly (no third-party tray library, which
/// Smart App Control would refuse to load). Left click opens the window; right click shows Open / Exit;
/// alerts appear as Windows notifications from this icon.
/// </summary>
public sealed partial class Tray : IDisposable
{
    private const int WmApp = 0x8000, WmTray = WmApp + 1, WmCommand = 0x0111, WmContextMenu = 0x007B;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, NinSelect = 0x0400, NinKeySelect = 0x0401, NinBalloonUserClick = 0x0405;
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const int NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NifShowTip = 0x80;
    private const int CmdOpen = 1, CmdExit = 2;

    private readonly WndProc _proc;   // kept alive: the window calls back into it
    private readonly nint _hwnd, _icon, _largeIcon;
    private readonly uint _taskbarCreated;
    private readonly Action _open, _exit, _openAlerts;
    private Action? _balloonClick; // what clicking the latest notification opens; the Alerts page when null

    public Tray(string iconPath, Action open, Action openAlerts, Action exit)
    {
        _open = open; _openAlerts = openAlerts; _exit = exit;
        _proc = Proc;
        var cls = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "GlassRea.Tray",
        };
        RegisterClassExW(ref cls);
        _hwnd = CreateWindowExW(0, cls.lpszClassName, "Glass-Rea tray", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, cls.hInstance, 0);
        _icon = LoadImageW(0, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49), GetSystemMetrics(50), 0x10 /* LR_LOADFROMFILE */);
        // A notification's large icon must be the full icon size; Windows refuses the small tray one.
        _largeIcon = LoadImageW(0, iconPath, 1, GetSystemMetrics(11), GetSystemMetrics(12), 0x10);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        Add();
    }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        data.szTip = "Glass-Rea";
        Shell_NotifyIconW(NimAdd, ref data);
        data.uVersion = 4; // NOTIFYICON_VERSION_4: richer callbacks
        Shell_NotifyIconW(NimSetVersion, ref data);
    }

    /// <summary>Shows a Windows notification from the tray icon. Clicking it runs <paramref name="onClick"/>, or opens Alerts.</summary>
    public void Notify(string title, string body, Action? onClick = null)
    {
        _balloonClick = onClick;
        var data = Data(NifInfo);
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(body, 255);
        data.dwInfoFlags = 0x20 | 0x04; // NIIF_LARGE_ICON | NIIF_USER: our own icon
        data.hBalloonIcon = _largeIcon;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private NotifyIconData Data(int flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WmTray,
        hIcon = _icon,
        szTip = "", szInfo = "", szInfoTitle = "",
    };

    private nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmTray)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case NinSelect or NinKeySelect or WmLButtonUp: _open(); return 0;
                case NinBalloonUserClick: (_balloonClick ?? _openAlerts)(); return 0;
                case WmContextMenu or WmRButtonUp: ShowMenu(); return 0;
            }
        }
        else if (msg == WmCommand)
        {
            switch ((int)(wParam & 0xFFFF))
            {
                case CmdOpen: _open(); break;
                case CmdExit: _exit(); break;
            }
            return 0;
        }
        else if (msg == _taskbarCreated) Add(); // Explorer restarted: put the icon back
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, CmdOpen, "Open Glass-Rea");
        AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenuW(menu, 0, CmdExit, "Exit");
        SetMenuDefaultItem(menu, CmdOpen, 0);
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // so the menu closes when you click elsewhere
        TrackPopupMenuEx(menu, 0x0002 /* TPM_RIGHTBUTTON */, pt.X, pt.Y, _hwnd, 0);
        DestroyMenu(menu);
    }

    public void Dispose()
    {
        var data = Data(0);
        Shell_NotifyIconW(NimDelete, ref data);
        if (_icon != 0) DestroyIcon(_icon);
        if (_largeIcon != 0) DestroyIcon(_largeIcon);
        DestroyWindow(_hwnd);
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(int msg, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nint id, string? item);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(nint menu, uint item, uint byPos);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
