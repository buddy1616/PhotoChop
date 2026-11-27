using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private sealed record CmykInput(Layer Layer, ColorPlanes Planes, long X, long Y, double InkScale, double[] InkOffset);

    // A source-over stack of ink samples can be combined before the profile transform. Effects
    // and blend modes that require the RGB renderer fall back to the normal compositor.
    private Pixels? CompositeCmyk(Document document)
    {
        if (document.Color is not { Mode: ChannelColorMode.Cmyk, Depth: ChannelDepth.Bit8 } ||
            document.IccProfile is null) return null;
        var inputs = new List<CmykInput>();
        if (!Collect(document.Layers)) return null;

        int width = document.Width, height = document.Height;
        byte[][] planes = Enumerable.Range(0, 5).Select(_ => new byte[checked(width * height)]).ToArray();
        Span<double> ink = stackalloc double[4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            ink.Clear();
            double alpha = 0;
            foreach (var input in inputs)
            {
                long sx = x - input.X, sy = y - input.Y;
                var native = input.Planes;
                if (sx < 0 || sy < 0 || sx >= native.Width || sy >= native.Height) continue;
                int pixel = (int)(sy * native.Width + sx);
                var layer = input.Layer;
                double coverage = layer.Opacity * layer.FillOpacity *
                    (native.Planes.Length == 5 ? native.Planes[4][pixel] / 255d : 1);
                if (layer.MaskEnabled && layer.Mask is { } mask)
                    coverage *= mask.At((int)sx + layer.RasterX, (int)sy + layer.RasterY) / 255d;
                if (coverage == 0) continue;
                for (int c = 0; c < 4; c++)
                {
                    double value = native.Planes[c][pixel] / 255d * input.InkScale + input.InkOffset[c];
                    ink[c] = value * coverage + ink[c] * (1 - coverage);
                }
                alpha = coverage + alpha * (1 - coverage);
            }
            if (alpha == 0) continue;
            int index = y * width + x;
            planes[4][index] = ChannelSamples.Display((float)alpha);
            for (int c = 0; c < 4; c++) planes[c][index] = ChannelSamples.Display((float)(ink[c] / alpha));
        }
        return PixelBuffer.FromNative(new(document.Color, width, height, planes));

        bool Collect(Layer[] layers)
        {
            foreach (var layer in layers)
            {
                if (!layer.Visible || layer.Opacity == 0) continue;
                if (layer.Clipped || ActiveVectorMask(layer.VectorMask) || SpatialMatrix(layer) != SKMatrix.Identity ||
                    (layer.TransformsEnabled ? CommandEngine.EffectiveBlend(layer) : layer.Blend) != Blend.Normal)
                    return false;
                var matrix = LayerMatrix(layer, !layer.TransformsEnabled);
                var overlays = layer.TransformsEnabled ? layer.Effects.Where(ProcessesPixels).ToArray() : [];
                if (layer.Kind == LayerKind.Group)
                {
                    if (matrix != SKMatrix.Identity || layer.Opacity != 1 || layer.FillOpacity != 1 ||
                        layer.MaskEnabled && layer.Mask is not null || overlays.Length > 0 || !Collect(layer.Children))
                        return false;
                    continue;
                }

                if (layer.Kind != LayerKind.Raster ||
                    overlays.Any(effect => effect.Kind != EffectKind.ColorOverlay || effect.Mode != Blend.Normal || effect.CmykInk is null || ReducesOverlayCoverage(effect)) ||
                    matrix != SKMatrix.CreateTranslation(matrix.TransX, matrix.TransY) ||
                    matrix.TransX != MathF.Truncate(matrix.TransX) || matrix.TransY != MathF.Truncate(matrix.TransY) ||
                    matrix.TransX is < -32768 or > 32768 || matrix.TransY is < -32768 or > 32768)
                    return false;
                if (ResolveRaster(layer) is not { } raster) continue;
                if (layer.PixelShifts.Length != 0 && layer.MaskEnabled && layer.Mask is not null) return false;
                if (raster.Pixels.Native is not { } native || native.Settings != document.Color) return false;
                // Compose the affine ink adjustments once per layer.
                double scale = 1;
                var offset = new double[4];
                foreach (var effect in overlays)
                {
                    var inkColor = effect.CmykInk!;
                    double mix = effect.Opacity * (Format.Color(effect.Ink) >> 24) / 255d;
                    scale *= 1 - mix;
                    offset[0] = offset[0] * (1 - mix) + inkColor.Cyan * mix;
                    offset[1] = offset[1] * (1 - mix) + inkColor.Magenta * mix;
                    offset[2] = offset[2] * (1 - mix) + inkColor.Yellow * mix;
                    offset[3] = offset[3] * (1 - mix) + inkColor.Black * mix;
                }
                inputs.Add(new(layer, native, raster.X + (long)matrix.TransX, raster.Y + (long)matrix.TransY, scale, offset));
            }
            return true;
        }
    }
}
