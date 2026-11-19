namespace Chop.Core;

public sealed record PathNode(PointD Point, PointD? Incoming = null, PointD? Outgoing = null);
public sealed record PathContour(PathNode[] Nodes, bool Closed = false);
public sealed record CreatePathArgs(string Id, string Name, PathContour[] Contours, string? Above = null, string? Parent = null);
public sealed record SelectionPathsArgs(string IdPrefix, string? Above = null, string? Parent = null);
public sealed record ChopSnapshotArgs(string SourcePath, byte[] Png);
public sealed record CreateChopArgs(string Id, string Name, ChopSnapshotArgs Snapshot, string? Parent = null);

/// <summary>Traces exact pixel edges. Diagonally touching islands are separate paths.</summary>
public static class SelectionContours
{
    private readonly record struct Vertex(int X, int Y);
    private readonly record struct Edge(Vertex From, Vertex To, int Direction);

    public static PathContour[][] Trace(Document document)
    {
        if (document.Selection is not { } area) return [];
        if (document.SelectionMask is null)
        {
            return [[new PathContour(
            [
                new(new(area.X, area.Y)), new(new(area.X + area.Width, area.Y)),
                new(new(area.X + area.Width, area.Y + area.Height)), new(new(area.X, area.Y + area.Height))
            ], true)]];
        }

        var starts = new Dictionary<Vertex, List<Edge>>();
        var edges = new List<Edge>();
        void Add(Vertex a, Vertex b, int direction)
        {
            if (edges.Count >= 400_000) throw new InvalidDataException("The selection is too detailed to convert to paths.");
            var edge = new Edge(a, b, direction);
            if (!starts.TryGetValue(a, out var outgoing)) starts.Add(a, outgoing = []);
            outgoing.Add(edge);
            edges.Add(edge);
        }
        for (int y = area.Y; y < area.Y + area.Height; y++)
        for (int x = area.X; x < area.X + area.Width; x++)
        {
            if (!document.Selected(x, y)) continue;
            if (!document.Selected(x, y - 1)) Add(new(x, y), new(x + 1, y), 0);
            if (!document.Selected(x + 1, y)) Add(new(x + 1, y), new(x + 1, y + 1), 1);
            if (!document.Selected(x, y + 1)) Add(new(x + 1, y + 1), new(x, y + 1), 2);
            if (!document.Selected(x - 1, y)) Add(new(x, y + 1), new(x, y), 3);
        }
        var visited = new HashSet<Edge>();
        var outlines = new List<(PathContour Path, long Area)>();
        foreach (var first in edges)
        {
            if (!visited.Add(first)) continue;
            var vertices = new List<Vertex> { first.From };
            var current = first;
            long signed = 0;
            while (true)
            {
                signed += (long)current.From.X * current.To.Y - (long)current.To.X * current.From.Y;
                if (current.To == first.From) break;
                var next = starts[current.To].Where(edge => !visited.Contains(edge)).OrderBy(edge =>
                    ((edge.Direction - current.Direction + 4) % 4) switch { 1 => 0, 0 => 1, 3 => 2, _ => 3 }).FirstOrDefault();
                if (next == default || !visited.Add(next)) throw new InvalidDataException("Unable to trace a selection boundary.");
                if (next.Direction != current.Direction) vertices.Add(current.To);
                current = next;
            }
            if (current.Direction == first.Direction && vertices.Count > 3) vertices.RemoveAt(0);
            if (vertices.Count >= 3 && signed != 0)
                outlines.Add((new PathContour(vertices.Select(v => new PathNode(new PointD(v.X, v.Y))).ToArray(), true), signed));
        }

        var islands = outlines.Where(loop => loop.Area > 0).ToArray();
        if (islands.Length > 2000) throw new InvalidDataException("The selection contains too many separate regions.");
        var result = islands.Select(loop => new List<PathContour> { loop.Path }).ToArray();
        foreach (var hole in outlines.Where(loop => loop.Area < 0))
        {
            var point = hole.Path.Nodes[0].Point;
            int owner = -1;
            for (int i = 0; i < islands.Length; i++)
            {
                if (owner >= 0 && islands[i].Area >= islands[owner].Area) continue;
                if (Contains(islands[i].Path, point)) owner = i;
            }
            if (owner >= 0) result[owner].Add(hole.Path);
        }
        return result.Select(paths => paths.ToArray()).ToArray();
    }

    private static bool Contains(PathContour contour, PointD point)
    {
        bool inside = false;
        var nodes = contour.Nodes;
        for (int i = 0; i < nodes.Length; i++)
        {
            var a = nodes[i].Point;
            var b = nodes[(i + 1) % nodes.Length].Point;
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < a.X + (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y))
                inside = !inside;
        }
        return inside;
    }
}
