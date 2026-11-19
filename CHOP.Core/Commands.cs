using System.Text.Json;

namespace Chop.Core;

/// <summary>Serializable command envelope. Stable IDs are supplied when creating objects.</summary>
public sealed record Command(string Name, string? Target, JsonElement Args)
{
    public static Command Of<T>(string name, T args, string? target = null) =>
        new(name, target, JsonSerializer.SerializeToElement(args, Format.Json));

    public T Read<T>() => Args.Deserialize<T>(Format.Json) ?? throw new InvalidDataException($"Missing arguments for {Name}.");
}

public sealed record NewArgs(string Id, int Width, int Height, string? Background = null, double PixelsPerInch = 72, bool StarterLayers = false);
public sealed record LayerArgs(string Id, string Name, LayerKind Kind, string? Parent = null,
    Transform? Transform = null, string Text = "Text", string Font = "Liberation Sans", double Size = 48,
    string Color = "#FFFFFFFF", int Width = 100, int Height = 100);
public sealed record ImportArgs(string Id, string Name, byte[]? Png = null, string? Parent = null, string? Path = null);
public sealed record PathArgs(string Path);
public sealed record OpenArgs(string Path, byte[]? Data = null);
public sealed record TextArgs(string Text, string Font, double Size, string Color);
public sealed record ShapeArgs(int Width, int Height, string Color);
public sealed record StrokeArgs(PointD[] Points, double Size, double Hardness, double Opacity, string Color, bool Erase);
public sealed record ReorderArgs(string? Parent, string? Before);
public sealed record GroupArgs(string Id, string Name, string[] Layers, string? Anchor = null);
public sealed record MergeArgs(string Id, string Name, string[] Layers);
public sealed record EffectOrder(string Id, int Index);
public sealed record LogEntry(DateTimeOffset Timestamp, string? DocumentId, Command Command, bool Success, string? Error);
public sealed record HistoryEntry(string Name, Document Document);

/// <summary>Executes the same document commands for all clients and retains reversible snapshots.</summary>
public sealed class CommandEngine(IRasterBackend backend)
{
    private readonly List<HistoryEntry> history = [];
    private int cursor;
    public int HistoryBase { get; private set; }
    private Document? saved;
    public Document? Current => history.Count == 0 ? null : history[cursor].Document;
    public bool Dirty => Current is { } doc && (saved is null || doc.Width != saved.Width ||
        doc.Height != saved.Height || !ReferenceEquals(doc.Layers, saved.Layers) ||
        !ReferenceEquals(doc.Metadata, saved.Metadata));
    public string? Path { get; private set; }
    public int Cursor => cursor;
    public IReadOnlyList<HistoryEntry> History => history;
    public event Action<LogEntry>? Executed;

    public Document? Execute(Command command)
    {
        string? documentId = Current?.Id;
        try
        {
            Dispatch(command);
        }
        catch (Exception error)
        {
            Executed?.Invoke(new(DateTimeOffset.Now, documentId, command, false, error.Message));
            throw;
        }
        Executed?.Invoke(new(DateTimeOffset.Now, Current?.Id ?? documentId, command, true, null));
        return Current;
    }

    private void Dispatch(Command command)
    {
        switch (command.Name)
        {
            case "document.create":
                var args = command.Read<NewArgs>();
                Format.Dimensions(args.Width, args.Height);
                CheckId(args.Id);
                var doc = new Document(args.Id, args.Width, args.Height) { PixelsPerInch = Range(args.PixelsPerInch, 1, 100000) };
                if (args.Background is { } color)
                {
                    Format.Color(color);
                    if (!args.StarterLayers)
                    {
                        doc = doc with { Layers = [new Layer(args.Id + "-background", "Background", LayerKind.Rectangle)
                            { Width = args.Width, Height = args.Height, Color = color }] };
                        CheckId(doc.Layers[0].Id);
                    }
                }
                if (args.StarterLayers)
                {
                    var groupId = args.Id + "-group";
                    var layerId = args.Id + "-layer";
                    CheckId(groupId);
                    CheckId(layerId);
                    doc = doc with { Layers = [new Layer(groupId, "Group 1", LayerKind.Group)
                    {
                        Children = [new Layer(layerId, "Layer 1", LayerKind.Raster)
                        {
                            Width = args.Width, Height = args.Height,
                            Pixels = args.Background is { } fill ? SolidPixels(args.Width, args.Height, Format.Color(fill)) : null
                        }]
                    }] };
                }
                Reset(doc, null);
                return;
            case "document.open":
                var open = command.Read<OpenArgs>();
                using (var embedded = open.Data is null ? null : new MemoryStream(open.Data, writable: false))
                    Reset(embedded is null ? ProjectFile.Open(open.Path) : ProjectFile.Open(embedded), open.Path);
                return;
        }

        var current = Current ?? throw new InvalidOperationException("Create or open a document first.");
        switch (command.Name)
        {
            case "document.save":
                var path = command.Read<PathArgs>().Path;
                ProjectFile.Save(path, current);
                Path = path;
                saved = current;
                return;
            case "document.export":
                File.WriteAllBytes(command.Read<PathArgs>().Path, backend.EncodePng(backend.Composite(current), current.PixelsPerInch));
                return;
            case "history.undo": cursor = Math.Max(0, cursor - 1); return;
            case "history.redo": cursor = Math.Min(history.Count - 1, cursor + 1); return;
            case "history.go":
                int step = checked(command.Read<int>() - HistoryBase);
                if (step < 0 || step >= history.Count) throw new InvalidDataException("Invalid history position.");
                cursor = step;
                return;
        }

        var result = Apply(current, command);
        if (ReferenceEquals(result, current)) return;
        history.RemoveRange(cursor + 1, history.Count - cursor - 1);
        history.Add(new(command.Name, result));
        cursor = history.Count - 1;
        TrimHistory();
    }

    private void TrimHistory()
    {
        const long limit = 384L * 1024 * 1024;
        var seen = new HashSet<Pixels>(ReferenceEqualityComparer.Instance);
        var seenMasks = new HashSet<AlphaMask>(ReferenceEqualityComparer.Instance);
        long size = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            foreach (var layer in history[i].Document.AllLayers())
            {
                if (layer.Pixels is { } pixels && seen.Add(pixels)) size += pixels.Rgba.LongLength;
                if (layer.Mask is { } mask && seenMasks.Add(mask)) size += mask.Alpha.LongLength;
            }
            if (i == history.Count - 1 || (size <= limit && history.Count - i <= 250)) continue;
            history.RemoveRange(0, i + 1);
            HistoryBase += i + 1;
            cursor = history.Count - 1;
            break;
        }
    }

    private void Reset(Document document, string? path)
    {
        history.Clear();
        history.Add(new(path is null ? "New document" : "Opened document", document));
        cursor = 0;
        HistoryBase = 0;
        Path = path;
        saved = path is null ? null : document;
    }

    private Document Apply(Document doc, Command command)
    {
        var layers = doc.Layers;
        Layer Wanted() => doc.AllLayers().FirstOrDefault(layer => layer.Id == command.Target)
            ?? throw new InvalidDataException("The target layer does not exist.");
        Document With(Layer[] value) => doc with { Layers = value };
        Document Edit(Func<Layer, Layer> edit, bool checkLock = true)
        {
            var layer = Wanted();
            if (checkLock) EnsureEditable(layers, layer.Id);
            return With(Update(layers, layer.Id, edit));
        }
        void NewId(string id)
        {
            CheckId(id);
            if (doc.AllLayers().Any(layer => layer.Id == id)) throw new InvalidDataException("Layer ID already exists.");
        }
        void CheckParent(string? parent)
        {
            if (parent is not null) EnsureEditable(layers, parent);
        }

        switch (command.Name)
        {
            case "document.selection":
                var selection = command.Args.Deserialize<Area?>(Format.Json);
                return doc with { Selection = selection is { } area ? Clip(area, doc) : null };
            case "document.crop":
                var crop = Clip(command.Read<Area>(), doc);
                return doc with { Width = crop.Width, Height = crop.Height, Selection = null,
                    Layers = layers.Select(layer => layer with { Transform = layer.Transform with
                    { X = layer.Transform.X - crop.X, Y = layer.Transform.Y - crop.Y } }).ToArray() };
            case "layer.create":
                var create = command.Read<LayerArgs>();
                NewId(create.Id);
                CheckParent(create.Parent);
                CheckTransform(create.Transform ?? new());
                if (!Enum.IsDefined(create.Kind)) throw new InvalidDataException("Unknown layer type.");
                Format.Color(create.Color);
                Format.Dimensions(create.Width, create.Height);
                Range(create.Size, 1, 512);
                if (create.Text is null || string.IsNullOrWhiteSpace(create.Font)) throw new InvalidDataException("Invalid text settings.");
                return With(Insert(layers, create.Parent, null, new(create.Id, Name(create.Name), create.Kind)
                { Transform = create.Transform ?? new(), Text = create.Text, Font = create.Font, Size = create.Size,
                    Color = create.Color, Width = create.Kind == LayerKind.Raster ? doc.Width : create.Width,
                    Height = create.Kind == LayerKind.Raster ? doc.Height : create.Height }));
            case "layer.importPng":
                var import = command.Read<ImportArgs>();
                NewId(import.Id);
                CheckParent(import.Parent);
                if ((import.Png is null) == (import.Path is null))
                    throw new InvalidDataException("Import requires either embedded PNG bytes or a PNG file path.");
                var png = import.Png ?? File.ReadAllBytes(import.Path!);
                return With(Insert(layers, import.Parent, null, new(import.Id, Name(import.Name), LayerKind.Raster)
                    { Pixels = backend.DecodePng(png) }));
            case "layer.delete":
                var deleted = Wanted();
                RequireEditableSubtree(layers, [deleted]);
                return With(Remove(layers, deleted.Id));
            case "layer.duplicate":
                var source = Wanted();
                var duplicateId = command.Read<string>();
                int suffix = 0;
                Layer Copy(Layer item, string id)
                {
                    NewId(id);
                    return item with { Id = id, Children = item.Children.Select(child => Copy(child, $"{duplicateId}-{++suffix}")).ToArray() };
                }
                var duplicateParent = ParentOf(layers, source.Id);
                CheckParent(duplicateParent);
                return With(Insert(layers, duplicateParent, source.Id, Copy(source, duplicateId) with { Name = source.Name + " copy" }));
            case "layer.rename": return Edit(layer => layer with { Name = Name(command.Read<string>()) });
            case "layer.visibility": return Edit(layer => layer with { Visible = command.Read<bool>() }, false);
            case "layer.lock":
                CheckParent(ParentOf(layers, Wanted().Id));
                return Edit(layer => layer with { Locked = command.Read<bool>() }, false);
            case "layer.opacity": return Edit(layer => layer with { Opacity = Range(command.Read<double>(), 0, 1) });
            case "layer.blend":
                var blend = command.Read<Blend>();
                if (!Enum.IsDefined(blend)) throw new InvalidDataException("Unknown blend mode.");
                return Edit(layer => layer with { Blend = blend });
            case "layer.transform":
                var transform = command.Read<Transform>();
                CheckTransform(transform);
                return Edit(layer => layer with { Transform = transform });
            case "layer.text":
                var text = command.Read<TextArgs>();
                Format.Color(text.Color);
                Range(text.Size, 1, 512);
                if (text.Text is null || string.IsNullOrWhiteSpace(text.Font)) throw new InvalidDataException("Invalid text settings.");
                return Edit(layer => layer.Kind == LayerKind.Text ? layer with
                    { Text = text.Text, Font = text.Font, Size = text.Size, Color = text.Color }
                    : throw new InvalidDataException("Select a text layer."));
            case "layer.shape":
                var shape = command.Read<ShapeArgs>();
                Format.Color(shape.Color);
                Format.Dimensions(shape.Width, shape.Height);
                return Edit(layer => layer.Kind is LayerKind.Rectangle or LayerKind.Ellipse ? layer with
                    { Width = shape.Width, Height = shape.Height, Color = shape.Color }
                    : throw new InvalidDataException("Select a shape layer."));
            case "layer.clearSelection":
            case "layer.moveSelection":
                if (doc.Selection is not { } selectedArea) return doc;
                PointD? delta = command.Name == "layer.moveSelection" ? command.Read<PointD>() : null;
                if (delta is { } offset)
                {
                    Range(offset.X, -16384, 16384);
                    Range(offset.Y, -16384, 16384);
                }
                var selectedLayer = Wanted();
                EnsureEditable(layers, selectedLayer.Id);
                if (selectedLayer.Kind != LayerKind.Raster) throw new InvalidDataException("Select a raster layer to edit pixels.");
                if (selectedLayer.Pixels is null) return doc;
                var edited = backend.EditSelection(doc, selectedLayer, selectedArea, delta);
                var updated = Edit(layer => layer with { Pixels = edited.Pixels, RasterX = edited.X, RasterY = edited.Y });
                if (delta is not { } displacement) return updated;
                var translated = selectedArea with { X = selectedArea.X + (int)Math.Round(displacement.X),
                    Y = selectedArea.Y + (int)Math.Round(displacement.Y) };
                return updated with { Selection = Intersects(translated, doc) ? Clip(translated, doc) : null };
            case "mask.add":
                return Edit(layer => layer.Mask is not null ? layer : layer with
                {
                    Mask = new AlphaMask(doc.Width, doc.Height, Enumerable.Repeat((byte)255, doc.Width * doc.Height).ToArray())
                });
            case "mask.remove": return Edit(layer => layer with { Mask = null });
            case "mask.paintStroke":
            case "layer.paintStroke":
                var stroke = command.Read<StrokeArgs>();
                Range(stroke.Size, 1, 2048);
                Range(stroke.Hardness, 0, 1);
                Range(stroke.Opacity, 0, 1);
                Format.Color(stroke.Color);
                if (stroke.Points is not { Length: > 0 and <= 100000 } ||
                    stroke.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    throw new InvalidDataException("Invalid stroke points.");
                if (command.Name == "mask.paintStroke")
                    return Edit(layer => layer.Mask is not null ? layer with { Mask = backend.PaintMask(doc, layer, stroke) }
                        : throw new InvalidDataException("Add a mask before painting it."));
                return Edit(layer => layer.Kind == LayerKind.Raster
                    ? layer with { Pixels = backend.Stroke(doc, layer, stroke) }
                    : throw new InvalidDataException("Select a raster layer to paint."));
            case "layer.reorder":
                var moving = Wanted();
                var order = command.Read<ReorderArgs>();
                EnsureEditable(layers, moving.Id);
                CheckParent(order.Parent);
                if (moving.Id == order.Before && ParentOf(layers, moving.Id) == order.Parent) return doc;
                return With(Insert(Remove(layers, moving.Id), order.Parent, order.Before, moving));
            case "layer.group":
                var group = command.Read<GroupArgs>();
                NewId(group.Id);
                if (group.Anchor is null)
                {
                    var items = Siblings(layers, group.Layers, out var parent);
                    foreach (var item in items) EnsureEditable(layers, item.Id);
                    var grouped = new Layer(group.Id, Name(group.Name), LayerKind.Group) { Children = items };
                    return With(ReplaceItems(layers, parent, items, [grouped]));
                }
                return With(GroupAtAnchor(doc, group));
            case "layer.ungroup":
                var ungroup = Wanted();
                EnsureEditable(layers, ungroup.Id);
                if (ungroup.Kind != LayerKind.Group || ungroup.Transform != new Transform() ||
                    ungroup.Mask is not null || !ungroup.Visible || ungroup.Opacity != 1 || ungroup.Effects.Length != 0 || ungroup.Blend != Blend.Normal)
                    throw new InvalidDataException("Ungroup requires a visible group with default transform, opacity and blend and no effects.");
                return With(ReplaceItems(layers, ParentOf(layers, ungroup.Id), [ungroup], ungroup.Children));
            case "layer.merge":
                var merge = command.Read<MergeArgs>();
                NewId(merge.Id);
                var selected = Siblings(layers, merge.Layers, out var mergeParent);
                RequireEditableSubtree(layers, selected);
                var siblings = mergeParent is null ? layers : doc.AllLayers().Single(layer => layer.Id == mergeParent).Children;
                var indexes = selected.Select(item => Array.IndexOf(siblings, item)).Order().ToArray();
                if (indexes.Length < 2 || indexes[^1] - indexes[0] + 1 != indexes.Length)
                    throw new InvalidDataException("Select at least two adjacent sibling layers to merge.");
                return With(ReplaceItems(layers, mergeParent, selected, [new(merge.Id, Name(merge.Name), LayerKind.Raster)
                    { Pixels = backend.Composite(doc with { Layers = selected }) }]));
            case "document.flatten":
                var flat = command.Read<string>();
                NewId(flat);
                RequireEditableSubtree(layers, layers);
                return With([new(flat, "Flattened", LayerKind.Raster) { Pixels = backend.Composite(doc) }]);
            case "effect.add":
                var effect = command.Read<Effect>();
                CheckEffect(effect);
                return Edit(layer => layer.Effects.Any(item => item.Id == effect.Id)
                    ? throw new InvalidDataException("Effect ID already exists.") : layer with { Effects = [.. layer.Effects, effect] });
            case "effect.update":
                var update = command.Read<Effect>();
                CheckEffect(update);
                return Edit(layer => layer with { Effects = UpdateEffects(layer.Effects, update.Id, list =>
                    list[list.FindIndex(item => item.Id == update.Id)] = update) });
            case "effect.remove":
                var id = command.Read<string>();
                return Edit(layer => layer with { Effects = UpdateEffects(layer.Effects, id, list =>
                    list.RemoveAt(list.FindIndex(item => item.Id == id))) });
            case "effect.reorder":
                var move = command.Read<EffectOrder>();
                return Edit(layer => layer with { Effects = UpdateEffects(layer.Effects, move.Id, list =>
                {
                    if (move.Index < 0 || move.Index >= list.Count) throw new InvalidDataException("Invalid effect position.");
                    var item = list.Single(effect => effect.Id == move.Id);
                    list.Remove(item);
                    list.Insert(move.Index, item);
                }) });
            default: throw new InvalidDataException($"Unknown command '{command.Name}'.");
        }
    }

    private static Effect[] UpdateEffects(Effect[] effects, string id, Action<List<Effect>> edit)
    {
        if (!effects.Any(item => item.Id == id)) throw new InvalidDataException("Effect not found.");
        var list = effects.ToList();
        edit(list);
        return list.ToArray();
    }

    private static Pixels SolidPixels(int width, int height, uint argb)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = (byte)(argb >> 16);
            bytes[i + 1] = (byte)(argb >> 8);
            bytes[i + 2] = (byte)argb;
            bytes[i + 3] = (byte)(argb >> 24);
        }
        return new(width, height, bytes);
    }

    private static bool Intersects(Area area, Document doc) => area.X < doc.Width && area.Y < doc.Height &&
        (long)area.X + area.Width > 0 && (long)area.Y + area.Height > 0;

    private static Area Clip(Area area, Document doc)
    {
        int x = Math.Clamp(area.X, 0, doc.Width);
        int y = Math.Clamp(area.Y, 0, doc.Height);
        int w = (int)Math.Clamp((long)area.X + area.Width, 0, doc.Width) - x;
        int h = (int)Math.Clamp((long)area.Y + area.Height, 0, doc.Height) - y;
        if (w <= 0 || h <= 0) throw new InvalidDataException("The rectangle does not intersect the canvas.");
        return new(x, y, w, h);
    }

    private static Layer[] Update(Layer[] layers, string id, Func<Layer, Layer> edit)
    {
        bool changed = false;
        var result = layers.Select(layer =>
        {
            if (layer.Id == id) { changed = true; return edit(layer); }
            var children = Update(layer.Children, id, edit);
            if (ReferenceEquals(children, layer.Children)) return layer;
            changed = true;
            return layer with { Children = children };
        }).ToArray();
        return changed ? result : layers;
    }

    private static Layer[] Remove(Layer[] layers, string id) => layers
        .Where(layer => layer.Id != id)
        .Select(layer => layer.Children.Length == 0 ? layer : layer with { Children = Remove(layer.Children, id) }).ToArray();

    private static Layer[] Insert(Layer[] layers, string? parent, string? before, Layer item)
    {
        Layer[] Add(Layer[] items)
        {
            int index = before is null ? items.Length : Array.FindIndex(items, layer => layer.Id == before);
            if (index < 0) throw new InvalidDataException("The insertion target is not a sibling.");
            return [.. items.Take(index), item, .. items.Skip(index)];
        }
        if (parent is null) return Add(layers);
        var updated = Update(layers, parent, layer => layer.Kind == LayerKind.Group
            ? layer with { Children = Add(layer.Children) } : throw new InvalidDataException("Destination is not a group."));
        if (ReferenceEquals(updated, layers)) throw new InvalidDataException("Group does not exist or is inside the moved layer.");
        return updated;
    }

    private static string? ParentOf(Layer[] layers, string id) => new Document("", 1, 1) { Layers = layers }
        .AllLayers().FirstOrDefault(layer => layer.Children.Any(child => child.Id == id))?.Id;

    private static Layer[] Siblings(Layer[] layers, string[] ids, out string? parent)
    {
        if (ids is not { Length: > 0 } || ids.Distinct().Count() != ids.Length)
            throw new InvalidDataException("Select one or more distinct layers.");
        parent = ParentOf(layers, ids[0]);
        var parentId = parent;
        var items = parentId is null ? layers : new Document("", 1, 1) { Layers = layers }.AllLayers()
            .Single(layer => layer.Id == parentId).Children;
        var found = items.Where(item => ids.Contains(item.Id)).ToArray();
        if (found.Length != ids.Length) throw new InvalidDataException("Selected layers must be siblings.");
        return found;
    }

    private static Layer[] GroupAtAnchor(Document doc, GroupArgs group)
    {
        var all = doc.AllLayers().ToArray();
        if (group.Layers is not { Length: > 0 } || group.Layers.Distinct().Count() != group.Layers.Length ||
            !group.Layers.Contains(group.Anchor) || group.Layers.Any(id => !all.Any(layer => layer.Id == id)))
            throw new InvalidDataException("Select existing layers to group.");

        var ids = group.Layers.ToHashSet();
        string RootSelected(string id)
        {
            string root = id;
            for (string? parent = ParentOf(doc.Layers, id); parent is not null; parent = ParentOf(doc.Layers, parent))
                if (ids.Contains(parent)) root = parent;
            return root;
        }
        var roots = ids.Select(RootSelected).ToHashSet();
        var items = all.Where(layer => roots.Contains(layer.Id)).ToArray();
        var anchor = RootSelected(group.Anchor!);
        foreach (var item in items) EnsureEditable(doc.Layers, item.Id);
        var grouped = new Layer(group.Id, Name(group.Name), LayerKind.Group) { Children = items };

        Layer[] Replace(Layer[] layers) => layers.SelectMany<Layer, Layer>(layer =>
        {
            if (layer.Id == anchor) return [grouped];
            if (roots.Contains(layer.Id)) return [];
            return [layer.Kind == LayerKind.Group ? layer with { Children = Replace(layer.Children) } : layer];
        }).ToArray();

        return Replace(doc.Layers);
    }

    private static Layer[] ReplaceItems(Layer[] layers, string? parent, Layer[] remove, Layer[] insert)
    {
        Layer[] Replace(Layer[] items) => items.TakeWhile(layer => layer.Id != remove[0].Id)
            .Concat(insert).Concat(items.SkipWhile(layer => layer.Id != remove[0].Id)
            .Where(layer => remove.All(item => item.Id != layer.Id))).ToArray();
        return parent is null ? Replace(layers) : Update(layers, parent, layer => layer with { Children = Replace(layer.Children) });
    }

    private static void RequireEditableSubtree(Layer[] all, Layer[] roots)
    {
        foreach (var root in roots)
        {
            EnsureEditable(all, root.Id);
            if (new Document("", 1, 1) { Layers = root.Children }.AllLayers().Any(item => item.Locked))
                throw new InvalidDataException("An affected group contains a locked layer.");
        }
    }

    private static void EnsureEditable(Layer[] layers, string id)
    {
        var doc = new Document("", 1, 1) { Layers = layers };
        for (string? current = id; current is not null; current = ParentOf(layers, current))
            if (doc.AllLayers().FirstOrDefault(layer => layer.Id == current) is not { Locked: false })
                throw new InvalidDataException("The layer or its group is locked or unavailable.");
    }

    internal static void CheckTransform(Transform t)
    {
        Range(t.X, -1_000_000, 1_000_000);
        Range(t.Y, -1_000_000, 1_000_000);
        Range(t.ScaleX, 0.001, 1000);
        Range(t.ScaleY, 0.001, 1000);
        Range(t.Rotation, -360_000, 360_000);
    }

    internal static void CheckEffect(Effect effect)
    {
        if (effect is null || !Enum.IsDefined(effect.Kind)) throw new InvalidDataException("Unknown effect.");
        CheckId(effect.Id);
        switch (effect.Kind)
        {
            case EffectKind.BrightnessContrast: Range(effect.A, -1, 1); Range(effect.B, -1, 1); break;
            case EffectKind.HueSaturation: Range(effect.A, -180, 180); Range(effect.B, -1, 1); break;
            case EffectKind.GaussianBlur: Range(effect.A, 0, 100); break;
            case EffectKind.Opacity: Range(effect.A, 0, 1); break;
            case EffectKind.Transform:
                CheckTransform(effect.Spatial ?? throw new InvalidDataException("A transform effect needs transform parameters."));
                break;
            case EffectKind.BlendMode:
                if (!Enum.IsDefined(effect.Mode)) throw new InvalidDataException("Unknown blend mode.");
                break;
        }
    }

    private static void CheckId(string id)
    {
        if (!Format.ValidId(id)) throw new InvalidDataException("Invalid object ID.");
    }

    private static string Name(string name) => string.IsNullOrWhiteSpace(name)
        ? throw new InvalidDataException("Layer names must contain text.") : name.Trim();

    private static double Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max
        ? value : throw new InvalidDataException($"Value must be between {min} and {max}.");
}
