using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Chop.Core;

namespace PhotoChop;

// Document-space overlay. The command engine applies the exact stroke to the layer on release.
internal sealed class BrushDragPreview(EditorCanvas view, Document document, StrokeArgs stroke)
{
    private const int TileSize = 64;
    private sealed class Tile(int x, int y)
    {
        public int X { get; } = x;
        public int Y { get; } = y;
        public byte[] Bgra { get; } = new byte[TileSize * TileSize * 4];
        public WriteableBitmap Bitmap { get; } = new(TileSize, TileSize, 96, 96, PixelFormats.Bgra32, null);
    }

    private readonly Dictionary<(int X, int Y), Tile> tiles = new();
    private readonly List<PointD> queued = [];
    private readonly HashSet<Tile> dirty = [];
    private readonly uint ink = Format.Color(stroke.Color);
    private PointD? previous;
    private double remaining = Math.Max(.5, stroke.Size / 6);
    private DispatcherTimer? timer;

    public void Add(PointD point)
    {
        queued.Add(point);
        if (timer is null)
        {
            timer = new DispatcherTimer(DispatcherPriority.Render, view.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            timer.Tick += (_, _) => Flush();
        }
        if (!timer.IsEnabled) timer.Start();
    }

    public void Flush(bool finish = false)
    {
        timer?.Stop();
        double spacing = Math.Max(.5, stroke.Size / 6);
        foreach (var point in queued)
        {
            if (previous is { } start)
            {
                double dx = point.X - start.X, dy = point.Y - start.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                double offset = remaining;
                for (; offset <= length; offset += spacing)
                    Stamp(new(start.X + dx * offset / length, start.Y + dy * offset / length));
                remaining = offset - length;
            }
            else Stamp(point);
            previous = point;
        }
        queued.Clear();
        if (finish && previous is { } end) Stamp(end);
        foreach (var tile in dirty)
            tile.Bitmap.WritePixels(new Int32Rect(0, 0, TileSize, TileSize), tile.Bgra, TileSize * 4, 0);
        if (dirty.Count > 0) view.InvalidateVisual();
        dirty.Clear();
    }

    public void Stop()
    {
        timer?.Stop();
        queued.Clear();
    }

    private void Stamp(PointD center)
    {
        if (stroke.Pencil) center = new(Math.Floor(center.X) + .5, Math.Floor(center.Y) + .5);
        double radius = stroke.Size / 2;
        int left = (int)Math.Clamp(Math.Floor(center.X - radius), 0, document.Width);
        int top = (int)Math.Clamp(Math.Floor(center.Y - radius), 0, document.Height);
        int right = (int)Math.Clamp(Math.Ceiling(center.X + radius), 0, document.Width);
        int bottom = (int)Math.Clamp(Math.Ceiling(center.Y + radius), 0, document.Height);
        double feather = Math.Max(.5, radius * (1 - stroke.Hardness));
        double maxAlpha = stroke.Opacity * (ink >> 24);
        if (maxAlpha <= 0) return;
        int peak = (int)Math.Round(maxAlpha);
        for (int ty = top / TileSize * TileSize; ty < bottom; ty += TileSize)
        for (int tx = left / TileSize * TileSize; tx < right; tx += TileSize)
        {
            tiles.TryGetValue((tx, ty), out var tile);
            for (int y = Math.Max(top, ty); y < Math.Min(bottom, ty + TileSize); y++)
            for (int x = Math.Max(left, tx); x < Math.Min(right, tx + TileSize); x++)
            {
                int i = ((y - ty) * TileSize + x - tx) * 4;
                if (tile is not null && tile.Bgra[i + 3] >= peak) continue;
                double selection = document.Selection is null ? 1 : document.SelectionAmount(x, y) / 255d;
                if (selection == 0) continue;
                double dx = x + .5 - center.X, dy = y + .5 - center.Y;
                double squared = dx * dx + dy * dy;
                if (squared > radius * radius) continue;
                byte alpha = (byte)Math.Round(maxAlpha * selection * (stroke.Pencil ? 1 : Math.Min(1, (radius - Math.Sqrt(squared)) / feather)));
                if (alpha == 0 || tile is not null && alpha <= tile.Bgra[i + 3]) continue;
                if (tile is null)
                {
                    tile = new(tx, ty);
                    tiles.Add((tx, ty), tile);
                }
                tile.Bgra[i] = (byte)ink;
                tile.Bgra[i + 1] = (byte)(ink >> 8);
                tile.Bgra[i + 2] = (byte)(ink >> 16);
                tile.Bgra[i + 3] = alpha;
                dirty.Add(tile);
            }
        }
    }

    public void Draw(DrawingContext context, Point offset, double zoom)
    {
        foreach (var tile in tiles.Values)
            context.DrawImage(tile.Bitmap, new Rect(offset.X + tile.X * zoom, offset.Y + tile.Y * zoom, TileSize * zoom, TileSize * zoom));
    }
}
