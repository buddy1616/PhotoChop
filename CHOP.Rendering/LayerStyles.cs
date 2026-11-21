using Chop.Core;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private static Frame OuterGlowFrame(Frame frame, Effect effect)
    {
        int spread = (int)Math.Round(effect.Size * effect.Spread / 100);
        double sigma = Math.Max(.1, (effect.Size - spread) / 3);
        if (effect.PreciseGlow) sigma *= .75;
        var halo = Blur(AlphaEffect(frame, spread, true, 0xFFFFFFFF, 1, spread), sigma);
        var pixels = halo.Pixels;
        var buffer = new PixelBuffer(pixels.Width, pixels.Height, PixelBuffer.Settings(pixels));
        var ink = PixelValue.FromArgb(Format.Color(effect.Ink));
        for (int i = 0; i < pixels.Width * pixels.Height; i++)
        {
            double coverage = Math.Pow(PixelBuffer.Alpha(pixels, i), 50 / effect.GlowRange);
            buffer.Set(i, ink with { A = ink.A * effect.Opacity * ContourCurve.Sample(effect.Contour, coverage) });
        }
        return halo with { Pixels = buffer.Finish() };
    }

    private Frame RenderLayerStyle(Frame frame, Effect effect, Frame? silhouette = null)
    {
        if (effect.Kind == EffectKind.Glow && effect.Alignment == StrokeAlignment.Outside)
            return StackFrames(OuterGlowFrame(frame, effect), frame);
        var source = frame.Pixels;
        var precision = PixelBuffer.Settings(source);
        var gradient = effect.Stops is { Length: > 0 } stops ? GradientRamp.Sampler(stops) : null;
        var texture = effect.Texture;
        Pixels? tile = texture?.Png is { } png ? DecodePng(png) : null;
        if (tile is { Width: > 1024 } or { Height: > 1024 })
            throw new InvalidDataException("Texture dimensions cannot exceed 1024 pixels per side.");
        PixelValue Texel(int x, int y)
        {
            if (texture is null) return default;
            int tx = (int)Math.Floor(x / texture.Scale), ty = (int)Math.Floor(y / texture.Scale);
            static int Wrap(int n, int size) => ((n % size) + size) % size;
            if (tile is not null) return PixelBuffer.Get(tile, Wrap(ty, tile.Height) * tile.Width + Wrap(tx, tile.Width));
            double gray = texture.Preset switch
            {
                TexturePreset.Grain => ((unchecked((tx * 73856093) ^ (ty * 19349663)) >>> 6) & 255) / 255d,
                TexturePreset.Weave => (Wrap(tx, 8) < 3 ? .35 : 0) + (Wrap(ty, 8) < 3 ? .35 : 0) + .15,
                _ => ((tx >> 3) + (ty >> 3) & 1) == 0 ? .22 : .8
            };
            return new(gray, gray, gray, 1);
        }

        double TextureHeight(int sx, int sy)
        {
            var pixel = Texel(sx, sy);
            return (pixel.R - .5) * pixel.A;
        }

        bool glow = effect.Kind == EffectKind.Glow;
        bool shadow = effect.Kind == EffectKind.InnerShadow;
        bool bevel = effect.Kind == EffectKind.BevelEmboss;
        int spread = glow ? (int)Math.Round(effect.Size * effect.Spread / 100) : 0;
        int pad = glow && effect.Alignment != StrokeAlignment.Inside ? (int)Math.Ceiling(effect.Size * 3) + spread :
            bevel && effect.Alignment != StrokeAlignment.Inside ? (int)Math.Ceiling(effect.Size * 3) : 0;
        var shapeFrame = silhouette ?? frame;
        var bevelCoverage = bevel ? new BevelCoverage(shapeFrame.Pixels, effect.Size, effect.Alignment, effect.Lighting) : null;
        int shapeX = frame.X - shapeFrame.X, shapeY = frame.Y - shapeFrame.Y;
        double ShapeAt(int x, int y) => bevelCoverage!.At(x + shapeX, y + shapeY);
        double ShapeAlpha(int x, int y)
        {
            x += shapeX;
            y += shapeY;
            var pixels = shapeFrame.Pixels;
            return x >= 0 && y >= 0 && x < pixels.Width && y < pixels.Height ? PixelBuffer.Alpha(pixels, y * pixels.Width + x) : 0;
        }
        var coverage = glow || shadow
            ? Blur(AlphaEffect(frame, spread, true, 0xFFFFFFFF, 1, spread),
                glow ? effect.Size * (1 - effect.Spread / 100) : effect.Size)
            : frame;
        var ink = PixelValue.FromArgb(Format.Color(effect.Ink));
        int width = checked(source.Width + 2 * pad), height = checked(source.Height + 2 * pad);
        var output = new PixelBuffer(width, height, precision);
        double angle = effect.A * Math.PI / 180, dx = Math.Cos(angle), dy = Math.Sin(angle);
        double extent = Math.Max(1, Math.Abs(dx) * (source.Width - 1) + Math.Abs(dy) * (source.Height - 1));
        double altitude = effect.B * Math.PI / 180;
        var lighting = effect.Lighting;
        var highlight = PixelValue.FromArgb(Format.Color(lighting?.Highlight ?? "#FFFFFFFF"));
        var shade = PixelValue.FromArgb(Format.Color(lighting?.Shadow ?? "#FF000000"));
        double AlphaAt(int x, int y)
        {
            int sx = x + frame.X - coverage.X, sy = y + frame.Y - coverage.Y;
            return sx >= 0 && sy >= 0 && sx < coverage.Pixels.Width && sy < coverage.Pixels.Height
                ? PixelBuffer.Alpha(coverage.Pixels, sy * coverage.Pixels.Width + sx) : 0;
        }
        for (int y = -pad; y < source.Height + pad; y++)
        for (int x = -pad; x < source.Width + pad; x++)
        {
            var original = x >= 0 && y >= 0 && x < source.Width && y < source.Height
                ? PixelBuffer.Get(source, y * source.Width + x) : default;
            var color = ink;
            var mode = effect.Mode;
            double weight = 1;
            double outside = 0;
            if (effect.Kind == EffectKind.GradientOverlay)
                color = gradient!(.5 + ((x - (source.Width - 1) / 2d) * dx + (y - (source.Height - 1) / 2d) * dy) / extent);
            else if (effect.Kind == EffectKind.PatternOverlay) color = Texel(x, y);
            else if (shadow) weight = ContourCurve.Sample(effect.Contour, 1 - AlphaAt(x + (int)Math.Round(dx * effect.B), y + (int)Math.Round(dy * effect.B)));
            else if (glow)
            {
                double halo = ContourCurve.Sample(effect.Contour, AlphaAt(x, y));
                weight = effect.Alignment == StrokeAlignment.Outside ? 0 : 1 - halo;
                if (effect.Alignment != StrokeAlignment.Inside) outside = halo * effect.Opacity * color.A;
            }
            else if (bevel)
            {
                double depth = effect.Size * (lighting?.Depth ?? 1) / 2;
                double gx = (ShapeAt(x + 1, y) - ShapeAt(x - 1, y)) * depth;
                double gy = (ShapeAt(x, y + 1) - ShapeAt(x, y - 1)) * depth;
                if (texture is { Depth: > 0 })
                {
                    // Texture depth is independent of bevel depth and width.
                    int sx = x + shapeX, sy = y + shapeY;
                    double relief = texture.Depth * ShapeAlpha(x, y) / 2;
                    gx += (TextureHeight(sx + 1, sy) - TextureHeight(sx - 1, sy)) * relief;
                    gy += (TextureHeight(sx, sy + 1) - TextureHeight(sx, sy - 1)) * relief;
                }
                // Normalize the surface normal before projecting onto the light. Doing this after
                // projection clips most angles to full intensity as depth increases.
                double normalLength = Math.Sqrt(1 + gx * gx + gy * gy);
                double light = -(gx * dx + gy * dy) * Math.Cos(altitude) / normalLength *
                    (lighting?.Down == true ? -1 : 1);
                double magnitude = Math.Abs(light);
                // Increase facet contrast without flattening the angular falloff around curved edges.
                if (lighting?.Technique == BevelTechnique.ChiselHard)
                    magnitude = magnitude * magnitude * (3 - 2 * magnitude);
                double strength = ContourCurve.Sample(effect.Contour, magnitude);
                weight = effect.Alignment == StrokeAlignment.Outside ? 0 : strength * ShapeAlpha(x, y);
                color = light < 0 ? shade : highlight;
                if (lighting is not null)
                {
                    mode = light < 0 ? lighting.ShadowMode : lighting.HighlightMode;
                    color = color with { A = color.A * (light < 0 ? lighting.ShadowOpacity : lighting.HighlightOpacity) };
                }
                if (effect.Alignment != StrokeAlignment.Inside)
                {
                    // Fade the relief into transparency instead of outlining its outer boundary.
                    double edge = Math.Clamp(ShapeAt(x, y) * 2, 0, 1);
                    outside = strength * effect.Opacity * color.A * edge;
                }
            }
            var blended = BlendRgb(original, color, mode);
            var result = PixelValue.Lerp(original, blended, weight * color.A * effect.Opacity) with { A = original.A };
            if (outside > 0) result = result.Over(color with { A = outside });
            output.Set((y + pad) * width + x + pad, result);
        }
        return new(output.Finish(), frame.X - pad, frame.Y - pad);
    }
}
