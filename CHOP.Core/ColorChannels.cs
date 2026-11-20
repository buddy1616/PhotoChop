using System.Buffers.Binary;

namespace Chop.Core;

public enum ChannelColorMode { Rgb, Grayscale, Cmyk }
public enum ChannelDepth { Bit8 = 8, Bit16 = 16, Bit32 = 32 }
public enum ColorComponent { Red, Green, Blue, Gray, Cyan, Magenta, Yellow, Black, Alpha }

public sealed record ColorSettings(ChannelColorMode Mode = ChannelColorMode.Rgb, ChannelDepth Depth = ChannelDepth.Bit8);
public sealed record ChannelInfo(string Id, string Name, int X = 0, int Y = 0, bool Spot = false);
public sealed record ChannelPlane(ChannelInfo Info, int Width, int Height, ChannelDepth Depth, byte[] Samples);
public sealed record ColorPlanes(ColorSettings Settings, int Width, int Height, byte[][] Planes);
public sealed record GradientArgs(PointD Start, PointD End, string StartColor, string EndColor, GradientStop[]? Stops = null);
public enum VectorMaskOperation { Continue = -1, Exclude = 0, Union = 1, Subtract = 2, Intersect = 3 }
public sealed record VectorMask(PathContour[] Contours, bool Enabled = true, bool Inverted = false,
    VectorMaskOperation[]? Operations = null, bool InitialFill = false);

/// <summary>Planar channels use unsigned bytes, little-endian unsigned words, or little-endian IEEE single samples.</summary>
public static class ChannelSamples
{
    public static ColorComponent[] Components(ChannelColorMode mode) => mode switch
    {
        ChannelColorMode.Grayscale => [ColorComponent.Gray],
        ChannelColorMode.Cmyk => [ColorComponent.Cyan, ColorComponent.Magenta, ColorComponent.Yellow, ColorComponent.Black],
        _ => [ColorComponent.Red, ColorComponent.Green, ColorComponent.Blue]
    };

    public static int ComponentCount(ChannelColorMode mode) => mode switch
    {
        ChannelColorMode.Grayscale => 1, ChannelColorMode.Cmyk => 4, _ => 3
    };

    public static int Bytes(ChannelDepth depth) => depth switch
    {
        ChannelDepth.Bit8 => 1, ChannelDepth.Bit16 => 2, ChannelDepth.Bit32 => 4,
        _ => throw new InvalidDataException("Invalid channel depth.")
    };

    public static void Validate(ColorSettings settings)
    {
        if (settings is null || !Enum.IsDefined(settings.Mode)) throw new InvalidDataException("Invalid color mode.");
        Bytes(settings.Depth);
    }

    public static void Validate(int width, int height, ChannelDepth depth, byte[] samples)
    {
        Format.Dimensions(width, height);
        if (samples is null || samples.Length != checked(width * height * Bytes(depth)))
            throw new InvalidDataException("Channel data does not match its dimensions or depth.");
    }

    public static void Validate(ColorPlanes source)
    {
        Validate(source.Settings);
        if (source.Planes is null || source.Planes.Length < ComponentCount(source.Settings.Mode) ||
            source.Planes.Length > ComponentCount(source.Settings.Mode) + 1)
            throw new InvalidDataException("Incorrect number of color planes.");
        foreach (byte[] samples in source.Planes) Validate(source.Width, source.Height, source.Settings.Depth, samples);
    }

    public static float Read(byte[] samples, ChannelDepth depth, int index) => depth switch
    {
        ChannelDepth.Bit8 => samples[index] / 255f,
        ChannelDepth.Bit16 => BinaryPrimitives.ReadUInt16LittleEndian(samples.AsSpan(index * 2, 2)) / 65535f,
        _ => BinaryPrimitives.ReadSingleLittleEndian(samples.AsSpan(index * 4, 4))
    };

    public static void Write(byte[] samples, ChannelDepth depth, int index, float value)
    {
        if (depth == ChannelDepth.Bit32)
            BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan(index * 4, 4), value);
        else if (depth == ChannelDepth.Bit16)
            BinaryPrimitives.WriteUInt16LittleEndian(samples.AsSpan(index * 2, 2), (ushort)Math.Round(Unit(value) * 65535));
        else samples[index] = Display(value);
    }

    public static float Unit(float value) => float.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
    public static byte Display(float value) => (byte)Math.Round(Unit(value) * 255);
    public static float Gray(float r, float g, float b) => .2126f * r + .7152f * g + .0722f * b;
    public static float Component(ColorComponent component, float r, float g, float b, float a)
    {
        float largest = Math.Max(r, Math.Max(g, b));
        return component switch
        {
            ColorComponent.Red => r, ColorComponent.Green => g, ColorComponent.Blue => b,
            ColorComponent.Gray => Gray(r, g, b), ColorComponent.Alpha => a,
            ColorComponent.Cyan => largest == 0 ? 0 : 1 - r / largest,
            ColorComponent.Magenta => largest == 0 ? 0 : 1 - g / largest,
            ColorComponent.Yellow => largest == 0 ? 0 : 1 - b / largest,
            ColorComponent.Black => 1 - largest,
            _ => 0
        };
    }

    public static Pixels ToPreview(ColorPlanes source)
    {
        Validate(source);
        var rgba = new byte[checked(source.Width * source.Height * 4)];
        int components = ComponentCount(source.Settings.Mode);
        for (int i = 0; i < rgba.Length / 4; i++)
        {
            float Sample(int channel) => Read(source.Planes[channel], source.Settings.Depth, i);
            float r = Sample(0);
            float g = components == 1 ? r : Sample(1);
            float b = components == 1 ? r : Sample(2);
            if (source.Settings.Mode == ChannelColorMode.Cmyk)
            {
                float light = 1 - Unit(Sample(3));
                r = (1 - Unit(r)) * light;
                g = (1 - Unit(g)) * light;
                b = (1 - Unit(b)) * light;
            }
            int p = i * 4;
            rgba[p] = Display(r);
            rgba[p + 1] = Display(g);
            rgba[p + 2] = Display(b);
            rgba[p + 3] = components < source.Planes.Length ? Display(Sample(components)) : (byte)255;
        }
        return new(source.Width, source.Height, rgba);
    }
}
