using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Chop.Core;
using Chop.Rendering;
using SkiaSharp;

namespace PhotoChop;

public partial class MainWindow
{
    private enum PathPart { Anchor, Incoming, Outgoing, Add, Extend }
    private sealed record PathHit(Layer Layer, int Contour, int Node, PathPart Part);
    private sealed class PathDrag(EditorCanvas view, string id, SKMatrix world, PathContour[] original,
        int contour, int node, PathPart part, Point start, PointD localStart)
    {
        public EditorCanvas View { get; } = view;
        public string Id { get; } = id;
        public SKMatrix World { get; } = world;
        public PathContour[] Original { get; } = original;
        public PathContour[] Pending { get; set; } = original;
        public int Contour { get; } = contour;
        public int Node { get; } = node;
        public PathPart Part { get; } = part;
        public Point Start { get; } = start;
        public PointD LocalStart { get; } = localStart;
    }

    private PathDrag? pathDrag;
    private string? penLayer;
    private int penContour = -1;

    private string? CreateNewPath(bool startTool = true)
    {
        if (active is null) return null;
        string id = Format.Id();
        var above = ActiveLayer();
        if (!Run("layer.createPath", new CreatePathArgs(id, NextLayerName("Path"), [], Above: above?.Id), selectId: id))
            return null;
        if (startTool) SetTool("Pen");
        return id;
    }

    private void CancelPathEditing()
    {
        var drag = pathDrag;
        pathDrag = null;
        penLayer = null;
        penContour = -1;
        if (drag?.View.IsMouseCaptured == true) drag.View.ReleaseMouseCapture();
        canvas.PathOverlay = null;
        canvas.InvalidateVisual();
    }

    private void EndCurrentPath()
    {
        penLayer = null;
        penContour = -1;
        RefreshPathOverlay();
        Say("Path ended");
    }

    private void CloseCurrentPath(string layerId, int contour)
    {
        var layer = active?.Engine.Current?.AllLayers().FirstOrDefault(item => item.Id == layerId);
        if (layer is null || contour < 0 || contour >= layer.Contours.Length || layer.Contours[contour].Nodes.Length < 3) return;
        var updated = layer.Contours.ToArray();
        updated[contour] = updated[contour] with { Closed = true };
        EndCurrentPath();
        Run("layer.path", updated, layerId, layerId);
    }

    private static PointD MapPath(SKMatrix matrix, PointD p)
    {
        var mapped = matrix.MapPoint((float)p.X, (float)p.Y);
        return new PointD(mapped.X, mapped.Y);
    }

    private static SKMatrix PathMatrix(Document doc, Layer layer) =>
        SkiaRenderer.WorldMatrix(doc.Layers, layer.Id, SKMatrix.Identity) ?? SKMatrix.Identity;

    private static Area? PathControlBounds(Document doc, Layer layer)
    {
        var world = PathMatrix(doc, layer);
        var points = layer.Contours.SelectMany(contour => contour.Nodes)
            .SelectMany(node => new[] { node.Point, node.Incoming ?? node.Point, node.Outgoing ?? node.Point })
            .Select(point => MapPath(world, point))
            .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y)).ToArray();
        if (points.Length == 0) return null;
        int x = checked((int)Math.Floor(points.Min(point => point.X)));
        int y = checked((int)Math.Floor(points.Min(point => point.Y)));
        int right = checked((int)Math.Ceiling(points.Max(point => point.X)));
        int bottom = checked((int)Math.Ceiling(points.Max(point => point.Y)));
        return new Area(x, y, Math.Max(1, checked(right - x)), Math.Max(1, checked(bottom - y)));
    }

    private void RefreshPathOverlay()
    {
        if (tool is not ("Pen" or "Path Select") || active?.Engine.Current is not { } doc || ActiveLayer() is not { Kind: LayerKind.Path } layer)
        {
            canvas.PathOverlay = null;
            canvas.InvalidateVisual();
            return;
        }
        if (pathDrag is { } drag) SetPathOverlay(drag.Pending, drag.World);
        else SetPathOverlay(layer.Contours, PathMatrix(doc, layer));
    }

    private void SetPathOverlay(PathContour[] contours, SKMatrix world)
    {
        canvas.PathOverlay = contours.Select(contour => contour with
        {
            Nodes = contour.Nodes.Select(node => new PathNode(MapPath(world, node.Point),
                node.Incoming is { } incoming ? MapPath(world, incoming) : null,
                node.Outgoing is { } outgoing ? MapPath(world, outgoing) : null)).ToArray()
        }).ToArray();
        canvas.InvalidateVisual();
    }

    private PathHit? HitPath(Document doc, PointD point)
    {
        IEnumerable<Layer> VisiblePaths(Layer[] layers)
        {
            foreach (var item in layers.Reverse())
            {
                if (!item.Visible) continue;
                if (item.Kind == LayerKind.Path) yield return item;
                foreach (var child in VisiblePaths(item.Children)) yield return child;
            }
        }
        var candidates = VisiblePaths(doc.Layers).OrderByDescending(layer => layer.Id == active?.ActiveId);
        foreach (var layer in candidates)
        {
            var world = PathMatrix(doc, layer);
            double Distance(PointD local)
            {
                var p = MapPath(world, local);
                return Math.Sqrt(Math.Pow(p.X - point.X, 2) + Math.Pow(p.Y - point.Y, 2)) * canvas.Zoom;
            }
            PathHit? best = null;
            double nearest = 8;
            for (int c = 0; c < layer.Contours.Length; c++)
            for (int n = 0; n < layer.Contours[c].Nodes.Length; n++)
            {
                var node = layer.Contours[c].Nodes[n];
                void Consider(PointD p, PathPart part)
                {
                    var distance = Distance(p);
                    if (distance >= nearest) return;
                    nearest = distance;
                    best = new PathHit(layer, c, n, part);
                }
                Consider(node.Point, PathPart.Anchor);
                if (node.Incoming is { } incoming) Consider(incoming, PathPart.Incoming);
                if (node.Outgoing is { } outgoing) Consider(outgoing, PathPart.Outgoing);
            }
            if (best is not null) return best;
        }
        return null;
    }

    private bool PathMouseDown(Point point, MouseButton button)
    {
        if (tool is not ("Pen" or "Path Select") || active?.Engine.Current is not { } doc) return false;
        var p = new PointD(point.X, point.Y);
        var hit = HitPath(doc, p);
        if (button == MouseButton.Right)
        {
            ShowPathContext(doc, hit);
            return true;
        }
        if (button != MouseButton.Left) return false;
        if (tool == "Path Select")
        {
            if (hit is null || !Editable(doc, hit.Layer.Id)) return true;
            if (active.ActiveId != hit.Layer.Id) SelectLayer(hit.Layer.Id);
            StartPathDrag(doc, hit.Layer, hit.Layer.Contours, hit.Contour, hit.Node, hit.Part, point);
            return true;
        }

        if (penLayer is { } drawing && ActiveLayer() is { Kind: LayerKind.Path } drawingLayer && drawingLayer.Id == drawing &&
            penContour >= 0 && penContour < drawingLayer.Contours.Length && !drawingLayer.Contours[penContour].Closed)
        {
            if (hit is { Part: PathPart.Anchor, Node: 0 } closing && closing.Layer.Id == drawing && closing.Contour == penContour &&
                drawingLayer.Contours[penContour].Nodes.Length >= 3)
            {
                CloseCurrentPath(drawing, penContour);
                return true;
            }
            var part = hit is { Part: PathPart.Anchor } tail && tail.Layer.Id == drawing &&
                tail.Contour == penContour && tail.Node == drawingLayer.Contours[penContour].Nodes.Length - 1
                ? PathPart.Extend : PathPart.Add;
            StartPathDrag(doc, drawingLayer, drawingLayer.Contours, penContour, -1, part, point);
            return true;
        }

        if (hit is { Part: PathPart.Anchor } endpoint)
        {
            var contour = endpoint.Layer.Contours[endpoint.Contour];
            if (!contour.Closed && (endpoint.Node == 0 || endpoint.Node == contour.Nodes.Length - 1))
            {
                if (active.ActiveId != endpoint.Layer.Id) SelectLayer(endpoint.Layer.Id);
                var contours = endpoint.Layer.Contours.ToArray();
                if (endpoint.Node == 0 && contour.Nodes.Length > 1)
                    contours[endpoint.Contour] = contour with { Nodes = contour.Nodes.Reverse()
                        .Select(node => node with { Incoming = node.Outgoing, Outgoing = node.Incoming }).ToArray() };
                penLayer = endpoint.Layer.Id;
                penContour = endpoint.Contour;
                StartPathDrag(doc, endpoint.Layer, contours, penContour, -1, PathPart.Extend, point);
                return true;
            }
        }

        if (point.X < 0 || point.Y < 0 || point.X >= doc.Width || point.Y >= doc.Height) return true;
        if (ActiveLayer()?.Kind != LayerKind.Path && CreateNewPath(startTool: false) is null) return true;
        if (ActiveLayer() is not { Kind: LayerKind.Path } layer || active?.Engine.Current is not { } current || !Editable(current, layer.Id)) return true;
        penLayer = layer.Id;
        penContour = layer.Contours.Length;
        StartPathDrag(current, layer, layer.Contours, penContour, -1, PathPart.Add, point);
        return true;
    }

    private void StartPathDrag(Document doc, Layer layer, PathContour[] original, int contour, int node, PathPart part, Point point)
    {
        if (!Editable(doc, layer.Id)) return;
        var world = PathMatrix(doc, layer);
        if (!world.TryInvert(out var inverse)) return;
        var local = MapPath(inverse, new PointD(point.X, point.Y));
        pathDrag = new(canvas, layer.Id, world, original, contour, node, part, point, local);
        canvas.CaptureMouse();
        UpdatePathDrag(point);
    }

    private bool UpdatePathDrag(Point point)
    {
        if (pathDrag is not { } drag || !drag.World.TryInvert(out var inverse)) return false;
        bool moved = (point - drag.Start).Length * canvas.Zoom >= 3;
        var current = MapPath(inverse, new PointD(point.X, point.Y));
        var contours = drag.Original.ToList();
        bool isNewContour = drag.Contour == contours.Count;
        if (isNewContour) contours.Add(new PathContour([]));
        var contour = contours[drag.Contour];
        var nodes = contour.Nodes.ToList();
        switch (drag.Part)
        {
            case PathPart.Add:
                var anchor = drag.LocalStart;
                nodes.Add(new PathNode(anchor, moved ? new PointD(2 * anchor.X - current.X, 2 * anchor.Y - current.Y) : null,
                    moved ? current : null));
                break;
            case PathPart.Extend:
                if (moved) nodes.Add(new PathNode(current));
                break;
            case PathPart.Anchor:
                var original = nodes[drag.Node];
                var dx = current.X - drag.LocalStart.X;
                var dy = current.Y - drag.LocalStart.Y;
                PointD Shift(PointD p) => new(p.X + dx, p.Y + dy);
                nodes[drag.Node] = original with { Point = Shift(original.Point),
                    Incoming = original.Incoming is { } a ? Shift(a) : null, Outgoing = original.Outgoing is { } b ? Shift(b) : null };
                break;
            case PathPart.Incoming:
                nodes[drag.Node] = nodes[drag.Node] with { Incoming = current };
                break;
            case PathPart.Outgoing:
                nodes[drag.Node] = nodes[drag.Node] with { Outgoing = current };
                break;
        }

        contours[drag.Contour] = contour with { Nodes = nodes.ToArray() };
        drag.Pending = contours.ToArray();
        SetPathOverlay(drag.Pending, drag.World);
        return true;
    }

    private bool FinishPathDrag(Point point)
    {
        if (pathDrag is not { } drag) return false;
        UpdatePathDrag(point);
        pathDrag = null;
        if (drag.View.IsMouseCaptured) drag.View.ReleaseMouseCapture();
        bool changed = drag.Pending.Length != drag.Original.Length ||
            drag.Pending.Zip(drag.Original).Any(pair =>
                pair.First.Closed != pair.Second.Closed || !pair.First.Nodes.SequenceEqual(pair.Second.Nodes));
        if (drag.Part == PathPart.Extend && active?.Engine.Current?.AllLayers().FirstOrDefault(l => l.Id == drag.Id) is { } existing)
            changed |= !existing.Contours[drag.Contour].Nodes.SequenceEqual(drag.Pending[drag.Contour].Nodes);
        if (changed) Run("layer.path", drag.Pending, drag.Id, drag.Id);
        else RefreshPathOverlay();
        return true;
    }

    private void ShowPathContext(Document doc, PathHit? hit)
    {
        var menu = new ContextMenu { PlacementTarget = canvas, Placement = PlacementMode.MousePoint };
        var target = hit?.Layer ?? ActiveLayer();
        if (target?.Kind == LayerKind.Path)
        {
            var contour = hit?.Contour ?? (penLayer == target.Id ? penContour : Array.FindIndex(target.Contours, item => !item.Closed));
            Item(menu, "Close Path", () => CloseCurrentPath(target.Id, contour)).IsEnabled =
                contour >= 0 && contour < target.Contours.Length && !target.Contours[contour].Closed && target.Contours[contour].Nodes.Length >= 3;
            Item(menu, "End Path", EndCurrentPath).IsEnabled = penLayer is not null;
            if (hit is { } node)
                Item(menu, "Make Sharp Corner", () =>
                {
                    var contours = node.Layer.Contours.ToArray();
                    var value = contours[node.Contour];
                    var nodes = value.Nodes.ToArray();
                    nodes[node.Node] = nodes[node.Node] with { Incoming = null, Outgoing = null };
                    contours[node.Contour] = value with { Nodes = nodes };
                    Run("layer.path", contours, node.Layer.Id, node.Layer.Id);
                }).IsEnabled = Editable(doc, node.Layer.Id);
        }

        if (tool == "Pen" && doc.Selection is not null)
            Item(menu, "Convert Selection to Paths", () =>
            {
                string prefix = Format.Id();
                EndCurrentPath();
                Run("selection.toPaths", new SelectionPathsArgs(prefix, ActiveLayer()?.Id), selectId: prefix + "-1");
            });
        if (menu.Items.Count != 0) menu.IsOpen = true;
    }
}
