namespace GlassRea.Core.Look;

/// <summary>
/// The optics behind the app's glass edges, worked out the way Apple describes Liquid Glass: a slab of glass whose
/// rim rounds over like a squircle, refracting what's behind it and catching light on its curve. Every number a
/// person can change in Settings > Appearance > Glass is a property here; <see cref="Default"/> is the look the app
/// ships with. The set of controls follows RealGlass (https://github.com/explosion-scratch/realglass); the optics
/// are this app's own.
///
/// Surface. Across the bezel (the rounded band of width B along the edge), t = d / B runs 0 at the edge to 1 where
/// the glass turns flat. The height is the convex squircle h(t) = (1 - (1 - t)^4)^(1/4), so the rim rises almost
/// vertically and flattens smoothly: h(0.1) = 0.77, h(0.3) = 0.93, h(0.5) = 0.98.
///
/// Refraction. Looking straight down, the view ray meets the surface at angle θi from its normal, where
/// tan θi = dz/dd = h'(t) · T / B (T is the bezel's height, <see cref="Thickness"/> times B). Snell's law,
/// sin θt = sin θi / n, bends it by (θi - θt), and over the glass under that point (z = S + T · h, the slab S being
/// half of T) the ray lands Δ = z · tan(θi - θt) further in. So the edge shows what's slightly inside it, which is
/// the bent-light look. With B = T = 12, S = 6 and n = 1.5: Δ is about 10.3 px at the rim, 7.3 at 1 px in, 4.7 at
/// 2 px, 1.3 at 5 px, under 0.1 past 9 px.
///
/// Colour. Real glass bends blue more than red (Cauchy: n(λ) = A + C / λ², C = 0.0042 µm² for crown glass), so each
/// colour lands a different distance in and the rim fringes. <see cref="Dispersion"/> scales that spread, from none
/// to 60 times crown glass's.
///
/// Light. The rim's brightness is Fresnel reflection (Schlick: F = F0 + (1 - F0)(1 - cos θi)^5,
/// F0 = ((n - 1) / (n + 1))^2, 0.04 for n = 1.5) plus a Blinn-Phong highlight from a light above and to the left,
/// and a weaker glint on the opposite edge where that light leaves the glass again.
/// </summary>
/// <param name="Ior">Refractive index; 1.5 is ordinary crown glass.</param>
/// <param name="Thickness">How tall the rim stands, as a multiple of its width. Taller glass bends light further.</param>
/// <param name="LightStrength">Multiplies the rim's reflection and highlights.</param>
/// <param name="Shininess">Blinn-Phong exponent: higher is a smaller, sharper highlight.</param>
/// <param name="Dispersion">0..1: how far the colours split at the rim.</param>
/// <param name="Softness">0..1: how far in from the edge the glass fades in, as a share of the corner radius.</param>
public sealed record LiquidGlass(
    double Ior = 1.5, double Thickness = 1, double LightStrength = 1, double Shininess = 28, double Dispersion = 0, double Softness = 0)
{
    public static readonly LiquidGlass Default = new();

    /// <summary>The light, from the upper left and in front: x right, y down, z towards the viewer.</summary>
    public static readonly Vec3 Light = Vec3.Normalize(new(-0.42, -0.82, 0.45));

    /// <summary>Cauchy's C for crown glass, µm².</summary>
    private const double CauchyC = 0.0042;
    /// <summary>Wavelengths, µm, for the red, green and blue that the screen shows.</summary>
    private const double Red = 0.65, Green = 0.55, Blue = 0.45;
    /// <summary>At full <see cref="Dispersion"/> the colours spread this many times more than in crown glass.</summary>
    private const double MaxDispersion = 60;

    /// <summary>Reflectance looking straight on: ((n - 1) / (n + 1))^2.</summary>
    public double F0 => Math.Pow((Ior - 1) / (Ior + 1), 2);

    /// <summary>Bezel width for a corner radius: most of the radius, at most 12.</summary>
    public static double BezelFor(double radius) => Math.Clamp(radius * 0.75, 2, 12);

    /// <summary>
    /// The corner radius to draw: <paramref name="roundness"/> (0..1) of the design's own radius, where a capsule's is
    /// half its height and no radius is ever more than half the shorter side.
    /// </summary>
    public static double Corner(double design, bool capsule, double width, double height, double roundness)
    {
        var most = Math.Min(width, height) / 2;
        return Math.Min(capsule ? most : design, most) * Math.Clamp(roundness, 0, 1);
    }

    /// <summary>Convex squircle profile, 0 at the edge to 1 where the glass is flat.</summary>
    public static double Height(double t) => t <= 0 ? 0 : t >= 1 ? 1 : Math.Pow(1 - Math.Pow(1 - t, 4), 0.25);

    /// <summary>dh/dt. Infinite at the very edge, 0 where the glass is flat.</summary>
    public static double Slope(double t) =>
        t >= 1 ? 0 : t <= 0 ? double.PositiveInfinity : Math.Pow(1 - t, 3) * Math.Pow(1 - Math.Pow(1 - t, 4), -0.75);

    /// <summary>Angle between the view ray and the surface normal, d pixels in from the edge.</summary>
    public double Incidence(double d, double bezel)
    {
        var t = Math.Max(d / bezel, 1e-4);
        return Math.Atan(Slope(t) * Thickness); // dz/dd = h'(t) · T / B
    }

    /// <summary>Snell's law: the refracted angle inside glass of index <paramref name="ior"/> (this glass's when left out).</summary>
    public double Refracted(double incidence, double? ior = null) =>
        Math.Asin(Math.Min(1, Math.Sin(incidence) / (ior ?? Ior)));

    /// <summary>How far inward (in pixels) the background seen d pixels in from the edge really sits.</summary>
    public double Shift(double d, double bezel, double? ior = null)
    {
        if (d >= bezel) return 0;
        var i = Incidence(d, bezel);
        var z = Thickness * (bezel * 0.5 + bezel * Height(Math.Max(d / bezel, 1e-4))); // slab S = T/2 plus the bezel T
        return z * Math.Tan(i - Refracted(i, ior));
    }

    /// <summary>The largest shift for this bezel, reached at the rim.</summary>
    public double MaxShift(double bezel) => Shift(bezel * 0.02, bezel);

    /// <summary>The index for one colour: this glass's index is green's, red bends less and blue more.</summary>
    public double IorFor(double wavelength) =>
        Math.Max(1.01, Ior + Dispersion * MaxDispersion * CauchyC * (1 / (wavelength * wavelength) - 1 / (Green * Green)));

    /// <summary>
    /// Refraction step <paramref name="level"/> of <paramref name="levels"/> shifts the background by
    /// MaxShift / levels · level. This is that shift for red, green and blue: green's is the step's own, red's and
    /// blue's are what their indices give where green's lands on the step.
    /// </summary>
    public (double R, double G, double B) ChannelShifts(double bezel, int level, int levels)
    {
        var g = MaxShift(bezel) / levels * level;
        if (Dispersion <= 0 || g <= 0) return (g, g, g);
        // Shift falls steadily across the bezel, so find where it equals g by halving.
        double lo = bezel * 0.02, hi = bezel;
        for (var k = 0; k < 40; k++)
        {
            var mid = (lo + hi) / 2;
            if (Shift(mid, bezel) > g) lo = mid; else hi = mid;
        }
        var d = (lo + hi) / 2;
        var here = Shift(d, bezel);
        if (here <= 0) return (g, g, g);
        return (g * Shift(d, bezel, IorFor(Red)) / here, g, g * Shift(d, bezel, IorFor(Blue)) / here);
    }

    /// <summary>Schlick's Fresnel reflectance.</summary>
    public double Fresnel(double cosIncidence) => F0 + (1 - F0) * Math.Pow(1 - Math.Clamp(cosIncidence, 0, 1), 5);

    /// <summary>The surface normal d pixels in from an edge whose outward direction is (nx, ny).</summary>
    public Vec3 Normal(double nx, double ny, double d, double bezel)
    {
        if (d >= bezel) return new(0, 0, 1);
        var s = Slope(Math.Max(d / bezel, 1e-4)) * Thickness;
        return Vec3.Normalize(new(nx * s, ny * s, 1));
    }

    /// <summary>
    /// White light reflected by the rim, 0..1, d pixels in from an edge facing (nx, ny). <paramref name="strength"/>
    /// scales it for the theme (dark glass reflects less of a dim room), on top of <see cref="LightStrength"/>.
    /// </summary>
    public double Rim(double nx, double ny, double d, double bezel, double strength)
    {
        if (d >= bezel) return 0;
        var n = Normal(nx, ny, d, bezel);
        var view = new Vec3(0, 0, 1);
        var half = Vec3.Normalize(Light + view);
        var back = Vec3.Normalize(new Vec3(-Light.X, -Light.Y, Light.Z) + view);
        var highlight = Math.Pow(Math.Max(0, Vec3.Dot(n, half)), Shininess);
        var glint = Math.Pow(Math.Max(0, Vec3.Dot(n, back)), Shininess);
        var fresnel = (Fresnel(n.Z) - F0) / (1 - F0);
        return Math.Clamp((0.85 * highlight + 0.4 * glint + 0.9 * fresnel) * strength * LightStrength, 0, 1);
    }

    /// <summary>
    /// How much of the glass covers a pixel whose centre is <paramref name="dist"/> pixels outside the edge (negative
    /// inside): the usual half-pixel antialiasing, faded in over the first <see cref="Softness"/> · radius pixels.
    /// </summary>
    public double Coverage(double dist, double radius)
    {
        var edge = Math.Clamp(0.5 - dist, 0, 1);
        var feather = Softness * radius;
        if (feather <= 0 || edge <= 0) return edge;
        var x = Math.Clamp(-dist / feather, 0, 1);
        return edge * x * x * (3 - 2 * x);
    }
}

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Normalize(Vec3 v)
    {
        var l = Math.Sqrt(Dot(v, v));
        return l == 0 ? v : new(v.X / l, v.Y / l, v.Z / l);
    }
}
