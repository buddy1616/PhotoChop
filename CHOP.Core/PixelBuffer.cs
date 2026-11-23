namespace Chop.Core;

/// <summary>Straight-alpha working samples. RGB channels may exceed the display range in float documents.</summary>
public readonly record struct PixelValue(double R, double G, double B, double A)
{
    public static PixelValue FromArgb(uint color) =>
        new(((color >> 16) & 255) / 255d, ((color >> 8) & 255) / 255d, (color & 255) / 255d, (color >> 24) / 255d);

    public PixelValue Over(PixelValue backdrop)
    {
        double retained = backdrop.A * (1 - A), alpha = A + retained;
        return alpha == 0 ? default : new((R * A + backdrop.R * retained) / alpha,
            (G * A + backdrop.G * retained) / alpha, (B * A + backdrop.B * retained) / alpha, alpha);
    }

    public static PixelValue Lerp(PixelValue a, PixelValue b, double t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
}

/// <summary>Writes in the selected document format and maintains a separate RGBA8 display proxy.</summary>
public sealed class PixelBuffer
{
    public static readonly ColorSettings Rgba8 = new(ChannelColorMode.Rgb, ChannelDepth.Bit8);
    private readonly Pixels pixels;

    public PixelBuffer(int width, int height, ColorSettings settings)
    {
        Format.Dimensions(width, height);
        ColorPlanes? native = null;
        if (settings != Rgba8)
        {
            int channels = ChannelSamples.ComponentCount(settings.Mode) + 1;
            int length = checked(width * height * ChannelSamples.Bytes(settings.Depth));
            native = new(settings, width, height, Enumerable.Range(0, channels).Select(_ => new byte[length]).ToArray());
        }
        pixels = new(width, height, new byte[checked(width * height * 4)], native);
    }

    public Pixels Finish() => pixels;

    public static ColorSettings Settings(Pixels? source) => source?.Native?.Settings ?? Rgba8;

    public static Pixels Convert(Pixels source, ColorSettings settings)
    {
        if (Settings(source) == settings) return source;
        var buffer = new PixelBuffer(source.Width, source.Height, settings);
        buffer.Copy(source, 0, 0, source.Width * source.Height);
        return buffer.Finish();
    }

    public static Pixels FromNative(ColorPlanes native)
    {
        ChannelSamples.Validate(native);
        var proxy = new Pixels(native.Width, native.Height, new byte[checked(native.Width * native.Height * 4)], native);
        for (int i = 0; i < native.Width * native.Height; i++) WriteProxy(proxy, i, Get(proxy, i));
        return proxy;
    }

    public static PixelValue Get(Pixels source, int index)
    {
        if (source.Native is not { } native)
        {
            int offset = index * 4;
            return new(source.Rgba[offset] / 255d, source.Rgba[offset + 1] / 255d,
                source.Rgba[offset + 2] / 255d, source.Rgba[offset + 3] / 255d);
        }
        double Read(int channel) => ChannelSamples.Read(native.Planes[channel], native.Settings.Depth, index);
        double first = Read(0);
        var color = native.Settings.Mode switch
        {
            ChannelColorMode.Grayscale => new PixelValue(first, first, first, native.Planes.Length > 1 ? Read(1) : 1),
            ChannelColorMode.Cmyk => new PixelValue((1 - first) * (1 - Read(3)), (1 - Read(1)) * (1 - Read(3)),
                (1 - Read(2)) * (1 - Read(3)), native.Planes.Length > 4 ? Read(4) : 1),
            _ => new PixelValue(first, Read(1), Read(2), native.Planes.Length > 3 ? Read(3) : 1)
        };
        return color with { A = Math.Clamp(color.A, 0, 1) };
    }

    public static double Alpha(Pixels source, int index) => source.Native is { } native
        ? native.Planes.Length == ChannelSamples.ComponentCount(native.Settings.Mode) ? 1 :
            Math.Clamp(ChannelSamples.Read(native.Planes[^1], native.Settings.Depth, index), 0, 1)
        : source.Rgba[index * 4 + 3] / 255d;

    public PixelValue Get(int index) => Get(pixels, index);

    public void Set(int index, PixelValue value)
    {
        if (pixels.Native is { } native)
        {
            void Write(int plane, double component) => ChannelSamples.Write(native.Planes[plane], native.Settings.Depth, index, (float)component);
            switch (native.Settings.Mode)
            {
                case ChannelColorMode.Grayscale:
                    Write(0, .2126 * value.R + .7152 * value.G + .0722 * value.B);
                    break;
                case ChannelColorMode.Cmyk:
                    double maximum = Math.Max(value.R, Math.Max(value.G, value.B));
                    Write(0, maximum <= 0 ? 0 : 1 - value.R / maximum);
                    Write(1, maximum <= 0 ? 0 : 1 - value.G / maximum);
                    Write(2, maximum <= 0 ? 0 : 1 - value.B / maximum);
                    Write(3, 1 - maximum);
                    break;
                default:
                    Write(0, value.R);
                    Write(1, value.G);
                    Write(2, value.B);
                    break;
            }
            Write(native.Planes.Length - 1, value.A);
            value = Get(index);
        }
        WriteProxy(pixels, index, value);
    }

    public void InvertColor(int index, byte coverage)
    {
        if (coverage == 0) return;
        double amount = coverage / 255d;
        if (pixels.Native is { } native)
        {
            int components = ChannelSamples.ComponentCount(native.Settings.Mode);
            for (int channel = 0; channel < components; channel++)
            {
                var plane = native.Planes[channel];
                float value = ChannelSamples.Read(plane, native.Settings.Depth, index);
                ChannelSamples.Write(plane, native.Settings.Depth, index, (float)(value + (1 - 2d * value) * amount));
            }
            WriteProxy(pixels, index, Get(index));
            return;
        }

        int offset = index * 4;
        for (int channel = 0; channel < 3; channel++)
        {
            int value = pixels.Rgba[offset + channel];
            pixels.Rgba[offset + channel] = (byte)Math.Round(value + (255 - 2 * value) * amount);
        }
    }

    private static void WriteProxy(Pixels pixels, int index, PixelValue value)
    {
        int offset = index * 4;
        static byte Display(double value) => double.IsNaN(value) ? (byte)0 :
            (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        pixels.Rgba[offset] = Display(value.R);
        pixels.Rgba[offset + 1] = Display(value.G);
        pixels.Rgba[offset + 2] = Display(value.B);
        pixels.Rgba[offset + 3] = Display(value.A);
    }

    public void CopyWithAlpha(Pixels source, int from, int to, double alpha)
    {
        Copy(source, from, to);
        if (pixels.Native is { } native)
        {
            ChannelSamples.Write(native.Planes[^1], native.Settings.Depth, to, (float)Math.Clamp(alpha, 0, 1));
            alpha = ChannelSamples.Read(native.Planes[^1], native.Settings.Depth, to);
        }
        pixels.Rgba[to * 4 + 3] = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
    }

    public void Copy(Pixels source, int from, int to, int count = 1)
    {
        if (source.Native?.Settings == pixels.Native?.Settings &&
            source.Native?.Planes.Length == pixels.Native?.Planes.Length)
        {
            Buffer.BlockCopy(source.Rgba, from * 4, pixels.Rgba, to * 4, count * 4);
            if (source.Native is { } original && pixels.Native is { } native)
            {
                int size = ChannelSamples.Bytes(native.Settings.Depth);
                for (int i = 0; i < native.Planes.Length; i++)
                    Buffer.BlockCopy(original.Planes[i], from * size, native.Planes[i], to * size, count * size);
            }
            return;
        }
        if (source.Native is null && pixels.Native is { Settings.Mode: ChannelColorMode.Rgb, Settings.Depth: ChannelDepth.Bit16 } rgb16)
        {
            var rgba = source.Rgba.AsSpan(from * 4, count * 4);
            rgba.CopyTo(pixels.Rgba.AsSpan(to * 4));
            int offset = to * 2, length = count * 2;
            for (int channel = 0; channel < 4; channel++)
            {
                var plane = rgb16.Planes[channel].AsSpan(offset, length);
                for (int sample = 0; sample < count; sample++)
                {
                    // Expanding an 8-bit sample to 16 bits repeats its byte in both positions.
                    byte value = rgba[sample * 4 + channel];
                    plane[sample * 2] = plane[sample * 2 + 1] = value;
                }
            }
            return;
        }
        if (source.Native is { } sourcePlanes && pixels.Native is { } targetPlanes &&
            sourcePlanes.Settings.Mode == targetPlanes.Settings.Mode)
        {
            int last = targetPlanes.Planes.Length - 1;
            for (int i = 0; i < count; i++)
            {
                for (int channel = 0; channel <= last; channel++)
                {
                    float sample = channel == last ? (float)Alpha(source, from + i) :
                        ChannelSamples.Read(sourcePlanes.Planes[channel], sourcePlanes.Settings.Depth, from + i);
                    ChannelSamples.Write(targetPlanes.Planes[channel], targetPlanes.Settings.Depth, to + i, sample);
                }
                WriteProxy(pixels, to + i, Get(pixels, to + i));
            }
            return;
        }
        for (int i = 0; i < count; i++) Set(to + i, Get(source, from + i));
    }
}
