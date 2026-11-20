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
                    canvas.Stroke = stroke;
                    canvas.StrokeColor = foreground;
                    canvas.StrokeSize = brushSize;
                    canvas.StrokeOpacity = brushOpacity;
                    canvas.Erasing = tool == "Eraser";
                    break;
            }
            gesture = tool;
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
                    stroke.Add(new(point.X, point.Y));
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
        var points = stroke.ToArray();
        var lasso = completed == "Lasso" ? lassoPoints.Append(new PointD(end.X, end.Y)).ToArray() : [];
        var moved = movePreview;
        var original = moveOriginal;
        string? id = moveId;
        var motion = end - gestureStart;
        var originalSelection = selectionOrigin;
        var mode = selectionMode;
        var moveSelection = new PointD(Math.Round(motion.X), Math.Round(motion.Y));
        bool mask = active.MaskTarget is { } maskId && ActiveLayer() is { Mask: not null } layer && layer.Id == maskId;
        CancelGesture();
        switch (completed)
        {
            case "Lasso":
                if (lasso.Length >= 3) Run("selection.lasso", new LassoArgs(lasso, mode));
                break;
            case "Masker":
            case "Brush":
            case "Eraser":
                if (points.Length > 0 && active.ActiveId is { } paintTarget)
                {
                    var paint = new StrokeArgs(points, brushSize, hardness, brushOpacity, foreground, completed == "Eraser");
                    var view = canvas;
                    var overlay = new EditorCanvas.PaintOverlay(paint, active.Engine.Current?.Selection);
                    view.PendingPaint.Add(overlay);
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

    private void CancelGesture()
    {
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

    private static readonly string[] UpperTools = ["Move", "Brush", "Eraser", "Bucket", "Chop"];
    private IEnumerable<string> OrderedTools() => (preferences.ToolOrder ?? [.. UpperTools, .. ToolNames])
        .Concat(ToolNames).Where(ToolNames.Contains).Distinct();

    private void RefreshTools()
    {
        toolsPanel.Children.Clear();
        secondaryTools.Children.Clear();
        var visible = preferences.Tools ?? ToolNames;
        foreach (var name in OrderedTools().Where(visible.Contains))
        {
            var button = Button(name, () => SetTool(name));
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            if (name == tool) button.BorderBrush = new SolidColorBrush(Color.FromRgb(80, 167, 255));
            (UpperTools.Contains(name) ? toolsPanel : secondaryTools).Children.Add(button);
        }

        toolColors.Children.Clear();
        toolColors.Children.Add(ColorButton("Foreground", foreground, value => foreground = value));
        toolColors.Children.Add(ColorButton("Background", background, value => background = value));
        toolColors.Children.Add(Button("⇄  Swap (X)", SwapColors, "Swap foreground and background colors"));
        ToolOptions.Children.Clear();
        if (interactiveTransform is { } transform) { TransformOptions(transform); return; }
        OptionLabel(tool);
        if (tool == "Bucket") SliderOption("Tolerance", 0, 255, bucketTolerance, value => bucketTolerance = (int)value);
        if (tool == "Magic Wand") SliderOption("Tolerance", 0, 255, wandTolerance, value => wandTolerance = (int)value);
        if (tool == "Lasso")
        {
            OptionLabel("Mode");
            var modes = new ComboBox { ItemsSource = new[] { "Normal", "Polygonal" }, SelectedIndex = polygonalLasso ? 1 : 0 };
            modes.SelectionChanged += (_, _) => { CancelGesture(); polygonalLasso = modes.SelectedIndex == 1; };
            ToolOptions.Children.Add(modes);
        }
        if (tool is "Brush" or "Eraser" or "Masker")
        {
            SliderOption("Size", 1, 512, brushSize, value => brushSize = value);
            SliderOption("Hardness", 0, 1, hardness, value => hardness = value);
            SliderOption("Opacity", 0, 1, brushOpacity, value => brushOpacity = value);
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
            input.TextChanged += (_, _) =>
            {
                bool valid = double.TryParse(input.Text, out var number) && double.IsFinite(number) && number is >= .01 and <= 16384 &&
                    (!size || number >= 1);
                input.BorderBrush = valid ? SystemColors.ControlDarkBrush : Brushes.OrangeRed;
                if (valid) assign(size ? Math.Round(number) : number);
            };
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
        var button = Button(label, () =>
        {
            if (ColorPickerDialog.Show(this, label + " color", value) is { } color)
            {
                apply(color);
                RefreshTools();
            }
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 14, Height = 14, Margin = new Thickness(0, 0, 5, 0), Stroke = Brushes.Gray,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!)
        });
        row.Children.Add(new TextBlock { Text = label });
        button.Content = row;
        return button;
    }

    private void OptionLabel(string text) => ToolOptions.Children.Add(new TextBlock
        { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });

    private void SliderOption(string name, double min, double max, double initial, Action<double> changed)
    {
        OptionLabel(name);
        var slider = new Slider { Minimum = min, Maximum = max, Value = initial, Width = 86, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { Text = initial.ToString("0.##"), MinWidth = 30, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, _) => { changed(slider.Value); value.Text = slider.Value.ToString("0.##"); };
        ToolOptions.Children.Add(slider);
        ToolOptions.Children.Add(value);
    }

    private void ConfigureTools()
    {
        var dialog = new Window { Owner = this, Title = "Configure tools", Width = 475, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(12) };
        var sections = new Grid();
        sections.ColumnDefinitions.Add(new ColumnDefinition());
        sections.ColumnDefinitions.Add(new ColumnDefinition());
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
                var check = new CheckBox { Content = name, IsChecked = visible.Contains(name), Margin = new Thickness(3) };
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
        var upper = Section(0, "Upper section", order.Where(UpperTools.Contains));
        var lower = Section(1, "Lower section", order.Where(name => !UpperTools.Contains(name)));
        panel.Children.Add(sections);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = Button("Apply", () => dialog.DialogResult = true);
        ok.IsDefault = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(2) });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        var checks = upper.Items.Cast<CheckBox>().Concat(lower.Items.Cast<CheckBox>()).ToArray();
        preferences.ToolOrder = checks.Select(check => (string)check.Content).ToArray();
        preferences.Tools = checks.Where(check => check.IsChecked == true).Select(check => (string)check.Content).ToArray();
        RefreshTools();
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
            ActiveLayer() is { Kind: LayerKind.Path } path)
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
            string? shortcut = e.Key switch
            {
                Key.V => "Move", Key.B => "Brush", Key.E => "Eraser", Key.M => "Selection", Key.L => "Lasso",
                Key.P => "Pen", Key.A => "Path Select", Key.G => "Bucket", Key.W => "Magic Wand", Key.I => "Eyedropper", Key.T => "Text", _ => null
            };
            if (shortcut is not null) action = () => SetTool(shortcut);
        }
        if (action is null) return;
        action();
        e.Handled = true;
    }
}
