using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private static Pixels Resample(Pixels source, Area area, SKMatrix inverse, int sourceX, int sourceY, ColorSettings settings)
    {
        var output = new PixelBuffer(area.Width, area.Height, settings);
        PixelValue Get(int x, int y) => x >= 0 && y >= 0 && x < source.Width && y < source.Height
            ? PixelBuffer.Get(source, y * source.Width + x) : default;
        static PixelValue Premultiply(PixelValue value) => new(value.R * value.A, value.G * value.A, value.B * value.A, value.A);
        for (int y = 0; y < area.Height; y++)
        for (int x = 0; x < area.Width; x++)
        {
            var point = inverse.MapPoint(area.X + x + .5f, area.Y + y + .5f);
            double sx = point.X - sourceX - .5, sy = point.Y - sourceY - .5;
            if (!double.IsFinite(sx) || !double.IsFinite(sy) || sx <= -1 || sy <= -1 || sx >= source.Width || sy >= source.Height)
                continue;
            int left = (int)Math.Floor(sx), top = (int)Math.Floor(sy);
            var a = PixelValue.Lerp(Premultiply(Get(left, top)), Premultiply(Get(left + 1, top)), sx - left);
            var b = PixelValue.Lerp(Premultiply(Get(left, top + 1)), Premultiply(Get(left + 1, top + 1)), sx - left);
            var value = PixelValue.Lerp(a, b, sy - top);
            if (value.A > 0) output.Set(y * area.Width + x, new(value.R / value.A, value.G / value.A, value.B / value.A, value.A));
        }
        return output.Finish();
    }
}
