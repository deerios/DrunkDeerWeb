namespace DrunkDeer.Web.Theming;

/// <summary>A colour in the OKLCH cylindrical space: lightness, chroma, hue angle.</summary>
/// <param name="L">Perceptual lightness, 0 (black) to 1 (white).</param>
/// <param name="C">Chroma (colourfulness). 0 is grey; the usable maximum depends on L and H.</param>
/// <param name="H">Hue angle in degrees, 0-360.</param>
public readonly record struct Oklch(double L, double C, double H);

/// <summary>An 8-bit sRGB colour.</summary>
public readonly record struct Srgb(byte R, byte G, byte B)
{
    /// <summary>Renders as <c>#rrggbb</c>, which is the form MudBlazor's palette parses.</summary>
    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";
}

/// <summary>
/// Conversions between OKLCH and sRGB.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the two halves of the theme speak different languages. The Zits palette we
/// copy is authored entirely in OKLCH, but MudBlazor's palette only parses hex and rgb() — so every
/// token has to be converted before MudBlazor ever sees it.
/// </para>
/// <para>
/// It also does the work that keeps the profile-driven accent readable. A hue taken off the
/// keyboard is just an angle; pinning it to a fixed lightness and chroma is what stops a yellow
/// board from producing an accent that glares and a blue one from producing an accent that
/// disappears into the background.
/// </para>
/// <para>Maths is Björn Ottosson's OKLab, https://bottosson.github.io/posts/oklab/.</para>
/// </remarks>
public static class Oklch2Srgb
{
    /// <summary>
    /// Converts OKLCH to sRGB, pulling the colour into gamut if it doesn't fit.
    /// </summary>
    /// <remarks>
    /// Most OKLCH triples have no sRGB equivalent — the space is far larger than the monitor's.
    /// Rather than clip the channels (which shifts the hue, the one property we care about
    /// preserving here), we keep L and H and reduce C until the colour fits. That is the standard
    /// gamut-mapping approach and it degrades a vivid colour to a duller one of the same hue.
    /// </remarks>
    public static Srgb ToSrgb(Oklch c) => Quantise(GamutMap(c));

    /// <summary>Converts sRGB to OKLCH. Hue is meaningless when chroma is ~0 and reports as 0.</summary>
    public static Oklch FromSrgb(Srgb c)
    {
        var (L, a, b) = LinearToOklab(
            SrgbToLinear(c.R / 255.0),
            SrgbToLinear(c.G / 255.0),
            SrgbToLinear(c.B / 255.0));

        double chroma = Math.Sqrt(a * a + b * b);
        double hue = chroma < 1e-6 ? 0 : Math.Atan2(b, a) * 180.0 / Math.PI;
        if (hue < 0) hue += 360;
        return new Oklch(L, chroma, hue);
    }

    /// <summary>True when the colour survives the trip to sRGB without any channel clipping.</summary>
    public static bool InGamut(Oklch c)
    {
        var (r, g, b) = ToLinearRgb(c);
        return Within(r) && Within(g) && Within(b);

        // A hair of slack: the matrices are floating point, so an exactly-in-gamut colour can land
        // a few ulps outside and be rejected for nothing.
        static bool Within(double v) => v >= -1e-6 && v <= 1 + 1e-6;
    }

    /// <summary>
    /// Returns the colour unchanged if it fits in sRGB, else the closest colour of the same
    /// lightness and hue that does.
    /// </summary>
    private static Oklch GamutMap(Oklch c)
    {
        if (InGamut(c)) return c;

        // Chroma 0 is grey, which is always in gamut for any lightness in 0..1, so the search
        // always has a valid lower bound to fall back on.
        double lo = 0, hi = c.C;
        for (int i = 0; i < 24; i++)
        {
            double mid = (lo + hi) / 2;
            if (InGamut(c with { C = mid })) lo = mid;
            else hi = mid;
        }
        return c with { C = lo };
    }

    private static Srgb Quantise(Oklch c)
    {
        var (r, g, b) = ToLinearRgb(c);
        return new Srgb(Channel(r), Channel(g), Channel(b));

        static byte Channel(double linear)
        {
            double encoded = LinearToSrgb(linear);
            return (byte)Math.Clamp(Math.Round(encoded * 255), 0, 255);
        }
    }

    private static (double R, double G, double B) ToLinearRgb(Oklch c)
    {
        double rad = c.H * Math.PI / 180.0;
        return OklabToLinear(c.L, c.C * Math.Cos(rad), c.C * Math.Sin(rad));
    }

    private static (double L, double A, double B) LinearToOklab(double r, double g, double b)
    {
        double l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
        double m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
        double s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

        double l_ = Math.Cbrt(l), m_ = Math.Cbrt(m), s_ = Math.Cbrt(s);

        return (
            0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
            1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
            0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_);
    }

    private static (double R, double G, double B) OklabToLinear(double L, double a, double b)
    {
        double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = L - 0.0894841775 * a - 1.2914855480 * b;

        double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;

        return (
            +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    private static double SrgbToLinear(double v) =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double v) =>
        v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
}
