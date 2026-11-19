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

    private void BuildHistoryMenu()
    {
        var menu = new ContextMenu();
        history.ContextMenu = menu;
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
        try { DragDrop.DoDragDrop(layers, new DataObject(typeof(LayerRow), row), DragDropEffects.Move); }
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
