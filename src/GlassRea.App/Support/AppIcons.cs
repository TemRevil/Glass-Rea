using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GlassRea.App.Support;

/// <summary>
/// App icons, cached by what they're drawn from: a program file's path or a Start menu entry
/// (<c>shell:AppsFolder\&lt;app id&gt;</c>), both drawn by the shell exactly as Explorer and Start show them, or an
/// app package's logo (<c>package:&lt;family name&gt;</c>) for packages with no Start entry. Null when there's nothing
/// to draw. The work happens off the UI thread; the bitmap is made on the caller's (UI) thread.
/// </summary>
public static partial class AppIcons
{
    private const int Size = 64; // crisp at 200% for the 30 px tiles

    private static readonly ConcurrentDictionary<string, Task<byte[]?>> Pixels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource> Bitmaps = new(StringComparer.OrdinalIgnoreCase);

    public const string PackagePrefix = "package:";
    public const string StartPrefix = @"shell:AppsFolder\";

    public static async Task<ImageSource?> For(string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        if (Bitmaps.TryGetValue(source, out var cached)) return cached;
        var pixels = await Pixels.GetOrAdd(source, s => s.StartsWith(PackagePrefix, StringComparison.Ordinal)
            ? Task.Run(() => Logo(s[PackagePrefix.Length..])) : OnShellThread(s));
        if (pixels is null) return null;
        if (Bitmaps.TryGetValue(source, out cached)) return cached;
        var bmp = new WriteableBitmap(Size, Size);
        using (var stream = bmp.PixelBuffer.AsStream()) stream.Write(pixels);
        bmp.Invalidate();
        return Bitmaps[source] = bmp;
    }

    /// <summary>A package's logo, fitted into Size x Size: top-down premultiplied BGRA.</summary>
    private static async Task<byte[]?> Logo(string family)
    {
        try
        {
            if (AppFaces.PackageOf(family)?.Package is not { } package) return null;
            using var stream = await package.GetLogoAsRandomAccessStreamReference(new Windows.Foundation.Size(Size, Size)).OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var scale = (double)Size / Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            var (w, h) = ((uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)), (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)));
            var data = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant },
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var src = data.DetachPixelData();
            // Centred: a logo that isn't square keeps its shape.
            var pixels = new byte[Size * Size * 4];
            var (left, top) = ((Size - (int)w) / 2, (Size - (int)h) / 2);
            for (var y = 0; y < h; y++) Array.Copy(src, y * w * 4, pixels, ((top + y) * Size + left) * 4, w * 4);
            return pixels;
        }
        catch { return null; }
    }

    // The shell draws one icon at a time: asked for several at once, it answers "pending" (E_PENDING) and draws
    // nothing. So every request waits its turn on one thread of its own.
    private static readonly BlockingCollection<(string Source, TaskCompletionSource<byte[]?> Done)> Queue = [];

    static AppIcons()
    {
        var worker = new Thread(() => { foreach (var (source, done) in Queue.GetConsumingEnumerable()) done.SetResult(Render(source)); })
        { IsBackground = true, Name = "App icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private static Task<byte[]?> OnShellThread(string source)
    {
        var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((source, done));
        return done.Task;
    }

    private const int E_PENDING = unchecked((int)0x8000000A);

    /// <summary>Top-down premultiplied BGRA, Size x Size.</summary>
    private static byte[]? Render(string source)
    {
        try
        {
            if (!source.StartsWith(StartPrefix, StringComparison.OrdinalIgnoreCase) && !File.Exists(source)) return null;
            if (SHCreateItemFromParsingName(source, 0, typeof(IShellItemImageFactory).GUID, out var factory) != 0 || factory is null) return null;
            nint hbitmap = 0;
            var hr = E_PENDING;
            // Still pending now and then while the shell reads a file's icon for the first time.
            for (var tries = 0; hr == E_PENDING && tries < 10; tries++)
            {
                if (tries > 0) Thread.Sleep(30);
                hr = factory.GetImage(new SIZE(Size, Size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbitmap);
            }
            if (hr != 0 || hbitmap == 0) return null;
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
