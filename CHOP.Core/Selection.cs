namespace Chop.Core;

public enum SelectionMode { Replace, Add, Subtract }
public sealed record RectangleSelection(Area Area, SelectionMode Mode, bool Ellipse = false);
public sealed record ColorSelection(string Color, double Fuzziness, bool Invert = false, SelectionMode Mode = SelectionMode.Replace);

/// <summary>A binary selection in document pixel coordinates.</summary>
public sealed record PixelSelection(Area Bounds, byte[] Bits)
{
    public bool Contains(int x, int y) => x >= Bounds.X && y >= Bounds.Y &&
        x < Bounds.X + Bounds.Width && y < Bounds.Y + Bounds.Height &&
        Bits[(y - Bounds.Y) * Bounds.Width + x - Bounds.X] != 0;

    public static (Area? Bounds, PixelSelection? Mask) ColorRange(Pixels pixels, ColorSelection query, Document? current = null)
    {
        uint color = Format.Color(query.Color);
        double cutoff = query.Fuzziness / 200.0;
        if (!double.IsFinite(cutoff) || cutoff is < 0 or > 1) throw new InvalidDataException("Fuzziness must be between 0 and 200.");
        return Create(pixels.Width, pixels.Height, (x, y) =>
        {
            int i = (y * pixels.Width + x) * 4;
            int r = pixels.Rgba[i] - (byte)(color >> 16), g = pixels.Rgba[i + 1] - (byte)(color >> 8), b = pixels.Rgba[i + 2] - (byte)color;
            bool match = pixels.Rgba[i + 3] > 0 && r * r + g * g + b * b <= cutoff * cutoff * 195075;
            if (query.Invert) match = !match;
            return query.Mode switch
            {
                SelectionMode.Add => match || current?.Selected(x, y) == true,
                SelectionMode.Subtract => !match && current?.Selected(x, y) == true,
                _ => match
            };
        });
    }

    public static (Area? Bounds, PixelSelection? Mask) Create(int width, int height, Func<int, int, bool> test)
    {
        var bits = new byte[checked(width * height)];
        int left = width, right = -1, top = height, bottom = -1, count = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (!test(x, y)) continue;
            bits[y * width + x] = 1;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
            count++;
        }
        if (count == 0) return (null, null);
        var bounds = new Area(left, top, right - left + 1, bottom - top + 1);
        if (count == bounds.Width * bounds.Height) return (bounds, null);
        var cropped = new byte[bounds.Width * bounds.Height];
        for (int y = 0; y < bounds.Height; y++)
            Array.Copy(bits, (top + y) * width + left, cropped, y * bounds.Width, bounds.Width);
        return (bounds, new(bounds, cropped));
    }
}
