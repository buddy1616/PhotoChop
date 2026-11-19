using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private sealed class TransformInteraction(Session session, string id, SpatialKind kind, Area source)
    {
        public Session Session { get; } = session;
        public string Id { get; } = id;
        public SpatialKind Kind { get; } = kind;
        public Area Source { get; } = source;
        public Point[] Corners { get; set; } =
        [
            new(source.X, source.Y), new(source.X + source.Width, source.Y),
            new(source.X + source.Width, source.Y + source.Height), new(source.X, source.Y + source.Height)
        ];
        public Point Origin { get; set; } = new(source.X + source.Width / 2d, source.Y + source.Height / 2d);
        public bool Live { get; set; } = kind != SpatialKind.Skew;
        public bool Changed { get; set; }
        public Point Press { get; set; }
        public Point[]? Start { get; set; }
        public int Handle { get; set; }
        public int Axis { get; set; }
        public long LastPreview { get; set; }
        public Point Center => new(Corners.Average(p => p.X), Corners.Average(p => p.Y));
        public WarpArgs Args => new(Kind, Source, Corners.Select(p => new PointD(p.X, p.Y)).ToArray());
    }

    private TransformInteraction? interactiveTransform;

    private void BeginTransform(SpatialKind kind)
    {
        if (active?.Engine.Current is not { } doc || ActiveLayer() is not { } layer) return;
        if (layer.Kind == LayerKind.Effect || !Editable(doc, layer.Id) || (doc.Selection is not null && layer.Kind != LayerKind.Raster))
        {
            Say("Select an unlocked raster layer to transform selected pixels.");
            return;
        }
        CancelTransform();
        CancelGesture();
        Area source = new(0, 0, doc.Width, doc.Height);
        if (!Guard(() => source = doc.Selection ?? renderer.VisibleLayerBounds(doc, layer.Id) ?? source)) return;
        interactiveTransform = new(active, layer.Id, kind, source);
        ShowTransformBox();
        RefreshTools();
        canvas.Focus();
    }

    private void ShowTransformBox()
    {
        if (interactiveTransform is not { } state) return;
        var view = state.Session.View;
        view.TransformBox = state.Corners.Select(p => new PointD(p.X, p.Y)).ToArray();
        view.TransformOrigin = new PointD(state.Origin.X, state.Origin.Y);
        view.ShowTransformEdges = state.Kind == SpatialKind.Skew;
        view.InvalidateVisual();
    }

    private void TransformOptions(TransformInteraction state)
    {
        OptionLabel(state.Kind switch { SpatialKind.FreeTransform => "Free Transform", _ => state.Kind.ToString() });
        if (state.Kind == SpatialKind.Skew)
        {
            var live = new CheckBox { Content = "Live preview", IsChecked = state.Live, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0) };
            live.Click += (_, _) =>
            {
                state.Live = live.IsChecked == true;
                PreviewTransform(force: true);
            };
            ToolOptions.Children.Add(live);
        }
        ToolOptions.Children.Add(Button("Apply", ApplyTransform, "Apply transform (Enter)"));
        ToolOptions.Children.Add(Button("Cancel", CancelTransform, "Cancel transform (Esc)"));
    }

    private void PreviewTransform(bool force)
    {
        if (interactiveTransform is not { } state) return;
        if (!force && Stopwatch.GetElapsedTime(state.LastPreview).TotalMilliseconds < 70) return;
        state.LastPreview = Stopwatch.GetTimestamp();
        preview = null;
        if (state.Live && state.Changed)
        {
            try { preview = state.Session.Engine.Preview(Command.Of("layer.warp", state.Args, state.Id)); }
            catch (InvalidDataException e) { Say(e.Message); }
        }
        UpdateCanvas();
    }

    private void ApplyTransform()
    {
        if (interactiveTransform is not { } state) return;
        var args = state.Args;
        if (state.Changed && !Guard(() => Projective.FromQuad(args.Source, args.Corners))) return;
        CancelTransform();
        if (state.Changed) Run("layer.warp", args, state.Id);
    }

    private void CancelTransform()
    {
        if (interactiveTransform is not { } state) return;
        interactiveTransform = null;
        state.Session.View.TransformBox = null;
        state.Session.View.TransformOrigin = null;
        state.Session.View.Cursor = ToolCursor(tool);
        state.Session.View.InvalidateVisual();
        if (state.Session.View.IsMouseCaptured) state.Session.View.ReleaseMouseCapture();
        preview = null;
        RefreshTools();
        UpdateCanvas();
    }

    private static int TransformHit(TransformInteraction state, Point p)
    {
        double zoom = state.Session.Zoom;
        double Distance(Point a) => (a - p).Length * zoom;
        if (state.Kind != SpatialKind.Rotate)
        {
            for (int i = 0; i < 4; i++) if (Distance(state.Corners[i]) <= 7) return i;
            if (state.Kind == SpatialKind.Skew)
            {
                for (int i = 0; i < 4; i++) if (Distance(Middle(state.Corners[i], state.Corners[(i + 1) % 4])) <= 9) return 4 + i;
            }
        }
        if (state.Kind != SpatialKind.Skew && !InsideBox(state.Corners, p))
        {
            for (int i = 0; i < 4; i++)
            {
                var corner = state.Corners[i];
                double distance = Distance(corner);
                if (distance is < 21 or > 47) continue;
                var fromCorner = p - corner;
                if (Vector.Multiply(fromCorner, state.Corners[(i + 1) % 4] - corner) < 0 &&
                    Vector.Multiply(fromCorner, state.Corners[(i + 3) % 4] - corner) < 0) return 8;
            }
        }
        return -1;
    }

    private bool TransformMouseDown(Point point, MouseButton button)
    {
        if (interactiveTransform is not { } state) return false;
        if (button == MouseButton.Right)
        {
            if ((point - state.Origin).Length * state.Session.Zoom <= 9) state.Origin = state.Center;
            else if (InsideBox(state.Corners, point)) state.Origin = point;
            ShowTransformBox();
            return true;
        }
        if (button != MouseButton.Left) return false;
        int hit = TransformHit(state, point);
        if (hit < 0) return true;
        state.Press = point;
        state.Start = state.Corners.ToArray();
        state.Handle = hit;
        state.Axis = 0;
        state.Session.View.CaptureMouse();
        return true;
    }

    private bool TransformMouseMove(Point point)
    {
        if (interactiveTransform is not { } state) return false;
        if (state.Start is not { } start)
        {
            int hit = TransformHit(state, point);
            state.Session.View.Cursor = hit switch
            {
                < 0 => Cursors.Arrow, 8 => ToolCursors.Rotate,
                _ when state.Kind == SpatialKind.Skew => Cursors.SizeAll,
                0 or 2 => Cursors.SizeNWSE, _ => Cursors.SizeNESW
            };
            return true;
        }

        var delta = point - state.Press;
        Point[] result;
        if (state.Handle == 8)
        {
            double angle = Math.Atan2(point.Y - state.Origin.Y, point.X - state.Origin.X) -
                Math.Atan2(state.Press.Y - state.Origin.Y, state.Press.X - state.Origin.X);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            result = start.Select(p =>
            {
                var d = p - state.Origin;
                return state.Origin + new Vector(c * d.X - s * d.Y, s * d.X + c * d.Y);
            }).ToArray();
        }
        else if (state.Kind == SpatialKind.Skew)
        {
            result = start.ToArray();
            if (state.Handle >= 4)
            {
                int a = state.Handle - 4, b = (a + 1) % 4;
                var axis = Unit(start[b] - start[a]);
                var slide = axis * Vector.Multiply(delta, axis);
                result[a] += slide;
                result[b] += slide;
            }
            else
            {
                int i = state.Handle;
                var first = Unit(start[(i + 1) % 4] - start[i]);
                var second = Unit(start[(i + 3) % 4] - start[i]);
                if (state.Axis == 0 && delta.Length * state.Session.Zoom >= 3)
                    state.Axis = Math.Abs(Vector.Multiply(delta, first)) >= Math.Abs(Vector.Multiply(delta, second)) ? 1 : 2;
                var axis = state.Axis == 2 ? second : first;
                result[i] += axis * Vector.Multiply(delta, axis);
            }
        }
        else
        {
            var anchor = start[(state.Handle + 2) % 4];
            var x = Unit(start[1] - start[0]);
            var y = Unit(start[3] - start[0]);
            var initial = start[state.Handle] - anchor;
            var next = initial + delta;
            double sx = Math.Clamp(Vector.Multiply(next, x) / Vector.Multiply(initial, x), .01, 100);
            double sy = Math.Clamp(Vector.Multiply(next, y) / Vector.Multiply(initial, y), .01, 100);
            result = start.Select(p => anchor + x * (Vector.Multiply(p - anchor, x) * sx) +
                y * (Vector.Multiply(p - anchor, y) * sy)).ToArray();
        }
        try
        {
            Projective.FromQuad(state.Source, result.Select(p => new PointD(p.X, p.Y)).ToArray());
            state.Corners = result;
            state.Changed = true;
            ShowTransformBox();
            PreviewTransform(force: false);
        }
        catch (InvalidDataException) { }
        return true;
    }

    private bool TransformMouseUp(Point point, MouseButton button)
    {
        if (interactiveTransform is not { Start: not null } state || button != MouseButton.Left) return false;
        TransformMouseMove(point);
        state.Start = null;
        if (state.Session.View.IsMouseCaptured) state.Session.View.ReleaseMouseCapture();
        PreviewTransform(force: true);
        return true;
    }

    private static Point Middle(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    private static Vector Unit(Vector v) => v / v.Length;
    private static bool InsideBox(Point[] corners, Point point)
    {
        double sign = 0;
        for (int i = 0; i < corners.Length; i++)
        {
            double cross = Vector.CrossProduct(corners[(i + 1) % corners.Length] - corners[i], point - corners[i]);
            if (Math.Abs(cross) < .001) continue;
            if (sign != 0 && Math.Sign(cross) != sign) return false;
            sign = Math.Sign(cross);
        }
        return true;
    }
}
