using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;
using Chop.Rendering;
using SkiaSharp;

namespace PhotoChop;

public partial class MainWindow
{
    private string? gesture;
    private string? moveId;
    private Chop.Core.Transform? moveOriginal;
    private Chop.Core.Transform? movePreview;
    private Document? preview;
    private Command? pencilPreview;
    private object? pencilGesture;
    private SKMatrix moveParentInverse = SKMatrix.Identity;
    private Point gestureStart;
    private Point panOrigin;
    private enum MarqueeStyle { Normal, FixedRatio, FixedSize }
    private MarqueeStyle marqueeStyle;
    private double marqueeWidth = 1;
    private double marqueeHeight = 1;
    private double fixedWidth = 100;
    private double fixedHeight = 100;
    private Area? selectionOrigin;
    private Chop.Core.SelectionMode selectionMode;
    private PointD? selectionDragDelta;
    private readonly List<PointD> stroke = [];
    private readonly List<PointD> lassoPoints = [];
    private int bucketTolerance;
    private bool polygonalLasso;
    private bool polygonDrawing;
    private int wandTolerance;
    private Chop.Core.SelectionMode ModifierSelectionMode() => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ?
        Chop.Core.SelectionMode.Subtract : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ?
        Chop.Core.SelectionMode.Add : Chop.Core.SelectionMode.Replace;

    private void HookCanvas(EditorCanvas view)
    {
        view.MouseWheel += (_, e) =>
        {
            if (gesture is not null || guideDrag is not null) return;
            if (sessions.FirstOrDefault(session => ReferenceEquals(session.View, view)) is { } hovered) Activate(hovered);
            if (active is null) return;
            var modifiers = Keyboard.Modifiers;
            double scroll = e.Delta / 120.0 * 64;
            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                active.Offset += new Vector(scroll, 0);
                UpdateView();
            }
            else if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                active.Offset += new Vector(0, scroll);
                UpdateView();
            }
            else ZoomBy(Math.Pow(1.15, e.Delta / 120.0), e.GetPosition(canvas));
            e.Handled = true;
        };
        view.MouseDown += CanvasDown;
        view.MouseMove += CanvasMove;
        view.PreviewMouseMove += (_, _) => RefreshMarqueeCursor();
        view.PreviewKeyDown += (_, _) => RefreshMarqueeCursor();
        view.PreviewKeyUp += (_, _) => RefreshMarqueeCursor();
        view.MouseUp += CanvasUp;
        view.LostMouseCapture += (_, _) =>
        {
            if (interactiveTransform is { } transform) transform.Start = null;
            CancelGesture();
            if (pathDrag is not null)
            {
                pathDrag = null;
                RefreshPathOverlay();
            }
        };
        view.MouseLeave += (_, _) => { if (gesture is null) { lastCursor = null; Say(); } };
    }

    private bool gradientFill;
    private Chop.Core.GradientStop[] fillGradient = DefaultRamp("#FF000000", "#FFFFFFFF");
    private GradientArgs GradientAt(Point end) => new(new(gestureStart.X, gestureStart.Y), new(end.X, end.Y), fillGradient[0].Color, fillGradient[^1].Color, fillGradient);

    private void CanvasDown(object sender, MouseButtonEventArgs e)
    {
        if (active?.Engine.Current is not { } doc || gesture is not null || guideDrag is not null) return;
        var view = e.GetPosition(canvas);
        var point = canvas.FromView(view);
        canvas.Focus();
        if (e.ChangedButton == MouseButton.Left && !Keyboard.IsKeyDown(Key.Space) &&
            sampleColorRange?.Invoke(active, point) == true)
        {
            e.Handled = true;
            return;
        }
        if (!Keyboard.IsKeyDown(Key.Space) && TransformMouseDown(point, e.ChangedButton))
        {
            e.Handled = true;
            return;
        }
        if (!Keyboard.IsKeyDown(Key.Space) && PathMouseDown(point, e.ChangedButton))
        {
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right && doc.Selection is not null &&
            tool is "Selection" or "Lasso" or "Magic Wand")
        {
            ShowPathContext(doc, null);
            e.Handled = true;
            return;
        }
        if (tool == "Move" && e.ChangedButton == MouseButton.Left && !Keyboard.IsKeyDown(Key.Space) && HitGuide(active, view) is { } guide)
        {
            BeginGuide(active, canvas, guide, existing: true);
            TrackGuide(view);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            gesture = "Pan";
            gestureStart = view;
            panOrigin = active.Offset;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            if (point.X < 0 || point.Y < 0 || point.X >= doc.Width || point.Y >= doc.Height) return;
            gestureStart = point;
            switch (tool)
            {
                case "Text": EditTextAt(doc, new PointD(point.X, point.Y)); return;
                case "Eyedropper": Sample(point); return;
                case "Transform": ScaleLayer(); return;
                case "Magic Wand":
                    RunLayer("selection.wand", new WandArgs(new PointD(point.X, point.Y), wandTolerance, ModifierSelectionMode()));
                    return;
                case "Lasso" when polygonalLasso:
                    if (e.ClickCount >= 2) { FinishPolygon(); return; }
                    if (!polygonDrawing) { polygonDrawing = true; selectionMode = ModifierSelectionMode(); }
                    if (lassoPoints.Count < 20000) lassoPoints.Add(new PointD(point.X, point.Y));
                    canvas.LassoPath = lassoPoints.ToArray();
                    canvas.InvalidateVisual();
                    e.Handled = true;
                    return;
                case "Bucket" when gradientFill:
                    if (ActiveLayer() is not { Kind: LayerKind.Raster } targetRaster || !Editable(doc, targetRaster.Id))
                    { Say("Select an unlocked raster layer to apply a gradient."); return; }
                    canvas.GradientGuide = GradientAt(point);
                    break;
                case "Bucket":
                    RunLayer("layer.bucket", new BucketArgs(new PointD(point.X, point.Y), foreground, bucketTolerance));
                    return;
                case "Lasso":
                    selectionMode = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? Chop.Core.SelectionMode.Subtract :
                        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Chop.Core.SelectionMode.Add : Chop.Core.SelectionMode.Replace;
                    lassoPoints.Add(new PointD(point.X, point.Y));
                    canvas.LassoPath = lassoPoints;
                    break;
                case "Selection":
                    selectionMode = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? Chop.Core.SelectionMode.Subtract
                        : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Chop.Core.SelectionMode.Add : Chop.Core.SelectionMode.Replace;
                    if (marqueeStyle != MarqueeStyle.FixedSize && selectionMode == Chop.Core.SelectionMode.Replace &&
                        doc.Selected((int)Math.Floor(point.X), (int)Math.Floor(point.Y)) && doc.Selection is { } selection &&
                        point.X >= selection.X && point.X < selection.X + selection.Width &&
                        point.Y >= selection.Y && point.Y < selection.Y + selection.Height)
                        selectionOrigin = selection;
                    else if (marqueeStyle != MarqueeStyle.FixedSize) gestureStart = SnapMarqueePoint(point);
                    break;
                case "Move":
                    if (ActiveLayer() is not { Locked: false } target || !Editable(doc, target.Id)) { Say("Select an unlocked layer to move."); return; }
                    if (target.Kind is not (LayerKind.Chop or LayerKind.Path) && doc.Selection is { } selected)
                    {
                        if (target.Kind != LayerKind.Raster || !doc.Selected((int)Math.Floor(point.X), (int)Math.Floor(point.Y)) || point.X < selected.X || point.X >= selected.X + selected.Width ||
                            point.Y < selected.Y || point.Y >= selected.Y + selected.Height) return;
                        selectionOrigin = selected;
                        moveId = target.Id;
                        gesture = "Move selection";
                        canvas.CaptureMouse();
                        e.Handled = true;
                        return;
                    }
                    moveId = target.Id;
                    moveOriginal = target.Transform;
                    moveParentInverse = SKMatrix.Identity;
                    var parent = doc.AllLayers().FirstOrDefault(layer => layer.Children.Any(child => child.Id == target.Id));
                    var parentWorld = parent is null ? SKMatrix.Identity :
                        SkiaRenderer.WorldMatrix(doc.Layers, parent.Id, SKMatrix.Identity) ?? SKMatrix.Identity;
                    var moveSpace = SKMatrix.Concat(parentWorld,
                        SKMatrix.CreateScale((float)target.Placement.ScaleX, (float)target.Placement.ScaleY));
                    if (moveSpace.TryInvert(out var inverted)) moveParentInverse = inverted;
                    break;
                case "Brush":
                case "Masker":
                case "Eraser":
                    if (ActiveLayer() is not { Locked: false } raster || !Editable(doc, raster.Id) ||
                        (tool != "Masker" && raster.Kind != LayerKind.Raster && !(active.MaskTarget == raster.Id && raster.Mask is not null)))
                    { Say("Select an unlocked raster layer to paint."); return; }
                    stroke.Add(new(point.X, point.Y));
                    if (tool == "Brush" && pencilMode)
                    {
                        pencilGesture = new object();
                        PreviewPencil(raster.Id);
                    }
                    else canvas.Stroke = stroke;
                    canvas.StrokeColor = foreground;
                    canvas.StrokeSize = brushSize;
                    canvas.StrokeOpacity = brushOpacity;
                    canvas.Erasing = tool == "Eraser";
                    break;
            }
            gesture = tool == "Bucket" && gradientFill ? "Gradient" : tool;
        }
        else return;
        canvas.CaptureMouse();
        canvas.InvalidateVisual();
        e.Handled = true;
    }

    private void CanvasMove(object sender, MouseEventArgs e)
    {
        if (active?.Engine.Current is not { } doc) return;
        var view = e.GetPosition(canvas);
        if (guideDrag?.Source == canvas)
        {
            TrackGuide(view);
            e.Handled = true;
            return;
        }
        if (tool == "Move" && gesture is null && sender is EditorCanvas hoveredView &&
            sessions.FirstOrDefault(session => ReferenceEquals(session.View, hoveredView)) is { } hoveredSession &&
            !IsSamplingCanvas(hoveredSession))
            hoveredView.Cursor = HitGuide(hoveredSession, e.GetPosition(hoveredView)) is { } hovered ? GuideCursor(hovered.Axis) : ToolCursor(tool);
        var point = canvas.FromView(view);
        if (sender == canvas && UpdatePathDrag(point)) { e.Handled = true; return; }
        if (polygonDrawing && gesture is null)
        {
            canvas.LassoPath = lassoPoints.Append(new PointD(point.X, point.Y)).ToArray();
            canvas.InvalidateVisual();
        }
        if (gesture != "Pan" && TransformMouseMove(point)) { e.Handled = true; return; }
        lastCursor = point;
        switch (gesture)
        {
            case "Gradient":
                canvas.GradientGuide = GradientAt(point);
                canvas.InvalidateVisual();
                break;
            case "Pan":
                active.Offset = panOrigin + (view - gestureStart);
                UpdateView();
                break;
            case "Lasso":
                if (lassoPoints.Count < 19999 && (point - new Point(lassoPoints[^1].X, lassoPoints[^1].Y)).Length * canvas.Zoom >= 2)
                    lassoPoints.Add(new PointD(point.X, point.Y));
                canvas.InvalidateVisual();
                break;
            case "Masker":
            case "Brush":
            case "Eraser":
                if (stroke.Count < 99999 && (point - new Point(stroke[^1].X, stroke[^1].Y)).Length >= .25)
                {
                    stroke.Add(new(point.X, point.Y));
                    if (pencilGesture is not null && active.ActiveId is { } paintId) PreviewPencil(paintId);
                }
                canvas.InvalidateVisual();
                break;
            case "Selection" when selectionOrigin is not null:
                var outline = GestureBounds(point);
                canvas.Selection = new Area((int)outline.X, (int)outline.Y, (int)outline.Width, (int)outline.Height);
                canvas.InvalidateVisual();
                break;
            case "Selection":
            case "Chop":
            case "Shape":
                canvas.Marquee = GestureBounds(point);
                canvas.InvalidateVisual();
                break;
            case "Move selection":
                var selectionDelta = point - gestureStart;
                selectionDragDelta = new(Math.Round(selectionDelta.X), Math.Round(selectionDelta.Y));
                UpdateCanvas();
                break;
            case "Move":
                var delta = point - gestureStart;
                var mapped = moveParentInverse.MapVector((float)delta.X, (float)delta.Y);
                movePreview = moveOriginal! with { X = Math.Round(moveOriginal!.X + mapped.X, 2),
                    Y = Math.Round(moveOriginal.Y + mapped.Y, 2) };
                preview = doc with { Layers = PreviewMove(doc.Layers, moveId!, movePreview) };
                UpdateCanvas();
                break;
        }
        Say();
    }

    private static Layer[] PreviewMove(Layer[] items, string id, Chop.Core.Transform transform) => items.Select(layer =>
        layer.Id == id ? layer.TranslatePlacement(new PointD(transform.X - layer.Transform.X, transform.Y - layer.Transform.Y))
        : layer.Children.Length == 0 ? layer : layer with { Children = PreviewMove(layer.Children, id, transform) }).ToArray();

    private void CanvasUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && FinishPathDrag(canvas.FromView(e.GetPosition(canvas))))
        {
            e.Handled = true;
            return;
        }
        if (TransformMouseUp(canvas.FromView(e.GetPosition(canvas)), e.ChangedButton))
        {
            e.Handled = true;
            return;
        }
        if (guideDrag?.Source == canvas && e.ChangedButton == MouseButton.Left)
        {
            FinishGuide(e.GetPosition(canvas));
            e.Handled = true;
            return;
        }
        if (gesture is null || active is null ||
            (gesture == "Pan" ? e.ChangedButton is not (MouseButton.Left or MouseButton.Middle) : e.ChangedButton != MouseButton.Left)) return;
        string completed = gesture;
        var end = canvas.FromView(e.GetPosition(canvas));
        var bounds = GestureBounds(end);
        int x = (int)Math.Floor(bounds.X), y = (int)Math.Floor(bounds.Y);
        var area = new Area(x, y, (int)Math.Ceiling(bounds.Right) - x, (int)Math.Ceiling(bounds.Bottom) - y);
        bool pencil = pencilGesture is not null;
        if (pencil && stroke.Count < 100000 && stroke[^1] != new PointD(end.X, end.Y))
            stroke.Add(new(end.X, end.Y));
        var points = stroke.ToArray();
        var lasso = completed == "Lasso" ? lassoPoints.Append(new PointD(end.X, end.Y)).ToArray() : [];
        var moved = movePreview;
        var original = moveOriginal;
        string? id = moveId;
        var motion = end - gestureStart;
        var gradient = completed == "Gradient" ? GradientAt(end) : null;
        var originalSelection = selectionOrigin;
        var mode = selectionMode;
        var moveSelection = new PointD(Math.Round(motion.X), Math.Round(motion.Y));
        bool mask = active.MaskTarget is { } maskId && ActiveLayer() is { Mask: not null } layer && layer.Id == maskId;
        CancelGesture(keepPencilFrame: pencil);
        switch (completed)
        {
            case "Gradient":
                if (gradient is not null && motion.LengthSquared > .01) RunLayer("layer.gradient", gradient);
                break;
            case "Lasso":
                if (lasso.Length >= 3) Run("selection.lasso", new LassoArgs(lasso, mode));
                break;
            case "Masker":
            case "Brush":
            case "Eraser":
                if (points.Length > 0 && active.ActiveId is { } paintTarget)
                {
                    var paint = new StrokeArgs(points, brushSize, hardness, brushOpacity, foreground, completed == "Eraser", Pencil: completed == "Brush" && pencilMode);
                    if (pencil)
                    {
                        active.KeepPencilFrame = true;
                        if (!Run(mask ? "mask.paintStroke" : "layer.paintStroke", paint, paintTarget))
                        {
                            active.KeepPencilFrame = false;
                            UpdateCanvas();
                        }
                        break;
                    }
                    var view = canvas;
                    var overlay = new EditorCanvas.PaintOverlay(paint, active.Engine.Current?.Selection);
                    view.PendingPaint.Add(overlay);
                    view.InvalidateVisual();
                    if (!Run(completed == "Masker" ? "mask.maskerStroke" : mask ? "mask.paintStroke" : "layer.paintStroke", paint, paintTarget))
                    {
                        view.PendingPaint.Remove(overlay);
                        view.InvalidateVisual();
                    }
                }
                break;
            case "Move selection":
                if (moveSelection != default) Run("layer.moveSelection", moveSelection, id);
                break;
            case "Move":
                if (moved is not null && original is not null && moved != original)
                    Run("layer.move", new PointD(moved.X - original.X, moved.Y - original.Y), id);
                break;
            case "Selection":
                if (originalSelection is { } previous)
                    Run("selection.translate", new PointD(area.X - previous.X, area.Y - previous.Y));
                else if (bounds.Width >= 1 && bounds.Height >= 1)
                    Run("selection.rectangle", new RectangleSelection(area, mode));
                else if (mode == Chop.Core.SelectionMode.Replace) Run<Area?>("document.selection", null);
                break;
            case "Chop":
                if (bounds.Width >= 1 && bounds.Height >= 1) { Run("document.chop", area); Fit(); }
                break;
            case "Shape":
                if (bounds.Width >= 1 && bounds.Height >= 1)
                    CreateLayer(shapeKind, new(x, y), area.Width, area.Height);
                break;
        }
        e.Handled = true;
    }

    private Rect GestureBounds(Point current)
    {
        if (selectionOrigin is not { } selection || active?.Engine.Current is not { } doc)
        {
            if (gesture != "Selection") return new Rect(gestureStart, current);
            if (marqueeStyle == MarqueeStyle.FixedSize)
                return SnapMarqueeEdges(new Rect(Math.Floor(current.X - fixedWidth / 2), Math.Floor(current.Y - fixedHeight / 2), fixedWidth, fixedHeight));
            if (marqueeStyle == MarqueeStyle.FixedRatio)
            {
                var motion = current - gestureStart;
                var direction = new Vector(motion.X < 0 ? -1 : 1, motion.Y < 0 ? -1 : 1);
                double limit = 32768 / Math.Max(marqueeWidth, marqueeHeight);
                double scale = Math.Min(Math.Max(Math.Abs(motion.X) / marqueeWidth, Math.Abs(motion.Y) / marqueeHeight), limit);
                scale = Math.Min(SnapRatioScale(gestureStart, direction, marqueeWidth, marqueeHeight, scale), limit);
                var end = gestureStart + new Vector(direction.X * marqueeWidth * scale, direction.Y * marqueeHeight * scale);
                return new Rect(gestureStart, end);
            }
            return new Rect(gestureStart, SnapMarqueePoint(current));
        }
        var delta = current - gestureStart;
        int x = (int)Math.Clamp(Math.Round(selection.X + delta.X), 0, doc.Width - selection.Width);
        int y = (int)Math.Clamp(Math.Round(selection.Y + delta.Y), 0, doc.Height - selection.Height);
        var snapped = SnapMarqueeEdges(new Rect(x, y, selection.Width, selection.Height));
        return new Rect(Math.Clamp(snapped.X, 0, doc.Width - selection.Width),
            Math.Clamp(snapped.Y, 0, doc.Height - selection.Height), selection.Width, selection.Height);
    }

    private static bool Editable(Document document, string id)
    {
        var all = document.AllLayers().ToArray();
        for (string? target = id; target is not null;)
        {
            if (all.FirstOrDefault(layer => layer.Id == target) is not { Locked: false }) return false;
            target = all.FirstOrDefault(layer => layer.Children.Any(child => child.Id == target))?.Id;
        }
        return true;
    }

    private void PreviewPencil(string id)
    {
        pencilPreview = Command.Of(active?.MaskTarget == id ? "mask.paintStroke" : "layer.paintStroke",
            new StrokeArgs(stroke.ToArray(), brushSize, hardness, brushOpacity, foreground, Erase: false, Pencil: true), id);
        UpdateCanvas();
    }

    private void CancelGesture(bool keepPencilFrame = false)
    {
        bool wasPencil = pencilGesture is not null;
        pencilGesture = null;
        pencilPreview = null;
        if (wasPencil && !keepPencilFrame) UpdateCanvas();
        canvas.GradientGuide = null;
        if (guideDrag is not null) CancelGuide();
        gesture = null;
        if (selectionOrigin is not null && active?.Engine.Current is { } document)
        {
            canvas.Selection = document.Selection;
            canvas.SelectionMask = document.SelectionMask;
        }
        selectionOrigin = null;
        moveId = null;
        moveOriginal = null;
        movePreview = null;
        if (preview is not null || selectionDragDelta is not null)
        {
            preview = null;
            selectionDragDelta = null;
            UpdateCanvas();
        }
        stroke.Clear();
        polygonDrawing = false;
        lassoPoints.Clear();
        canvas.LassoPath = null;
        canvas.Stroke = null;
        canvas.Marquee = null;
        if (canvas.IsMouseCaptured) canvas.ReleaseMouseCapture();
        canvas.InvalidateVisual();
    }

    private void Sample(Point point)
    {
        if (active?.Composite is not { } pixels || active.Rendered != active.Engine.Current) return;
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || x >= pixels.Width || y < 0 || y >= pixels.Height) return;
        int i = (y * pixels.Width + x) * 4;
        var bytes = pixels.Rgba;
        foreground = $"#{bytes[i + 3]:X2}{bytes[i]:X2}{bytes[i + 1]:X2}{bytes[i + 2]:X2}";
        RefreshTools();
    }

    private void ZoomBy(double factor, Point? anchor = null)
    {
        if (active is null) return;
        var pivot = anchor ?? new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
        var pixel = canvas.FromView(pivot);
        active.Zoom = Math.Clamp(active.Zoom * factor, .01, 64);
        active.Offset = new(pivot.X - pixel.X * active.Zoom, pivot.Y - pixel.Y * active.Zoom);
        UpdateView();
    }

    private void Fit()
    {
        if (active?.Engine.Current is not { } doc || canvas.ActualWidth < 1 || canvas.ActualHeight < 1) return;
        active.NeedsFit = false;
        active.Zoom = Math.Clamp(Math.Min((canvas.ActualWidth - 36) / doc.Width, (canvas.ActualHeight - 36) / doc.Height), .01, 64);
        Center(doc);
    }

    private void ActualPixels()
    {
        if (active?.Engine.Current is not { } doc) return;
        active.Zoom = 1 / VisualTreeHelper.GetDpi(canvas).DpiScaleX;
        Center(doc);
    }

    private void Center(Document doc)
    {
        active!.Offset = new((canvas.ActualWidth - doc.Width * active.Zoom) / 2, (canvas.ActualHeight - doc.Height * active.Zoom) / 2);
        UpdateView();
    }

    private void UpdateView()
    {
        if (active is null) return;
        canvas.Zoom = active.Zoom;
        canvas.Offset = active.Offset;
        UpdateScrollBars(active);
        canvas.InvalidateVisual();
        RefreshNavigation();
        Say();
    }

    private void UpdateScrollBars(Session session)
    {
        if (session.Engine.Current is { } doc)
        {
            session.Viewport.UpdateBars(session.Offset, session.Zoom, doc.Width, doc.Height);
            UpdateRulers(session);
        }
    }

    private void FinishPolygon()
    {
        var points = lassoPoints.ToArray();
        var mode = selectionMode;
        CancelGesture();
        if (points.Length >= 3) Run("selection.lasso", new LassoArgs(points, mode));
    }

    private void SetTool(string name)
    {
        CancelTransform();
        CancelPathEditing();
        CancelGesture();
        tool = name;
        if (active?.VectorMaskTarget is not null && name is not ("Pen" or "Path Select")) FinishVectorMaskEditing();
        foreach (var session in sessions)
            session.View.Cursor = IsSamplingCanvas(session) ? ToolCursors.Eyedropper : ToolCursor(name);
        RefreshTools();
        RefreshPathOverlay();
    }

    private void RefreshMarqueeCursor()
    {
        if (tool is not ("Selection" or "Lasso" or "Magic Wand") || interactiveTransform is not null) return;
        foreach (var session in sessions)
            session.View.Cursor = IsSamplingCanvas(session) ? ToolCursors.Eyedropper : ToolCursor(tool);
    }

    private static Cursor ToolCursor(string name) => name switch
    {
        "Move" => Cursors.SizeAll, "Brush" => Cursors.Pen, "Eraser" => ToolCursors.Eraser, "Masker" => ToolCursors.Masker,
        "Bucket" => ToolCursors.Bucket, "Pen" => Cursors.Pen, "Path Select" => Cursors.Arrow,
        "Magic Wand" => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? ToolCursors.WandSubtract :
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? ToolCursors.WandAdd : ToolCursors.Wand,
        "Selection" or "Lasso" => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? ToolCursors.SelectionSubtract :
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? ToolCursors.SelectionAdd : Cursors.Cross, "Chop" => Cursors.SizeNWSE, "Text" => Cursors.IBeam,
        "Shape" => Cursors.SizeNESW, "Eyedropper" => ToolCursors.Eyedropper,
        "Transform" => Cursors.ScrollAll, _ => Cursors.Arrow
    };

    private bool pencilMode;
    private static readonly (string Title, string[] Tools)[] ToolSections =
    [
        ("Painting Tools", ["Brush", "Eraser", "Masker", "Bucket", "Eyedropper"]),
        ("Selection Tools", ["Selection", "Lasso", "Magic Wand"]),
        ("Vector Tools", ["Pen", "Path Select", "Shape", "Text"]),
        ("Other Tools", ["Move", "Transform", "Chop"])
    ];
    private IEnumerable<string> OrderedTools() => (preferences.ToolOrder ?? ToolSections.SelectMany(section => section.Tools).ToArray())
        .Concat(ToolNames).Where(ToolNames.Contains).Distinct();

    private static string ToolLabel(string name) => name switch
    {
        "Selection" => "Marquee", "Bucket" => "Fill", _ => name
    };

    private static (string Icon, string? Key) ToolPresentation(string name) => name switch
    {
        "Brush" => ("✎", "B"), "Eraser" => ("eraser", "E"), "Masker" => ("masker", null),
        "Bucket" => ("bucket", "G"), "Eyedropper" => ("eyedropper", "I"),
        "Selection" => ("⬚", "M"), "Lasso" => ("⤺", "L"), "Magic Wand" => ("wand", "W"),
        "Pen" => ("✒", "P"), "Path Select" => ("➚", "A"), "Shape" => ("△", null),
        "Text" => ("Ⅰ", "T"), "Move" => ("✥", "V"), "Transform" => ("⤢", null), "Chop" => ("⌗", null),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private void RefreshTools()
    {
        foreach (var section in toolPanes.Values) section.Children.Clear();
        var visible = preferences.Tools ?? ToolNames;
        foreach (var name in OrderedTools().Where(visible.Contains))
        {
            var (icon, key) = ToolPresentation(name);
            string label = ToolLabel(name);
            var button = Button("", () => SetTool(name), key is null ? label : $"{label} ({key})");
            button.Width = 27;
            button.Height = 27;
            button.Padding = new Thickness(2);
            button.Content = icon.Length == 1
                ? new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 16 }
                : new Image { Width = 21, Height = 21, Source = ToolCursors.Icon(icon) };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            if (name == tool) button.BorderBrush = new SolidColorBrush(Color.FromRgb(80, 167, 255));
            toolPanes[ToolSections.First(section => section.Tools.Contains(name)).Title].Children.Add(button);
        }

        toolColors.Children.Clear();
        toolColors.Children.Add(ColorButton("Foreground", foreground, value => foreground = value));
        toolColors.Children.Add(ColorButton("Background", background, value => background = value));
        toolColors.Children.Add(Button("⇄  Swap (X)", SwapColors, "Swap foreground and background colors"));
        ToolOptions.Children.Clear();
        if (interactiveTransform is { } transform) { TransformOptions(transform); return; }
        OptionLabel(ToolLabel(tool));
        if (active?.VectorMaskTarget is not null && (tool is "Pen" or "Path Select"))
            ToolOptions.Children.Add(Button("Finish Vector Mask Editing", FinishVectorMaskEditing));
        if (tool == "Bucket")
        {
            var fillModes = new ComboBox { ItemsSource = new[] { "Paint Bucket", "Gradient" }, SelectedIndex = gradientFill ? 1 : 0 };
            fillModes.SelectionChanged += (_, _) =>
            {
                CancelGesture();
                gradientFill = fillModes.SelectedIndex == 1;
                RefreshTools();
            };
            ToolOptions.Children.Add(fillModes);
            if (gradientFill)
            {
                ToolOptions.Children.Add(Button("Edit Gradient Stops…", () =>
                    fillGradient = EditGradientStops(EditorOwner, fillGradient) ?? fillGradient));
            }
            else NumberOption("Tolerance", 0, 255, bucketTolerance, value => bucketTolerance = (int)value);
        }
        if (tool == "Magic Wand") NumberOption("Tolerance", 0, 255, wandTolerance, value => wandTolerance = (int)value);
        if (tool == "Lasso")
        {
            OptionLabel("Mode");
            var modes = new ComboBox { ItemsSource = new[] { "Normal", "Polygonal" }, SelectedIndex = polygonalLasso ? 1 : 0 };
            modes.SelectionChanged += (_, _) => { CancelGesture(); polygonalLasso = modes.SelectedIndex == 1; };
            ToolOptions.Children.Add(modes);
        }
        if (tool == "Brush")
        {
            OptionLabel("Mode");
            var modes = new ComboBox { ItemsSource = new[] { "Brush", "Pencil" }, SelectedIndex = pencilMode ? 1 : 0 };
            modes.SelectionChanged += (_, _) => { CancelGesture(); pencilMode = modes.SelectedIndex == 1; RefreshTools(); };
            ToolOptions.Children.Add(modes);
        }
        if (tool is "Brush" or "Eraser" or "Masker")
        {
            NumberOption("Size", 1, 512, brushSize, value => brushSize = value);
            if (tool != "Brush" || !pencilMode) NumberOption("Hardness", 0, 1, hardness, value => hardness = value);
            OptionLabel("Opacity (%)");
            var opacity = StyleFields.Range(brushOpacity * 100, 0, 100);
            opacity.Slider.Width = 75;
            opacity.Slider.ValueChanged += (_, _) => brushOpacity = opacity.Slider.Value / 100;
            ToolOptions.Children.Add(opacity.Row);
        }
        if (tool == "Selection") AddMarqueeOptions();
        if (tool == "Shape")
        {
            var list = new ComboBox { ItemsSource = new[] { LayerKind.Rectangle, LayerKind.Ellipse }, SelectedItem = shapeKind };
            list.SelectionChanged += (_, _) => { if (list.SelectedItem is LayerKind kind) shapeKind = kind; };
            ToolOptions.Children.Add(list);
        }
        if (tool == "Transform")
        {
            ToolOptions.Children.Add(Button("Free Transform", () => BeginTransform(SpatialKind.FreeTransform)));
            ToolOptions.Children.Add(Button("Scale…", ScaleLayer));
            ToolOptions.Children.Add(Button("Position and Rotation…", EditTransform));
        }
    }

    private void AddMarqueeOptions()
    {
        OptionLabel("Style");
        var styles = new ComboBox { ItemsSource = new[] { "Normal", "Fixed Ratio", "Fixed Size" }, SelectedIndex = (int)marqueeStyle };
        styles.SelectionChanged += (_, _) =>
        {
            if (styles.SelectedIndex < 0) return;
            marqueeStyle = (MarqueeStyle)styles.SelectedIndex;
            RefreshTools();
        };
        ToolOptions.Children.Add(styles);
        if (marqueeStyle == MarqueeStyle.Normal) return;
        bool size = marqueeStyle == MarqueeStyle.FixedSize;
        AddNumber(size ? "Width (px)" : "Width ratio", size ? fixedWidth : marqueeWidth,
            value => { if (size) fixedWidth = value; else marqueeWidth = value; });
        AddNumber(size ? "Height (px)" : "Height ratio", size ? fixedHeight : marqueeHeight,
            value => { if (size) fixedHeight = value; else marqueeHeight = value; });

        void AddNumber(string label, double initial, Action<double> assign)
        {
            OptionLabel(label);
            var input = new TextBox { Text = initial.ToString(), Width = 66, Margin = new Thickness(3, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = label };
            NumericWheel.Enable(input, size ? 1 : .01, 16384, value => assign(size ? Math.Round(value) : value));
            ToolOptions.Children.Add(input);
        }
    }

    private void SwapColors()
    {
        (foreground, background) = (background, foreground);
        RefreshTools();
    }

    private Button ColorButton(string label, string value, Action<string> apply)
    {
        var button = StyleFields.Swatch(this, label + " color", value, color =>
        {
            apply(color);
            RefreshTools();
        });
        return button;
    }

    private void OptionLabel(string text) => ToolOptions.Children.Add(new TextBlock
        { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });

    private void NumberOption(string name, double min, double max, double initial, Action<double> changed)
    {
        OptionLabel(name);
        var field = new TextBox { Text = initial.ToString(max == 1 ? "0.00" : "0.##"), Width = 54, VerticalAlignment = VerticalAlignment.Center };
        NumericWheel.Enable(field, min, max, changed);
        ToolOptions.Children.Add(field);
    }

    private void ConfigureTools()
    {
        var dialog = new Window { Owner = this, Title = "Configure tools", Width = 730, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(12) };
        var sections = new Grid();
        foreach (var _ in ToolSections) sections.ColumnDefinitions.Add(new ColumnDefinition());
        var order = OrderedTools().ToArray();
        var visible = preferences.Tools ?? ToolNames;
        ListBox Section(int column, string title, IEnumerable<string> names)
        {
            var section = new StackPanel { Margin = new Thickness(5) };
            Grid.SetColumn(section, column);
            sections.Children.Add(section);
            section.Children.Add(new TextBlock { Text = title, Margin = new Thickness(2, 0, 0, 5) });
            var items = new ListBox { MinHeight = 175 };
            foreach (string name in names)
            {
                var check = new CheckBox { Content = ToolLabel(name), Tag = name, IsChecked = visible.Contains(name), Margin = new Thickness(3) };
                check.PreviewMouseLeftButtonDown += (_, _) => items.SelectedItem = check;
                items.Items.Add(check);
            }
            items.SelectedIndex = 0;
            section.Children.Add(items);
            var move = new StackPanel { Orientation = Orientation.Horizontal };
            void Reorder(int offset)
            {
                int current = items.SelectedIndex, target = current + offset;
                if (current < 0 || target < 0 || target >= items.Items.Count) return;
                var item = items.Items[current];
                items.Items.RemoveAt(current);
                items.Items.Insert(target, item);
                items.SelectedItem = item;
                items.ScrollIntoView(item);
            }
            move.Children.Add(Button("↑ Up", () => Reorder(-1)));
            move.Children.Add(Button("↓ Down", () => Reorder(1)));
            section.Children.Add(move);
            return items;
        }
        var toolLists = ToolSections.Select((section, index) =>
            Section(index, section.Title, order.Where(section.Tools.Contains))).ToArray();
        panel.Children.Add(sections);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = Button("Apply", () => dialog.DialogResult = true);
        ok.IsDefault = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(2) });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        var checks = toolLists.SelectMany(list => list.Items.Cast<CheckBox>()).ToArray();
        preferences.ToolOrder = checks.Select(check => (string)check.Tag).ToArray();
        preferences.Tools = checks.Where(check => check.IsChecked == true).Select(check => (string)check.Tag).ToArray();
        RefreshTools();
    }

    private void WorkspaceShortcuts(object sender, PreProcessInputEventArgs args)
    {
        if (args.StagingItem.Input is not KeyEventArgs e) return;
        if ((e.Key is Key.LeftAlt or Key.RightAlt || e.SystemKey is Key.LeftAlt or Key.RightAlt) &&
            (e.RoutedEvent == Keyboard.PreviewKeyDownEvent || e.RoutedEvent == Keyboard.PreviewKeyUpEvent))
            Dispatcher.BeginInvoke(new Action(Mouse.UpdateCursor), System.Windows.Threading.DispatcherPriority.Input);
        if (e.RoutedEvent != Keyboard.PreviewKeyDownEvent || e.Handled || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (!IsActive && !Application.Current.Windows.OfType<AvalonDock.Controls.LayoutFloatingWindowControl>().Any(host => host.IsActive))
            return;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox ||
            Menus.Items.OfType<MenuItem>().Any(menu => menu.IsSubmenuOpen)) return;
        string? shortcut = e.Key switch
        {
            Key.V => "Move", Key.B => "Brush", Key.E => "Eraser", Key.M => "Selection", Key.L => "Lasso",
            Key.P => "Pen", Key.A => "Path Select", Key.G => "Bucket", Key.W => "Magic Wand", Key.I => "Eyedropper", Key.T => "Text", _ => null
        };
        if (shortcut is null) return;
        e.Handled = true;
        SetTool(shortcut);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if ((tool is "Pen" or "Path Select") && (e.Key is Key.Escape or Key.Enter) && (pathDrag is not null || penLayer is not null))
        {
            if (e.Key == Key.Escape) { CancelPathEditing(); RefreshPathOverlay(); }
            else
            {
                if (pathDrag is not null) FinishPathDrag(canvas.FromView(Mouse.GetPosition(canvas)));
                EndCurrentPath();
            }
            e.Handled = true;
            return;
        }
        if (polygonDrawing && e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Enter) FinishPolygon(); else CancelGesture();
            e.Handled = true;
            return;
        }
        if (interactiveTransform is not null && e.Key is Key.Escape or Key.Enter)
        {
            if (e.Key == Key.Enter) ApplyTransform(); else CancelTransform();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && (gesture is not null || guideDrag is not null)) { CancelGesture(); e.Handled = true; return; }
        if (e.OriginalSource is TextBoxBase or PasswordBox) return;
        var modifiers = Keyboard.Modifiers;
        if (canvas.IsKeyboardFocusWithin && pathDrag is null && (tool is "Pen" or "Path Select") &&
            EditablePathLayer() is { Kind: LayerKind.Path } path)
        {
            if (e.Key == Key.A && modifiers == ModifierKeys.Control)
            {
                SelectAllPathNodes(path);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Delete && selectedNodes.Count > 0 && active?.Engine.Current is { } doc && Editable(doc, path.Id))
            {
                DeletePathNodes(path, selectedNodes);
                e.Handled = true;
                return;
            }
        }
        bool control = modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        Action? action = control ? e.Key switch
        {
            Key.C when !shift => CopyPixels, Key.V when !shift => PastePixels,
            Key.N => NewDocument, Key.O => OpenDocument, Key.S => () => Save(active, shift),
            Key.E when shift => ExportPng, Key.E => MergeDown, Key.W => () => CloseSession(active),
            Key.Z => () => Run("history.undo", 0), Key.Y => () => Run("history.redo", 0),
            Key.D => () => Run<Area?>("document.selection", null),
            Key.G when !shift => Group,
            Key.OemSemicolon when !shift => ToggleGuides,
            Key.R when !shift => ToggleRulers,
            Key.OemPlus or Key.Add => () => ZoomBy(1.25), Key.OemMinus or Key.Subtract => () => ZoomBy(.8),
            Key.T => () => BeginTransform(SpatialKind.FreeTransform),
            Key.D0 or Key.NumPad0 => Fit, Key.D1 or Key.NumPad1 => ActualPixels,
            _ => null
        } : null;
        if (action is null && modifiers == ModifierKeys.None)
        {
            if (e.Key == Key.X) action = SwapColors;
            if (e.Key == Key.Delete) action = () => RunLayer("layer.clearSelection", 0);
        }
        if (action is null) return;
        action();
        e.Handled = true;
    }
}
