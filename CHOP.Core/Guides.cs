namespace Chop.Core;

public enum GuideAxis { Horizontal, Vertical }

/// <summary>A guide lies on a pixel boundary in document coordinates.</summary>
public sealed record Guide(string Id, GuideAxis Axis, int Position);

public static class GuideSnap
{
    public static double Nearest(double value, IEnumerable<int> boundaries, double tolerance)
    {
        double best = tolerance;
        double result = value;
        foreach (int boundary in boundaries)
        {
            double distance = Math.Abs(value - boundary);
            if (distance > best) continue;
            if (distance < best || result == value || boundary < result) result = boundary;
            best = distance;
        }
        return result;
    }

    public static (int[] Vertical, int[] Horizontal) SelectionEdges(Document document)
    {
        if (document.Selection is not { } area) return ([], []);
        if (document.SelectionMask is not { } mask)
            return ([area.X, area.X + area.Width], [area.Y, area.Y + area.Height]);

        var vertical = new bool[area.Width + 1];
        var horizontal = new bool[area.Height + 1];
        for (int y = 0; y < area.Height; y++)
        for (int x = 0; x < area.Width; x++)
        {
            int i = y * area.Width + x;
            if (mask.Bits[i] == 0) continue;
            if (x == 0 || mask.Bits[i - 1] == 0) vertical[x] = true;
            if (x == area.Width - 1 || mask.Bits[i + 1] == 0) vertical[x + 1] = true;
            if (y == 0 || mask.Bits[i - area.Width] == 0) horizontal[y] = true;
            if (y == area.Height - 1 || mask.Bits[i + area.Width] == 0) horizontal[y + 1] = true;
        }

        return (Enumerable.Range(0, vertical.Length).Where(i => vertical[i]).Select(i => area.X + i).ToArray(),
            Enumerable.Range(0, horizontal.Length).Where(i => horizontal[i]).Select(i => area.Y + i).ToArray());
    }
}

public sealed record ScaleArgs(double X, double Y, int AnchorX = 1, int AnchorY = 1);

/// <summary>An axis-aligned scale or reflection around a document-space pixel boundary.</summary>
public readonly record struct PixelTransform(double X, double Y, double AnchorX, double AnchorY)
{
    public PointD Map(double x, double y) => new(AnchorX + (x - AnchorX) * X, AnchorY + (y - AnchorY) * Y);
    public PointD Inverse(double x, double y) => new(AnchorX + (x - AnchorX) / X, AnchorY + (y - AnchorY) / Y);
}
