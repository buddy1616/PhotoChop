using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Blend, Lazy<SKRuntimeEffect>> ExtendedBlenders = new();

    private static SKBlender? CreateBlender(Blend blend, bool profiledStyle = false)
    {
        if (profiledStyle && blend is Blend.Multiply or Blend.Screen)
            return LinearStyleBlenders.GetOrAdd(blend, mode => new Lazy<SKRuntimeEffect>(() =>
            {
                string operation = mode == Blend.Multiply ? "b * f" : "1.0 - (1.0 - b) * (1.0 - f)";
                return SKRuntimeEffect.CreateBlender($$"""
                    float linearize(float x) { return x <= 0.04045 ? x / 12.92 : pow((x + 0.055) / 1.055, 2.4); }
                    float encode(float x) { return x <= 0.0031308 ? x * 12.92 : 1.055 * pow(x, 1.0 / 2.4) - 0.055; }
                    float blend(float base, float front) {
                        float b = linearize(clamp(base, 0.0, 1.0));
                        float f = linearize(clamp(front, 0.0, 1.0));
                        return encode({{operation}});
                    }
                    half4 main(half4 src, half4 dst) {
                        if (src.a == 0.0 || dst.a == 0.0) return src + dst;
                        float3 f = src.rgb / src.a, b = dst.rgb / dst.a;
                        float3 color = float3(blend(b.r, f.r), blend(b.g, f.g), blend(b.b, f.b));
                        float overlap = src.a * dst.a;
                        return half4(src.rgb * (1.0 - dst.a) + dst.rgb * (1.0 - src.a) + color * overlap,
                            src.a + dst.a - overlap);
                    }
                    """, out var error) ?? throw new InvalidOperationException(error);
            })).Value.ToBlender();
        if (blend == Blend.Add) return addBlend.Value.ToBlender();
        string? formula = blend switch
        {
            Blend.LinearBurn => "max(b + f - 1.0, 0.0)",
            Blend.LinearLight => "clamp(b + 2.0 * f - 1.0, 0.0, 1.0)",
            Blend.VividLight => "vivid(b, f)",
            Blend.PinLight => "f < 0.5 ? min(b, 2.0 * f) : max(b, 2.0 * f - 1.0)",
            Blend.HardMix => "step(0.5, vivid(b, f))",
            Blend.Subtract => "max(b - f, 0.0)",
            Blend.Divide => "f <= 0.0 ? 1.0 : min(b / f, 1.0)",
            _ => null
        };
        if (formula is null) return null;
        return ExtendedBlenders.GetOrAdd(blend, _ => new Lazy<SKRuntimeEffect>(() =>
        {
            string shader = $$"""
                float vivid(float b, float f) {
                    if (f < 0.5) return b >= 1.0 ? 1.0 : (f <= 0.0 ? 0.0 : max(1.0 - (1.0 - b) / (2.0 * f), 0.0));
                    return b <= 0.0 ? 0.0 : (f >= 1.0 ? 1.0 : min(b / (2.0 - 2.0 * f), 1.0));
                }
                float channel(float b, float f) { return {{formula}}; }
                half4 main(half4 src, half4 dst) {
                    if (src.a == 0.0 || dst.a == 0.0) return src + dst;
                    float3 f = src.rgb / src.a, b = dst.rgb / dst.a;
                    float overlap = src.a * dst.a;
                    float3 mixed = float3(channel(b.r, f.r), channel(b.g, f.g), channel(b.b, f.b));
                    return half4(src.rgb * (1.0 - dst.a) + dst.rgb * (1.0 - src.a) + mixed * overlap,
                        src.a + dst.a - overlap);
                }
                """;
            return SKRuntimeEffect.CreateBlender(shader, out var error) ?? throw new InvalidOperationException(error);
        })).Value.ToBlender();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Blend, Lazy<SKRuntimeEffect>> LinearStyleBlenders = new();

    private static PixelValue BlendLinearStyle(PixelValue backdrop, PixelValue foreground, Blend mode)
    {
        double Channel(double b, double f)
        {
            double value = BlendComponent(LinearChannel(b), LinearChannel(f), mode);
            return value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
        }
        return new(Channel(backdrop.R, foreground.R), Channel(backdrop.G, foreground.G),
            Channel(backdrop.B, foreground.B), foreground.A);
    }

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
        layer.Kind == LayerKind.Group && layer.PassThrough && layer.Opacity == 1 && layer.FillOpacity == 1 && !layer.Clipped &&
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
        Blend.LinearBurn => Math.Max(0, backdrop + foreground - 1),
        Blend.LinearLight => Math.Clamp(backdrop + 2 * foreground - 1, 0, 1),
        Blend.VividLight => VividLight(backdrop, foreground),
        Blend.PinLight => foreground < .5 ? Math.Min(backdrop, 2 * foreground) : Math.Max(backdrop, 2 * foreground - 1),
        Blend.HardMix => VividLight(backdrop, foreground) >= .5 ? 1 : 0,
        Blend.Subtract => Math.Max(0, backdrop - foreground),
        Blend.Divide => foreground <= 0 ? 1 : Math.Min(1, backdrop / foreground),
        Blend.Difference => Math.Abs(backdrop - foreground),
        Blend.Exclusion => backdrop + foreground - 2 * backdrop * foreground,
        _ => foreground
    };

    private static double VividLight(double backdrop, double foreground) => foreground < .5
        ? BlendComponent(backdrop, foreground * 2, Blend.ColorBurn)
        : BlendComponent(backdrop, foreground * 2 - 1, Blend.ColorDodge);

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
