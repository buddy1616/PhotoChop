using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chop.Core;

namespace PhotoChop;

internal sealed class EditorCanvas : FrameworkElement
{
    private static readonly Brush workspace = Frozen(new SolidColorBrush(Color.FromRgb(23, 25, 30)));
    private static readonly Brush checker = CreateChecker();
    private static readonly Pen border = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(110, 119, 136)), 1));
    private static readonly Pen selectionLight = Frozen(new Pen(Brushes.White, 1));
    private static readonly Pen selectionDark = Frozen(new Pen(Brushes.Black, 1) { DashStyle = DashStyles.Dash });
    public BitmapSource? Frame { get; set; }
    public double Zoom { get; set; } = 1;
    public Point Offset { get; set; }
    public Area? Selection { get; set; }
    public Rect? Marquee { get; set; }
    public IReadOnlyList<PointD>? Stroke { get; set; }
    public string StrokeColor { get; set; } = "#FFFFFFFF";
    public double StrokeSize { get; set; }
    public double StrokeOpacity { get; set; }
    public bool Erasing { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    public EditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public Point FromView(Point view) => new((view.X - Offset.X) / Zoom, (view.Y - Offset.Y) / Zoom);
    private Point ToView(PointD p) => new(Offset.X + p.X * Zoom, Offset.Y + p.Y * Zoom);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(workspace, null, new Rect(RenderSize));
        if (ImageWidth == 0 || ImageHeight == 0) return;
        var imageArea = new Rect(Offset.X, Offset.Y, ImageWidth * Zoom, ImageHeight * Zoom);
        dc.DrawRectangle(checker, border, imageArea);
        dc.PushClip(new RectangleGeometry(imageArea));
        if (Frame is { } frame) dc.DrawImage(frame, imageArea);
        if (Stroke is { Count: > 0 } points)
        {
            var clip = Selection is { } area ? ViewRect(new Rect(area.X, area.Y, area.Width, area.Height)) : imageArea;
            dc.PushClip(new RectangleGeometry(clip));
            var color = (Color)ColorConverter.ConvertFromString(StrokeColor)!;
            var brush = new SolidColorBrush(Erasing ? Color.FromArgb(160, 230, 230, 230) : color) { Opacity = StrokeOpacity };
            var pen = new Pen(brush, StrokeSize * Zoom) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            if (points.Count == 1)
                dc.DrawEllipse(brush, null, ToView(points[0]), pen.Thickness / 2, pen.Thickness / 2);
            else
            {
                var path = new StreamGeometry();
                using (var ctx = path.Open())
                {
                    ctx.BeginFigure(ToView(points[0]), false, false);
                    ctx.PolyLineTo(points.Skip(1).Select(ToView).ToArray(), true, false);
                }
                dc.DrawGeometry(null, pen, path);
            }
            dc.Pop();
        }
        if (Selection is { } selection) DrawOutline(dc, ViewRect(new Rect(selection.X, selection.Y, selection.Width, selection.Height)));
        if (Marquee is { } marquee) DrawOutline(dc, ViewRect(marquee));
        dc.Pop();
    }

    private Rect ViewRect(Rect rect) => new(ToView(new PointD(rect.X, rect.Y)),
        new Size(rect.Width * Zoom, rect.Height * Zoom));

    private static void DrawOutline(DrawingContext dc, Rect rect)
    {
        dc.DrawRectangle(null, selectionLight, rect);
        dc.DrawRectangle(null, selectionDark, rect);
    }

    public static BitmapSource Bitmap(Pixels pixels)
    {
        var bgra = (byte[])pixels.Rgba.Clone();
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return Frozen(BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, bgra, pixels.Width * 4));
    }

    private static Brush CreateChecker()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(170, 176, 185)), null, new Rect(0, 0, 16, 16));
            var square = new SolidColorBrush(Color.FromRgb(123, 130, 143));
            dc.DrawRectangle(square, null, new Rect(8, 0, 8, 8));
            dc.DrawRectangle(square, null, new Rect(0, 8, 8, 8));
        }
        return Frozen(new DrawingBrush(group)
        {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16),
            Stretch = Stretch.None
        });
    }

    private static T Frozen<T>(T item) where T : Freezable
    {
        item.Freeze();
        return item;
    }
}
