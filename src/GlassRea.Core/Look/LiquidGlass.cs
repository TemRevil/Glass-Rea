namespace GlassRea.Core.Look;

/// <summary>
/// The optics behind the app's glass edges, worked out the way Apple describes Liquid Glass: a slab of glass whose
/// rim rounds over like a squircle, refracting what's behind it and catching light on its curve.
///
/// Surface. Across the bezel (the rounded band of width B along the edge), t = d / B runs 0 at the edge to 1 where
/// the glass turns flat. The height is the convex squircle h(t) = (1 - (1 - t)^4)^(1/4), so the rim rises almost
/// vertically and flattens smoothly: h(0.1) = 0.77, h(0.3) = 0.93, h(0.5) = 0.98.
///
/// Refraction. Looking straight down, the view ray meets the surface at angle θi from its normal, where
/// tan θi = dz/dd = h'(t) · T / B (T is the bezel's height). Snell's law, sin θt = sin θi / n with n = 1.5, bends
/// it by (θi - θt), and over the glass under that point (z = S + T · h) the ray lands
/// Δ = z · tan(θi - θt) further in. So the edge shows what's slightly inside it, which is the bent-light look.
/// With B = T = 12 and S = 6: Δ is about 10.3 px at the rim, 7.3 at 1 px in, 4.7 at 2 px, 1.3 at 5 px, under
/// 0.1 past 9 px.
///
/// Light. The rim's brightness is Fresnel reflection (Schlick: F = F0 + (1 - F0)(1 - cos θi)^5,
/// F0 = ((n - 1) / (n + 1))^2 = 0.04) plus a Blinn-Phong highlight from a light above and to the left, and a
/// weaker glint on the opposite edge where that light leaves the glass again.
/// </summary>
public static class LiquidGlass
{
    /// <summary>Refractive index of the glass (ordinary crown glass).</summary>
    public const double Ior = 1.5;

    /// <summary>Reflectance looking straight on: ((n - 1) / (n + 1))^2.</summary>
    public static readonly double F0 = Math.Pow((Ior - 1) / (Ior + 1), 2);

    /// <summary>The light, from the upper left and in front: x right, y down, z towards the viewer.</summary>
    public static readonly Vec3 Light = Vec3.Normalize(new(-0.42, -0.82, 0.45));

    /// <summary>Bezel width for a corner radius: most of the radius, at most 12.</summary>
    public static double BezelFor(double radius) => Math.Clamp(radius * 0.75, 2, 12);

    /// <summary>Convex squircle profile, 0 at the edge to 1 where the glass is flat.</summary>
    public static double Height(double t) => t <= 0 ? 0 : t >= 1 ? 1 : Math.Pow(1 - Math.Pow(1 - t, 4), 0.25);

    /// <summary>dh/dt. Infinite at the very edge, 0 where the glass is flat.</summary>
    public static double Slope(double t) =>
        t >= 1 ? 0 : t <= 0 ? double.PositiveInfinity : Math.Pow(1 - t, 3) * Math.Pow(1 - Math.Pow(1 - t, 4), -0.75);

    /// <summary>Angle between the view ray and the surface normal, d pixels in from the edge.</summary>
    public static double Incidence(double d, double bezel)
    {
        var t = Math.Max(d / bezel, 1e-4);
        return Math.Atan(Slope(t)); // T = B, so dz/dd = h'(t)
    }

    /// <summary>Snell's law: the refracted angle inside the glass.</summary>
    public static double Refracted(double incidence) => Math.Asin(Math.Sin(incidence) / Ior);

    /// <summary>How far inward (in pixels) the background seen d pixels in from the edge really sits.</summary>
    public static double Shift(double d, double bezel)
    {
        if (d >= bezel) return 0;
        var i = Incidence(d, bezel);
        var z = bezel * 0.5 + bezel * Height(Math.Max(d / bezel, 1e-4)); // slab S = B/2 plus the bezel T = B
        return z * Math.Tan(i - Refracted(i));
    }

    /// <summary>The largest shift for this bezel, reached at the rim.</summary>
    public static double MaxShift(double bezel) => Shift(bezel * 0.02, bezel);

    /// <summary>Schlick's Fresnel reflectance.</summary>
    public static double Fresnel(double cosIncidence) => F0 + (1 - F0) * Math.Pow(1 - Math.Clamp(cosIncidence, 0, 1), 5);

    /// <summary>The surface normal d pixels in from an edge whose outward direction is (nx, ny).</summary>
    public static Vec3 Normal(double nx, double ny, double d, double bezel)
    {
        if (d >= bezel) return new(0, 0, 1);
        var s = Slope(Math.Max(d / bezel, 1e-4));
        return Vec3.Normalize(new(nx * s, ny * s, 1));
    }

    /// <summary>
    /// White light reflected by the rim, 0..1, d pixels in from an edge facing (nx, ny). <paramref name="strength"/>
    /// scales it for the theme (dark glass reflects less of a dim room).
    /// </summary>
    public static double Rim(double nx, double ny, double d, double bezel, double strength)
    {
        if (d >= bezel) return 0;
        var n = Normal(nx, ny, d, bezel);
        var view = new Vec3(0, 0, 1);
        var half = Vec3.Normalize(Light + view);
        var back = Vec3.Normalize(new Vec3(-Light.X, -Light.Y, Light.Z) + view);
        var highlight = Math.Pow(Math.Max(0, Vec3.Dot(n, half)), 28);
        var glint = Math.Pow(Math.Max(0, Vec3.Dot(n, back)), 28);
        var fresnel = (Fresnel(n.Z) - F0) / (1 - F0);
        return Math.Clamp((0.85 * highlight + 0.4 * glint + 0.9 * fresnel) * strength, 0, 1);
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
