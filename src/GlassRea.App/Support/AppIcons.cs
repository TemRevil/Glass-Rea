using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GlassRea.App.Support;

/// <summary>
/// Program icons, drawn by the shell exactly as Explorer shows them, cached by path. Null when the file is gone or
/// has no icon. The shell renders off the UI thread; the bitmap is made on the caller's (UI) thread.
/// </summary>
public static partial class AppIcons
{
    private const int Size = 64; // crisp at 200% for the 30 px tiles

    private static readonly ConcurrentDictionary<string, Task<byte[]?>> Pixels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource> Bitmaps = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ImageSource?> For(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Bitmaps.TryGetValue(path, out var cached)) return cached;
        var pixels = await Pixels.GetOrAdd(path, p => Task.Run(() => Render(p)));
        if (pixels is null) return null;
        if (Bitmaps.TryGetValue(path, out cached)) return cached;
        var bmp = new WriteableBitmap(Size, Size);
        using (var stream = bmp.PixelBuffer.AsStream()) stream.Write(pixels);
        bmp.Invalidate();
        return Bitmaps[path] = bmp;
    }

    /// <summary>Top-down premultiplied BGRA, Size x Size.</summary>
    private static byte[]? Render(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (SHCreateItemFromParsingName(path, 0, typeof(IShellItemImageFactory).GUID, out var factory) != 0 || factory is null) return null;
            if (factory.GetImage(new SIZE(Size, Size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbitmap) != 0 || hbitmap == 0) return null;
            try
            {
                var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = Size, biHeight = -Size, biPlanes = 1, biBitCount = 32 };
                var pixels = new byte[Size * Size * 4];
                var dc = GetDC(0);
                try
                {
                    if (GetDIBits(dc, hbitmap, 0, Size, pixels, ref info, 0) != Size) return null;
                }
                finally { ReleaseDC(0, dc); }
                // Icons without an alpha channel come back fully transparent; treat them as opaque.
                var anyAlpha = false;
                for (var i = 3; i < pixels.Length && !anyAlpha; i += 4) anyAlpha = pixels[i] != 0;
                if (!anyAlpha) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return pixels;
            }
            finally { DeleteObject(hbitmap); }
        }
        catch { return null; }
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct SIZE(int Cx, int Cy);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [GeneratedComInterface, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    internal partial interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out nint hbitmap);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid,
        [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IShellItemImageFactory>))] out IShellItemImageFactory? item);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);
}
