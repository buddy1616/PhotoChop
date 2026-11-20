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
    private sealed record DropHint(LayerRow[] Source, ReorderArgs Order, bool IntoGroup, Rect Bounds, double LineY);

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
            Item(menu, "Properties…", () => { SelectLayer(layer.Id); Rename(); });
            if (layer.Kind == LayerKind.Text) Item(menu, "Edit Text…", () => EditText(layer));
            Item(menu, "Duplicate", () => Duplicate(layer.Id));
            Item(menu, $"Bake {kind}…", () =>
            {
                if (Confirm($"Bake this {kind.ToLowerInvariant()} into a single raster layer? This applies its visible transforms, effects, and mask."))
                    Run("layer.bake", 0, layer.Id, layer.Id);
            }).IsEnabled = layer.Kind != LayerKind.Effect && Editable(active!.Engine.Current!, layer.Id);
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
        var icon = new System.Windows.Shapes.Path { Width = 21, Height = 16, Stretch = Stretch.Uniform,
            Data = (Geometry)FindResource("MaskIcon") };
        icon.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
        button.Content = icon;
        return button;
    }

    private static Button LayerArtButton(string name, Action action, string geometry)
    {
        var button = LayerIconButton(name, name, action, glyph: false);
        var icon = new System.Windows.Shapes.Path
        {
            Width = 21, Height = 16, Stretch = Stretch.Uniform, Data = Geometry.Parse(geometry)
        };
        icon.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
        button.Content = icon;
        return button;
    }

    private static void BindLayerDrop(Button button, string hint, Action<LayerRow[]> drop, bool copy = true, bool single = false)
    {
        button.AllowDrop = true;
        string name = (string)button.ToolTip;
        void Hover(DragEventArgs e)
        {
            e.Effects = e.Data.GetData(typeof(LayerRow[])) is LayerRow[] rows && (!single || rows.Length == 1) ? (copy ? DragDropEffects.Copy : DragDropEffects.Move) : DragDropEffects.None;
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
            if (e.Data.GetData(typeof(LayerRow[])) is LayerRow[] rows && (!single || rows.Length == 1)) drop(rows);
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

    private static LayerRow[] DragRoots(IEnumerable<LayerRow> selected)
    {
        var rows = selected.ToArray();
        var descendants = rows.SelectMany(row => new Document("", 1, 1) { Layers = row.Layer.Children }.AllLayers())
            .Select(layer => layer.Id).ToHashSet();
        return rows.Where(row => !descendants.Contains(row.Layer.Id)).ToArray();
    }

    private void SelectRows(string[] ids)
    {
        syncing = true;
        try
        {
            var selected = ids.ToHashSet();
            layers.SelectedItems.Clear();
            foreach (var row in layers.Items.OfType<LayerRow>().Where(row => selected.Contains(row.Layer.Id)))
                layers.SelectedItems.Add(row);
        }
        finally { syncing = false; }
    }

    private void DuplicateDropped(LayerRow[] rows)
    {
        var copies = rows.Select(_ => Format.Id()).ToArray();
        var ids = rows.Select(row => row.Layer.Id).ToArray();
        // The command visits layers in stacking order. Each generated ID identifies one new root.
        if (Run("layers.duplicate", new LayerBatch(ids, CopyIds: copies), selectId: copies[0]))
            SelectRows(copies);
    }

    private void DragLayer(LayerRow[] rows)
    {
        if (rows.Length > 1) SelectRows(rows.Select(row => row.Layer.Id).ToArray());
        var layer = AdornerLayer.GetAdornerLayer(layers);
        var adorner = new LayerDragAdorner(layers, rows.Length == 1 ? rows[0].Layer.Name : $"{rows.Length} layers") { Pointer = Mouse.GetPosition(layers) };
        layerDragAdorner = adorner;
        layer?.Add(adorner);
        try { DragDrop.DoDragDrop(layers, new DataObject(typeof(LayerRow[]), rows), DragDropEffects.Move | DragDropEffects.Copy); }
        finally { layer?.Remove(adorner); layerDragAdorner = null; }
    }

    private DropHint? GetDrop(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LayerRow[])) is not LayerRow[] source) return null;
        var moved = source.SelectMany(row => new Document("", 1, 1) { Layers = [row.Layer] }.AllLayers())
            .Select(item => item.Id).ToHashSet();
        var target = RowAt(e.OriginalSource);
        if (target is not null && moved.Contains(target.Layer.Id)) return null;
        var item = target is null ? null : layers.ItemContainerGenerator.ContainerFromItem(target) as ListBoxItem;
        double fraction = item is null || item.ActualHeight == 0 ? 1 : e.GetPosition(item).Y / item.ActualHeight;
        bool into = target?.Layer.Kind == LayerKind.Group && fraction > .25 && fraction < .75;
        string? parent = into ? target!.Layer.Id : target?.Parent;
        if (parent is not null && moved.Contains(parent)) return null;
        var siblings = layers.Items.Cast<LayerRow>().Where(row => row.Parent == parent && !moved.Contains(row.Layer.Id)).ToArray();
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
