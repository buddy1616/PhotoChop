using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private byte[] EncodeIco(Document document)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var frames = new List<byte[]>(sizes.Length);
        using var source = Bitmap(Composite(document));
        using var image = SKImage.FromBitmap(source);
        foreach (int size in sizes)
        {
            double scale = (double)size / Math.Max(source.Width, source.Height);
            float width = (float)(source.Width * scale), height = (float)(source.Height * scale);
            using var frame = Surface(size, size);
            using var canvas = new SKCanvas(frame);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, SKRect.Create((size - width) / 2, (size - height) / 2, width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.Flush();
            using var png = frame.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("Could not encode an icon frame.");
            frames.Add(png.ToArray());
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        int offset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte side = (byte)(sizes[i] % 256);
            writer.Write(side);
            writer.Write(side);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        return output.ToArray();
    }
}
