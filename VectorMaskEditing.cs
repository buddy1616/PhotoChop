using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private bool TryToggleClipping(MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || IsButton(e.OriginalSource) ||
            RowAt(e.OriginalSource) is not { } hit ||
            layers.ItemContainerGenerator.ContainerFromItem(hit) is not ListBoxItem container) return false;
        var tier = layers.Items.OfType<LayerRow>().Where(row => row.Parent == hit.Parent).ToArray();
        int i = Array.IndexOf(tier, hit);
        double y = e.GetPosition(container).Y;
        double margin = Math.Min(7, container.ActualHeight / 3);
        if (y <= margin) i--;
        else if (y < container.ActualHeight - margin) return false;
        if (i < 0 || i >= tier.Length - 1) return false;
        var upper = tier[i].Layer;
        Run("layer.clipping", !upper.Clipped, upper.Id, upper.Id);
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
        if (sender is FrameworkElement { DataContext: LayerRow row }) EditVectorMask(row.Layer.Id);
    }

    private void EditVectorMask(string layerId)
    {
        if (active is null) return;
        SelectLayer(layerId);
        active.VectorMaskTarget = layerId;
        active.MaskTarget = active.MaskOnly = null;
        SetTool("Pen");
        canvas.Focus();
        UpdateCanvas();
        Say("Editing vector mask");
    }

    private void FinishVectorMaskEditing()
    {
        if (active is null) return;
        active.VectorMaskTarget = null;
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
