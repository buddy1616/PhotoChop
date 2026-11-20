using Chop.Core;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    // OKLab coordinates of the ICC-converted display colors.
    private readonly record struct Lab(double L, double A, double B);
    private static readonly double[] LinearSrgb = Enumerable.Range(0, 256).Select(i => Decode(i / 255d)).ToArray();

    private static double Decode(double x) => x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4);
    private static double LinearChannel(double x)
    {
        int index = (int)Math.Round(x * 255);
        return index is >= 0 and <= 255 && Math.Abs(index / 255d - x) < 1e-7 ? LinearSrgb[index] : Decode(x);
    }

    private static Lab PerceptualLab(PixelValue value)
    {
        double r = LinearChannel(value.R), g = LinearChannel(value.G), b = LinearChannel(value.B);
        double l = Math.Cbrt(.4122214708 * r + .5363325363 * g + .0514459929 * b);
        double m = Math.Cbrt(.2119034982 * r + .6806995451 * g + .1073969566 * b);
        double s = Math.Cbrt(.0883024619 * r + .2817188376 * g + .6299787005 * b);
        return new(.2104542553 * l + .7936177850 * m - .0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + .4505937099 * s,
            .0259040371 * l + .7827717662 * m - .8086757660 * s);
    }

    private static PixelValue PerceptualRecolor(PixelValue backdrop, Lab ink)
    {
        double lightness = PerceptualLab(backdrop).L;
        PixelValue WithChroma(double scale)
        {
            double a = ink.A * scale, b = ink.B * scale;
            double l = lightness + .3963377774 * a + .2158037573 * b;
            double m = lightness - .1055613458 * a - .0638541728 * b;
            double s = lightness - .0894841775 * a - 1.2914855480 * b;
            (l, m, s) = (l * l * l, m * m * m, s * s * s);
            return new(4.0767416621 * l - 3.3077115913 * m + .2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - .3413193965 * s,
                -.0041960863 * l - .7034186147 * m + 1.7076147010 * s, backdrop.A);
        }
        static bool Fits(PixelValue rgb) => rgb.R is >= 0 and <= 1 && rgb.G is >= 0 and <= 1 && rgb.B is >= 0 and <= 1;
        // Carry the ink's chroma relative to its lightness into darker areas instead of applying
        // the full chroma of a bright swatch to shadows. This keeps the underlying gradation.
        double scale = ink.L > 0 ? Math.Min(1, lightness / ink.L) : 1;
        var result = WithChroma(scale);
        if (!Fits(result))
        {
            // Keep lightness and hue when the requested chroma is outside the display gamut.
            double low = 0, high = scale;
            for (int i = 0; i < 10; i++)
            {
                double mid = (low + high) / 2;
                if (Fits(WithChroma(mid))) low = mid;
                else high = mid;
            }
            result = WithChroma(low);
        }
        static double Encode(double x)
        {
            x = Math.Clamp(x, 0, 1);
            return x <= .0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - .055;
        }
        return new(Encode(result.R), Encode(result.G), Encode(result.B), result.A);
    }
}
