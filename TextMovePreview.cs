using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chop.Core;
using Chop.Rendering;
using SkiaSharp;

namespace PhotoChop;

public partial class MainWindow
{
    private void ShowTextMovePreview(Session session, Document doc, Layer layer, BitmapSource? underlay = null, bool newlyCreated = false)
    {
        if (session.MaskOnly is not null || session.VectorMaskOnly || session.ChannelView is not (null or "composite")) return;
        try
        {
            ApplyTextMovePreview(session, doc, layer, underlay, newlyCreated);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OverflowException)
        {
            Say(error.Message);
        }
    }

    private void ApplyTextMovePreview(Session session, Document doc, Layer layer, BitmapSource? underlay, bool newlyCreated)
    {
        if (renderer.TextMoveRaster(doc, layer) is not { } visual) return;
        var world = visual.World;
        var origin = world.MapPoint(visual.Raster.X, visual.Raster.Y);
        var placement = new Matrix(world.ScaleX, world.SkewY, world.SkewX, world.ScaleY, origin.X, origin.Y);
        if (session.View.MovingText is { } current && current.LayerId == layer.Id &&
            current.Placement == placement && current.Delta.LengthSquared == 0)
        {
            if (current.Underlay is null && !current.LoadingUnderlay) PrepareTextUnderlay(session, doc, layer, current);
            return;
        }
        var overlay = new EditorCanvas.TextOverlay(layer.Id, EditorCanvas.Bitmap(visual.Raster.Pixels), placement, layer.Opacity)
        {
            Underlay = underlay ?? (session.View.MovingText is { } existing && existing.LayerId == layer.Id ? existing.Underlay : null),
            ShowAtOrigin = newlyCreated
        };
        session.View.MovingText = overlay;
        session.View.InvalidateVisual();
        if (overlay.Underlay is null && !newlyCreated) PrepareTextUnderlay(session, doc, layer, overlay);
    }

    private async void PrepareTextUnderlay(Session session, Document doc, Layer layer, EditorCanvas.TextOverlay overlay)
    {
        overlay.LoadingUnderlay = true;
        try
        {
            var background = await Task.Run(() => EditorCanvas.Bitmap(renderer.Composite(
                doc.ReplaceLayer(layer.Id, layer with { Visible = false }))));
            if (!ReferenceEquals(session.View.MovingText, overlay)) return;
            overlay.Underlay = background;
            session.View.InvalidateVisual();
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OverflowException)
        {
            System.Diagnostics.Debug.WriteLine(error.Message);
        }
        finally { overlay.LoadingUnderlay = false; }
    }

    private static void ClearTextMovePreview(Session session)
    {
        session.View.MovingText = null;
        session.View.InvalidateVisual();
    }

    private void PreviewLayerMove(Document doc, Point end)
    {
        var delta = end - gestureStart;
        var mapped = moveParentInverse.MapVector((float)delta.X, (float)delta.Y);
        var original = moveOriginal!;
        var placement = original with { X = Math.Round(original.X + mapped.X, 2), Y = Math.Round(original.Y + mapped.Y, 2) };
        if (placement == (movePreview ?? original)) return;
        movePreview = placement;
        if (canvas.MovingText is { } overlay && overlay.LayerId == moveId &&
            doc.AllLayers().FirstOrDefault(layer => layer.Id == moveId) is { } layer)
        {
            var moved = layer.TranslatePlacement(new(placement.X - original.X, placement.Y - original.Y));
            var branch = PreviewMove(doc.Layers, layer.Id, placement);
            if (SkiaRenderer.TextMoveMatrix(branch, moved.Id) is { } world &&
                SkiaRenderer.TextMoveMatrix(doc.Layers, layer.Id) is { } before)
            {
                overlay.Delta = new Vector(world.TransX - before.TransX, world.TransY - before.TransY);
                canvas.InvalidateVisual();
                return;
            }
        }
        preview = doc with { Layers = PreviewMove(doc.Layers, moveId!, placement) };
        UpdateCanvas();
    }
}
