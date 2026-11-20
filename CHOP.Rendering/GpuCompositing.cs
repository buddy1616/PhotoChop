using System.Runtime.InteropServices;
using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    public static bool CanCompositeOnGpu(Document document)
    {
        if ((long)document.Width * document.Height < 1_000_000 || document.Floating is not null ||
            document.Color.Mode == ChannelColorMode.Grayscale) return false;
        int leaves = 0;
        bool Supported(Layer layer)
        {
            if (!layer.Visible || layer.Opacity == 0) return true;
            if (layer.Clipped || layer.Kind == LayerKind.Effect) return false;
            if (layer.Kind != LayerKind.Group)
            {
                leaves++;
                return true;
            }
            return layer.Placement == new Placement() && SpatialMatrix(layer) == SKMatrix.Identity &&
                (layer.Mask is null || !layer.MaskEnabled) && !ActiveVectorMask(layer.VectorMask) &&
                (!layer.TransformsEnabled || !layer.Effects.Any(ProcessesPixels)) &&
                (layer.TransformsEnabled ? CommandEngine.EffectiveBlend(layer) : layer.Blend) == Blend.Normal &&
                layer.Children.All(Supported);
        }
        return document.Layers.All(Supported) && leaves >= 2;
    }

    /// <summary>Uses the caller's current, thread-affine OpenGL context. The caller owns that context.</summary>
    public GpuCompositor CreateGpuCompositor(GRContext context) => new(this, context);

    public sealed class GpuCompositor : IDisposable
    {
        private readonly SkiaRenderer renderer;
        private readonly GRContext context;
        private readonly FrameImageCache textures = new(96L * 1024 * 1024);

        internal GpuCompositor(SkiaRenderer renderer, GRContext context)
        {
            this.renderer = renderer;
            this.context = context;
            context.SetResourceCacheLimit(160L * 1024 * 1024);
        }

        public Pixels? Composite(Document document)
        {
            if (!CanCompositeOnGpu(document)) return null;
            if (context.IsAbandoned) throw new InvalidOperationException("Skia lost the OpenGL context.");
            var region = new Area(0, 0, document.Width, document.Height);
            var info = new SKImageInfo(region.Width, region.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(context, true, info);
            if (surface is null) return null;
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            foreach (var layer in document.Layers) DrawLayer(layer, SKMatrix.Identity);
            return ReadIntoArray(region.Width, region.Height, (target, address) =>
                surface.ReadPixels(target, address, target.RowBytes, 0, 0));

            void DrawLayer(Layer layer, SKMatrix parent)
            {
                if (!layer.Visible || layer.Opacity == 0) return;
                if (layer.Kind != LayerKind.Group)
                {
                    renderer.Draw(canvas, layer, region, parent, DrawGpuFrame);
                    return;
                }
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Byte(layer.Opacity * layer.FillOpacity)) };
                bool isolate = !IsPassThroughGroup(layer);
                if (isolate) canvas.SaveLayer(paint);
                var world = SKMatrix.Concat(parent, LayerMatrix(layer, !layer.TransformsEnabled));
                foreach (var child in layer.Children) DrawLayer(child, world);
                if (isolate) canvas.Restore();
            }

            void DrawGpuFrame(SKCanvas target, Frame frame, SKPaint? paint) =>
                textures.Use(frame.Pixels, Upload, image =>
                    target.DrawImage(image, frame.X, frame.Y, new SKSamplingOptions(SKFilterMode.Linear), paint));
        }

        private SKImage Upload(Pixels pixels)
        {
            using var image = CreateFrameImage(pixels);
            return image.ToTextureImage(context) ??
                throw new InvalidOperationException("Skia could not upload a layer texture.");
        }

        public void Dispose() => textures.Dispose();
    }

    private static Pixels ReadIntoArray(int width, int height, Func<SKImageInfo, IntPtr, bool> read)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bytes = GC.AllocateUninitializedArray<byte>(checked(info.RowBytes * height));
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!read(info, pinned.AddrOfPinnedObject()))
                throw new InvalidOperationException("Could not read the composited image.");
            return new(width, height, bytes);
        }
        finally { pinned.Free(); }
    }

    private static SKImage CreateFrameImage(Pixels pixels)
    {
        using var bitmap = Bitmap(pixels);
        // Let Skia share the native pixels with the image instead of cloning the bitmap again.
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    private sealed class FrameImageCache(long capacity) : IDisposable
    {
        private sealed record Entry(Pixels Source, SKImage Image, long Bytes);
        private readonly LinkedList<Entry> order = new();
        private readonly Dictionary<Pixels, LinkedListNode<Entry>> lookup = new(ReferenceEqualityComparer.Instance);
        private long bytes;

        public void Use(Pixels pixels, Func<Pixels, SKImage> make, Action<SKImage> draw)
        {
            long size = pixels.Rgba.LongLength;
            if (size > capacity)
            {
                using var image = make(pixels);
                draw(image);
                return;
            }
            lock (order)
            {
                if (!lookup.TryGetValue(pixels, out var node))
                {
                    while (bytes + size > capacity) RemoveOldest();
                    node = order.AddFirst(new Entry(pixels, make(pixels), size));
                    lookup.Add(pixels, node);
                    bytes += size;
                }
                else
                {
                    order.Remove(node);
                    order.AddFirst(node);
                }
                draw(node.Value.Image);
            }
        }

        private void RemoveOldest()
        {
            var item = order.Last!.Value;
            order.RemoveLast();
            lookup.Remove(item.Source);
            bytes -= item.Bytes;
            item.Image.Dispose();
        }

        public void Dispose()
        {
            lock (order)
                while (order.Count > 0) RemoveOldest();
        }
    }
}
