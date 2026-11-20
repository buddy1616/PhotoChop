using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    public static Pixels ViewComponent(Pixels source, ColorComponent component, bool opaque = true)
    {
        var bytes = (byte[])source.Rgba.Clone();
        for (int i = 0; i < bytes.Length; i += 4)
        {
            byte value = ChannelSamples.Display(ChannelSamples.Component(component, bytes[i] / 255f,
                bytes[i + 1] / 255f, bytes[i + 2] / 255f, bytes[i + 3] / 255f));
            bytes[i] = bytes[i + 1] = bytes[i + 2] = value;
            if (opaque) bytes[i + 3] = 255;
        }
        return source with { Rgba = bytes };
    }

    public static Pixels ViewChannel(Document document, ChannelPlane plane)
    {
        var bytes = new byte[checked(document.Width * document.Height * 4)];
        for (int y = 0; y < document.Height; y++)
        for (int x = 0; x < document.Width; x++)
        {
            long sx = (long)x - plane.Info.X, sy = (long)y - plane.Info.Y;
            int i = (y * document.Width + x) * 4;
            if (sx >= 0 && sx < plane.Width && sy >= 0 && sy < plane.Height)
                bytes[i] = bytes[i + 1] = bytes[i + 2] = ChannelSamples.Display(
                    ChannelSamples.Read(plane.Samples, plane.Depth, (int)(sy * plane.Width + sx)));
            bytes[i + 3] = 255;
        }
        return new(document.Width, document.Height, bytes);
    }

    public Pixels ViewLayerChannel(Document document, Layer layer, ColorPlanes source, int index)
    {
        var data = source.Planes[index];
        var gray = new byte[checked(source.Width * source.Height * 4)];
        for (int i = 0; i < gray.Length / 4; i++)
        {
            gray[i * 4] = gray[i * 4 + 1] = gray[i * 4 + 2] =
                ChannelSamples.Display(ChannelSamples.Read(data, source.Settings.Depth, i));
            gray[i * 4 + 3] = 255;
        }
        using var output = Surface(document.Width, document.Height);
        using var canvas = new SKCanvas(output);
        using var bitmap = Bitmap(new(source.Width, source.Height, gray));
        canvas.Clear(SKColors.Black);
        canvas.Concat(WorldMatrix(document.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity);
        canvas.DrawBitmap(bitmap, layer.RasterX, layer.RasterY);
        canvas.Flush();
        return Unpremultiply(output);
    }

    public RasterEdit PaintGradient(Document document, Layer layer, Area bounds, GradientArgs gradient)
    {
        var sample = GradientRamp.Sampler(GradientRamp.Stops(gradient.StartColor, gradient.EndColor, gradient.Stops));
        double dx = gradient.End.X - gradient.Start.X, dy = gradient.End.Y - gradient.Start.Y;
        double lengthSquared = dx * dx + dy * dy;
        return PaintArea(document, layer, bounds, (x, y) => document.Selection is null || document.Selected(x, y), (x, y) =>
        {
            double t = Math.Clamp(((x - gradient.Start.X) * dx + (y - gradient.Start.Y) * dy) / lengthSquared, 0, 1);
            var blended = sample(t);
            if (document.Color.Depth != ChannelDepth.Bit8) return blended;
            static double Round(double value) => Math.Round(value * 255) / 255;
            return new(Round(blended.R), Round(blended.G), Round(blended.B), Round(blended.A));
        });
    }

    private void DrawClippingStack(SKCanvas destination, ReadOnlySpan<Layer> stack, Area region, SKMatrix parent, ColorSettings? precision)
    {
        var basis = stack[0];
        if (!basis.Visible || basis.Opacity == 0) return;
        using var surface = Surface(region.Width, region.Height, precision);
        using var canvas = new SKCanvas(surface);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-region.X, -region.Y);
        Draw(canvas, basis with { Blend = Blend.Normal,
            Effects = basis.Effects.Where(effect => effect.Kind != EffectKind.BlendMode).ToArray() }, region, parent, precision: precision);
        canvas.Flush();
        var basePixels = Unpremultiply(surface, precision);
        if (precision is not null)
        {
            RestoreCoverage(canvas, surface, basePixels, region, precision, opaque: true);
            DrawUnclippedTier(surface, canvas, stack[1..], region, parent, precision);
            RestoreCoverage(canvas, surface, basePixels, region, precision, opaque: false);
            canvas.Flush();
        }
        using var corrected = precision is null ? DrawByteStack(stack[1..]) : null;
        SKBitmap DrawByteStack(ReadOnlySpan<Layer> clipped)
        {
            var opaque = (byte[])basePixels.Rgba.Clone();
            for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
            using var normalBase = Bitmap(basePixels with { Rgba = opaque });
            using var replace = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(normalBase, region.X, region.Y, replace);
            DrawUnclippedTier(surface, canvas, clipped, region, parent);
            canvas.Flush();
            var mixed = Unpremultiply(surface);
            for (int i = 3; i < mixed.Rgba.Length; i += 4) mixed.Rgba[i] = basePixels.Rgba[i];
            return Bitmap(mixed);
        }
        var mode = basis.TransformsEnabled ? CommandEngine.EffectiveBlend(basis) : basis.Blend;
        using var blender = CreateBlender(mode);
        using var paint = new SKPaint
        {
            BlendMode = ToSkiaBlend(mode)
        };
        if (blender is not null) paint.Blender = blender;
        destination.DrawBitmap(corrected ?? surface, region.X, region.Y, paint);
    }

    public Pixels VectorMaskView(Document document, Layer layer)
    {
        var region = new Area(0, 0, document.Width, document.Height);
        var world = FindMatrix(document.Layers, layer.Id, SKMatrix.Identity, SKMatrix.Identity, false) ?? SKMatrix.Identity;
        using var coverage = VectorCoverage(layer.VectorMask is { } mask ? mask with { Enabled = true } : null, region, world);
        var bytes = coverage is null ? new byte[checked(region.Width * region.Height * 4)] : Read(coverage).Rgba;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = bytes[i + 1] = bytes[i + 2] = coverage is null ? (byte)255 : bytes[i + 3];
            bytes[i + 3] = 255;
        }
        return new(region.Width, region.Height, bytes);
    }

    private static bool ActiveVectorMask(VectorMask? mask) =>
        mask is { Enabled: true } && (mask.Operations is not null || mask.Contours.Any(contour => contour.Closed));

    private static SKBitmap? VectorCoverage(VectorMask? mask, Area region, SKMatrix world)
    {
        if (!ActiveVectorMask(mask)) return null;
        using var path = ComposeVectorMask(mask!);
        var bitmap = Surface(region.Width, region.Height);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-region.X, -region.Y);
        canvas.Concat(world);
        canvas.DrawPath(path, paint);
        canvas.Flush();
        return bitmap;
    }

    private static SKPath ComposeVectorMask(VectorMask mask)
    {
        var composed = new SKPath { FillType = SKPathFillType.EvenOdd };
        if (mask.Operations is not { } operations || operations.Length != mask.Contours.Length)
        {
            foreach (var contour in mask.Contours)
                if (contour.Closed) AddContour(composed, contour);
        }
        else
        {
            bool first = true;
            for (int position = 0; position < operations.Length;)
            {
                var op = operations[position];
                using var component = new SKPath { FillType = SKPathFillType.EvenOdd };
                do
                {
                    var contour = mask.Contours[position++];
                    if (contour.Closed) AddContour(component, contour);
                } while (position < operations.Length && operations[position] == VectorMaskOperation.Continue);
                if (component.IsEmpty) continue;

                // The first subtract or intersect operates on the whole canvas.
                if (first && (op is VectorMaskOperation.Subtract or VectorMaskOperation.Intersect))
                    composed.FillType = SKPathFillType.InverseEvenOdd;
                var next = composed.Op(component, op switch
                {
                    VectorMaskOperation.Exclude => SKPathOp.Xor,
                    VectorMaskOperation.Subtract => SKPathOp.Difference,
                    VectorMaskOperation.Intersect => SKPathOp.Intersect,
                    _ => SKPathOp.Union
                });
                if (next is not null)
                {
                    composed.Dispose();
                    composed = next;
                }
                first = false;
            }
            if (first && mask.InitialFill) composed.FillType = SKPathFillType.InverseEvenOdd;
        }
        if (mask.Inverted)
            composed.FillType = composed.FillType switch
            {
                SKPathFillType.Winding => SKPathFillType.InverseWinding,
                SKPathFillType.InverseWinding => SKPathFillType.Winding,
                SKPathFillType.InverseEvenOdd => SKPathFillType.EvenOdd,
                _ => SKPathFillType.InverseEvenOdd
            };
        return composed;
    }

    private static void AddContour(SKPath builder, PathContour contour)
    {
        var first = contour.Nodes[0].Point;
        builder.MoveTo((float)first.X, (float)first.Y);
        int count = contour.Closed ? contour.Nodes.Length : contour.Nodes.Length - 1;
        for (int i = 0; i < count; i++)
        {
            var from = contour.Nodes[i];
            var to = contour.Nodes[(i + 1) % contour.Nodes.Length];
            if (from.Outgoing is null && to.Incoming is null)
                builder.LineTo((float)to.Point.X, (float)to.Point.Y);
            else
            {
                var a = from.Outgoing ?? from.Point;
                var b = to.Incoming ?? to.Point;
                builder.CubicTo((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, (float)to.Point.X, (float)to.Point.Y);
            }
        }
        if (contour.Closed) builder.Close();
    }
}
