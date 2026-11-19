using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private const string MaskClipboardFormat = "CHOP.AlphaMask";
    private LayerDragAdorner? layerDragAdorner;
    private int historyContextIndex = -1;
    private sealed record DropHint(LayerRow Source, ReorderArgs Order, bool IntoGroup, Rect Bounds, double LineY);

    private void OpenMaskMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        var layer = button.DataContext is LayerRow row ? row.Layer : ActiveLayer();
        menu.Items.Clear();
        if (layer is null) { e.Handled = true; return; }
        if (layer.Mask is { } mask)
        {
            Item(menu, layer.MaskEnabled ? "Disable Mask" : "Enable Mask", () => Run("mask.enable", !layer.MaskEnabled, layer.Id));
            Item(menu, "Delete Mask", () => { if (Confirm("Delete this mask?")) Run("mask.remove", 0, layer.Id); });
            Item(menu, "Bake Mask", () =>
            {
                if (Confirm("Bake this mask into a raster layer? This also rasterizes its content and effects."))
                    Run("mask.bake", 0, layer.Id);
            });
            Item(menu, "Copy Mask", () => Guard(() => Clipboard.SetData(MaskClipboardFormat, JsonSerializer.Serialize(mask, Format.Json))));
        }
        Item(menu, "Paste Mask", () => Guard(() =>
        {
            if (Clipboard.GetData(MaskClipboardFormat) is not string json) return;
            if (JsonSerializer.Deserialize<AlphaMask>(json, Format.Json) is { } copied) Run("mask.paste", copied, layer.Id);
        })).IsEnabled = Clipboard.ContainsData(MaskClipboardFormat);
    }

    private void BuildLayerMenu()
    {
        var menu = new ContextMenu();
        layers.ContextMenu = menu;
        string? target = null;
        layers.PreviewMouseRightButtonDown += (_, e) => target = RowAt(e.OriginalSource)?.Layer.Id;
        layers.ContextMenuOpening += (_, e) =>
        {
            menu.Items.Clear();
            var layer = active?.Engine.Current?.AllLayers().FirstOrDefault(item => item.Id == target) ?? ActiveLayer();
            if (layer is null) { e.Handled = true; return; }
            string kind = layer.Kind == LayerKind.Group ? "Group" : "Layer";
            Item(menu, "Rename and Effects…", () => { SelectLayer(layer.Id); Rename(); });
            if (layer.Kind == LayerKind.Text) Item(menu, "Edit Text…", () => EditText(layer));
            Item(menu, "Duplicate", () => Duplicate(layer.Id));
            Item(menu, $"Bake {kind}…", () =>
            {
                if (Confirm($"Bake this {kind.ToLowerInvariant()} into a single raster layer? This applies its visible transforms, effects, and mask."))
                    Run("layer.bake", 0, layer.Id, layer.Id);
            }).IsEnabled = Editable(active!.Engine.Current!, layer.Id);
            Item(menu, "Delete", () => Run("layer.delete", 0, layer.Id));
        };
        menu.Closed += (_, _) => target = null;
    }

    private void SelectLayer(string id)
    {
        if (active is null) return;
        var all = active.Engine.Current?.AllLayers().ToArray() ?? [];
        for (string? child = id; child is not null;)
        {
            var parent = all.FirstOrDefault(layer => layer.Children.Any(item => item.Id == child));
            if (parent is null) break;
            active.Collapsed.Remove(parent.Id);
            child = parent.Id;
        }
        active.ActiveId = id;
        active.MaskTarget = active.MaskOnly = null;
        Refresh();
    }

    private static Button LayerIconButton(string icon, string name, Action click, bool glyph = true)
    {
        var button = Button(icon, click, name);
        button.MinWidth = 36;
        button.MinHeight = 28;
        button.FontSize = 15;
        if (glyph) button.FontFamily = new FontFamily("Segoe MDL2 Assets");
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    private Button LayerMaskButton()
    {
        var button = LayerIconButton("Mask", "Add a mask. Alt-click for grayscale view.", AddOrSelectMask, glyph: false);
        button.Content = new System.Windows.Shapes.Path
        {
            Width = 18, Height = 14, Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M 0,1 L 18,1 18,13 0,13 Z M 9,3 A 4,4 0 1 0 9.01,3 Z")
        };
        ((System.Windows.Shapes.Path)button.Content).SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
        return button;
    }

    private static void BindLayerDrop(Button button, string hint, Action<string> drop, bool copy = true)
    {
        button.AllowDrop = true;
        string name = (string)button.ToolTip;
        void Hover(DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(LayerRow)) ? (copy ? DragDropEffects.Copy : DragDropEffects.Move) : DragDropEffects.None;
            if (e.Effects != DragDropEffects.None)
            {
                button.BorderBrush = Brushes.DeepSkyBlue;
                button.ToolTip = hint;
            }
            e.Handled = true;
        }
        void Clear()
        {
            button.ClearValue(Control.BorderBrushProperty);
            button.ToolTip = name;
        }
        button.DragOver += (_, e) => Hover(e);
        button.DragLeave += (_, _) => Clear();
        button.Drop += (_, e) =>
        {
            Clear();
            if (e.Data.GetData(typeof(LayerRow)) is LayerRow row) drop(row.Layer.Id);
            e.Handled = true;
        };
    }

    private void ToggleRowTransforms(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayerRow row })
            Run("layer.transformsEnabled", !row.Layer.TransformsEnabled, row.Layer.Id);
    }

    private void BuildHistoryMenu()
    {
        var menu = new ContextMenu();
        history.ContextMenu = menu;
        history.AllowDrop = true;
        Point press = default;
        int pressedIndex = -1;
        int IndexAt(object original)
        {
            var item = original is DependencyObject source ? ItemsControl.ContainerFromElement(history, source) as ListBoxItem : null;
            return item is null ? -1 : history.ItemContainerGenerator.IndexFromContainer(item);
        }

        history.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressedIndex = IndexAt(e.OriginalSource);
            if (pressedIndex < 0) return;
            history.Focus();
            press = e.GetPosition(history);
            e.Handled = true;
        };
        history.PreviewMouseLeftButtonUp += (_, e) =>
        {
            int index = pressedIndex;
            pressedIndex = -1;
            if (index >= 0 && index == IndexAt(e.OriginalSource) && active?.Engine is { } engine)
            {
                Run("history.go", index + engine.HistoryBase);
                e.Handled = true;
            }
        };
        history.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || pressedIndex <= 0 ||
                (e.GetPosition(history) - press).Length < Math.Max(5, SystemParameters.MinimumVerticalDragDistance)) return;
            int index = pressedIndex;
            pressedIndex = -1;
            DragDrop.DoDragDrop(history, new DataObject("CHOP.HistoryIndex", index), DragDropEffects.Move);
            e.Handled = true;
        };
        history.DragOver += (_, e) =>
        {
            int target = IndexAt(e.OriginalSource);
            e.Effects = target > 0 && e.Data.GetData("CHOP.HistoryIndex") is int source && source != target
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        history.Drop += (_, e) =>
        {
            int target = IndexAt(e.OriginalSource);
            if (target > 0 && e.Data.GetData("CHOP.HistoryIndex") is int source && source != target && active?.Engine is { } engine)
                Run("history.change", new HistoryChange(engine.HistoryBase + source, engine.HistoryBase + target));
            e.Handled = true;
        };
        history.PreviewMouseRightButtonDown += (_, e) =>
        {
            var item = e.OriginalSource is DependencyObject source
                ? ItemsControl.ContainerFromElement(history, source) as ListBoxItem : null;
            historyContextIndex = item is null ? -1 : history.ItemContainerGenerator.IndexFromContainer(item);
        };
        history.ContextMenuOpening += (_, e) =>
        {
            menu.Items.Clear();
            if (active?.Engine is not { } engine || historyContextIndex <= 0 || historyContextIndex >= engine.History.Count)
            { e.Handled = true; return; }
            int index = historyContextIndex + engine.HistoryBase;
            var entry = engine.History[historyContextIndex];
            Item(menu, entry.Enabled ? "Disable Entry" : "Enable Entry",
                () => Run("history.change", new HistoryChange(index, Enabled: !entry.Enabled)));
            Item(menu, "Move Earlier", () => Run("history.change", new HistoryChange(index, index - 1))).IsEnabled = historyContextIndex > 1;
            Item(menu, "Move Later", () => Run("history.change", new HistoryChange(index, index + 1))).IsEnabled =
                historyContextIndex + 1 < engine.History.Count;
        };
    }

    private static IEnumerable<LogEntry> DisplayLog(Session session)
    {
        if (!session.HistoryEdited) return session.Log;
        var starts = session.Log.Where(entry => entry.Command.Name is "document.create" or "document.open");
        var timeline = session.Engine.History.Where(entry => entry.Command is not null).Select(entry =>
        {
            var record = session.Log.FirstOrDefault(log => ReferenceEquals(log.Command, entry.Command));
            return record is null ? null : record with { Success = entry.Enabled && entry.Error is null,
                Error = entry.Enabled ? entry.Error : "Disabled in History" };
        }).OfType<LogEntry>();
        return starts.Concat(timeline);
    }

    private void DragLayer(LayerRow row)
    {
        var layer = AdornerLayer.GetAdornerLayer(layers);
        var adorner = new LayerDragAdorner(layers, row.Layer.Name) { Pointer = Mouse.GetPosition(layers) };
        layerDragAdorner = adorner;
        layer?.Add(adorner);
        try { DragDrop.DoDragDrop(layers, new DataObject(typeof(LayerRow), row), DragDropEffects.Move | DragDropEffects.Copy); }
        finally { layer?.Remove(adorner); layerDragAdorner = null; }
    }

    private DropHint? GetDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LayerRow)) || e.Data.GetData(typeof(LayerRow)) is not LayerRow source) return null;
        var target = RowAt(e.OriginalSource);
        if (target?.Layer.Id == source.Layer.Id) return null;
        var item = target is null ? null : layers.ItemContainerGenerator.ContainerFromItem(target) as ListBoxItem;
        double fraction = item is null || item.ActualHeight == 0 ? 1 : e.GetPosition(item).Y / item.ActualHeight;
        bool into = target?.Layer.Kind == LayerKind.Group && fraction > .25 && fraction < .75;
        string? parent = into ? target!.Layer.Id : target?.Parent;
        if (parent == source.Layer.Id || new Document("", 1, 1) { Layers = source.Layer.Children }.AllLayers().Any(layer => layer.Id == parent))
            return null;
        var siblings = layers.Items.Cast<LayerRow>().Where(row => row.Parent == parent && row.Layer.Id != source.Layer.Id).ToArray();
        string? before = into ? null : target is null ? siblings.LastOrDefault()?.Layer.Id :
            fraction >= .5 ? target.Layer.Id : siblings.TakeWhile(row => row.Layer.Id != target.Layer.Id).LastOrDefault()?.Layer.Id;
        var origin = item?.TranslatePoint(new Point(), layers) ?? new Point(4, Math.Max(4, e.GetPosition(layers).Y));
        var bounds = new Rect(origin, item is null ? new Size(Math.Max(0, layers.ActualWidth - 8), 0) : item.RenderSize);
        return new DropHint(source, new ReorderArgs(parent, before), into, bounds, fraction >= .5 ? bounds.Bottom : bounds.Top);
    }

    private sealed class LayerDragAdorner : Adorner
    {
        private readonly string title;
        public Point Pointer { get; set; }
        public DropHint? Hint { get; set; }

        public LayerDragAdorner(UIElement owner, string title) : base(owner)
        {
            this.title = title;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var pen = new Pen(Brushes.DeepSkyBlue, 1.5) { DashStyle = DashStyles.Dot };
            if (Hint is { } hint)
            {
                if (hint.IntoGroup) dc.DrawRectangle(null, pen, hint.Bounds);
                else dc.DrawLine(pen, new Point(hint.Bounds.Left + 1, hint.LineY), new Point(hint.Bounds.Right - 1, hint.LineY));
            }
            var label = new FormattedText(title, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var ghost = new Rect(Pointer.X + 10, Pointer.Y + 10, label.Width + 16, label.Height + 8);
            dc.PushOpacity(.7);
            dc.DrawRoundedRectangle(Brushes.DimGray, new Pen(Brushes.White, 1), ghost, 3, 3);
            dc.DrawText(label, new Point(ghost.X + 8, ghost.Y + 4));
            dc.Pop();
        }
    }
}
