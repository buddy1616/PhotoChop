using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private LayerRow? ClippingUpperAt(Point position)
    {
        var source = layers.InputHitTest(position);
        if (source is null || IsButton(source) || RowAt(source) is not { } hit ||
            layers.ItemContainerGenerator.ContainerFromItem(hit) is not ListBoxItem container) return null;

        var visible = layers.Items.OfType<LayerRow>().ToArray();
        var origin = container.TranslatePoint(new Point(), layers);
        double y = position.Y - origin.Y;
        double margin = Math.Min(7, container.ActualHeight / 3);
        int index = Array.IndexOf(visible, hit);
        if (y >= container.ActualHeight - margin)
        {
            index++;
            if (index < visible.Length && visible[index].Parent == hit.Layer.Id)
                return visible.Skip(index).Any(row => row.Parent == hit.Parent) ? hit : null;
        }
        else if (y > margin) return null;
        if (index < 1 || index >= visible.Length) return null;

        // Start from the lower row. It establishes the sibling tier even when the upper group is expanded.
        var lower = visible[index];
        return visible.Take(index).LastOrDefault(row => row.Parent == lower.Parent);
    }

    private bool TryToggleClipping(MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || ClippingUpperAt(e.GetPosition(layers)) is not { } upper) return false;
        Run("layer.clipping", !upper.Layer.Clipped, upper.Layer.Id, upper.Layer.Id);
        return true;
    }

    private void AddVectorMask() => Guard(AddVectorMaskCore);

    private void AddVectorMaskCore()
    {
        if (active?.Engine.Current is not { } doc || ActiveLayer() is not { } layer) return;
        if (layer.VectorMask is null)
        {
            var contours = SelectionContours.Trace(doc).SelectMany(region => region).ToArray();
            if (contours.Length != 0 && PathMatrix(doc, layer).TryInvert(out var inverse))
                contours = contours.Select(contour => contour with
                {
                    Nodes = contour.Nodes.Select(node => new PathNode(MapPath(inverse, node.Point))).ToArray()
                }).ToArray();
            if (!Run("vectorMask.add", new VectorMask(contours), layer.Id, layer.Id)) return;
        }
        EditVectorMask(layer.Id);
    }

    private void EditRowVectorMask(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayerRow row })
            EditVectorMask(row.Layer.Id, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
    }

    private void EditVectorMask(string layerId, bool maskOnly = false)
    {
        if (active is null) return;
        SelectLayer(layerId);
        bool alreadyIsolated = active.VectorMaskTarget == layerId && active.VectorMaskOnly;
        active.VectorMaskTarget = layerId;
        active.VectorMaskOnly = maskOnly && !alreadyIsolated;
        active.MaskTarget = active.MaskOnly = null;
        SetTool("Pen");
        RefreshProperties();
        canvas.Focus();
        UpdateCanvas();
        Say("Editing vector mask");
    }

    private void FinishVectorMaskEditing()
    {
        if (active is null) return;
        active.VectorMaskTarget = null;
        active.VectorMaskOnly = false;
        RefreshProperties();
        UpdateCanvas();
        CancelPathEditing();
        RefreshPathOverlay();
        RefreshTools();
        Say();
    }

    private Layer? EditablePathLayer(Layer? layer = null)
    {
        layer ??= ActiveLayer();
        if (layer?.Id == active?.VectorMaskTarget && layer?.VectorMask is { } mask)
            return layer with { Kind = LayerKind.Path, Contours = mask.Contours };
        return layer;
    }

    private void RunPathGeometry(PathContour[] contours, string id) =>
        Run(active?.VectorMaskTarget == id ? "vectorMask.path" : "layer.path", contours, id, id);
}
