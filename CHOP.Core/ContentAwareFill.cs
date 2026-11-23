namespace Chop.Core;

public sealed record SamplingStroke(PointD[] Points, double Radius, bool Include);
public sealed record ContentAwareFillArgs(string Id, bool SampleAll = true, SamplingStroke[]? Strokes = null);

public static class ContentAwareFill
{
    public const int MaxTargetPixels = 1_048_576;
    private const int ContextSize = 12;
    private const int CorpusLimit = 32_768;
    private static readonly (int X, int Y)[] ContextOffsets = (
        from y in Enumerable.Range(-5, 11)
        from x in Enumerable.Range(-5, 11)
        where (x != 0 || y != 0) && x * x + y * y <= 25
        orderby x * x + y * y
        select (x, y)).ToArray();
    private static readonly int[] Difference = Enumerable.Range(0, 256)
        .Select(value => 1024 * value * value / (1024 + value * value)).ToArray();
    private readonly record struct ContextPixel(int X, int Y, int Rgba);

    // Independent exemplar synthesis based on the published Resynthesizer description.
    // https://github.com/bootchk/resynthesizer/wiki/About-the-algorithm
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
        var sampling = BuildSampling(width, height, args.SampleAll, strokes);
        bool Allowed(int x, int y) => (uint)x < (uint)width && (uint)y < (uint)height &&
            source.Rgba[(y * width + x) * 4 + 3] != 0 && (sampling is null || sampling[y * width + x] != 0) &&
            !document.Selected(x, y);

        var corpus = new List<int>();
        int stride = 1, seen = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (!Allowed(x, y) || seen++ % stride != 0) continue;
            if (corpus.Count == CorpusLimit)
            {
                // Keep evenly spaced samples when the corpus is larger than the search index.
                for (int i = 0; i < CorpusLimit / 2; i++) corpus[i] = corpus[i * 2];
                corpus.RemoveRange(CorpusLimit / 2, CorpusLimit / 2);
                stride *= 2;
                if ((seen - 1) % stride != 0) continue;
            }
            corpus.Add(y * width + x);
        }
        if (corpus.Count == 0) throw new InvalidDataException("Paint a sampling area over visible pixels outside the selection.");
        var byColor = corpus.GroupBy(pixel => ColorKey(source.Rgba, pixel * 4))
            .ToDictionary(group => group.Key, group => group.ToArray());

        var order = FillOrder(document, target);
        if (order.Count == 0) throw new InvalidDataException("The selection contains no pixels.");
        var reconstruction = new byte[length * 4];
        var context = new ContextPixel[ContextSize];
        for (int pass = 0; pass < 3; pass++)
        {
            bool changed = false;
            foreach (int cell in order)
            {
                int x = target.X + cell % target.Width, y = target.Y + cell / target.Width;
                int count = ReadContext(x, y);
                int best = corpus[cell % corpus.Count], lowest = int.MaxValue;
                if (count > 0)
                {
                    // A color index proposes centers by aligning one known neighbor. A fixed,
                    // distributed scan also considers source neighborhoods outside that color bin.
                    var anchor = context[0];
                    byte[] anchorBytes = anchor.Rgba < 0 ? reconstruction : source.Rgba;
                    int index = anchor.Rgba < 0 ? ~anchor.Rgba : anchor.Rgba;
                    if (byColor.TryGetValue(ColorKey(anchorBytes, index), out var bucket))
                    {
                        int step = Math.Max(1, (bucket.Length + 31) / 32);
                        for (int i = cell % step; i < bucket.Length && lowest > 0; i += step)
                            Consider(bucket[i] % width - anchor.X, bucket[i] / width - anchor.Y);
                    }
                    int interval = Math.Max(1, (corpus.Count + 63) / 64);
                    for (int i = cell % interval; i < corpus.Count && lowest > 0; i += interval)
                        Consider(corpus[i] % width, corpus[i] / width);
                }

                var sample = source.Rgba.AsSpan(best * 4, 4);
                var output = reconstruction.AsSpan(cell * 4, 4);
                if (!sample.SequenceEqual(output)) { sample.CopyTo(output); changed = true; }

                void Consider(int sx, int sy)
                {
                    if (!Allowed(sx, sy)) return;
                    int cost = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var neighbor = context[i];
                        int nx = sx + neighbor.X, ny = sy + neighbor.Y;
                        if (!Allowed(nx, ny)) cost += 3072;
                        else
                        {
                            int input = (ny * width + nx) * 4;
                            byte[] reference = neighbor.Rgba < 0 ? reconstruction : source.Rgba;
                            int other = neighbor.Rgba < 0 ? ~neighbor.Rgba : neighbor.Rgba;
                            cost += Difference[Math.Abs(source.Rgba[input] - reference[other])] +
                                Difference[Math.Abs(source.Rgba[input + 1] - reference[other + 1])] +
                                Difference[Math.Abs(source.Rgba[input + 2] - reference[other + 2])];
                        }
                        if (cost >= lowest) return;
                    }
                    lowest = cost;
                    best = sy * width + sx;
                }
            }
            if (!changed) break;
        }
        return new(new Pixels(target.Width, target.Height, reconstruction), target.X, target.Y);

        int ReadContext(int x, int y)
        {
            int count = 0;
            foreach (var offset in ContextOffsets)
            {
                int nx = x + offset.X, ny = y + offset.Y;
                if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;
                int sample;
                if (document.Selected(nx, ny))
                {
                    int filled = ((ny - target.Y) * target.Width + nx - target.X) * 4;
                    if (reconstruction[filled + 3] == 0) continue;
                    sample = ~filled;
                }
                else
                {
                    sample = (ny * width + nx) * 4;
                    if (source.Rgba[sample + 3] == 0) continue;
                }
                context[count++] = new(offset.X, offset.Y, sample);
                if (count == context.Length) break;
            }
            return count;
        }
    }

    private static int ColorKey(byte[] rgba, int i) =>
        (rgba[i] >> 4) << 8 | (rgba[i + 1] >> 4) << 4 | rgba[i + 2] >> 4;

    private static List<int> FillOrder(Document document, Area area)
    {
        // Seed the boundary and advance the queue inward so each new pixel can use earlier results.
        var visited = new bool[area.Width * area.Height];
        var order = new List<int>();
        void Enqueue(int x, int y)
        {
            if ((uint)x >= (uint)area.Width || (uint)y >= (uint)area.Height) return;
            int cell = y * area.Width + x;
            if (visited[cell] || !document.Selected(area.X + x, area.Y + y)) return;
            visited[cell] = true;
            order.Add(cell);
        }

        for (int y = 0; y < area.Height; y++)
        for (int x = 0; x < area.Width; x++)
        {
            int px = area.X + x, py = area.Y + y;
            if (!document.Selected(px - 1, py) || !document.Selected(px + 1, py) ||
                !document.Selected(px, py - 1) || !document.Selected(px, py + 1))
                Enqueue(x, y);
        }
        for (int i = 0; i < order.Count; i++)
        {
            int x = order[i] % area.Width, y = order[i] / area.Width;
            Enqueue(x - 1, y);
            Enqueue(x + 1, y);
            Enqueue(x, y - 1);
            Enqueue(x, y + 1);
        }
        return order;
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
