using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private static SKBlendMode ToSkiaBlend(Blend blend) => blend switch
    {
        Blend.Multiply => SKBlendMode.Multiply,
        Blend.Screen => SKBlendMode.Screen,
        Blend.Overlay => SKBlendMode.Overlay,
        Blend.Add => SKBlendMode.Plus,
        Blend.Darken => SKBlendMode.Darken,
        Blend.Lighten => SKBlendMode.Lighten,
        Blend.ColorBurn => SKBlendMode.ColorBurn,
        Blend.ColorDodge => SKBlendMode.ColorDodge,
        Blend.HardLight => SKBlendMode.HardLight,
        Blend.SoftLight => SKBlendMode.SoftLight,
        Blend.Difference => SKBlendMode.Difference,
        Blend.Exclusion => SKBlendMode.Exclusion,
        Blend.Hue => SKBlendMode.Hue,
        Blend.Saturation => SKBlendMode.Saturation,
        Blend.Color => SKBlendMode.Color,
        Blend.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver
    };

    private static bool IsPassThroughGroup(Layer layer) =>
        layer.Kind == LayerKind.Group && layer.PassThrough && layer.Opacity == 1 && !layer.Clipped &&
        layer.Placement == new Placement() && SpatialMatrix(layer) == SKMatrix.Identity &&
        (layer.Mask is null || !layer.MaskEnabled) && !ActiveVectorMask(layer.VectorMask) &&
        (!layer.TransformsEnabled || !layer.Effects.Any(ProcessesPixels)) &&
        (layer.TransformsEnabled ? CommandEngine.EffectiveBlend(layer) : layer.Blend) == Blend.Normal;

    private static double BlendComponent(double backdrop, double foreground, Blend mode) => mode switch
    {
        Blend.Multiply => backdrop * foreground,
        Blend.Screen => 1 - (1 - backdrop) * (1 - foreground),
        Blend.Overlay => HardLight(foreground, backdrop),
        Blend.Add => Math.Min(1, backdrop + foreground),
        Blend.Darken => Math.Min(backdrop, foreground),
        Blend.Lighten => Math.Max(backdrop, foreground),
        Blend.ColorBurn => backdrop >= 1 ? 1 : foreground <= 0 ? 0 : 1 - Math.Min(1, (1 - backdrop) / foreground),
        Blend.ColorDodge => backdrop <= 0 ? 0 : foreground >= 1 ? 1 : Math.Min(1, backdrop / (1 - foreground)),
        Blend.HardLight => HardLight(backdrop, foreground),
        Blend.SoftLight => foreground <= .5
            ? backdrop - (1 - 2 * foreground) * backdrop * (1 - backdrop)
            : backdrop + (2 * foreground - 1) * (SoftLightCurve(backdrop) - backdrop),
        Blend.Difference => Math.Abs(backdrop - foreground),
        Blend.Exclusion => backdrop + foreground - 2 * backdrop * foreground,
        _ => foreground
    };

    private static double HardLight(double backdrop, double foreground) =>
        foreground <= .5 ? 2 * backdrop * foreground : 1 - 2 * (1 - backdrop) * (1 - foreground);

    private static double SoftLightCurve(double value) =>
        value <= .25 ? ((16 * value - 12) * value + 4) * value : Math.Sqrt(value);

    // Photoshop's nonseparable modes replace luminance and saturation in RGB space.
    private static PixelValue BlendRgb(PixelValue backdrop, PixelValue foreground, Blend mode) => mode switch
    {
        Blend.Hue => SetLuminance(SetSaturation(foreground, Saturation(backdrop)), Luminance(backdrop)),
        Blend.Saturation => SetLuminance(SetSaturation(backdrop, Saturation(foreground)), Luminance(backdrop)),
        Blend.Color => SetLuminance(foreground, Luminance(backdrop)),
        Blend.Luminosity => SetLuminance(backdrop, Luminance(foreground)),
        _ => new(BlendComponent(backdrop.R, foreground.R, mode), BlendComponent(backdrop.G, foreground.G, mode),
            BlendComponent(backdrop.B, foreground.B, mode), foreground.A)
    };

    private static double Luminance(PixelValue value) => .3 * value.R + .59 * value.G + .11 * value.B;
    private static double Minimum(PixelValue value) => Math.Min(value.R, Math.Min(value.G, value.B));
    private static double Maximum(PixelValue value) => Math.Max(value.R, Math.Max(value.G, value.B));
    private static double Saturation(PixelValue value) => Maximum(value) - Minimum(value);

    private static PixelValue SetSaturation(PixelValue value, double saturation)
    {
        double minimum = Minimum(value), range = Saturation(value);
        if (range == 0) return new(0, 0, 0, value.A);
        return new((value.R - minimum) * saturation / range, (value.G - minimum) * saturation / range,
            (value.B - minimum) * saturation / range, value.A);
    }

    private static PixelValue SetLuminance(PixelValue value, double luminance)
    {
        double delta = luminance - Luminance(value);
        value = new(value.R + delta, value.G + delta, value.B + delta, value.A);
        double minimum = Minimum(value), maximum = Maximum(value);
        if (minimum < 0)
            value = Scale(value, luminance, luminance - minimum);
        if (maximum > 1)
            value = Scale(value, 1 - luminance, maximum - luminance);
        return value;

        PixelValue Scale(PixelValue color, double numerator, double denominator)
        {
            if (denominator == 0) return new(luminance, luminance, luminance, color.A);
            double scale = numerator / denominator;
            return new(luminance + (color.R - luminance) * scale,
                luminance + (color.G - luminance) * scale, luminance + (color.B - luminance) * scale, color.A);
        }
    }
}
