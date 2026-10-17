using GlassRea.Core.Look;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.Graphics.DirectX;

namespace GlassRea.App.Controls;

/// <summary>
/// Paints the per-pixel images the glass edge is built from, using the optics in <see cref="LiquidGlass"/>:
/// the glass's own outline (soft or crisp), the rim light, and the masks that pick how far inward each pixel of the
/// bezel samples the background. Everything depends only on the distance to the edge, so each image is one
/// corner-sized square drawn once and stretched to any width by a nine-grid brush; the same images serve every
/// glass with the same radius. Changing the optics in Settings draws them all again.
/// </summary>
internal static class GlassLens
{
    /// <summary>How many refraction steps the bezel is split into; neighbouring steps blend, so the bend is smooth.</summary>
    public const int Levels = 8;

    private static CompositionGraphicsDevice? _device;
    private static readonly Dictionary<string, CompositionDrawingSurface> Surfaces = [];
    private static LiquidGlass? _drawnFor;

    public static double Bezel(float radius) => LiquidGlass.BezelFor(radius);

    /// <summary>The inward shift, in pixels, that refraction step <paramref name="level"/> stands for, for red, green and blue.</summary>
    public static (float R, float G, float B) StepShift(LiquidGlass optics, float radius, int level)
    {
        var (r, g, b) = optics.ChannelShifts(Bezel(radius), level, Levels);
        return ((float)r, (float)g, (float)b);
    }

    /// <summary>The glass's outline: opaque inside, fading in from the edge as far as the optics' softness says.</summary>
    public static CompositionNineGridBrush Body(Compositor c, LiquidGlass optics, float radius, float scale) =>
        NineGrid(c, optics, radius, scale, "b", (_, _, _) => 1);

    /// <summary>Mask for refraction step <paramref name="level"/> (1..Levels): opaque wherever the shift reaches that step.</summary>
    public static CompositionNineGridBrush Mask(Compositor c, LiquidGlass optics, float radius, float scale, int level) =>
        NineGrid(c, optics, radius, scale, $"m{level}", (d, _, _) =>
        {
            var bezel = Bezel(radius);
            var step = optics.MaxShift(bezel) / Levels;
            return Math.Clamp(optics.Shift(d, bezel) / step - level + 1, 0, 1);
        });

    /// <summary>The rim light: Fresnel reflection all round, a highlight top left, a glint bottom right.</summary>
    public static CompositionNineGridBrush Rim(Compositor c, LiquidGlass optics, float radius, float scale, bool dark) =>
        NineGrid(c, optics, radius, scale, dark ? "rd" : "rl", (d, nx, ny) => optics.Rim(nx, ny, d, Bezel(radius), dark ? 0.55 : 1.0));

    /// <summary>Called when the graphics device is replaced (driver update, GPU reset): every image is drawn again.</summary>
    public static event Action? Lost;

    private static CompositionNineGridBrush NineGrid(Compositor c, LiquidGlass optics, float radius, float scale, string kind,
        Func<double, double, double, double> alpha)
    {
        // Only the current optics' images are kept: a slider being dragged would otherwise pile up every step's. The old
        // ones live in video memory, which the garbage collector doesn't see, so they're freed by hand once every glass
        // has taken its new images (all of them do, in the same pass that changed the optics).
        if (optics != _drawnFor)
        {
            var old = Surfaces.Values.ToList();
            Surfaces.Clear();
            _drawnFor = optics;
            if (old.Count > 0)
                c.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => old.ForEach(s => s.Dispose()));
        }
        var r = Math.Max(2, (int)Math.Round(radius * scale));
        var key = $"{kind}:{r}:{scale:0.###}";
        if (!Surfaces.TryGetValue(key, out var surface))
            Surfaces[key] = surface = Draw(c, optics, r, scale, alpha);
        var brush = c.CreateNineGridBrush();
        var source = c.CreateSurfaceBrush(surface);
        source.Stretch = CompositionStretch.Fill;
        brush.Source = source;
        brush.SetInsets(r);
        brush.SetInsetScales(1 / scale);
        return brush;
    }

    /// <summary>
    /// A (2r + 2) px square holding a rounded square of radius r: four corners and a 2 px straight middle that the
    /// nine-grid stretches. White, premultiplied, alpha from <paramref name="alpha"/>(distance in, normal x, normal y)
    /// times how much of the pixel the glass covers.
    /// </summary>
    private static CompositionDrawingSurface Draw(Compositor c, LiquidGlass optics, int r, float scale, Func<double, double, double, double> alpha)
    {
        _device ??= CreateDevice(c);
        var size = 2 * r + 2;
        var pixels = new byte[size * size * 4];
        double half = size / 2.0, straight = half - r;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            // Signed distance to the rounded square, and the outward normal there.
            double px = x + 0.5 - half, py = y + 0.5 - half;
            double qx = Math.Abs(px) - straight, qy = Math.Abs(py) - straight;
            double dist, nx, ny;
            if (qx > 0 && qy > 0)
            {
                var l = Math.Sqrt(qx * qx + qy * qy);
                dist = l - r;
                nx = Math.Sign(px) * qx / l;
                ny = Math.Sign(py) * qy / l;
            }
            else if (qx > qy) { dist = qx - r; nx = Math.Sign(px); ny = 0; }
            else { dist = qy - r; nx = 0; ny = Math.Sign(py); }
            var coverage = optics.Coverage(dist, r);
            if (coverage <= 0) continue;
            var a = alpha(Math.Max(0, -dist) / scale, nx, ny) * coverage;
            var b = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);
            var i = (y * size + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = b;
        }
        var surface = _device.CreateDrawingSurface(new Windows.Foundation.Size(size, size),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        Paint(surface, pixels, size);
        return surface;
    }

    private static void Paint(CompositionDrawingSurface surface, byte[] pixels, int size)
    {
        var device = CanvasComposition.GetCanvasDevice(_device!);
        using var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, size, size, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var ds = CanvasComposition.CreateDrawingSession(surface);
        ds.Clear(Microsoft.UI.Colors.Transparent);
        ds.DrawImage(bitmap);
    }

    private static CompositionGraphicsDevice CreateDevice(Compositor c)
    {
        var device = CanvasComposition.CreateCompositionGraphicsDevice(c, CanvasDevice.GetSharedDevice());
        device.RenderingDeviceReplaced += (_, _) =>
        {
            // The old images are gone with the old device; drop them and let every glass ask again.
            c.DispatcherQueue.TryEnqueue(() => { Surfaces.Clear(); _drawnFor = null; Lost?.Invoke(); });
        };
        return device;
    }
}
