namespace Chop.Core;

public sealed record SamplingStroke(PointD[] Points, double Radius, bool Include);
public sealed record ContentAwareFillArgs(string Id, bool SampleAll = true, SamplingStroke[]? Strokes = null);

public static class ContentAwareFill
{
    public const int MaxTargetPixels = 1_048_576;
    private const int PatchRadius = 2;
    private const int Passes = 4;

    // Barnes et al., PatchMatch (SIGGRAPH 2009): alternate propagation directions
    // and search around the best match with an exponentially decreasing radius.
    // https://gfx.cs.princeton.edu/pubs/Barnes_2009_PAR/index.php
    public static RasterEdit Synthesize(Document document, Pixels source, ContentAwareFillArgs args)
    {
        if (document.Selection is not { } target) throw new InvalidDataException("Select the pixels to fill first.");
        if ((long)target.Width * target.Height > MaxTargetPixels)
            throw new InvalidDataException($"Content-Aware Fill supports selection bounds up to {MaxTargetPixels:N0} pixels per fill.");
        if (source.Width != document.Width || source.Height != document.Height)
            throw new InvalidDataException("The sampling image must match the document.");

        var strokes = args.Strokes ?? [];
        if (strokes.Length > 10_000 || strokes.Sum(s => (long)(s?.Points?.Length ?? 0)) > 100_000 ||
            strokes.Any(s => s is null || s.Points is not { Length: > 0 } ||
                !double.IsFinite(s.Radius) || s.Radius is < .5 or > 2048 ||
                s.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                    p.X < 0 || p.Y < 0 || p.X > document.Width || p.Y > document.Height)))
            throw new InvalidDataException("Invalid content-aware sampling strokes.");

        int width = source.Width, height = source.Height, length = target.Width * target.Height;
        var random = new Random(8279);
        var sampling = BuildSampling(width, height, args.SampleAll, strokes);
        bool Allowed(int x, int y) => (uint)x < (uint)width && (uint)y < (uint)height &&
            source.Rgba[(y * width + x) * 4 + 3] != 0 && (sampling is null || sampling[y * width + x] != 0) &&
            !document.Selected(x, y);

        var matches = new int[length];
        Array.Fill(matches, -1);
        var holes = new List<int>();
        for (int y = 0; y < target.Height; y++)
        for (int x = 0; x < target.Width; x++)
            if (document.Selected(target.X + x, target.Y + y)) holes.Add(y * target.Width + x);
        if (holes.Count == 0) throw new InvalidDataException("The selection contains no pixels.");

        const int reservoirSize = 32_768;
        var candidates = new List<int>(reservoirSize);
        int seen = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (!Allowed(x, y)) continue;
            seen++;
            if (candidates.Count < reservoirSize) candidates.Add(y * width + x);
            else if (random.Next(seen) is < reservoirSize and var replacement) candidates[replacement] = y * width + x;
        }
        if (candidates.Count == 0) throw new InvalidDataException("Paint a sampling area over visible pixels outside the selection.");
        foreach (int hole in holes) matches[hole] = candidates[random.Next(candidates.Count)];

        var reconstruction = new byte[length * 4];
        for (int pass = 0; pass < Passes; pass++)
        {
            int direction = pass % 2 == 0 ? 1 : -1;
            int start = direction == 1 ? 0 : holes.Count - 1;
            bool reconstructed = pass > 0;
            for (int step = 0; step < holes.Count; step++)
            {
                int cell = holes[start + step * direction];
                int column = cell % target.Width, row = cell / target.Width;
                int x = target.X + column, y = target.Y + row;
                int best = matches[cell];
                double score = Distance(x, y, best % width, best / width, reconstructed);
                void Try(int sx, int sy)
                {
                    if (!Allowed(sx, sy)) return;
                    double distance = Distance(x, y, sx, sy, reconstructed);
                    if (distance >= score) return;
                    score = distance;
                    best = sy * width + sx;
                }

                if ((uint)(column - direction) < (uint)target.Width && matches[cell - direction] is >= 0 and var left)
                    Try(left % width + direction, left / width);
                if ((uint)(row - direction) < (uint)target.Height && matches[cell - direction * target.Width] is >= 0 and var above)
                    Try(above % width, above / width + direction);
                int candidate = candidates[random.Next(candidates.Count)];
                Try(candidate % width, candidate / width);
                for (int radius = Math.Max(width, height); radius >= 1; radius /= 2)
                    Try(best % width + random.Next(-radius, radius + 1), best / width + random.Next(-radius, radius + 1));
                matches[cell] = best;
            }

            // Overlapping patches vote on each hole pixel. Never vote from outside the painted sampling area.
            foreach (int cell in holes)
            {
                int x = cell % target.Width, y = cell / target.Width;
                int r = 0, g = 0, b = 0, a = 0, total = 0;
                for (int dy = -PatchRadius; dy <= PatchRadius; dy++)
                for (int dx = -PatchRadius; dx <= PatchRadius; dx++)
                {
                    if ((uint)(x + dx) >= (uint)target.Width || (uint)(y + dy) >= (uint)target.Height) continue;
                    int match = matches[(y + dy) * target.Width + x + dx];
                    if (match < 0) continue;
                    int sx = match % width - dx, sy = match / width - dy;
                    if (!Allowed(sx, sy)) continue;
                    int weight = 1 + PatchRadius - Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int sample = (sy * width + sx) * 4;
                    r += source.Rgba[sample] * weight;
                    g += source.Rgba[sample + 1] * weight;
                    b += source.Rgba[sample + 2] * weight;
                    a += source.Rgba[sample + 3] * weight;
                    total += weight;
                }
                int pixel = cell * 4;
                reconstruction[pixel] = (byte)((r + total / 2) / total);
                reconstruction[pixel + 1] = (byte)((g + total / 2) / total);
                reconstruction[pixel + 2] = (byte)((b + total / 2) / total);
                reconstruction[pixel + 3] = (byte)((a + total / 2) / total);
            }
        }
        return new(new Pixels(target.Width, target.Height, reconstruction), target.X, target.Y);

        double Distance(int tx, int ty, int sx, int sy, bool useReconstruction)
        {
            double cost = 0, total = 0;
            for (int dy = -PatchRadius; dy <= PatchRadius; dy++)
            for (int dx = -PatchRadius; dx <= PatchRadius; dx++)
            {
                int x = tx + dx, y = ty + dy;
                if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
                bool hole = document.Selected(x, y);
                if (hole && !useReconstruction) continue;
                int original = hole ? ((y - target.Y) * target.Width + x - target.X) * 4 : (y * width + x) * 4;
                byte[] reference = hole ? reconstruction : source.Rgba;
                if (reference[original + 3] == 0) continue;
                double weight = hole ? .25 : 1;
                total += weight;
                if (!Allowed(sx + dx, sy + dy)) { cost += 100_000 * weight; continue; }
                int sample = ((sy + dy) * width + sx + dx) * 4;
                int r = reference[original] - source.Rgba[sample];
                int g = reference[original + 1] - source.Rgba[sample + 1];
                int b = reference[original + 2] - source.Rgba[sample + 2];
                cost += weight * (r * r + g * g + b * b);
            }
            return (total == 0 ? 0 : cost / total) + .01 * (Math.Abs(sx - tx) + Math.Abs(sy - ty));
        }
    }

    public static byte[]? BuildSampling(int width, int height, bool sampleAll, SamplingStroke[] strokes, double scale = 1)
    {
        if (sampleAll && strokes.Length == 0) return null;
        var mask = new byte[checked(width * height)];
        if (sampleAll) Array.Fill(mask, (byte)1);
        foreach (var stroke in strokes)
            for (int i = 0; i < stroke.Points.Length; i++)
                PaintSampling(mask, width, height, stroke.Points[Math.Max(0, i - 1)], stroke.Points[i], stroke.Radius, stroke.Include, scale);
        return mask;
    }

    public static Area PaintSampling(byte[] mask, int width, int height, PointD start, PointD end, double radius, bool include, double scale = 1)
    {
        double ax = start.X * scale, ay = start.Y * scale;
        double bx = end.X * scale, by = end.Y * scale;
        radius = Math.Max(.5, radius * scale);
        double vx = bx - ax, vy = by - ay, length = vx * vx + vy * vy;
        int left = (int)Math.Clamp(Math.Floor(Math.Min(ax, bx) - radius), 0, width);
        int top = (int)Math.Clamp(Math.Floor(Math.Min(ay, by) - radius), 0, height);
        int right = (int)Math.Clamp(Math.Ceiling(Math.Max(ax, bx) + radius), left, width);
        int bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(ay, by) + radius), top, height);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            double t = length == 0 ? 0 : Math.Clamp(((x + .5 - ax) * vx + (y + .5 - ay) * vy) / length, 0, 1);
            double dx = x + .5 - ax - vx * t, dy = y + .5 - ay - vy * t;
            if (dx * dx + dy * dy <= radius * radius) mask[y * width + x] = include ? (byte)1 : (byte)0;
        }
        return new(left, top, right - left, bottom - top);
    }
}
