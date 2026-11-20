using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private static ColorSettings? NativePrecision(Document document) =>
        document.Color.Depth == ChannelDepth.Bit8 ? null : document.Color;

    private static ColorSettings? WorkingPrecision(Pixels pixels) =>
        pixels.Native?.Settings is { Depth: not ChannelDepth.Bit8 } settings ? settings : null;

    public Pixels CompositeForStorage(Document document)
    {
        document = ResolveFloating(document);
        if (CompositeCmyk(document) is { } native) return native;
        if (document.Color is { Mode: ChannelColorMode.Cmyk, Depth: ChannelDepth.Bit8 } && document.IccProfile is { } profile)
        {
            using var transform = CmykProfile.Open(profile, fromSrgb: true);
            if (transform is not null && StorageCmyk(Composite(document), document.Color, transform) is { } managed)
                return managed;
            return CompositeRegion(document.Layers, new(0, 0, document.Width, document.Height), document.Color);
        }
        if (NativePrecision(document) is not { } precision) return Composite(document);
        return CompositeRegion(document.Layers, new(0, 0, document.Width, document.Height), precision);
    }

    private static unsafe SKBitmap RasterBitmap(Pixels pixels, ColorSettings? precision)
    {
        if (precision is null || pixels.Native is null) return Bitmap(pixels);
        var bitmap = new SKBitmap(pixels.Width, pixels.Height, SKColorType.RgbaF32, SKAlphaType.Premul);
        IntPtr address = bitmap.GetPixels();
        if (address == IntPtr.Zero)
        {
            bitmap.Dispose();
            throw new InvalidOperationException("Could not allocate the high-precision raster.");
        }
        var values = new Span<float>((void*)address, checked(pixels.Width * pixels.Height * 4));
        for (int i = 0; i < values.Length / 4; i++)
        {
            var p = PixelBuffer.Get(pixels, i);
            int j = i * 4;
            values[j] = (float)(p.R * p.A);
            values[j + 1] = (float)(p.G * p.A);
            values[j + 2] = (float)(p.B * p.A);
            values[j + 3] = (float)p.A;
        }
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    private static unsafe Pixels ReadNative(SKBitmap bitmap, ColorSettings precision)
    {
        var address = bitmap.GetPixels();
        if (address == IntPtr.Zero) throw new InvalidOperationException("Could not read the high-precision raster.");
        var values = new ReadOnlySpan<float>((void*)address, checked(bitmap.Width * bitmap.Height * 4));
        var output = new PixelBuffer(bitmap.Width, bitmap.Height, precision);
        for (int i = 0; i < values.Length / 4; i++)
        {
            int offset = i * 4;
            double a = values[offset + 3];
            if (a > 0) output.Set(i, new(values[offset] / a, values[offset + 1] / a, values[offset + 2] / a, a));
        }
        return output.Finish();
    }

    private static void DrawNativeFrame(SKCanvas canvas, Frame frame, SKPaint? paint)
    {
        using var bitmap = RasterBitmap(frame.Pixels, WorkingPrecision(frame.Pixels));
        bitmap.SetImmutable();
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, frame.X, frame.Y, new SKSamplingOptions(SKFilterMode.Linear), paint);
    }

    private static void RestoreCoverage(SKCanvas canvas, SKBitmap surface, Pixels backdrop, Area region,
        ColorSettings precision, bool opaque)
    {
        canvas.Flush();
        var colors = Unpremultiply(surface, precision);
        var corrected = new PixelBuffer(colors.Width, colors.Height, precision);
        for (int i = 0; i < colors.Width * colors.Height; i++)
            corrected.CopyWithAlpha(colors, i, i, opaque ? 1 : PixelBuffer.Alpha(backdrop, i));
        using var image = RasterBitmap(corrected.Finish(), precision);
        using var replace = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(image, region.X, region.Y, replace);
    }
}
