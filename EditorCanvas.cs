using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chop.Core;

namespace PhotoChop;

internal sealed class EditorCanvas : FrameworkElement
{
    private static readonly Brush workspace = Frozen(new SolidColorBrush(Color.FromRgb(23, 25, 30)));
    private Brush checker = CreateChecker(8);
    private int checkerSize = 8;
    public bool ShowPixelGrid { get; set; } = true;
    public Point? BrushPointer { get; set; }
    public double BrushDiameter { get; set; }
    public bool EllipticalMarquee { get; set; }
    public PointD[]? ShapePreview { get; set; }
    public bool ClosedShapePreview { get; set; }
    private static readonly Pen pixelGrid = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(100, 235, 235, 235)), 1));
    private static readonly Pen brushLight = Frozen(new Pen(Brushes.White, 1));
    private static readonly Pen brushDark = Frozen(new Pen(Brushes.Black, 2.5));

    public void SetCheckerSize(int size)
    {
        size = Math.Clamp(size, 2, 64);
        if (size == checkerSize) return;
        checkerSize = size;
        checker = CreateChecker(size);
    }
    private static readonly Pen border = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(110, 119, 136)), 1));
    private static readonly Pen selectionLight = Frozen(new Pen(Brushes.White, 3));
    private static readonly Pen selectionDark = Frozen(new Pen(Brushes.Black, 2) { DashStyle = DashStyles.Dash });
    public BitmapSource? Frame { get; set; }
    public double Zoom { get; set; } = 1;
    public Point Offset { get; set; }
    private PixelSelection? outlineSource;
    private Geometry? outline;
    public PixelSelection? SelectionMask { get; set; }
    private Pen guidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(72, 205, 233)), 1));
    private Pen ghostGuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(127, 231, 252)), 1)
        { DashStyle = DashStyles.Dash });
    private Color? currentGuideColor;
    public void SetGuideColor(Color color)
    {
        if (currentGuideColor == color) return;
        currentGuideColor = color;
        var brush = Frozen(new SolidColorBrush(color));
        guidePen = Frozen(new Pen(brush, 1));
        ghostGuidePen = Frozen(new Pen(brush, 1) { DashStyle = DashStyles.Dash });
    }

    public PathContour[]? PathOverlay { get; set; }
    public HashSet<(int Contour, int Node)> SelectedPathNodes { get; set; } = [];
    public IReadOnlyList<PointD>? LassoPath { get; set; }
    public GradientArgs? GradientGuide { get; set; }
    public PointD[]? TransformBox { get; set; }
    public PointD? TransformOrigin { get; set; }
    public bool ShowTransformEdges { get; set; }

    public IReadOnlyList<Guide> Guides { get; set; } = [];
    public Guide? GhostGuide { get; set; }
    public string? DraggedGuide { get; set; }
    public Area? Selection { get; set; }
    public Rect? Marquee { get; set; }
    internal sealed record PaintOverlay(StrokeArgs Stroke, Area? Clip);
    public List<PaintOverlay> PendingPaint { get; } = [];
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
        if (ShowPixelGrid && Zoom >= 8)
        {
            var visible = Rect.Intersect(imageArea, new Rect(RenderSize));
            if (!visible.IsEmpty)
            {
                for (int x = (int)Math.Ceiling((visible.Left - Offset.X) / Zoom); x <= (visible.Right - Offset.X) / Zoom; x++)
                    dc.DrawLine(pixelGrid, new Point(Offset.X + x * Zoom, visible.Top), new Point(Offset.X + x * Zoom, visible.Bottom));
                for (int y = (int)Math.Ceiling((visible.Top - Offset.Y) / Zoom); y <= (visible.Bottom - Offset.Y) / Zoom; y++)
                    dc.DrawLine(pixelGrid, new Point(visible.Left, Offset.Y + y * Zoom), new Point(visible.Right, Offset.Y + y * Zoom));
            }
        }
        foreach (var pending in PendingPaint)
            DrawStroke(dc, imageArea, pending.Stroke.Points, pending.Stroke.Size, pending.Stroke.Opacity,
                pending.Stroke.Color, pending.Stroke.Erase, pending.Clip);
        if (Stroke is { Count: > 0 } points)
            DrawStroke(dc, imageArea, points, StrokeSize, StrokeOpacity, StrokeColor, Erasing, Selection);
        if (Selection is { } selection)
        {
            if (SelectionMask is { } mask)
            {
                if (!ReferenceEquals(outlineSource, mask))
                {
                    outlineSource = mask;
                    outline = BuildOutline(mask);
                }
                dc.PushTransform(new MatrixTransform(Zoom, 0, 0, Zoom, Offset.X + selection.X * Zoom, Offset.Y + selection.Y * Zoom));
                var light = new Pen(Brushes.White, 3 / Zoom);
                var dark = new Pen(Brushes.Black, 2 / Zoom) { DashStyle = DashStyles.Dash };
                dc.DrawGeometry(null, light, outline);
                dc.DrawGeometry(null, dark, outline);
                dc.Pop();
            }
            else DrawOutline(dc, ViewRect(new Rect(selection.X, selection.Y, selection.Width, selection.Height)));
        }
        if (ShapePreview is { Length: > 1 } shapePoints)
        {
            var path = new StreamGeometry();
            using (var writer = path.Open())
            {
                writer.BeginFigure(ToView(shapePoints[0]), false, ClosedShapePreview);
                writer.PolyLineTo(shapePoints.Skip(1).Select(ToView).ToArray(), true, false);
            }
            dc.DrawGeometry(null, selectionLight, path);
            dc.DrawGeometry(null, selectionDark, path);
        }
        else if (Marquee is { } marquee)
        {
            var r = ViewRect(marquee);
            if (EllipticalMarquee)
            {
                var center = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
                dc.DrawEllipse(null, selectionLight, center, r.Width / 2, r.Height / 2);
                dc.DrawEllipse(null, selectionDark, center, r.Width / 2, r.Height / 2);
            }
            else DrawOutline(dc, r);
        }
        dc.Pop();
        if (BrushPointer is { } pointer && MouseCursorVisible())
        {
            double radius = BrushDiameter * Zoom / 2;
            foreach (var pen in new[] { brushDark, brushLight })
            {
                dc.DrawEllipse(null, pen, pointer, radius, radius);
                double arm = Math.Max(5, radius);
                dc.DrawLine(pen, pointer - new Vector(arm, 0), pointer + new Vector(arm, 0));
                dc.DrawLine(pen, pointer - new Vector(0, arm), pointer + new Vector(0, arm));
            }
        }
        foreach (var guide in Guides)
            if (guide.Id != DraggedGuide) DrawGuide(dc, guide, imageArea, guidePen);
        if (GhostGuide is { } ghost) DrawGuide(dc, ghost, imageArea, ghostGuidePen);
        if (GradientGuide is { } gradient)
        {
            var start = ToView(gradient.Start);
            var end = ToView(gradient.End);
            var line = end - start;
            var steps = new LinearGradientBrush();
            var stops = GradientRamp.Stops(gradient.StartColor, gradient.EndColor, gradient.Stops);
            foreach (var stop in stops)
                steps.GradientStops.Add(new System.Windows.Media.GradientStop(ToColor(stop.Color), stop.Position));
            if (line.Length > 0)
            {
                steps.MappingMode = BrushMappingMode.Absolute;
                steps.StartPoint = start;
                steps.EndPoint = end;
                dc.DrawLine(new Pen(Brushes.Black, 7), start, end);
                dc.DrawLine(new Pen(steps, 4), start, end);
                foreach (var stop in stops)
                {
                    var point = start + line * stop.Position;
                    dc.DrawEllipse(new SolidColorBrush(ToColor(stop.Color)), new Pen(Brushes.Black, 1), point, 4, 4);
                }
            }
        }
        if (PathOverlay is { } paths) DrawVectorPaths(dc, paths);
        if (LassoPath is { Count: > 1 } lasso)
        {
            for (int i = 1; i < lasso.Count; i++) dc.DrawLine(selectionLight, ToView(lasso[i - 1]), ToView(lasso[i]));
            dc.DrawLine(selectionDark, ToView(lasso[^1]), ToView(lasso[0]));
        }
        if (TransformBox is { Length: 4 } box)
        {
            var controlPoints = box.Select(ToView).ToArray();
            for (int i = 0; i < 4; i++)
            {
                var a = controlPoints[i]; var b = controlPoints[(i + 1) % 4];
                dc.DrawLine(selectionLight, a, b);
                dc.DrawRectangle(Brushes.White, border, new Rect(a.X - 4, a.Y - 4, 8, 8));
                if (ShowTransformEdges) dc.DrawRectangle(Brushes.White, border,
                    new Rect((a.X + b.X) / 2 - 4, (a.Y + b.Y) / 2 - 4, 8, 8));
            }
            if (TransformOrigin is { } origin)
            {
                var center = ToView(origin);
                dc.DrawEllipse(Brushes.Black, selectionLight, center, 6, 6);
                dc.DrawEllipse(Brushes.White, null, center, 2, 2);
            }
        }
    }

    private static readonly Pen pathPen = Frozen(new Pen(Brushes.DeepSkyBlue, 2));
    private static readonly Pen pathOutline = Frozen(new Pen(Brushes.Black, 4));
    private static readonly Pen pathHandlePen = Frozen(new Pen(Brushes.LightSkyBlue, 1));

    private static Color ToColor(string value)
    {
        uint color = Format.Color(value);
        return Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
    }

    private void DrawVectorPaths(DrawingContext dc, PathContour[] contours)
    {
        for (int c = 0; c < contours.Length; c++)
        {
            var contour = contours[c];
            var nodes = contour.Nodes;
            if (nodes.Length == 0) continue;
            var geometry = new StreamGeometry();
            using (var stream = geometry.Open())
            {
                stream.BeginFigure(ToView(nodes[0].Point), false, contour.Closed);
                int count = contour.Closed ? nodes.Length : nodes.Length - 1;
                for (int i = 0; i < count; i++)
                {
                    var a = nodes[i];
                    var b = nodes[(i + 1) % nodes.Length];
                    if (a.Outgoing is null && b.Incoming is null) stream.LineTo(ToView(b.Point), true, false);
                    else stream.BezierTo(ToView(a.Outgoing ?? a.Point), ToView(b.Incoming ?? b.Point), ToView(b.Point), true, false);
                }
            }
            dc.DrawGeometry(null, pathOutline, geometry);
            dc.DrawGeometry(null, pathPen, geometry);
            for (int n = 0; n < nodes.Length; n++)
            {
                var node = nodes[n];
                var anchor = ToView(node.Point);
                void Handle(PointD? point)
                {
                    if (point is not { } p) return;
                    var end = ToView(p);
                    dc.DrawLine(pathHandlePen, anchor, end);
                    dc.DrawEllipse(Brushes.White, pathPen, end, 3, 3);
                }
                Handle(node.Incoming);
                Handle(node.Outgoing);
                dc.DrawRectangle(SelectedPathNodes.Contains((c, n)) ? Brushes.DeepSkyBlue : Brushes.White,
                    pathPen, new Rect(anchor.X - 3, anchor.Y - 3, 6, 6));
            }
        }
    }

    private void DrawGuide(DrawingContext dc, Guide guide, Rect bounds, Pen pen)
    {
        if (guide.Axis == GuideAxis.Vertical)
        {
            double x = Offset.X + guide.Position * Zoom;
            dc.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
        else
        {
            double y = Offset.Y + guide.Position * Zoom;
            dc.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }
    }

    private void DrawStroke(DrawingContext dc, Rect imageArea, IReadOnlyList<PointD> points,
        double size, double opacity, string ink, bool erasing, Area? selection)
    {
        var clip = selection is { } area ? ViewRect(new Rect(area.X, area.Y, area.Width, area.Height)) : imageArea;
        dc.PushClip(new RectangleGeometry(clip));
        var color = (Color)ColorConverter.ConvertFromString(ink)!;
        var brush = new SolidColorBrush(erasing ? Color.FromArgb(160, 230, 230, 230) : color) { Opacity = opacity };
        var pen = new Pen(brush, size * Zoom) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
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

    private static Geometry BuildOutline(PixelSelection mask)
    {
        var geometry = new StreamGeometry();
        var bounds = mask.Bounds;
        using (var path = geometry.Open())
        {
            void Segment(int x, int y, int rx, int ry)
            {
                path.BeginFigure(new Point(x, y), false, false);
                path.LineTo(new Point(rx, ry), true, false);
            }
            bool Hit(int x, int y) => mask.Contains(x + bounds.X, y + bounds.Y);
            for (int y = 0; y <= bounds.Height; y++)
            {
                int start = -1;
                for (int x = 0; x <= bounds.Width; x++)
                {
                    bool edge = x < bounds.Width && Hit(x, y - 1) != Hit(x, y);
                    if (edge && start < 0) start = x;
                    if (!edge && start >= 0) { Segment(start, y, x, y); start = -1; }
                }
            }
            for (int x = 0; x <= bounds.Width; x++)
            {
                int start = -1;
                for (int y = 0; y <= bounds.Height; y++)
                {
                    bool edge = y < bounds.Height && Hit(x - 1, y) != Hit(x, y);
                    if (edge && start < 0) start = y;
                    if (!edge && start >= 0) { Segment(x, start, x, y); start = -1; }
                }
            }
        }
        return Frozen(geometry);
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

    private bool MouseCursorVisible() => Cursor == System.Windows.Input.Cursors.None && System.Windows.Input.Mouse.OverrideCursor is null;

    private static Brush CreateChecker(int size)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(170, 176, 185)), null, new Rect(0, 0, size * 2, size * 2));
            var square = new SolidColorBrush(Color.FromRgb(123, 130, 143));
            dc.DrawRectangle(square, null, new Rect(size, 0, size, size));
            dc.DrawRectangle(square, null, new Rect(0, size, size, size));
        }
        return Frozen(new DrawingBrush(group)
        {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size * 2, size * 2),
            Stretch = Stretch.None
        });
    }

    private static T Frozen<T>(T item) where T : Freezable
    {
        item.Freeze();
        return item;
    }
}
