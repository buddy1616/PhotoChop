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

    private void HookCanvas(EditorCanvas view)
    {
        view.MouseWheel += (_, e) =>
        {
            if (gesture is not null) return;
            if (sessions.FirstOrDefault(session => ReferenceEquals(session.View, view)) is { } hovered) Activate(hovered);
            if (active is null) return;
            ZoomBy(Math.Pow(1.15, e.Delta / 120.0), e.GetPosition(canvas));
            e.Handled = true;
        };
        view.MouseDown += CanvasDown;
        view.MouseMove += CanvasMove;
        view.MouseUp += CanvasUp;
        view.LostMouseCapture += (_, _) => CancelGesture();
        view.MouseLeave += (_, _) => { if (gesture is null) { lastCursor = null; Say(); } };
    }

    private void CanvasDown(object sender, MouseButtonEventArgs e)
    {
        if (active?.Engine.Current is not { } doc || gesture is not null) return;
        var view = e.GetPosition(canvas);
        var point = canvas.FromView(view);
        canvas.Focus();
        if (e.ChangedButton == MouseButton.Left && !Keyboard.IsKeyDown(Key.Space) &&
            sampleColorRange?.Invoke(active, point) == true)
        {
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
                case "Transform": EditTransform(); return;
                case "Selection":
                    selectionMode = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? Chop.Core.SelectionMode.Subtract
                        : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Chop.Core.SelectionMode.Add : Chop.Core.SelectionMode.Replace;
                    if (marqueeStyle != MarqueeStyle.FixedSize && selectionMode == Chop.Core.SelectionMode.Replace &&
                        doc.Selected((int)Math.Floor(point.X), (int)Math.Floor(point.Y)) && doc.Selection is { } selection &&
                        point.X >= selection.X && point.X < selection.X + selection.Width &&
                        point.Y >= selection.Y && point.Y < selection.Y + selection.Height)
                        selectionOrigin = selection;
                    break;
                case "Move":
                    if (ActiveLayer() is not { Locked: false } target || !Editable(doc, target.Id)) { Say("Select an unlocked layer to move."); return; }
                    if (doc.Selection is { } selected)
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
                    if (!target.TransformsEnabled) { Say("Enable transforms before moving this layer."); return; }
                    moveOriginal = target.Transform;
                    moveParentInverse = SKMatrix.Identity;
                    var parent = doc.AllLayers().FirstOrDefault(layer => layer.Children.Any(child => child.Id == target.Id));
                    if (parent is not null && SkiaRenderer.WorldMatrix(doc.Layers, parent.Id, SKMatrix.Identity) is { } world &&
                        world.TryInvert(out var inverted)) moveParentInverse = inverted;
                    break;
                case "Brush":
                case "Eraser":
                    if (ActiveLayer() is not { Locked: false } raster || !Editable(doc, raster.Id) ||
                        (raster.Kind != LayerKind.Raster && !(active.MaskTarget == raster.Id && raster.Mask is not null)))
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
        var point = canvas.FromView(view);
        lastCursor = point;
        switch (gesture)
        {
            case "Pan":
                active.Offset = panOrigin + (view - gestureStart);
                UpdateView();
                break;
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
        layer.Id == id ? layer with { Transform = transform }
        : layer.Children.Length == 0 ? layer : layer with { Children = PreviewMove(layer.Children, id, transform) }).ToArray();

    private void CanvasUp(object sender, MouseButtonEventArgs e)
    {
        if (gesture is null || active is null ||
            (gesture == "Pan" ? e.ChangedButton is not (MouseButton.Left or MouseButton.Middle) : e.ChangedButton != MouseButton.Left)) return;
        string completed = gesture;
        var end = canvas.FromView(e.GetPosition(canvas));
        var bounds = GestureBounds(end);
        int x = (int)Math.Floor(bounds.X), y = (int)Math.Floor(bounds.Y);
        var area = new Area(x, y, (int)Math.Ceiling(bounds.Right) - x, (int)Math.Ceiling(bounds.Bottom) - y);
        var points = stroke.ToArray();
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
            case "Brush":
            case "Eraser":
                if (points.Length > 0)
                    RunLayer(mask ? "mask.paintStroke" : "layer.paintStroke", new StrokeArgs(points, brushSize, hardness, brushOpacity, foreground, completed == "Eraser"));
                break;
            case "Move selection":
                if (moveSelection != default) Run("layer.moveSelection", moveSelection, id);
                break;
            case "Move":
                if (moved is not null && moved != original) Run("layer.transform", moved, id);
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
                return new Rect(Math.Floor(current.X - fixedWidth / 2), Math.Floor(current.Y - fixedHeight / 2), fixedWidth, fixedHeight);
            if (marqueeStyle == MarqueeStyle.FixedRatio)
            {
                var motion = current - gestureStart;
                double scale = Math.Min(Math.Max(Math.Abs(motion.X) / marqueeWidth, Math.Abs(motion.Y) / marqueeHeight),
                    32768 / Math.Max(marqueeWidth, marqueeHeight));
                var end = new Point(gestureStart.X + (motion.X < 0 ? -1 : 1) * marqueeWidth * scale,
                    gestureStart.Y + (motion.Y < 0 ? -1 : 1) * marqueeHeight * scale);
                return new Rect(gestureStart, end);
            }
            return new Rect(gestureStart, current);
        }
        var delta = current - gestureStart;
        int x = (int)Math.Clamp(Math.Round(selection.X + delta.X), 0, doc.Width - selection.Width);
        int y = (int)Math.Clamp(Math.Round(selection.Y + delta.Y), 0, doc.Height - selection.Height);
        return new Rect(x, y, selection.Width, selection.Height);
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

    private static void UpdateScrollBars(Session session)
    {
        if (session.Engine.Current is { } doc)
            session.Viewport.UpdateBars(session.Offset, session.Zoom, doc.Width, doc.Height);
    }

    private void SetTool(string name)
    {
        CancelGesture();
        tool = name;
        foreach (var session in sessions)
            session.View.Cursor = IsSamplingCanvas(session) ? ToolCursors.Eyedropper : ToolCursor(name);
        RefreshTools();
    }

    private static Cursor ToolCursor(string name) => name switch
    {
        "Move" => Cursors.SizeAll, "Brush" => Cursors.Pen, "Eraser" => ToolCursors.Eraser,
        "Selection" => Cursors.Cross, "Chop" => Cursors.SizeNWSE, "Text" => Cursors.IBeam,
        "Shape" => Cursors.SizeNESW, "Eyedropper" => ToolCursors.Eyedropper,
        "Transform" => Cursors.ScrollAll, _ => Cursors.Arrow
    };

    private void RefreshTools()
    {
        toolsPanel.Children.Clear();
        foreach (var name in preferences.Tools ?? ToolNames)
        {
            var button = Button(name, () => SetTool(name));
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            if (name == tool) button.BorderBrush = new SolidColorBrush(Color.FromRgb(80, 167, 255));
            toolsPanel.Children.Add(button);
        }

        toolColors.Children.Clear();
        toolColors.Children.Add(ColorButton("Foreground", foreground, value => foreground = value));
        toolColors.Children.Add(ColorButton("Background", background, value => background = value));
        toolColors.Children.Add(Button("⇄  Swap (X)", SwapColors, "Swap foreground and background colors"));
        ToolOptions.Children.Clear();
        OptionLabel(tool);
        if (tool is "Brush" or "Eraser")
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
        if (tool == "Transform") ToolOptions.Children.Add(Button("Edit transform…", EditTransform));
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
        var dialog = new Window { Owner = this, Title = "Configure tools", Width = 300, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(12) };
        foreach (var name in ToolNames)
            panel.Children.Add(new CheckBox { Content = name, IsChecked = (preferences.Tools ?? ToolNames).Contains(name), Margin = new Thickness(3) });
        var ok = Button("Apply", () => dialog.DialogResult = true);
        ok.IsDefault = true;
        panel.Children.Add(ok);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        preferences.Tools = panel.Children.OfType<CheckBox>().Where(box => box.IsChecked == true)
            .Select(box => (string)box.Content).ToArray();
        RefreshTools();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && gesture is not null) { CancelGesture(); e.Handled = true; return; }
        if (e.OriginalSource is TextBoxBase or PasswordBox) return;
        var modifiers = Keyboard.Modifiers;
        bool control = modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        Action? action = control ? e.Key switch
        {
            Key.N => NewDocument, Key.O => OpenDocument, Key.S => () => Save(active, shift),
            Key.E when shift => ExportPng, Key.W => () => CloseSession(active),
            Key.Z => () => Run("history.undo", 0), Key.Y => () => Run("history.redo", 0),
            Key.D => () => Run<Area?>("document.selection", null),
            Key.G when !shift => Group,
            Key.OemPlus or Key.Add => () => ZoomBy(1.25), Key.OemMinus or Key.Subtract => () => ZoomBy(.8),
            Key.D0 or Key.NumPad0 => Fit, Key.D1 or Key.NumPad1 => ActualPixels,
            _ => null
        } : null;
        if (action is null && modifiers == ModifierKeys.None)
        {
            if (e.Key == Key.X) action = SwapColors;
            if (e.Key == Key.Delete) action = () => RunLayer("layer.clearSelection", 0);
            string? shortcut = e.Key switch
            {
                Key.V => "Move", Key.B => "Brush", Key.E => "Eraser", Key.M => "Selection",
                Key.I => "Eyedropper", Key.T => "Text", _ => null
            };
            if (shortcut is not null) action = () => SetTool(shortcut);
        }
        if (action is null) return;
        action();
        e.Handled = true;
    }
}
