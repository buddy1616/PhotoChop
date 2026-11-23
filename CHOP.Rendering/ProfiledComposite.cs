using System.Runtime.CompilerServices;
using Chop.Core;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private readonly ConditionalWeakTable<byte[], ConditionalWeakTable<Pixels, Pixels>> profilePreviews = new();

    private readonly ConditionalWeakTable<byte[], ConditionalWeakTable<Layer, Layer>> profiledLayers = new();

    private Layer[] ProfiledLayers(Document document)
    {
        if (document.Color.Mode != ChannelColorMode.Cmyk || document.IccProfile is not { } profile)
            return document.Layers;

        var cache = profilePreviews.GetOrCreateValue(profile);
        var layers = profiledLayers.GetOrCreateValue(profile);
        var transform = new Lazy<CmykProfile?>(() => CmykProfile.Open(profile));
        try { return document.Layers.Select(Convert).ToArray(); }
        finally { if (transform.IsValueCreated) transform.Value?.Dispose(); }

        Layer Convert(Layer layer) => layers.GetValue(layer, ConvertLayer);

        Layer ConvertLayer(Layer layer)
        {
            if (!layer.Visible || layer.Opacity == 0) return layer;
            if (layer.Effects.Any(effect => effect.PhotoshopStyle))
                layer = layer with { Effects = layer.Effects.Select(effect => effect.PhotoshopStyle
                    ? effect with { ProfiledCmykStyle = true } : effect).ToArray() };
            if (layer.Children.Length > 0) return layer with { Children = layer.Children.Select(Convert).ToArray() };
            var raster = ResolveRaster(layer);
            if (raster?.Pixels.Native is not { Settings.Mode: ChannelColorMode.Cmyk } native) return layer;
            var pixels = cache.GetValue(raster.Pixels, input => transform.Value is { } converter
                ? ConvertCmyk(input, native, converter) : input);
            return layer with { Pixels = pixels, PixelShifts = [], RasterX = raster.X, RasterY = raster.Y };
        }
    }

    private static Pixels? StorageCmyk(Pixels input, ColorSettings settings, CmykProfile converter)
    {
        const int batch = 4096;
        var source = new ushort[batch * 3];
        var output = new ushort[batch * 4];
        int size = input.Width * input.Height;
        var planes = new byte[5][];
        for (int c = 0; c < planes.Length; c++) planes[c] = new byte[size * ChannelSamples.Bytes(settings.Depth)];
        for (int start = 0; start < size; start += batch)
        {
            int count = Math.Min(batch, size - start);
            for (int i = 0; i < count; i++)
                for (int c = 0; c < 3; c++) source[i * 3 + c] = (ushort)(input.Rgba[(start + i) * 4 + c] * 257);
            if (!converter.Convert(source, output, count)) return null;
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < 4; c++)
                    ChannelSamples.Write(planes[c], settings.Depth, start + i, output[i * 4 + c] / 65535f);
                ChannelSamples.Write(planes[4], settings.Depth, start + i, input.Rgba[(start + i) * 4 + 3] / 255f);
            }
        }
        return PixelBuffer.FromNative(new(settings, input.Width, input.Height, planes));
    }

    private static Pixels ConvertCmyk(Pixels input, ColorPlanes native, CmykProfile converter)
    {
        const int batch = 4096;
        var source = new ushort[batch * 4];
        var output = new ushort[batch * 3];
        byte[] rgba = new byte[input.Rgba.Length];
        int size = input.Width * input.Height;
        for (int start = 0; start < size; start += batch)
        {
            int count = Math.Min(batch, size - start);
            for (int i = 0; i < count; i++)
                for (int c = 0; c < 4; c++)
                    source[i * 4 + c] = (ushort)Math.Round(ushort.MaxValue * Math.Clamp(
                        ChannelSamples.Read(native.Planes[c], native.Settings.Depth, start + i), 0, 1));
            if (!converter.Convert(source, output, count)) return input;
            for (int i = 0; i < count; i++)
            {
                int pixel = (start + i) * 4;
                for (int c = 0; c < 3; c++) rgba[pixel + c] = (byte)((output[i * 3 + c] + 128) / 257);
                rgba[pixel + 3] = input.Rgba[pixel + 3];
            }
        }
        return new(input.Width, input.Height, rgba);
    }
}
