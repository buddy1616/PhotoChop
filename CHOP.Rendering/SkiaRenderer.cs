using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

/// <summary>CPU renderer with immutable input snapshots and a weakly keyed operation cache.</summary>
public sealed class SkiaRenderer : IRasterBackend
{
    private sealed record Frame(Pixels Pixels, int X = 0, int Y = 0);
    private sealed record Evaluation(Frame Frame, Effect Effect, Evaluation? Input);
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

    public Pixels DecodePng(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data)) ?? throw new InvalidDataException("Invalid PNG.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Png) throw new InvalidDataException("Only PNG import is supported.");
        Format.Dimensions(codec.Info.Width, codec.Info.Height);
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException($"PNG decode failed: {result}.");
        return Read(bitmap);
    }

    public byte[] EncodePng(Pixels pixels)
    {
        using var bitmap = Bitmap(pixels);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("PNG encoding failed.");
        return data.ToArray();
    }

    public Pixels Composite(Document document)
    {
        using var bitmap = Surface(document.Width, document.Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        foreach (var layer in document.Layers) Draw(canvas, layer, document, SKMatrix.Identity);
        canvas.Flush();
        return Unpremultiply(bitmap);
    }

    private void Draw(SKCanvas canvas, Layer layer, Document doc, SKMatrix parent)
    {
        if (!layer.Visible || layer.Opacity == 0) return;
        var world = SKMatrix.Concat(parent, Matrix(layer.Transform));
        var blend = layer.Effects.LastOrDefault(effect => effect.Enabled && effect.Kind == EffectKind.BlendMode)?.Mode ?? layer.Blend;
        using var blender = blend == Blend.Add ? addBlend.Value.ToBlender() : null;
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(Byte(layer.Opacity)),
            BlendMode = blend switch
            {
                Blend.Multiply => SKBlendMode.Multiply, Blend.Screen => SKBlendMode.Screen,
                Blend.Overlay => SKBlendMode.Overlay, _ => SKBlendMode.SrcOver
            }
        };
        if (blender is not null) paint.Blender = blender;

        if (layer.Kind == LayerKind.Group)
        {
            using var group = Surface(doc.Width, doc.Height);
            using var groupCanvas = new SKCanvas(group);
            groupCanvas.Clear(SKColors.Transparent);
            foreach (var child in layer.Children) Draw(groupCanvas, child, doc, world);
            groupCanvas.Flush();
            if (layer.Effects.Any(ProcessesPixels))
            {
                var frame = Evaluate(Unpremultiply(group), layer.Effects);
                using var adjusted = Bitmap(frame.Pixels);
                canvas.DrawBitmap(adjusted, frame.X, frame.Y, paint);
            }
            else canvas.DrawBitmap(group, 0, 0, paint);
            return;
        }

        Pixels? source = layer.Kind == LayerKind.Raster ? layer.Pixels : vectors.GetValue(layer, Rasterize);
        if (source is null) return;
        var evaluated = Evaluate(source, layer.Effects);
        using var bitmap = Bitmap(evaluated.Pixels);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.Save();
        canvas.Concat(world);
        canvas.DrawImage(image, evaluated.X, evaluated.Y, new SKSamplingOptions(SKFilterMode.Linear), paint);
        canvas.Restore();
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
            int width = Math.Clamp((int)Math.Ceiling(lines.Max(line => font.MeasureText(line))) + 4, 1, 16384);
            int height = Math.Clamp((int)Math.Ceiling(lineHeight * lines.Length) + 4, 1, Math.Min(16384, 32_000_000 / width));
            using var text = Surface(width, height);
            using var canvas = new SKCanvas(text);
            canvas.Clear(SKColors.Transparent);
            for (int i = 0; i < lines.Length && i * lineHeight < height; i++)
                canvas.DrawText(lines[i], 1, 1 - font.Metrics.Ascent + i * lineHeight, SKTextAlign.Left, font, paint);
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

    private Frame Evaluate(Pixels source, Effect[] effects)
    {
        if (!effects.Any(ProcessesPixels)) return new(source);
        var cache = evaluations.GetOrCreateValue(source);
        lock (cache)
        {
            Evaluation? input = null;
            foreach (var effect in effects)
            {
                if (!ProcessesPixels(effect)) continue;
                var node = cache.Nodes.Find(item => item.Effect == effect && ReferenceEquals(item.Input, input));
                if (node is null)
                {
                    node = new Evaluation(ApplyEffect(input?.Frame ?? new Frame(source), effect), effect, input);
                    cache.Nodes.Add(node);
                }
                input = node;
            }
            const long budget = 64 * 1024 * 1024;
            if (cache.Nodes.Sum(node => node.Frame.Pixels.Rgba.LongLength) > budget)
            {
                cache.Nodes.Clear();
                long size = 0;
                for (var node = input; node is not null; node = node.Input) size += node.Frame.Pixels.Rgba.LongLength;
                if (size <= budget)
                    for (var node = input; node is not null; node = node.Input) cache.Nodes.Add(node);
            }
            return input?.Frame ?? new Frame(source);
        }
    }

    private static Frame ApplyEffect(Frame frame, Effect effect)
    {
        var source = frame.Pixels;
        if (effect.Kind == EffectKind.Transform)
        {
            var transform = Matrix(effect.Spatial!);
            var area = transform.MapRect(SKRect.Create(frame.X, frame.Y, source.Width, source.Height));
            int x = (int)Math.Floor(area.Left), y = (int)Math.Floor(area.Top);
            int width = (int)Math.Ceiling(area.Right) - x, height = (int)Math.Ceiling(area.Bottom) - y;
            Format.Dimensions(width, height);
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
        if (effect.Kind == EffectKind.GaussianBlur) return Blur(frame, effect.A);

        var bytes = (byte[])source.Rgba.Clone();
        if (effect.Kind == EffectKind.Opacity)
        {
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = (byte)Math.Round(bytes[i] * effect.A);
            return frame with { Pixels = source with { Rgba = bytes } };
        }
        float hueR = 0, hueG = 0, hueB = 0;
        if (effect.Kind == EffectKind.HueSaturation)
        {
            double angle = effect.A * Math.PI / 180;
            hueR = (float)((1 + 2 * Math.Cos(angle)) / 3);
            hueG = (float)((1 - Math.Cos(angle) - Math.Sqrt(3) * Math.Sin(angle)) / 3);
            hueB = (float)((1 - Math.Cos(angle) + Math.Sqrt(3) * Math.Sin(angle)) / 3);
        }
        for (int i = 0; i < bytes.Length; i += 4)
        {
            double r = bytes[i] / 255.0, g = bytes[i + 1] / 255.0, b = bytes[i + 2] / 255.0;
            switch (effect.Kind)
            {
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
            bytes[i] = Byte(r);
            bytes[i + 1] = Byte(g);
            bytes[i + 2] = Byte(b);
        }
        return frame with { Pixels = source with { Rgba = bytes } };
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

        using var input = Bitmap(source);
        using var output = Surface(width, height);
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
                byte weight = Byte(kernel[x + radius] * kernel[y + radius] / (sum * sum));
                if (weight == 0) continue;
                paint.Color = SKColors.White.WithAlpha(weight);
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
        return new Frame(Unpremultiply(output), frame.X - padding, frame.Y - padding);
    }

    public Pixels Stroke(Document doc, Layer layer, StrokeArgs stroke)
    {
        int width = layer.Pixels?.Width ?? layer.Width;
        int height = layer.Pixels?.Height ?? layer.Height;
        var bytes = layer.Pixels is { } source ? (byte[])source.Rgba.Clone() : new byte[width * height * 4];
        var world = WorldMatrix(doc.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
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
        var coverage = new byte[stride * (lastY - firstY)];

        void Stamp(PointD point)
        {
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
                    p.Y < selection.Y || p.Y >= selection.Y + selection.Height) continue;
                double dx = p.X - point.X, dy = p.Y - point.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                byte mask = Byte(Math.Clamp((radius - distance) / Math.Max(.5, radius * (1 - stroke.Hardness)), 0, 1));
                int i = (y - firstY) * stride + x - firstX;
                coverage[i] = Math.Max(coverage[i], mask);
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
            double alpha = opacity * coverage[(y - firstY) * stride + x - firstX] / 255;
            if (alpha == 0) continue;
            int i = (y * width + x) * 4;
            double retained = bytes[i + 3] / 255.0 * (1 - alpha);
            if (stroke.Erase) { bytes[i + 3] = Byte(retained); continue; }
            double output = alpha + retained;
            bytes[i] = Mix(bytes[i], (byte)(color >> 16), retained, alpha, output);
            bytes[i + 1] = Mix(bytes[i + 1], (byte)(color >> 8), retained, alpha, output);
            bytes[i + 2] = Mix(bytes[i + 2], (byte)color, retained, alpha, output);
            bytes[i + 3] = Byte(output);
        }
        return new(width, height, bytes);
    }

    /// <summary>Maps source coordinates onto the canvas for transformed brush strokes.</summary>
    public static SKMatrix? WorldMatrix(IEnumerable<Layer> layers, string id, SKMatrix parent) =>
        FindMatrix(layers, id, parent, SKMatrix.Identity);

    private static SKMatrix? FindMatrix(IEnumerable<Layer> layers, string id, SKMatrix parent, SKMatrix outerEffects)
    {
        foreach (var layer in layers)
        {
            var native = SKMatrix.Concat(parent, Matrix(layer.Transform));
            var effects = SKMatrix.Identity;
            foreach (var effect in layer.Effects)
                if (effect.Enabled && effect.Kind == EffectKind.Transform)
                    effects = SKMatrix.Concat(Matrix(effect.Spatial!), effects);

            if (layer.Kind == LayerKind.Group)
            {
                var output = SKMatrix.Concat(outerEffects, effects);
                if (layer.Id == id) return SKMatrix.Concat(output, native);
                if (FindMatrix(layer.Children, id, native, output) is { } child) return child;
            }
            else if (layer.Id == id) return SKMatrix.Concat(outerEffects, SKMatrix.Concat(native, effects));
        }
        return null;
    }

    private static SKMatrix Matrix(Transform t) => SKMatrix.Concat(SKMatrix.CreateTranslation((float)t.X, (float)t.Y),
        SKMatrix.Concat(SKMatrix.CreateRotationDegrees((float)t.Rotation), SKMatrix.CreateScale((float)t.ScaleX, (float)t.ScaleY)));
    private static byte Mix(byte old, byte color, double retained, double alpha, double output) =>
        (byte)Math.Round((old * retained + color * alpha) / output);
    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private static SKBitmap Surface(int width, int height) => new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

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

    private static Pixels Unpremultiply(SKBitmap bitmap)
    {
        using var straight = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var pixmap = bitmap.PeekPixels();
        if (!pixmap.ReadPixels(straight.Info, straight.GetPixels(), straight.RowBytes))
            throw new InvalidOperationException("Could not read the composited image.");
        return Read(straight);
    }
}
