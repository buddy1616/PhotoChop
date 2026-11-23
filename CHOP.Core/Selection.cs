namespace Chop.Core;

public enum SelectionMode { Replace, Add, Subtract }
public sealed record RectangleSelection(Area Area, SelectionMode Mode, bool Ellipse = false);
public sealed record ColorSelection(string Color, double Fuzziness, bool Invert = false, SelectionMode Mode = SelectionMode.Replace);

/// <summary>A mask in document pixel coordinates. Legacy masks use binary samples.</summary>
public sealed record PixelSelection(Area Bounds, byte[] Bits, bool Soft = false)
{
    public bool Contains(int x, int y) => Amount(x, y) != 0;

    public byte Amount(int x, int y)
    {
        if (x < Bounds.X || y < Bounds.Y || x >= Bounds.X + Bounds.Width || y >= Bounds.Y + Bounds.Height) return 0;
        byte value = Bits[(y - Bounds.Y) * Bounds.Width + x - Bounds.X];
        return Soft || value == 0 ? value : byte.MaxValue;
    }

    internal static byte Combine(Document? document, SelectionMode mode, bool hit, int x, int y) => mode switch
    {
        SelectionMode.Add when !hit => document?.SelectionAmount(x, y) ?? 0,
        SelectionMode.Subtract when !hit => document?.SelectionAmount(x, y) ?? 0,
        SelectionMode.Subtract => 0,
        _ => hit ? byte.MaxValue : (byte)0
    };

    public static (Area? Bounds, PixelSelection? Mask) ColorRange(Pixels pixels, ColorSelection query, Document? current = null)
    {
        uint color = Format.Color(query.Color);
        double cutoff = query.Fuzziness / 200.0;
        if (!double.IsFinite(cutoff) || cutoff is < 0 or > 1) throw new InvalidDataException("Fuzziness must be between 0 and 200.");
        return CreateCoverage(new Area(0, 0, pixels.Width, pixels.Height), (x, y) =>
        {
            int i = (y * pixels.Width + x) * 4;
            int r = pixels.Rgba[i] - (byte)(color >> 16), g = pixels.Rgba[i + 1] - (byte)(color >> 8), b = pixels.Rgba[i + 2] - (byte)color;
            bool match = pixels.Rgba[i + 3] > 0 && r * r + g * g + b * b <= cutoff * cutoff * 195075;
            if (query.Invert) match = !match;
            return Combine(current, query.Mode, match, x, y);
        });
    }

    public static (Area? Bounds, PixelSelection? Mask) Create(int width, int height, Func<int, int, bool> test) =>
        CreateCoverage(new Area(0, 0, width, height), (x, y) => test(x, y) ? byte.MaxValue : (byte)0);

    internal static (Area? Bounds, PixelSelection? Mask) CreateCoverage(Area area, Func<int, int, byte> sample)
    {
        var bits = new byte[checked(area.Width * area.Height)];
        int left = area.Width, right = -1, top = area.Height, bottom = -1, solid = 0;
        bool soft = false;
        for (int y = 0; y < area.Height; y++)
        for (int x = 0; x < area.Width; x++)
        {
            byte value = sample(area.X + x, area.Y + y);
            if (value == 0) continue;
            bits[y * area.Width + x] = value;
            if (value == 255) solid++;
            else soft = true;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        if (right < left) return (null, null);
        var bounds = new Area(area.X + left, area.Y + top, right - left + 1, bottom - top + 1);
        if (solid == bounds.Width * bounds.Height) return (bounds, null);
        byte[] cropped = bits;
        if (bounds != area)
        {
            cropped = new byte[bounds.Width * bounds.Height];
            for (int y = 0; y < bounds.Height; y++)
                Array.Copy(bits, (top + y) * area.Width + left, cropped, y * bounds.Width, bounds.Width);
        }
        if (!soft)
            for (int i = 0; i < cropped.Length; i++) if (cropped[i] != 0) cropped[i] = 1;
        return (bounds, new(bounds, cropped, soft));
    }

    internal static (Area? Bounds, PixelSelection? Mask) Filter(Document document, int radius, bool feather)
    {
        if (document.Selection is not { } selection) return (null, null);
        int left = Math.Max(0, selection.X - radius), top = Math.Max(0, selection.Y - radius);
        int right = Math.Min(document.Width, selection.X + selection.Width + radius);
        int bottom = Math.Min(document.Height, selection.Y + selection.Height + radius);
        int width = right - left, height = bottom - top, diameter = 2 * radius + 1;
        int divisor = diameter * diameter;
        var horizontal = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            int sum = 0;
            for (int x = 0; x < Math.Min(radius, width); x++) sum += document.SelectionAmount(left + x, top + y);
            for (int x = 0; x < width; x++)
            {
                if (x + radius < width) sum += document.SelectionAmount(left + x + radius, top + y);
                if (x - radius - 1 >= 0) sum -= document.SelectionAmount(left + x - radius - 1, top + y);
                horizontal[y * width + x] = (ushort)sum;
            }
        }
        // A running sum per column avoids scanning the full square for each pixel.
        var vertical = new int[width];
        for (int y = 0; y < Math.Min(radius, height); y++)
            for (int x = 0; x < width; x++) vertical[x] += horizontal[y * width + x];
        return CreateCoverage(new Area(left, top, width, height), (px, py) =>
        {
            int x = px - left, y = py - top;
            ref int sum = ref vertical[x];
            if (y + radius < height) sum += horizontal[(y + radius) * width + x];
            if (y - radius - 1 >= 0) sum -= horizontal[(y - radius - 1) * width + x];
            int coverage = (sum + divisor / 2) / divisor;
            return feather ? (byte)coverage : coverage >= 128 ? byte.MaxValue : (byte)0;
        });
    }
}
