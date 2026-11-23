using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    public Layer InvertPixels(Document document, Layer layer)
    {
        if (ResolveRaster(layer) is not { } raster) return layer;
        var source = raster.Pixels;
        var bounds = new SKRectI(raster.X, raster.Y, raster.X + source.Width, raster.Y + source.Height);
        var world = document.Selection is null ? SKMatrix.Identity :
            WorldMatrix(document.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
        bool integerTranslation = world == SKMatrix.CreateTranslation(world.TransX, world.TransY) &&
            world.TransX == MathF.Truncate(world.TransX) && world.TransY == MathF.Truncate(world.TransY) &&
            Math.Abs(world.TransX) <= 1_000_000 && Math.Abs(world.TransY) <= 1_000_000;
        int dx = integerTranslation ? (int)world.TransX : 0, dy = integerTranslation ? (int)world.TransY : 0;
        if (document.Selection is { } selection && world.TryInvert(out var inverse) &&
            world.Persp0 == 0 && world.Persp1 == 0 && world.Persp2 == 1)
        {
            var local = inverse.MapRect(SKRect.Create(selection.X, selection.Y, selection.Width, selection.Height));
            bounds.Intersect(new SKRectI(
                (int)Math.Clamp(Math.Floor(local.Left), bounds.Left, bounds.Right),
                (int)Math.Clamp(Math.Floor(local.Top), bounds.Top, bounds.Bottom),
                (int)Math.Clamp(Math.Ceiling(local.Right), bounds.Left, bounds.Right),
                (int)Math.Clamp(Math.Ceiling(local.Bottom), bounds.Top, bounds.Bottom)));
            if (bounds.IsEmpty) return layer;
        }

        var output = new PixelBuffer(source.Width, source.Height, PixelBuffer.Settings(source));
        output.Copy(source, 0, 0, source.Width * source.Height);
        bool changed = false;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            int index = (y - raster.Y) * source.Width + bounds.Left - raster.X;
            if (document.Selection is null || integerTranslation && document.SelectionMask is null)
            {
                output.InvertColorRow(index, bounds.Width);
                changed = true;
                continue;
            }
            for (int x = bounds.Left; x < bounds.Right; x++, index++)
            {
                byte amount = integerTranslation ? document.SelectionAmount(x + dx, y + dy) : SelectionAt(document, world, x, y);
                if (amount == 0) continue;
                output.InvertColor(index, amount);
                changed = true;
            }
        }
        return changed ? layer with { Pixels = output.Finish(), RasterX = raster.X, RasterY = raster.Y, PixelShifts = [] } : layer;
    }

    public AlphaMask InvertMask(Document document, Layer layer)
    {
        var mask = layer.Mask!;
        if (document.Selection is not { } selection)
        {
            var inverted = new byte[mask.Alpha.Length];
            for (int i = 0; i < inverted.Length; i++) inverted[i] = (byte)(255 - mask.Alpha[i]);
            return mask with { Alpha = inverted, Outside = (byte)(255 - mask.Outside) };
        }

        var world = FindMatrix(document.Layers, layer.Id, SKMatrix.Identity, SKMatrix.Identity, false) ?? SKMatrix.Identity;
        if (!world.TryInvert(out var inverse)) throw new InvalidDataException("The mask transform cannot be inverted.");
        var local = inverse.MapRect(SKRect.Create(selection.X, selection.Y, selection.Width, selection.Height));
        local.Union(SKRect.Create(mask.X, mask.Y, mask.Width, mask.Height));
        var bounds = EnclosingPixels(local);
        var samples = new byte[bounds.Width * bounds.Height];
        for (int y = 0; y < bounds.Height; y++)
        for (int x = 0; x < bounds.Width; x++)
        {
            int sx = x + bounds.X, sy = y + bounds.Y;
            int original = mask.At(sx, sy);
            byte amount = SelectionAt(document, world, sx, sy);
            samples[y * bounds.Width + x] = (byte)Math.Round(original + (255 - 2 * original) * amount / 255d);
        }
        return new(bounds.Width, bounds.Height, samples, bounds.X, bounds.Y, mask.Outside);
    }

    private static byte SelectionAt(Document document, SKMatrix world, int x, int y)
    {
        if (document.Selection is null) return 255;
        var point = world.MapPoint(x + .5f, y + .5f);
        return float.IsFinite(point.X) && float.IsFinite(point.Y)
            ? document.SelectionAmount((int)Math.Floor(point.X), (int)Math.Floor(point.Y)) : (byte)0;
    }
}
