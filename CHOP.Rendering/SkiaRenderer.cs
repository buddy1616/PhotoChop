using System.Buffers.Binary;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

/// <summary>CPU renderer with immutable input snapshots and a weakly keyed operation cache.</summary>
public sealed partial class SkiaRenderer : IRasterBackend
{
    private sealed record Frame(Pixels Pixels, int X = 0, int Y = 0);
    private sealed record Evaluation(Frame Frame, Effect Effect, Evaluation? Input, int X, int Y);
    private sealed class Evaluations
    {
        public readonly List<Evaluation> Nodes = [];
    }

    private static readonly Lazy<SKRuntimeEffect> addBlend = new(() =>
    {
        var effect = SKRuntimeEffect.CreateBlender("""
            half4 main(half4 src, half4 dst) {
                half overlap = src.a * dst.a;
                half3 add = min(src.rgb * dst.a + dst.rgb * src.a, half3(overlap));
                half3 rgb = src.rgb * (1 - dst.a) + dst.rgb * (1 - src.a) + add;
                return half4(rgb, src.a + dst.a - overlap);
            }
            """, out var error);
        return effect ?? throw new InvalidOperationException(error);
    });

    private readonly ConditionalWeakTable<Pixels, Evaluations> evaluations = new();
    private readonly ConditionalWeakTable<Layer, Pixels> vectors = new();

    public Pixels DecodePng(byte[] data) => DecodeImage(data, pngOnly: true);

    public Pixels DecodeImage(byte[] data) => DecodeImage(data, pngOnly: false);

    private static Pixels DecodeImage(byte[] data, bool pngOnly)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data)) ?? throw new InvalidDataException("Invalid image data.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Png &&
            (pngOnly || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp)))
            throw new InvalidDataException(pngOnly ? "Expected PNG data." : "Choose a PNG, JPEG, or WebP image.");
        Format.Dimensions(codec.Info.Width, codec.Info.Height);
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException($"Image decode failed: {result}.");
        return Read(bitmap);
    }

    public byte[] EncodePng(Pixels pixels, double? pixelsPerInch = null)
    {
        using var bitmap = Bitmap(pixels);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("PNG encoding failed.");
        var png = data.ToArray();
        if (pixelsPerInch is not { } ppi) return png;
        const int afterHeader = 33;
        const int chunkLength = 21;
        var output = new byte[png.Length + chunkLength];
        png.AsSpan(0, afterHeader).CopyTo(output);
        var chunk = output.AsSpan(afterHeader, chunkLength);
        BinaryPrimitives.WriteInt32BigEndian(chunk, 9);
        "pHYs"u8.CopyTo(chunk[4..]);
        uint resolution = checked((uint)Math.Round(ppi / .0254));
        BinaryPrimitives.WriteUInt32BigEndian(chunk[8..], resolution);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[12..], resolution);
        chunk[16] = 1;
        uint checksum = uint.MaxValue;
        foreach (byte value in chunk[4..17])
        {
            checksum ^= value;
            for (int bit = 0; bit < 8; bit++)
                checksum = (checksum >> 1) ^ ((checksum & 1) == 0 ? 0 : 0xEDB88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk[17..], ~checksum);
        png.AsSpan(afterHeader).CopyTo(output.AsSpan(afterHeader + chunkLength));
        return output;
    }

    public Pixels Composite(Document document)
    {
        var composite = CompositeRegion(ProfiledLayers(ResolveFloating(document)), new Area(0, 0, document.Width, document.Height));
        return document.Color.Mode == ChannelColorMode.Grayscale ? ViewComponent(composite, ColorComponent.Gray, opaque: false) : composite;
    }

    private Pixels CompositeRegion(Layer[] layers, Area region, ColorSettings? precision = null)
    {
        using var bitmap = Surface(region.Width, region.Height, precision);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-region.X, -region.Y);
        DrawTier(bitmap, canvas, layers, region, SKMatrix.Identity, precision);
        canvas.Flush();
        return Unpremultiply(bitmap, precision);
    }

    private void DrawTier(SKBitmap surface, SKCanvas canvas, Layer[] layers, Area region, SKMatrix parent, ColorSettings? precision = null)
    {
        int start = 0;
        while (start < layers.Length)
        {
            int end = start + 1;
            while (end < layers.Length && layers[end].Clipped) end++;
            var basis = layers[start];
            if (end == start + 1 || basis.Kind == LayerKind.Effect)
                DrawUnclippedTier(surface, canvas, layers.AsSpan(start, end - start), region, parent, precision);
            else
                DrawClippingStack(canvas, layers.AsSpan(start, end - start), region, parent, precision);
            start = end;
        }
    }

    private void DrawUnclippedTier(SKBitmap surface, SKCanvas canvas, ReadOnlySpan<Layer> layers, Area region, SKMatrix parent, ColorSettings? precision = null)
    {
        foreach (var layer in layers)
        {
            if (!layer.Visible || layer.Opacity == 0) continue;
            if (IsPassThroughGroup(layer))
            {
                var world = SKMatrix.Concat(parent, LayerMatrix(layer, !layer.TransformsEnabled));
                DrawTier(surface, canvas, layer.Children, region, world, precision);
                continue;
            }
            if (layer.Kind != LayerKind.Effect) { Draw(canvas, layer, region, parent, precision: precision); continue; }
            if (!layer.TransformsEnabled) continue;
            var mode = CommandEngine.EffectiveBlend(layer);
            if (mode == Blend.Normal && !layer.Effects.Any(ProcessesPixels)) continue;
            canvas.Flush();
            var original = Unpremultiply(surface, precision);
            var evaluated = Evaluate(original, layer.Effects, region.X, region.Y);
            var maskMatrix = SKMatrix.Concat(parent, SpatialMatrix(layer));
            if (!maskMatrix.TryInvert(out var inverse)) continue;
            using var vectorCoverage = VectorCoverage(layer.VectorMask, region, maskMatrix);
            var bytes = original.Rgba;
            PixelBuffer? working = precision is null ? null : new(region.Width, region.Height, precision);
            working?.Copy(original, 0, 0, region.Width * region.Height);
            for (int y = 0; y < region.Height; y++)
            for (int x = 0; x < region.Width; x++)
            {
                double mix = layer.Opacity * layer.FillOpacity * (vectorCoverage is null ? 1 : vectorCoverage.GetPixel(x, y).Alpha / 255d);
                int wx = x + region.X, wy = y + region.Y;
                if (layer.MaskEnabled && layer.Mask is { } mask)
                {
                    var point = inverse.MapPoint(wx + .5f, wy + .5f);
                    int mx = (int)Math.Floor(point.X), my = (int)Math.Floor(point.Y);
                    mix *= mask.At(mx, my) / 255d;
                }
                if (mix == 0) continue;
                int dst = (y * region.Width + x) * 4;
                int sx = wx - evaluated.X, sy = wy - evaluated.Y;
                bool inside = sx >= 0 && sy >= 0 && sx < evaluated.Pixels.Width && sy < evaluated.Pixels.Height;
                int src = inside ? (sy * evaluated.Pixels.Width + sx) * 4 : 0;
                if (working is not null)
                {
                    var backdrop = PixelBuffer.Get(original, dst / 4);
                    var foreground = inside ? PixelBuffer.Get(evaluated.Pixels, src / 4) : default;
                    double added = foreground.A * mix, kept = backdrop.A * (1 - mix), total = added + kept;
                    var blended = backdrop.A == 0 ? foreground : BlendRgb(backdrop, foreground, mode);
                    double Channel(double b, double f) => (b * kept + f * added) / total;
                    working.Set(dst / 4, total == 0 ? default :
                        new(Channel(backdrop.R, blended.R), Channel(backdrop.G, blended.G), Channel(backdrop.B, blended.B), total));
                    continue;
                }
                var adjusted = evaluated.Pixels.Rgba;
                double addition = inside ? adjusted[src + 3] / 255d * mix : 0;
                double retained = bytes[dst + 3] / 255d * (1 - mix);
                double alpha = addition + retained;
                var sourceColor = PixelBuffer.Get(evaluated.Pixels, src / 4);
                var blendedColor = inside && bytes[dst + 3] != 0
                    ? BlendRgb(PixelBuffer.Get(original, dst / 4), sourceColor, mode)
                    : sourceColor;
                bytes[dst] = alpha == 0 ? (byte)0 : Mix(bytes[dst], Byte(blendedColor.R), retained, addition, alpha);
                bytes[dst + 1] = alpha == 0 ? (byte)0 : Mix(bytes[dst + 1], Byte(blendedColor.G), retained, addition, alpha);
                bytes[dst + 2] = alpha == 0 ? (byte)0 : Mix(bytes[dst + 2], Byte(blendedColor.B), retained, addition, alpha);
                bytes[dst + 3] = Byte(alpha);
            }
            using var result = RasterBitmap(working?.Finish() ?? original, precision);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(result, region.X, region.Y, paint);
        }
    }

    private void Draw(SKCanvas canvas, Layer layer, Area region, SKMatrix parent,
        Action<SKCanvas, Frame, SKPaint?>? drawLeaf = null, ColorSettings? precision = null)
    {
        if (!layer.Visible || layer.Opacity == 0 || layer.Kind == LayerKind.Effect) return;
        bool bypass = !layer.TransformsEnabled;
        var world = SKMatrix.Concat(parent, LayerMatrix(layer, bypass));
        if (layer.RasterGrid is { } grid) world = SKMatrix.Concat(world, ToSkia(grid));
        var blend = bypass ? layer.Blend : CommandEngine.EffectiveBlend(layer);
        Effect[] effects = bypass ? [] : layer.Effects;
        using var blender = CreateBlender(blend);
        double opacity = layer.Opacity * (layer.Kind == LayerKind.Group ? layer.FillOpacity : 1);
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(Byte(opacity)),
            BlendMode = ToSkiaBlend(blend)
        };
        if (precision is not null) paint.ColorF = new SKColorF(1, 1, 1, (float)opacity);
        if (blender is not null) paint.Blender = blender;
        drawLeaf ??= precision is null ? DrawFrame : DrawNativeFrame;

        var spatial = SpatialMatrix(layer);
        if (spatial != SKMatrix.Identity)
        {
            if (!parent.TryInvert(out var parentInverse)) return;
            var projection = SKMatrix.Concat(parent, SKMatrix.Concat(spatial, parentInverse));
            if (layer.Kind != LayerKind.Group)
            {
                if (PrepareLayer(layer, effects, precision) is not { } frame) return;
                canvas.Save();
                canvas.Concat(SKMatrix.Concat(projection, world));
                drawLeaf(canvas, frame, paint);
                canvas.Restore();
                return;
            }
            if (!projection.TryInvert(out var inverse)) return;
            var neutral = layer with { Transform = layer.Transform with { Warp = null }, ImagePlacement = null, Opacity = 1, FillOpacity = 1, Blend = Blend.Normal,
                Effects = layer.Effects.Where(effect => effect.Kind != EffectKind.BlendMode).ToArray() };
            var sourceBounds = SKRect.Create(region.X, region.Y, region.Width, region.Height);
            var visible = inverse.MapRect(SKRect.Create(region.X, region.Y, region.Width, region.Height));
            var regionToDraw = visible;
            if (projection.Persp0 == 0 && projection.Persp1 == 0)
            {
                regionToDraw.Intersect(sourceBounds);
                if (regionToDraw.IsEmpty) return;
            }
            else regionToDraw = sourceBounds;
            var local = EnclosingPixels(regionToDraw);
            using var output = Surface(local.Width, local.Height, precision);
            using var drawing = new SKCanvas(output);
            drawing.Clear(SKColors.Transparent);
            drawing.Translate(-local.X, -local.Y);
            Draw(drawing, neutral, local, parent, precision: precision);
            drawing.Flush();
            using var image = SKImage.FromBitmap(output);
            canvas.Save();
            canvas.Concat(projection);
            canvas.DrawImage(image, local.X, local.Y, new SKSamplingOptions(SKFilterMode.Linear), paint);
            canvas.Restore();
            return;
        }

        if (layer.Kind == LayerKind.Group && TryDrawPlacedGroup(canvas, layer, region, parent, paint, precision)) return;

        if (layer.Kind == LayerKind.Group)
        {
            using var group = Surface(region.Width, region.Height, precision);
            using var groupCanvas = new SKCanvas(group);
            groupCanvas.Clear(SKColors.Transparent);
            groupCanvas.Translate(-region.X, -region.Y);
            DrawTier(group, groupCanvas, layer.Children, region, world, precision);
            groupCanvas.Flush();
            if (!effects.Any(ProcessesPixels) && (layer.Mask is null || !layer.MaskEnabled) && !ActiveVectorMask(layer.VectorMask))
            {
                canvas.DrawBitmap(group, region.X, region.Y, paint);
                return;
            }

            using var vectorCoverage = VectorCoverage(layer.VectorMask, region, world);
            var groupMask = layer.MaskEnabled ? layer.Mask : null;
            using var maskPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
            if (groupMask is not null)
            {
                using var maskSurface = Surface(region.Width, region.Height);
                using var maskCanvas = new SKCanvas(maskSurface);
                using var maskBitmap = Bitmap(MaskPixels(groupMask));
                using var sourcePaint = new SKPaint { BlendMode = SKBlendMode.Src };
                maskCanvas.Clear(SKColors.White.WithAlpha(groupMask.Outside));
                maskCanvas.Translate(-region.X, -region.Y);
                maskCanvas.Concat(world);
                maskCanvas.DrawBitmap(maskBitmap, groupMask.X, groupMask.Y, sourcePaint);
                maskCanvas.Flush();
                groupCanvas.DrawBitmap(maskSurface, region.X, region.Y, maskPaint);
            }
            if (vectorCoverage is not null) groupCanvas.DrawBitmap(vectorCoverage, region.X, region.Y, maskPaint);
            groupCanvas.Flush();
            var frame = Evaluate(Unpremultiply(group, precision), effects, region.X, region.Y);
            using var result = RasterBitmap(frame.Pixels, precision);
            canvas.DrawBitmap(result, frame.X, frame.Y, paint);
            return;
        }

        if (PrepareLayer(layer, effects, precision) is not { } evaluated) return;
        canvas.Save();
        canvas.Concat(world);
        drawLeaf(canvas, evaluated, paint);
        canvas.Restore();
    }

    // Resize a group after its children and effects have been composited.
    private bool TryDrawPlacedGroup(SKCanvas canvas, Layer layer, Area region, SKMatrix parent, SKPaint paint, ColorSettings? precision)
    {
        if (layer.Placement == new Placement() || !parent.TryInvert(out var parentInverse)) return false;
        var projection = SKMatrix.Concat(parent, SKMatrix.Concat(PlacementMatrix(layer.Placement), parentInverse));
        if (!projection.TryInvert(out var inverse)) return false;
        Area local;
        try { local = EnclosingPixels(inverse.MapRect(SKRect.Create(region.X, region.Y, region.Width, region.Height))); }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return false; }

        var source = layer with { Placement = new(), Opacity = 1, FillOpacity = 1, Blend = Blend.Normal,
            Effects = layer.Effects.Where(effect => effect.Kind != EffectKind.BlendMode).ToArray() };
        using var intermediate = Surface(local.Width, local.Height, precision);
        using var intermediateCanvas = new SKCanvas(intermediate);
        intermediateCanvas.Clear(SKColors.Transparent);
        intermediateCanvas.Translate(-local.X, -local.Y);
        Draw(intermediateCanvas, source, local, parent, precision: precision);
        intermediateCanvas.Flush();
        using var image = SKImage.FromBitmap(intermediate);
        canvas.Save();
        canvas.Concat(projection);
        canvas.DrawImage(image, local.X, local.Y, new SKSamplingOptions(SKFilterMode.Linear), paint);
        canvas.Restore();
        return true;
    }

    private Frame? PrepareLayer(Layer layer, Effect[] effects, ColorSettings? precision = null)
    {
        Frame frame;
        if (layer.Kind == LayerKind.Path)
        {
            var stroke = effects.FirstOrDefault(effect => effect.Enabled && effect.Kind == EffectKind.Stroke);
            var fill = effects.FirstOrDefault(effect => effect.Enabled && effect.Kind == EffectKind.PathFill);
            if (RasterizePath(layer.Contours, stroke, fill) is not { } vector) return null;
            frame = new(precision is null ? vector.Pixels : PixelBuffer.Convert(vector.Pixels, precision), vector.X, vector.Y);
            effects = effects.Where(effect => !ReferenceEquals(effect, stroke) && effect.Kind != EffectKind.PathFill).ToArray();
        }
        else
        {
            var raster = layer.Kind is LayerKind.Raster or LayerKind.Chop or LayerKind.Text ? ResolveRaster(layer) : null;
            Pixels? source = layer.Kind switch
            {
                LayerKind.Raster or LayerKind.Chop => raster?.Pixels,
                LayerKind.Text => raster?.Pixels ?? vectors.GetValue(layer, Rasterize),
                LayerKind.Fill => FillSource(layer, layer.Width, layer.Height),
                _ => vectors.GetValue(layer, Rasterize)
            };
            if (source is null) return null;
            frame = new(precision is null ? source : PixelBuffer.Convert(source, precision), raster?.X ?? layer.RasterX, raster?.Y ?? layer.RasterY);
        }
        if (layer.MaskEnabled && layer.Mask is { } mask) frame = ApplyMask(frame, mask);
        using var coverage = VectorCoverage(layer.VectorMask, new(frame.X, frame.Y, frame.Pixels.Width, frame.Pixels.Height), SKMatrix.Identity);
        if (coverage is not null)
        {
            var masked = ApplyMask(frame with { X = 0, Y = 0 }, new(frame.Pixels.Width, frame.Pixels.Height,
                Read(coverage).Rgba.Where((_, index) => index % 4 == 3).ToArray()));
            frame = masked with { X = frame.X, Y = frame.Y };
        }
        frame = Evaluate(frame.Pixels, effects, frame.X, frame.Y);
        return layer.FillOpacity == 1 ? frame :
            Evaluate(frame.Pixels, [new("fill-opacity", EffectKind.Opacity, A: layer.FillOpacity)], frame.X, frame.Y);
    }

    private readonly FrameImageCache frameImages = new(128L * 1024 * 1024);

    private void DrawFrame(SKCanvas canvas, Frame frame, SKPaint? paint = null) =>
        frameImages.Use(frame.Pixels, CreateFrameImage, image =>
            canvas.DrawImage(image, frame.X, frame.Y, new SKSamplingOptions(SKFilterMode.Linear), paint));

    public RasterEdit RasterizeLayer(Document document, Layer layer)
    {
        if (layer.Kind == LayerKind.Group || SpatialMatrix(layer) != SKMatrix.Identity)
        {
            var parent = NativeParentMatrix(document.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
            if (!parent.TryInvert(out var inverse)) throw new InvalidDataException("The parent group transform cannot be inverted.");
            var region = EnclosingPixels(inverse.MapRect(SKRect.Create(document.Width, document.Height)));
            return new(CompositeRegion([layer], region, NativePrecision(document)), region.X, region.Y);
        }
        Effect[] effects = layer.TransformsEnabled ? layer.Effects : [];
        if (PrepareLayer(layer, effects, NativePrecision(document)) is not { } frame)
            return new(new Pixels(layer.Width, layer.Height, new byte[layer.Width * layer.Height * 4]), layer.RasterX, layer.RasterY);
        if (layer.ActiveTransform == new Transform() && layer.Placement == new Placement() && layer.RasterGrid is null) return new(frame.Pixels, frame.X, frame.Y);

        var matrix = LayerMatrix(layer, !layer.TransformsEnabled);
        if (layer.RasterGrid is { } grid) matrix = SKMatrix.Concat(matrix, ToSkia(grid));
        var area = EnclosingPixels(matrix.MapRect(SKRect.Create(frame.X, frame.Y, frame.Pixels.Width, frame.Pixels.Height)));
        if (NativePrecision(document) is { } precision && matrix.TryInvert(out var inverseMatrix))
            return new(Resample(frame.Pixels, area, inverseMatrix, frame.X, frame.Y, precision), area.X, area.Y);
        using var output = Surface(area.Width, area.Height);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-area.X, -area.Y);
        canvas.Concat(matrix);
        DrawFrame(canvas, frame);
        canvas.Flush();
        return new(Unpremultiply(output), area.X, area.Y);
    }

    private static Area EnclosingPixels(SKRect bounds)
    {
        int x = checked((int)Math.Floor(bounds.Left)), y = checked((int)Math.Floor(bounds.Top));
        int width = checked((int)Math.Ceiling(bounds.Right) - x), height = checked((int)Math.Ceiling(bounds.Bottom) - y);
        Format.Dimensions(width, height);
        return new(x, y, width, height);
    }

    private static SKMatrix? NativeParentMatrix(IEnumerable<Layer> layers, string id, SKMatrix parent)
    {
        foreach (var layer in layers)
        {
            if (layer.Id == id) return parent;
            if (layer.Kind == LayerKind.Group &&
                NativeParentMatrix(layer.Children, id, SKMatrix.Concat(parent, LayerMatrix(layer, !layer.TransformsEnabled))) is { } found)
                return found;
        }
        return null;
    }

    private static Layer[] IsolatedBranch(IEnumerable<Layer> layers, string id)
    {
        foreach (var layer in layers)
        {
            if (layer.Id == id) return [layer];
            if (layer.Kind == LayerKind.Group && IsolatedBranch(layer.Children, id) is { Length: > 0 } child)
                return [layer with { Children = child }];
        }
        return [];
    }

    public Area? VisibleLayerBounds(Document doc, string id)
    {
        var layers = IsolatedBranch(doc.Layers, id);
        if (layers.Length == 0) return null;
        var pixels = Composite(doc with { Layers = layers });
        int left = pixels.Width, right = -1, top = pixels.Height, bottom = -1;
        for (int y = 0; y < pixels.Height; y++)
        for (int x = 0; x < pixels.Width; x++)
        {
            if (pixels.Rgba[(y * pixels.Width + x) * 4 + 3] == 0) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        return right < left ? null : new Area(left, top, right - left + 1, bottom - top + 1);
    }

    public bool HitsText(Document doc, Layer layer, PointD point)
    {
        if (layer.Kind != LayerKind.Text || !layer.Visible || layer.Opacity == 0 ||
            WorldMatrix(doc.Layers, layer.Id, SKMatrix.Identity) is not { } world || !world.TryInvert(out var inverse))
            return false;
        var local = inverse.MapPoint((float)point.X, (float)point.Y);
        var text = layer.Pixels ?? vectors.GetValue(layer, Rasterize);
        return local.X >= layer.RasterX && local.Y >= layer.RasterY &&
            local.X < layer.RasterX + text.Width && local.Y < layer.RasterY + text.Height;
    }

    private static Frame? RasterizePath(PathContour[] contours, Effect? stroke, Effect? fill)
    {
        if (stroke is null && fill is null) return null;
        using var closed = new SKPath { FillType = SKPathFillType.EvenOdd };
        using var open = new SKPath();
        bool anyClosed = false, anyOpen = false;
        foreach (var contour in contours)
        {
            if (contour.Nodes.Length < 2 || (!contour.Closed && stroke is null)) continue;
            var builder = contour.Closed ? closed : open;
            if (contour.Closed) anyClosed = true; else anyOpen = true;
            AddContour(builder, contour);
        }
        if (!anyClosed && !anyOpen) return null;
        var range = anyClosed ? closed.Bounds : open.Bounds;
        if (anyOpen && anyClosed)
        {
            var b = open.Bounds;
            range = new SKRect(Math.Min(range.Left, b.Left), Math.Min(range.Top, b.Top),
                Math.Max(range.Right, b.Right), Math.Max(range.Bottom, b.Bottom));
        }
        float padding = (float)(stroke?.A ?? 0) + 2;
        range.Inflate(padding, padding);
        var bounds = EnclosingPixels(range);
        using var bitmap = Surface(bounds.Width, bounds.Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-bounds.X, -bounds.Y);
        if (anyClosed && fill is not null)
        {
            using var fillPaint = new SKPaint { Color = new SKColor(Format.Color(fill.Ink)), IsAntialias = true };
            canvas.DrawPath(closed, fillPaint);
        }
        if (stroke is not null)
        {
            using var paint = new SKPaint
            {
                Color = new SKColor(Format.Color(stroke.Ink)), IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)stroke.A, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
            };
            if (anyOpen) canvas.DrawPath(open, paint);
            if (anyClosed)
            {
                if (stroke.Alignment != StrokeAlignment.Center)
                {
                    canvas.ClipPath(closed, stroke.Alignment == StrokeAlignment.Inside ? SKClipOperation.Intersect : SKClipOperation.Difference, true);
                    paint.StrokeWidth *= 2;
                }
                canvas.DrawPath(closed, paint);
            }
        }
        canvas.Flush();
        return new Frame(Unpremultiply(bitmap), bounds.X, bounds.Y);
    }

    private static Pixels Rasterize(Layer layer)
    {
        using var paint = new SKPaint { Color = new SKColor(Format.Color(layer.Color)), IsAntialias = true };
        if (layer.Kind == LayerKind.Text)
        {
            using var typeface = CreateTypeface(layer.Font);
            using var font = new SKFont(typeface, (float)layer.Size);
            string[] lines = layer.Text.Replace("\r", "").Split('\n');
            float lineHeight = Math.Max(font.Spacing, font.Size);
            float widest = lines.Max(line => font.MeasureText(line));
            float rise = (float)(layer.TextBend * widest * .22);
            float guard = rise == 0 ? 0 : font.Size;
            int width = Math.Clamp((int)Math.Ceiling(widest + 2 * guard) + 4, 1, 16384);
            int height = Math.Clamp((int)Math.Ceiling(lineHeight * lines.Length + Math.Abs(rise) + guard) + 4,
                1, Math.Min(16384, 32_000_000 / width));
            using var text = Surface(width, height);
            using var canvas = new SKCanvas(text);
            canvas.Clear(SKColors.Transparent);
            for (int i = 0; i < lines.Length && i * lineHeight < height; i++)
            {
                float baseline = 1 - font.Metrics.Ascent + i * lineHeight;
                if (rise == 0 || lines[i].Length == 0)
                {
                    canvas.DrawText(lines[i], 1, baseline, SKTextAlign.Left, font, paint);
                    continue;
                }
                float span = font.MeasureText(lines[i]);
                float lineRise = rise * span / widest;
                float y = baseline + Math.Max(0, rise);
                using var path = new SKPath();
                path.MoveTo(1 + guard, y);
                path.QuadTo(1 + guard + span / 2, y - 2 * lineRise, 1 + guard + span, y);
                canvas.DrawTextOnPath(lines[i], path, new SKPoint(0, 0), SKTextAlign.Center, font, paint);
            }
            canvas.Flush();
            return Unpremultiply(text);
        }

        using var shape = Surface(layer.Width, layer.Height);
        using var shapeCanvas = new SKCanvas(shape);
        shapeCanvas.Clear(SKColors.Transparent);
        var bounds = SKRect.Create(layer.Width, layer.Height);
        if (layer.Kind == LayerKind.Ellipse) shapeCanvas.DrawOval(bounds, paint);
        else shapeCanvas.DrawRect(bounds, paint);
        shapeCanvas.Flush();
        return Unpremultiply(shape);
    }

    private static SKTypeface CreateTypeface(string family)
    {
        if (!family.Equals("Liberation Sans", StringComparison.OrdinalIgnoreCase) &&
            SKTypeface.FromFamilyName(family) is { } custom)
            return custom;
        using var stream = typeof(SkiaRenderer).Assembly.GetManifestResourceStream("Chop.LiberationSans.ttf")!;
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException("Could not load the bundled text font.");
    }

    private static bool ProcessesPixels(Effect effect) => effect.Enabled && effect.Kind != EffectKind.BlendMode;

    private Frame Evaluate(Pixels source, Effect[] effects, int x = 0, int y = 0)
    {
        if (!effects.Any(ProcessesPixels)) return new(source, x, y);
        var cache = evaluations.GetOrCreateValue(source);
        lock (cache)
        {
            Evaluation? input = null;
            foreach (var effect in effects)
            {
                if (!ProcessesPixels(effect)) continue;
                var node = cache.Nodes.Find(item => item.Effect == effect && ReferenceEquals(item.Input, input) && item.X == x && item.Y == y);
                if (node is null)
                {
                    node = new Evaluation(ApplyEffect(input?.Frame ?? new Frame(source, x, y), effect), effect, input, x, y);
                    cache.Nodes.Add(node);
                }
                input = node;
            }
            const long budget = 64 * 1024 * 1024;
            if (cache.Nodes.Sum(node => node.Frame.Pixels.StorageBytes) > budget)
            {
                cache.Nodes.Clear();
                long size = 0;
                for (var node = input; node is not null; node = node.Input) size += node.Frame.Pixels.StorageBytes;
                if (size <= budget)
                    for (var node = input; node is not null; node = node.Input) cache.Nodes.Add(node);
            }
            return input?.Frame ?? new Frame(source, x, y);
        }
    }

    private Frame ApplyEffect(Frame frame, Effect effect)
    {
        var source = frame.Pixels;
        if (effect.Kind == EffectKind.Transform)
        {
            var transform = Matrix(effect.Spatial!);
            var area = transform.MapRect(SKRect.Create(frame.X, frame.Y, source.Width, source.Height));
            int x = (int)Math.Floor(area.Left), y = (int)Math.Floor(area.Top);
            int width = (int)Math.Ceiling(area.Right) - x, height = (int)Math.Ceiling(area.Bottom) - y;
            Format.Dimensions(width, height);
            if (source.Native is { } native && transform.TryInvert(out var inverse))
                return new(Resample(source, new(x, y, width, height), inverse, frame.X, frame.Y, native.Settings), x, y);
            using var bitmap = Bitmap(source);
            using var image = SKImage.FromBitmap(bitmap);
            using var output = Surface(width, height);
            using var canvas = new SKCanvas(output);
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-x, -y);
            canvas.Concat(transform);
            canvas.DrawImage(image, frame.X, frame.Y, new SKSamplingOptions(SKFilterMode.Linear));
            canvas.Flush();
            return new Frame(Unpremultiply(output), x, y);
        }
        if (effect.Kind is EffectKind.Glow or EffectKind.InnerShadow or EffectKind.BevelEmboss or
            EffectKind.GradientOverlay or EffectKind.PatternOverlay) return RenderLayerStyle(frame, effect);
        if (effect.Kind == EffectKind.GaussianBlur) return Blur(frame, effect.A);
        if (effect.Kind == EffectKind.MotionBlur) return MotionBlur(frame, effect.A, effect.B);
        if (effect.Kind == EffectKind.Stroke) return StrokeEffect(frame, effect);
        if (effect.Kind == EffectKind.DropShadow) return ShadowEffect(frame, effect);
        if (effect.Kind == EffectKind.BitCrush)
        {
            int block = Math.Max(1, (int)Math.Round(effect.A));
            var crushed = new PixelBuffer(source.Width, source.Height, PixelBuffer.Settings(source));
            for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                crushed.Copy(source, y / block * block * source.Width + x / block * block, y * source.Width + x);
            return frame with { Pixels = crushed.Finish() };
        }

        var buffer = new PixelBuffer(source.Width, source.Height, PixelBuffer.Settings(source));
        var ink = effect.Kind == EffectKind.ColorOverlay ? PixelValue.FromArgb(Format.Color(effect.Ink)) : default;
        double amount = ink.A * effect.Opacity;
        static double MixOverlay(double original, double color, double amount) => original + (color - original) * amount;
        float hueR = 0, hueG = 0, hueB = 0;
        if (effect.Kind == EffectKind.HueSaturation)
        {
            double angle = effect.A * Math.PI / 180;
            hueR = (float)((1 + 2 * Math.Cos(angle)) / 3);
            hueG = (float)((1 - Math.Cos(angle) - Math.Sqrt(3) * Math.Sin(angle)) / 3);
            hueB = (float)((1 - Math.Cos(angle) + Math.Sqrt(3) * Math.Sin(angle)) / 3);
        }
        for (int i = 0; i < source.Width * source.Height; i++)
        {
            var sample = PixelBuffer.Get(source, i);
            double r = sample.R, g = sample.G, b = sample.B;
            switch (effect.Kind)
            {
                case EffectKind.ColorOverlay:
                    var blended = BlendRgb(sample, ink, effect.Mode);
                    (r, g, b) = (MixOverlay(r, blended.R, amount), MixOverlay(g, blended.G, amount), MixOverlay(b, blended.B, amount));
                    break;
                case EffectKind.BrightnessContrast:
                    double contrast = (1 + effect.B) / Math.Max(0.01, 1 - effect.B);
                    r = (r - 0.5) * contrast + 0.5 + effect.A;
                    g = (g - 0.5) * contrast + 0.5 + effect.A;
                    b = (b - 0.5) * contrast + 0.5 + effect.A;
                    break;
                case EffectKind.HueSaturation:
                    (r, g, b) = (hueR * r + hueG * g + hueB * b, hueB * r + hueR * g + hueG * b, hueG * r + hueB * g + hueR * b);
                    double gray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    double saturation = 1 + effect.B;
                    r = gray + (r - gray) * saturation;
                    g = gray + (g - gray) * saturation;
                    b = gray + (b - gray) * saturation;
                    break;
                case EffectKind.Grayscale: r = g = b = 0.2126 * r + 0.7152 * g + 0.0722 * b; break;
            }
            if (effect.Kind == EffectKind.Opacity) buffer.CopyWithAlpha(source, i, i, sample.A * effect.A);
            else buffer.Set(i, new(r, g, b, sample.A));
        }
        return frame with { Pixels = buffer.Finish() };
    }

    private static Frame AlphaEffect(Frame frame, int radius, bool grow, uint ink, double opacity, int padding)
    {
        var source = frame.Pixels;
        int width = source.Width + padding * 2, height = source.Height + padding * 2;
        Format.Dimensions(width, height);
        var precision = WorkingPrecision(source);
        using var input = RasterBitmap(source, precision);
        using var output = Surface(width, height, precision);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        using var filter = radius == 0 ? null : grow ? SKImageFilter.CreateDilate(radius, radius) : SKImageFilter.CreateErode(radius, radius);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.DrawBitmap(input, padding, padding, paint);
        canvas.Flush();
        var rgba = Unpremultiply(output, precision);
        double alpha = (ink >> 24) / 255d * opacity;
        if (precision is not null)
        {
            var buffer = new PixelBuffer(width, height, precision);
            var color = PixelValue.FromArgb(ink);
            for (int i = 0; i < width * height; i++)
                buffer.Set(i, color with { A = PixelBuffer.Alpha(rgba, i) * alpha });
            return new(buffer.Finish(), frame.X - padding, frame.Y - padding);
        }
        for (int i = 0; i < rgba.Rgba.Length; i += 4)
        {
            rgba.Rgba[i] = (byte)(ink >> 16);
            rgba.Rgba[i + 1] = (byte)(ink >> 8);
            rgba.Rgba[i + 2] = (byte)ink;
            rgba.Rgba[i + 3] = (byte)Math.Round(rgba.Rgba[i + 3] * alpha);
        }
        return new(rgba, frame.X - padding, frame.Y - padding);
    }

    private static Frame StackFrames(Frame bottom, Frame top, Blend mode = Blend.Normal)
    {
        int x = Math.Min(bottom.X, top.X), y = Math.Min(bottom.Y, top.Y);
        int width = Math.Max(bottom.X + bottom.Pixels.Width, top.X + top.Pixels.Width) - x;
        int height = Math.Max(bottom.Y + bottom.Pixels.Height, top.Y + top.Pixels.Height) - y;
        Format.Dimensions(width, height);
        var precision = WorkingPrecision(top.Pixels) ?? WorkingPrecision(bottom.Pixels);
        using var output = Surface(width, height, precision);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-x, -y);
        DrawNativeFrame(canvas, bottom, null);
        using var paint = new SKPaint { BlendMode = ToSkiaBlend(mode) };
        using var blender = CreateBlender(mode);
        if (blender is not null) paint.Blender = blender;
        DrawNativeFrame(canvas, top, paint);
        canvas.Flush();
        return new(Unpremultiply(output, precision), x, y);
    }

    private Frame StrokeEffect(Frame frame, Effect effect)
    {
        int total = (int)Math.Round(effect.A);
        int outside = effect.Alignment switch { StrokeAlignment.Outside => total, StrokeAlignment.Center => total / 2, _ => 0 };
        int inside = total - outside;
        var ink = Format.Color(effect.Ink);
        var outer = AlphaEffect(frame, outside, true, ink, effect.Opacity, outside);
        Frame Style(Frame outline) => effect.Stops is { Length: > 0 }
            ? RenderLayerStyle(outline, effect with { Kind = EffectKind.GradientOverlay, A = effect.B, Mode = Blend.Normal, Opacity = 1 })
            : outline;
        if (outside == total) return StackFrames(Style(outer), frame);
        var inner = AlphaEffect(frame, inside, false, 0xFFFFFFFF, 1, inside);
        PixelBuffer? native = WorkingPrecision(outer.Pixels) is { } precision
            ? new(outer.Pixels.Width, outer.Pixels.Height, precision) : null;
        native?.Copy(outer.Pixels, 0, 0, outer.Pixels.Width * outer.Pixels.Height);
        for (int y = 0; y < outer.Pixels.Height; y++)
        for (int x = 0; x < outer.Pixels.Width; x++)
        {
            int sx = x + outer.X - inner.X, sy = y + outer.Y - inner.Y;
            if (sx < 0 || sy < 0 || sx >= inner.Pixels.Width || sy >= inner.Pixels.Height) continue;
            int i = y * outer.Pixels.Width + x;
            int maskIndex = sy * inner.Pixels.Width + sx;
            if (native is not null)
                native.CopyWithAlpha(outer.Pixels, i, i,
                    PixelBuffer.Alpha(outer.Pixels, i) * (1 - PixelBuffer.Alpha(inner.Pixels, maskIndex)));
            else outer.Pixels.Rgba[i * 4 + 3] = (byte)(outer.Pixels.Rgba[i * 4 + 3] *
                (255 - inner.Pixels.Rgba[maskIndex * 4 + 3]) / 255);
        }
        return StackFrames(frame, Style(native is null ? outer : outer with { Pixels = native.Finish() }), effect.Mode);
    }

    private static Frame ShadowEffect(Frame frame, Effect effect)
    {
        int spread = (int)Math.Round(effect.Size * effect.Spread / 100);
        uint ink = Format.Color(effect.Ink);
        var shadow = AlphaEffect(frame, spread, true, ink, effect.Opacity, spread);
        shadow = Blur(shadow, effect.Size * (1 - effect.Spread / 100));
        if (effect.Contour != StyleContour.Linear)
        {
            double maximum = PixelValue.FromArgb(ink).A * effect.Opacity;
            if (maximum > 0)
            {
                var buffer = new PixelBuffer(shadow.Pixels.Width, shadow.Pixels.Height, PixelBuffer.Settings(shadow.Pixels));
                for (int i = 0; i < shadow.Pixels.Width * shadow.Pixels.Height; i++)
                {
                    double alpha = ContourCurve.Sample(effect.Contour, PixelBuffer.Alpha(shadow.Pixels, i) / maximum) * maximum;
                    buffer.CopyWithAlpha(shadow.Pixels, i, i, alpha);
                }
                shadow = shadow with { Pixels = buffer.Finish() };
            }
        }
        double radians = effect.A * Math.PI / 180;
        shadow = shadow with { X = shadow.X + (int)Math.Round(Math.Cos(radians) * effect.B),
            Y = shadow.Y + (int)Math.Round(Math.Sin(radians) * effect.B) };
        return StackFrames(shadow, frame);
    }

    private static Frame MotionBlur(Frame frame, double distance, double angle)
    {
        if (distance < .01) return frame;
        double radians = angle * Math.PI / 180;
        double dx = Math.Cos(radians) * distance, dy = Math.Sin(radians) * distance;
        int padX = (int)Math.Ceiling(Math.Abs(dx) / 2), padY = (int)Math.Ceiling(Math.Abs(dy) / 2);
        int width = frame.Pixels.Width + 2 * padX, height = frame.Pixels.Height + 2 * padY;
        if (width > 16384 || height > 16384 || (long)width * height > 32_000_000)
        {
            padX = padY = 0;
            width = frame.Pixels.Width;
            height = frame.Pixels.Height;
        }
        var precision = WorkingPrecision(frame.Pixels);
        using var output = Surface(width, height, precision);
        using var canvas = new SKCanvas(output);
        using var bitmap = RasterBitmap(frame.Pixels, precision);
        bitmap.SetImmutable();
        using var image = SKImage.FromBitmap(bitmap);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Plus };
        canvas.Clear(SKColors.Transparent);
        int taps = Math.Clamp((int)Math.Ceiling(distance) + 1, 2, 63);
        for (int i = 0; i < taps; i++)
        {
            double position = i / (double)(taps - 1) - .5;
            byte weight = (byte)(Math.Round((i + 1) * 255d / taps) - Math.Round(i * 255d / taps));
            if (precision is null) paint.Color = SKColors.White.WithAlpha(weight);
            else paint.ColorF = new SKColorF(1, 1, 1, 1f / taps);
            canvas.DrawImage(image, (float)(padX + dx * position), (float)(padY + dy * position),
                new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
        canvas.Flush();
        return new Frame(Unpremultiply(output, precision), frame.X - padX, frame.Y - padY);
    }

    private static Frame Blur(Frame frame, double sigma)
    {
        if (sigma <= 0) return frame;
        var source = frame.Pixels;
        int padding = (int)Math.Ceiling(3 * sigma);
        int width = source.Width + padding * 2, height = source.Height + padding * 2;
        if (width > 16384 || height > 16384 || (long)width * height > 32_000_000)
        {
            padding = 0;
            width = source.Width;
            height = source.Height;
        }

        var precision = WorkingPrecision(source);
        using var input = RasterBitmap(source, precision);
        using var output = Surface(width, height, precision);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        if (sigma < 1)
        {
            // Skia quantizes small CPU blur radii. Evaluate the subpixel kernel directly
            // in premultiplied space so transparent neighbors cannot contribute stray RGB.
            int radius = (int)Math.Ceiling(3 * sigma);
            var kernel = Enumerable.Range(-radius, radius * 2 + 1)
                .Select(i => Math.Exp(-(double)i * i / (2 * sigma * sigma))).ToArray();
            double sum = kernel.Sum();
            using var paint = new SKPaint { BlendMode = SKBlendMode.Plus };
            for (int y = -radius; y <= radius; y++)
            for (int x = -radius; x <= radius; x++)
            {
                double weight = kernel[x + radius] * kernel[y + radius] / (sum * sum);
                if (precision is null)
                {
                    byte quantized = Byte(weight);
                    if (quantized == 0) continue;
                    paint.Color = SKColors.White.WithAlpha(quantized);
                }
                else paint.ColorF = new SKColorF(1, 1, 1, (float)weight);
                canvas.DrawBitmap(input, padding + x, padding + y, paint);
            }
        }
        else
        {
            using var filter = SKImageFilter.CreateBlur((float)sigma, (float)sigma, SKShaderTileMode.Decal);
            using var paint = new SKPaint { ImageFilter = filter };
            canvas.DrawBitmap(input, padding, padding, paint);
        }
        canvas.Flush();
        return new Frame(Unpremultiply(output, precision), frame.X - padding, frame.Y - padding);
    }

    private static Frame ApplyMask(Frame frame, AlphaMask mask)
    {
        var source = frame.Pixels;
        var buffer = new PixelBuffer(source.Width, source.Height, PixelBuffer.Settings(source));
        int left = mask.Outside == 255 ? (int)Math.Clamp((long)mask.X - frame.X, 0, source.Width) : 0;
        int top = mask.Outside == 255 ? (int)Math.Clamp((long)mask.Y - frame.Y, 0, source.Height) : 0;
        int right = mask.Outside == 255 ? (int)Math.Clamp((long)mask.X + mask.Width - frame.X, 0, source.Width) : source.Width;
        int bottom = mask.Outside == 255 ? (int)Math.Clamp((long)mask.Y + mask.Height - frame.Y, 0, source.Height) : source.Height;
        buffer.Copy(source, 0, 0, source.Width * source.Height);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            int i = y * source.Width + x;
            buffer.CopyWithAlpha(source, i, i, PixelBuffer.Alpha(source, i) * mask.At(x + frame.X, y + frame.Y) / 255d);
        }
        return frame with { Pixels = buffer.Finish() };
    }

    private static Pixels MaskPixels(AlphaMask mask)
    {
        var rgba = new byte[mask.Alpha.Length * 4];
        for (int i = 0; i < mask.Alpha.Length; i++) rgba[i * 4 + 3] = mask.Alpha[i];
        return new(mask.Width, mask.Height, rgba);
    }

    public AlphaMask PaintMask(Document doc, Layer layer, StrokeArgs stroke)
    {
        var mask = layer.Mask!;
        var bytes = new byte[mask.Alpha.Length * 4];
        for (int i = 0; i < mask.Alpha.Length; i++)
        {
            int p = i * 4;
            bytes[p] = bytes[p + 1] = bytes[p + 2] = mask.Alpha[i];
            bytes[p + 3] = 255;
        }
        var painted = Stroke(doc, layer with { Pixels = new(mask.Width, mask.Height, bytes), RasterX = mask.X, RasterY = mask.Y, PixelShifts = [] },
            stroke.Erase ? stroke with { Color = "#FF000000", Erase = false } : stroke, mask: true);
        var alpha = new byte[mask.Alpha.Length];
        for (int i = 0; i < alpha.Length; i++)
        {
            int p = i * 4;
            alpha[i] = (byte)((54 * painted.Rgba[p] + 183 * painted.Rgba[p + 1] + 19 * painted.Rgba[p + 2] + 128) / 256);
        }
        return mask with { Alpha = alpha };
    }

    private readonly ConditionalWeakTable<Layer, RasterEdit> resolved = new();
    private readonly ConditionalWeakTable<Layer, Dictionary<(int, int), Pixels>> fills = new();

    private Pixels FillSource(Layer layer, int width, int height)
    {
        var sizes = fills.GetOrCreateValue(layer);
        lock (sizes)
        {
            if (sizes.TryGetValue((width, height), out var pixels)) return pixels;
            using var image = Surface(width, height);
            image.Erase(new SKColor(Format.Color(layer.Color)));
            sizes.Clear();
            return sizes[(width, height)] = Unpremultiply(image);
        }
    }

    /// <summary>Show the selected mask alone as opaque grayscale in document coordinates.</summary>
    public Pixels MaskView(Document doc, Layer layer)
    {
        var mask = layer.Mask!;
        var bytes = new byte[mask.Alpha.Length * 4];
        for (int i = 0; i < mask.Alpha.Length; i++)
        {
            int p = i * 4;
            bytes[p] = bytes[p + 1] = bytes[p + 2] = mask.Alpha[i];
            bytes[p + 3] = 255;
        }
        using var output = Surface(doc.Width, doc.Height);
        using var canvas = new SKCanvas(output);
        using var image = Bitmap(new(mask.Width, mask.Height, bytes));
        canvas.Clear(new SKColor(mask.Outside, mask.Outside, mask.Outside));
        canvas.Concat(FindMatrix(doc.Layers, layer.Id, SKMatrix.Identity, SKMatrix.Identity, false) ?? SKMatrix.Identity);
        canvas.DrawBitmap(image, mask.X, mask.Y);
        canvas.Flush();
        return Unpremultiply(output);
    }

    public RasterEdit? ResolveRaster(Layer layer)
    {
        if (layer.Pixels is not { } source) return null;
        if (layer.PixelShifts.Length == 0) return new(source, layer.RasterX, layer.RasterY);
        return resolved.GetValue(layer, _ =>
        {
            var edited = new RasterEdit(source, layer.RasterX, layer.RasterY);
            foreach (var shift in layer.PixelShifts) edited = ApplyShift(edited, shift);
            return edited;
        });
    }

    public RasterEdit PaintArea(Document doc, Layer layer, Area bounds, Func<int, int, bool> contains, uint color) =>
        PaintArea(doc, layer, bounds, contains, (_, _) => PixelValue.FromArgb(color));

    private RasterEdit PaintArea(Document doc, Layer layer, Area bounds, Func<int, int, bool> contains, Func<float, float, PixelValue> sample)
    {
        var raster = ResolveRaster(layer) ?? new RasterEdit(new Pixels(layer.Width, layer.Height,
            new byte[layer.Width * layer.Height * 4]), layer.RasterX, layer.RasterY);
        var world = WorldMatrix(doc.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
        if (!world.TryInvert(out var inverse)) throw new InvalidDataException("Layer transform cannot be inverted.");
        var area = EnclosingPixels(inverse.MapRect(SKRect.Create(bounds.X, bounds.Y, bounds.Width, bounds.Height)));
        var patch = new PixelBuffer(area.Width, area.Height, doc.Color);
        bool changed = false;
        for (int y = 0; y < area.Height; y++)
        for (int x = 0; x < area.Width; x++)
        {
            int lx = area.X + x, ly = area.Y + y;
            int dest = y * area.Width + x;
            if (lx >= raster.X && ly >= raster.Y &&
                lx - raster.X < raster.Pixels.Width && ly - raster.Y < raster.Pixels.Height)
                patch.Copy(raster.Pixels, (ly - raster.Y) * raster.Pixels.Width + lx - raster.X, dest);
            var point = world.MapPoint(lx + .5f, ly + .5f);
            int px = (int)Math.Floor(point.X), py = (int)Math.Floor(point.Y);
            if (px < bounds.X || py < bounds.Y || px >= bounds.X + bounds.Width || py >= bounds.Y + bounds.Height || !contains(px, py)) continue;
            var color = sample(point.X, point.Y);
            if (color.A == 0) continue;
            changed = true;
            patch.Set(dest, color.Over(patch.Get(dest)));
        }
        var pixels = patch.Finish();
        if (!changed) return raster;
        var edit = new PixelShift(area, [], 0, 0, Patch: pixels);
        return ApplyShift(raster, edit) with { Shift = edit };
    }

    public PixelSelection? BucketRegion(Document doc, Layer layer, BucketArgs options, bool clipToSelection = true)
    {
        if (!double.IsFinite(options.Point.X) || !double.IsFinite(options.Point.Y) ||
            options.Point.X < 0 || options.Point.Y < 0 || options.Point.X >= doc.Width || options.Point.Y >= doc.Height) return null;
        int x = (int)options.Point.X, y = (int)options.Point.Y, width = doc.Width, height = doc.Height;
        if (clipToSelection && doc.Selection is not null && !doc.Selected(x, y)) return null;
        var visible = layer.Kind switch
        {
            LayerKind.Raster or LayerKind.Chop => null,
            LayerKind.Effect => Composite(doc),
            _ => CompositeRegion(IsolatedBranch(doc.Layers, layer.Id), new Area(0, 0, width, height))
        };
        var raster = layer.Kind is LayerKind.Raster or LayerKind.Chop ? ResolveRaster(layer) : null;
        var world = WorldMatrix(doc.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
        if (!world.TryInvert(out var inverse)) throw new InvalidDataException("Layer transform cannot be inverted.");
        uint ColorAt(int px, int py)
        {
            if (visible is { } rendered)
            {
                int offset = (py * width + px) * 4;
                return rendered.Rgba[offset + 3] == 0 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(rendered.Rgba.AsSpan(offset));
            }
            if (raster is null) return 0;
            var local = inverse.MapPoint(px + .5f, py + .5f);
            var lx = (int)Math.Floor(local.X) - raster.X;
            var ly = (int)Math.Floor(local.Y) - raster.Y;
            if (lx < 0 || ly < 0 || lx >= raster.Pixels.Width || ly >= raster.Pixels.Height) return 0;
            int index = (ly * raster.Pixels.Width + lx) * 4;
            return raster.Pixels.Rgba[index + 3] == 0 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(raster.Pixels.Rgba.AsSpan(index));
        }
        var seed = ColorAt(x, y);
        var states = new byte[width * height];
        var pending = new Queue<int>();
        void Add(int px, int py)
        {
            if (px < 0 || py < 0 || px >= width || py >= height) return;
            int i = py * width + px;
            if (states[i] != 0) return;
            states[i] = 2;
            if (clipToSelection && doc.Selection is not null && !doc.Selected(px, py)) return;
            uint color = ColorAt(px, py);
            for (int shift = 0; shift < 32; shift += 8)
                if (Math.Abs((byte)(color >> shift) - (byte)(seed >> shift)) > options.Tolerance) return;
            states[i] = 1;
            pending.Enqueue(i);
        }
        Add(x, y);
        while (pending.TryDequeue(out var i))
        {
            int px = i % width, py = i / width;
            Add(px - 1, py); Add(px + 1, py); Add(px, py - 1); Add(px, py + 1);
        }
        var (bounds, mask) = PixelSelection.Create(width, height, (px, py) => states[py * width + px] == 1);
        return mask ?? (bounds is { } area ? new PixelSelection(area, Enumerable.Repeat((byte)1, area.Width * area.Height).ToArray()) : null);
    }

    public Projective ParentCoordinates(Document doc, Layer layer)
    {
        var parent = doc.AllLayers().FirstOrDefault(item => item.Children.Any(child => child.Id == layer.Id));
        return parent is null ? Projective.Identity :
            FromSkia(WorldMatrix(doc.Layers, parent.Id, SKMatrix.Identity) ?? SKMatrix.Identity);
    }

    public Projective ToParentSpace(Document doc, Layer layer, Projective transform)
    {
        var parent = ParentCoordinates(doc, layer);
        var image = layer.ImagePlacement ?? Projective.Identity;
        var coordinates = image.Then(parent);
        return coordinates.Then(transform).Then(coordinates.Inverse());
    }

    private static SKMatrix ToSkia(Projective m) =>
        new((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F, (float)m.G, (float)m.H, (float)m.I);

    private static Projective FromSkia(SKMatrix m) =>
        new(m.ScaleX, m.SkewX, m.TransX, m.SkewY, m.ScaleY, m.TransY, m.Persp0, m.Persp1, m.Persp2);

    private static SKMatrix SpatialMatrix(Layer layer) => ToSkia(
        (layer.TransformsEnabled ? layer.Transform.Warp ?? Projective.Identity : Projective.Identity)
            .Then(layer.ImagePlacement ?? Projective.Identity));

    private static bool IsPixelGrid(SKMatrix m)
    {
        static bool Near(float value, float expected) => Math.Abs(value - expected) < .0001;
        bool axes = Near(Math.Abs(m.ScaleX), 1) && Near(Math.Abs(m.ScaleY), 1) && Near(m.SkewX, 0) && Near(m.SkewY, 0);
        bool swappedAxes = Near(Math.Abs(m.SkewX), 1) && Near(Math.Abs(m.SkewY), 1) && Near(m.ScaleX, 0) && Near(m.ScaleY, 0);
        return (axes || swappedAxes) && Near(m.TransX, MathF.Round(m.TransX)) &&
            Near(m.TransY, MathF.Round(m.TransY)) && Near(m.Persp0, 0) && Near(m.Persp1, 0) && Near(m.Persp2, 1);
    }

    public Layer PrepareRasterEditing(Document doc, Layer layer)
    {
        var world = WorldMatrix(doc.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
        if (IsPixelGrid(world)) return layer;
        if (!world.TryInvert(out var inverse)) throw new InvalidDataException("Layer transform cannot be inverted.");
        var parentGrid = FindMatrix(doc.Layers, layer.Id, SKMatrix.Identity, SKMatrix.Identity, false, false) ?? SKMatrix.Identity;
        var grid = FromSkia(parentGrid).Inverse();
        var raster = ResolveRaster(layer);
        var area = new Area(0, 0, doc.Width, doc.Height);
        if (raster is not null)
        {
            // Keep any off-canvas source pixels that fit within the supported raster limits.
            var bounds = world.MapRect(SKRect.Create(raster.X, raster.Y, raster.Pixels.Width, raster.Pixels.Height));
            try
            {
                area = EnclosingPixels(new SKRect(Math.Min(0, bounds.Left), Math.Min(0, bounds.Top),
                    Math.Max(doc.Width, bounds.Right), Math.Max(doc.Height, bounds.Bottom)));
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                throw new InvalidDataException("The transformed raster exceeds the editable pixel area.", error);
            }
        }
        Pixels pixels;
        if (raster is { } native && doc.Color != PixelBuffer.Rgba8)
            pixels = Resample(native.Pixels, area, inverse, native.X, native.Y, doc.Color);
        else if (raster is { } input)
        {
            using var output = Surface(area.Width, area.Height);
            using var canvas = new SKCanvas(output);
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-area.X, -area.Y);
            canvas.Concat(world);
            DrawFrame(canvas, new Frame(input.Pixels, input.X, input.Y));
            canvas.Flush();
            pixels = Unpremultiply(output);
        }
        else pixels = new Pixels(area.Width, area.Height, new byte[area.Width * area.Height * 4]);
        AlphaMask? mask = null;
        if (layer.Mask is not null)
        {
            var gray = MaskView(doc, layer);
            var alpha = new byte[doc.Width * doc.Height];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = gray.Rgba[i * 4];
            mask = new AlphaMask(doc.Width, doc.Height, alpha);
        }
        return layer with
        {
            Pixels = pixels, RasterX = area.X, RasterY = area.Y, RasterGrid = grid,
            PixelShifts = [], Effects = layer.Effects.Where(effect => effect.Kind != EffectKind.Transform).ToArray(), Mask = mask
        };
    }

    public AlphaMask ReprojectMask(Document doc, Layer layer)
    {
        var grayscale = MaskView(doc, layer);
        var alpha = new byte[doc.Width * doc.Height];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = grayscale.Rgba[i * 4];
        return new(doc.Width, doc.Height, alpha);
    }

    private readonly ConditionalWeakTable<Document, Document> resolvedFloating = new();

    public Document ResolveFloating(Document doc) => doc.Floating is null ? doc : resolvedFloating.GetValue(doc, CommitFloating);

    private Document CommitFloating(Document doc)
    {
        var floating = doc.Floating!;
        var committed = doc with { Floating = null };
        if (floating.X == 0 && floating.Y == 0) return committed;
        var layer = doc.AllLayers().FirstOrDefault(item => item.Id == floating.LayerId);
        if (layer is null) return committed;
        var original = committed with { Selection = floating.Source, SelectionMask = floating.Mask };
        var moved = MoveSelection(original, layer, floating.Source, floating.X, floating.Y);
        return moved is null ? committed : committed.ReplaceLayer(layer.Id, moved);
    }

    public RasterEdit? CopyPixels(Document doc, string id)
    {
        doc = ResolveFloating(doc);
        var branch = IsolatedBranch(doc.Layers, id);
        if (branch.Length == 0) return null;
        var area = doc.Selection ?? VisibleLayerBounds(doc, id);
        if (area is not { } box) return null;
        var pixels = CompositeRegion(branch, box, NativePrecision(doc));
        if (doc.SelectionMask is null) return new(pixels, box.X, box.Y);
        var selection = new PixelBuffer(box.Width, box.Height, doc.Color);
        for (int y = 0; y < box.Height; y++)
        for (int x = 0; x < box.Width; x++)
        {
            int offset = y * box.Width + x;
            if (doc.Selected(x + box.X, y + box.Y)) selection.Copy(pixels, offset, offset);
        }
        return new(selection.Finish(), box.X, box.Y);
    }

    public Layer BakeStroke(Layer layer, OutlineArgs outline)
    {
        if (ResolveRaster(layer) is not { } raster) return layer;
        var stroked = StrokeEffect(new Frame(raster.Pixels, raster.X, raster.Y),
            new Effect("stroke", EffectKind.Stroke, A: outline.Thickness, Ink: outline.Color, Alignment: outline.Alignment));
        return layer with { Pixels = stroked.Pixels, RasterX = stroked.X, RasterY = stroked.Y, PixelShifts = [] };
    }

    public Layer CrushSelection(Document doc, Layer layer, int size)
    {
        var editable = PrepareRasterEditing(doc, layer);
        if (ResolveRaster(editable) is not { } raster) return layer;
        var working = doc.ReplaceLayer(layer.Id, editable);
        var world = WorldMatrix(working.Layers, editable.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
        var before = raster.Pixels;
        var data = new PixelBuffer(before.Width, before.Height, doc.Color);
        data.Copy(before, 0, 0, before.Width * before.Height);
        var area = doc.Selection!.Value;
        if (!world.TryInvert(out var inverse)) return layer;
        for (int y = 0; y < raster.Pixels.Height; y++)
        for (int x = 0; x < raster.Pixels.Width; x++)
        {
            var point = world.MapPoint(x + raster.X + .5f, y + raster.Y + .5f);
            if (!doc.Selected((int)Math.Floor(point.X), (int)Math.Floor(point.Y))) continue;
            int fx = area.X + ((int)point.X - area.X) / size * size;
            int fy = area.Y + ((int)point.Y - area.Y) / size * size;
            var src = inverse.MapPoint(fx + .5f, fy + .5f);
            int sx = (int)Math.Floor(src.X) - raster.X, sy = (int)Math.Floor(src.Y) - raster.Y;
            int destination = y * before.Width + x;
            if (sx >= 0 && sy >= 0 && sx < before.Width && sy < before.Height)
                data.Copy(before, sy * before.Width + sx, destination);
            else data.Set(destination, default);
        }
        return editable with { Pixels = data.Finish(), RasterX = raster.X, RasterY = raster.Y, PixelShifts = [] };
    }

    public Layer? MoveSelection(Document doc, Layer layer, Area selection, int dx, int dy)
    {
        var editable = PrepareRasterEditing(doc, layer);
        Layer[] Replace(Layer[] layers) => layers.Select(item => item.Id == layer.Id ? editable
            : item.Children.Length == 0 ? item : item with { Children = Replace(item.Children) }).ToArray();
        var working = ReferenceEquals(editable, layer) ? doc : doc with { Layers = Replace(doc.Layers) };
        var edit = TransformPixels(working, editable, selection, new Projective(1, 0, dx, 0, 1, dy));
        return edit?.Shift is null ? null : editable with
        {
            Pixels = edit.Pixels, RasterX = edit.X, RasterY = edit.Y, PixelShifts = []
        };
    }

    public RasterEdit? TransformPixels(Document doc, Layer layer, Area selection, Projective transform)
    {
        if (ResolveRaster(layer) is not { } raster) return null;
        var source = raster.Pixels;
        var world = WorldMatrix(doc.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
        if (!world.TryInvert(out var inverse)) throw new InvalidDataException("Layer transform cannot be inverted.");
        var before = SKRect.Create(selection.X, selection.Y, selection.Width, selection.Height);
        var after = ToSkia(transform).MapRect(before);
        var reverse = transform.Inverse();
        var localBefore = inverse.MapRect(before);
        localBefore.Intersect(SKRect.Create(raster.X, raster.Y, source.Width, source.Height));
        var localAfter = inverse.MapRect(after);
        if (localBefore.IsEmpty) return null;
        var area = EnclosingPixels(new SKRect(Math.Min(localBefore.Left, localAfter.Left), Math.Min(localBefore.Top, localAfter.Top),
            Math.Max(localBefore.Right, localAfter.Right), Math.Max(localBefore.Bottom, localAfter.Bottom)));
        var buffer = new PixelBuffer(area.Width, area.Height, doc.Color);
        int SourceIndex(int x, int y) => x >= raster.X && y >= raster.Y &&
            x - raster.X < source.Width && y - raster.Y < source.Height
            ? (y - raster.Y) * source.Width + x - raster.X : -1;
        bool changed = false;
        for (int y = 0; y < area.Height; y++)
        for (int x = 0; x < area.Width; x++)
        {
            int lx = area.X + x, ly = area.Y + y;
            int index = y * area.Width + x;
            var point = world.MapPoint(lx + .5f, ly + .5f);
            int original = SourceIndex(lx, ly);
            bool cut = doc.Selected((int)Math.Floor(point.X), (int)Math.Floor(point.Y));
            if (!cut && original >= 0) buffer.Copy(source, original, index);
            if (cut && original >= 0 && PixelBuffer.Alpha(source, original) != 0) changed = true;

            var from = reverse.Map(point.X, point.Y);
            if (!double.IsFinite(from.X) || !double.IsFinite(from.Y)) continue;
            if (!doc.Selected((int)Math.Floor(from.X), (int)Math.Floor(from.Y))) continue;
            var previous = inverse.MapPoint((float)from.X, (float)from.Y);
            int sample = SourceIndex((int)Math.Floor(previous.X), (int)Math.Floor(previous.Y));
            if (sample < 0 || PixelBuffer.Alpha(source, sample) == 0) continue;
            changed = true;
            var backdrop = buffer.Get(index);
            if (backdrop.A == 0) buffer.Copy(source, sample, index);
            else buffer.Set(index, PixelBuffer.Get(source, sample).Over(backdrop));
        }
        if (!changed) return null;
        var edit = new PixelShift(area, [], 0, 0, Patch: buffer.Finish());
        return ApplyShift(raster, edit) with { Shift = edit };
    }

    public RasterEdit ClearSelection(Document doc, Layer layer)
    {
        var raster = ResolveRaster(layer)!;
        var source = raster.Pixels;
        var world = WorldMatrix(doc.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
        var mask = PixelSelection.Create(source.Width, source.Height, (x, y) =>
        {
            if (PixelBuffer.Alpha(source, y * source.Width + x) == 0) return false;
            var p = world.MapPoint(x + raster.X + .5f, y + raster.Y + .5f);
            return doc.Selected((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
        });
        if (mask.Bounds is not { } bounds) return raster;
        var operation = new PixelShift(bounds with { X = bounds.X + raster.X, Y = bounds.Y + raster.Y },
            mask.Mask?.Bits ?? [], 0, 0, Clear: true);
        return ApplyShift(raster, operation) with { Shift = operation };
    }

    private static RasterEdit ApplyShift(RasterEdit raster, PixelShift shift)
    {
        var source = raster.Pixels;
        var area = shift.Source;
        int left = Math.Min(raster.X, checked(area.X + shift.Dx));
        int top = Math.Min(raster.Y, checked(area.Y + shift.Dy));
        int right = Math.Max(raster.X + source.Width, checked(area.X + area.Width + shift.Dx));
        int bottom = Math.Max(raster.Y + source.Height, checked(area.Y + area.Height + shift.Dy));
        int width = checked(right - left), height = checked(bottom - top);
        Format.Dimensions(width, height);
        var buffer = new PixelBuffer(width, height, PixelBuffer.Settings(shift.Patch?.Native is not null ? shift.Patch : source));
        int Index(int x, int y) => (y - top) * width + x - left;
        bool Selected(int x, int y) => shift.Bits.Length == 0 || shift.Bits[(y - area.Y) * area.Width + x - area.X] != 0;
        for (int y = 0; y < source.Height; y++)
            buffer.Copy(source, y * source.Width, Index(raster.X, raster.Y + y), source.Width);
        if (shift.Patch is { } patch)
        {
            for (int y = 0; y < patch.Height; y++)
                buffer.Copy(patch, y * patch.Width, Index(area.X, area.Y + y), patch.Width);
            return new(buffer.Finish(), left, top);
        }
        for (int y = area.Y; y < area.Y + area.Height; y++)
        for (int x = area.X; x < area.X + area.Width; x++)
            if (Selected(x, y)) buffer.Set(Index(x, y), default);
        if (!shift.Clear)
            for (int y = area.Y; y < area.Y + area.Height; y++)
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                if (!Selected(x, y)) continue;
                int destination = Index(x + shift.Dx, y + shift.Dy);
                int pixel = (y - raster.Y) * source.Width + x - raster.X;
                var backdrop = buffer.Get(destination);
                var foreground = PixelBuffer.Get(source, pixel);
                if (foreground.A == 0) continue;
                if (backdrop.A == 0) buffer.Copy(source, pixel, destination);
                else buffer.Set(destination, foreground.Over(backdrop));
            }
        return new(buffer.Finish(), left, top);
    }

    public Pixels Stroke(Document doc, Layer layer, StrokeArgs stroke) => Stroke(doc, layer, stroke, mask: false);

    private Pixels Stroke(Document doc, Layer layer, StrokeArgs stroke, bool mask)
    {
        var raster = ResolveRaster(layer);
        int width = raster?.Pixels.Width ?? layer.Width;
        int height = raster?.Pixels.Height ?? layer.Height;
        var settings = mask ? PixelBuffer.Rgba8 : doc.Color;
        var buffer = new PixelBuffer(width, height, settings);
        if (raster?.Pixels is { } source) buffer.Copy(source, 0, 0, width * height);
        var world = FindMatrix(doc.Layers, layer.Id, SKMatrix.Identity, SKMatrix.Identity, !mask) ?? SKMatrix.Identity;
        world = SKMatrix.Concat(world, SKMatrix.CreateTranslation(raster?.X ?? layer.RasterX, raster?.Y ?? layer.RasterY));
        if (!world.TryInvert(out var inverse)) throw new InvalidDataException("Layer transform cannot be inverted.");
        double radius = stroke.Size / 2;
        uint color = Format.Color(stroke.Color);
        double opacity = stroke.Opacity * (stroke.Erase ? 1 : (color >> 24) / 255.0);
        var selection = doc.Selection ?? new Area(0, 0, doc.Width, doc.Height);

        var corners = inverse.MapRect(new SKRect(
            (float)(stroke.Points.Min(p => p.X) - radius), (float)(stroke.Points.Min(p => p.Y) - radius),
            (float)(stroke.Points.Max(p => p.X) + radius), (float)(stroke.Points.Max(p => p.Y) + radius)));
        int firstX = (int)Math.Clamp(Math.Floor(corners.Left), 0, width);
        int firstY = (int)Math.Clamp(Math.Floor(corners.Top), 0, height);
        int lastX = (int)Math.Clamp(Math.Ceiling(corners.Right), firstX, width);
        int lastY = (int)Math.Clamp(Math.Ceiling(corners.Bottom), firstY, height);
        int stride = lastX - firstX;
        bool eightBit = settings.Depth == ChannelDepth.Bit8;
        var coverage = new float[stride * (lastY - firstY)];

        void Stamp(PointD point)
        {
            if (stroke.Pencil) point = new(Math.Floor(point.X) + .5, Math.Floor(point.Y) + .5);
            var bounds = inverse.MapRect(new SKRect((float)(point.X - radius), (float)(point.Y - radius),
                (float)(point.X + radius), (float)(point.Y + radius)));
            int left = (int)Math.Clamp(Math.Floor(bounds.Left), firstX, lastX);
            int top = (int)Math.Clamp(Math.Floor(bounds.Top), firstY, lastY);
            int right = (int)Math.Clamp(Math.Ceiling(bounds.Right), firstX, lastX);
            int bottom = (int)Math.Clamp(Math.Ceiling(bounds.Bottom), firstY, lastY);
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var p = world.MapPoint(x + .5f, y + .5f);
                if (p.X < selection.X || p.X >= selection.X + selection.Width ||
                    p.Y < selection.Y || p.Y >= selection.Y + selection.Height || (doc.SelectionMask is not null &&
                    !doc.Selected((int)Math.Floor(p.X), (int)Math.Floor(p.Y)))) continue;
                double dx = p.X - point.X, dy = p.Y - point.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                double amount = stroke.Pencil ? (distance <= radius ? 1 : 0) : Math.Clamp((radius - distance) / Math.Max(.5, radius * (1 - stroke.Hardness)), 0, 1);
                int i = (y - firstY) * stride + x - firstX;
                coverage[i] = Math.Max(coverage[i], eightBit ? Byte(amount) : (float)amount);
            }
        }

        var previous = stroke.Points[0];
        Stamp(previous);
        double spacing = Math.Max(0.5, radius / 3);
        double remaining = spacing;
        foreach (var point in stroke.Points.Skip(1))
        {
            double dx = point.X - previous.X, dy = point.Y - previous.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(distance) || distance > 1_000_000) throw new InvalidDataException("Stroke segment exceeds the supported length.");
            double offset = remaining;
            for (; offset <= distance; offset += spacing)
                Stamp(new(previous.X + dx * offset / distance, previous.Y + dy * offset / distance));
            remaining = offset - distance;
            previous = point;
        }
        if (remaining < spacing) Stamp(previous);
        for (int y = firstY; y < lastY; y++)
        for (int x = firstX; x < lastX; x++)
        {
            double alpha = opacity * coverage[(y - firstY) * stride + x - firstX] / (eightBit ? 255 : 1);
            if (alpha == 0) continue;
            int i = y * width + x;
            var backdrop = buffer.Get(i);
            var foreground = PixelValue.FromArgb(color) with { A = alpha };
            if (stroke.Erase) buffer.CopyWithAlpha(buffer.Finish(), i, i, backdrop.A * (1 - alpha));
            else buffer.Set(i, foreground.Over(backdrop));
        }
        return buffer.Finish();
    }

    /// <summary>Maps source coordinates onto the canvas for transformed brush strokes.</summary>
    public static SKMatrix? WorldMatrix(IEnumerable<Layer> layers, string id, SKMatrix parent) =>
        FindMatrix(layers, id, parent, SKMatrix.Identity, true);

    private static SKMatrix? FindMatrix(IEnumerable<Layer> layers, string id, SKMatrix parent, SKMatrix outerEffects, bool includeTargetEffects, bool includeRasterGrid = true)
    {
        foreach (var layer in layers)
        {
            bool skip = !layer.TransformsEnabled;
            var layerOutput = outerEffects;
            if (parent.TryInvert(out var spatialParentInverse))
                layerOutput = SKMatrix.Concat(outerEffects,
                    SKMatrix.Concat(parent, SKMatrix.Concat(SpatialMatrix(layer), spatialParentInverse)));
            bool placedGroup = layer.Kind == LayerKind.Group && layer.Placement != new Placement() && parent.TryInvert(out _);
            var native = SKMatrix.Concat(parent, placedGroup ? Matrix(skip ? new Transform() : layer.Transform) : LayerMatrix(layer, skip));
            var effects = SKMatrix.Identity;
            foreach (var effect in layer.Effects)
                if (!skip && effect.Enabled && effect.Kind == EffectKind.Transform)
                    effects = SKMatrix.Concat(Matrix(effect.Spatial!), effects);

            if (layer.Kind == LayerKind.Group)
            {
                var placementOutput = layerOutput;
                if (placedGroup && parent.TryInvert(out var parentInverse))
                    placementOutput = SKMatrix.Concat(layerOutput,
                        SKMatrix.Concat(parent, SKMatrix.Concat(PlacementMatrix(layer.Placement), parentInverse)));
                var output = SKMatrix.Concat(placementOutput, effects);
                if (layer.Id == id) return SKMatrix.Concat(includeTargetEffects ? output : placementOutput, native);
                if (FindMatrix(layer.Children, id, native, output, includeTargetEffects, includeRasterGrid) is { } child) return child;
            }
            else if (layer.Id == id)
            {
                if (includeRasterGrid && layer.RasterGrid is { } grid) native = SKMatrix.Concat(native, ToSkia(grid));
                return SKMatrix.Concat(layerOutput, SKMatrix.Concat(native, includeTargetEffects ? effects : SKMatrix.Identity));
            }
        }
        return null;
    }

    private static SKMatrix PlacementMatrix(Placement p) =>
        SKMatrix.Concat(SKMatrix.CreateTranslation((float)p.X, (float)p.Y),
            SKMatrix.CreateScale((float)p.ScaleX, (float)p.ScaleY));

    private static SKMatrix LayerMatrix(Layer layer, bool bypass)
    {
        var placement = PlacementMatrix(layer.Placement);
        return bypass ? placement : SKMatrix.Concat(placement, Matrix(layer.Transform));
    }

    private static SKMatrix Matrix(Transform t) => SKMatrix.Concat(SKMatrix.CreateTranslation((float)t.X, (float)t.Y),
        SKMatrix.Concat(SKMatrix.CreateRotationDegrees((float)t.Rotation), SKMatrix.CreateScale((float)t.ScaleX, (float)t.ScaleY)));
    private static byte Mix(byte old, byte color, double retained, double alpha, double output) =>
        (byte)Math.Round((old * retained + color * alpha) / output);
    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private static SKBitmap Surface(int width, int height, ColorSettings? precision = null) =>
        new(width, height, precision is null ? SKColorType.Rgba8888 : SKColorType.RgbaF32, SKAlphaType.Premul);

    private static SKBitmap Bitmap(Pixels pixels)
    {
        var bitmap = new SKBitmap(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        Marshal.Copy(pixels.Rgba, 0, bitmap.GetPixels(), pixels.Rgba.Length);
        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    private static Pixels Read(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.Width * bitmap.Height * 4];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return new(bitmap.Width, bitmap.Height, bytes);
    }

    private static Pixels Unpremultiply(SKBitmap bitmap, ColorSettings? precision = null)
    {
        if (precision is not null) return ReadNative(bitmap, precision);
        using var pixmap = bitmap.PeekPixels();
        return ReadIntoArray(bitmap.Width, bitmap.Height, (info, address) =>
            pixmap.ReadPixels(info, address, info.RowBytes));
    }
}
