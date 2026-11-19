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
public sealed record HistoryEntry(string Name, Document Document, Command? Command = null,
    bool Enabled = true, string? Error = null);
public sealed record ResizeImageArgs(int Width, int Height, double? PixelsPerInch = null);
public sealed record ResizeCanvasArgs(int Width, int Height, int AnchorX = 1, int AnchorY = 1);
public enum StrokeAlignment { Inside, Center, Outside }
public sealed record OutlineArgs(string Color, int Thickness, StrokeAlignment Alignment);
public sealed record FlipArgs(bool Horizontal);
public sealed record HistoryChange(int Index, int? Destination = null, bool? Enabled = null);

/// <summary>Executes the same document commands for all clients and retains reversible snapshots.</summary>
public sealed class CommandEngine(IRasterBackend backend)
{
    private readonly List<HistoryEntry> history = [];
    private int cursor;
    public int HistoryBase { get; private set; }
    private Document? saved;
    public Document? Current => history.Count == 0 ? null : history[cursor].Document;
    public bool Dirty => Current is { } doc && (saved is null || doc.Width != saved.Width ||
        doc.Height != saved.Height || doc.PixelsPerInch != saved.PixelsPerInch || !ReferenceEquals(doc.Layers, saved.Layers) ||
        !ReferenceEquals(doc.Metadata, saved.Metadata) || !ReferenceEquals(doc.Guides, saved.Guides) || doc.GuidesLocked != saved.GuidesLocked);
    public string? Path { get; private set; }
    public int Cursor => cursor;
    public IReadOnlyList<HistoryEntry> History => history;
    public event Action<LogEntry>? Executed;

    /// <summary>Evaluate a command against the current document without committing it.</summary>
    public Document Preview(Command command) => Apply(Current ?? throw new InvalidOperationException("Open a document first."), command);


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
            case "history.change":
                ChangeHistory(command.Read<HistoryChange>());
                return;
            case "history.go":
                int step = checked(command.Read<int>() - HistoryBase);
                if (step < 0 || step >= history.Count) throw new InvalidDataException("Invalid history position.");
                cursor = step;
                return;
        }

        var result = Apply(current, command);
        if (ReferenceEquals(result, current)) return;
        history.RemoveRange(cursor + 1, history.Count - cursor - 1);
        history.Add(new(HistoryLabel(command), result, command));
        cursor = history.Count - 1;
        TrimHistory();
    }

    private static string HistoryLabel(Command command) => command.Name switch
    {
        "layer.create" => $"Create {command.Read<LayerArgs>().Kind.ToString().ToLowerInvariant()} layer",
        "layer.paintStroke" => command.Read<StrokeArgs>().Erase ? "Erase pixels" : "Brush stroke",
        "mask.paintStroke" => command.Read<StrokeArgs>().Erase ? "Erase mask" : "Paint mask",
        "layer.text" => "Edit text layer",
        "effect.add" => $"Add {EffectLabel(command.Read<Effect>().Kind)}",
        "effect.update" => $"Edit {EffectLabel(command.Read<Effect>().Kind)}",
        "layer.scale" => "Scale",
        "layer.move" => "Move layer",
        "layer.moveSelection" => "Move pixels",
        "layer.warp" => command.Read<WarpArgs>().Kind.ToString(),
        "layer.bucket" => "Paint Bucket",
        "selection.lasso" => "Lasso Selection",
        "mask.maskerStroke" => "Masker Stroke",
        "layer.flip" => command.Read<FlipArgs>().Horizontal ? "Flip horizontal" : "Flip vertical",
        "guide.set" => $"Set {command.Read<Guide>().Axis.ToString().ToLowerInvariant()} guide",
        "guide.remove" => "Remove guide",
        "document.resizeImage" => "Image Size",
        "document.resizeCanvas" => "Canvas Size",
        _ => command.Name
    };

    public static string EffectLabel(EffectKind kind) => kind switch
    {
        EffectKind.BrightnessContrast => "Brightness / Contrast",
        EffectKind.HueSaturation => "Hue / Saturation",
        EffectKind.GaussianBlur => "Gaussian Blur",
        EffectKind.MotionBlur => "Motion Blur",
        EffectKind.BlendMode => "Blend mode",
        _ => kind.ToString()
    };

    private void ChangeHistory(HistoryChange change)
    {
        int index = change.Index - HistoryBase;
        int destination = (change.Destination ?? change.Index) - HistoryBase;
        if (index <= 0 || index >= history.Count || destination <= 0 || destination >= history.Count)
            throw new InvalidDataException("Choose a history entry after the starting state.");
        var entry = history[index];
        history.RemoveAt(index);
        history.Insert(destination, entry with { Enabled = change.Enabled ?? entry.Enabled });
        for (int i = Math.Min(index, destination); i < history.Count; i++)
        {
            var next = history[i];
            var doc = history[i - 1].Document;
            string? error = null;
            if (next.Enabled && next.Command is { } command)
            {
                try { doc = Apply(doc, command); }
                catch (Exception failure) { error = failure.Message; }
            }
            history[i] = next with { Document = doc, Error = error };
        }
        cursor = history.Count - 1;
    }

    private void TrimHistory()
    {
        const long limit = 384L * 1024 * 1024;
        var seen = new HashSet<Pixels>(ReferenceEqualityComparer.Instance);
        var seenMasks = new HashSet<AlphaMask>(ReferenceEqualityComparer.Instance);
        var seenMaps = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        long size = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Document.SelectionMask is { } selection && seenMaps.Add(selection.Bits))
                size += selection.Bits.LongLength;
            foreach (var layer in history[i].Document.AllLayers())
            {
                if (layer.Pixels is { } pixels && seen.Add(pixels)) size += pixels.Rgba.LongLength;
                if (layer.Mask is { } mask && seenMasks.Add(mask)) size += mask.Alpha.LongLength;
                foreach (var shift in layer.PixelShifts)
                {
                    if (seenMaps.Add(shift.Bits)) size += shift.Bits.LongLength;
                    if (shift.Patch is { } patch && seen.Add(patch)) size += patch.Rgba.LongLength;
                }
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

        Document ApplySpatial(Projective transform)
        {
            var layer = Wanted();
            EnsureEditable(layers, layer.Id);
            if (transform == Projective.Identity) return doc;
            if (doc.Selection is { } area) return TransformSelected(doc, layer, area, transform);
            var local = backend.ToParentSpace(doc, layer, transform);
            var combined = (layer.Transform.Warp ?? Projective.Identity).Then(local);
            combined.Inverse();
            return Edit(item => item with { Transform = item.Transform with { Warp = combined } });
        }

        switch (command.Name)
        {
            case "document.selection":
                var selection = command.Args.Deserialize<Area?>(Format.Json);
                return doc with { Selection = selection is { } area ? Clip(area, doc) : null, SelectionMask = null };
            case "selection.rectangle":
                var rect = command.Read<RectangleSelection>();
                if (rect.Mode == SelectionMode.Replace)
                    return WithSelection(doc, (Clip(rect.Area, doc), null));
                return SelectPixels(doc, (x, y) =>
                {
                    bool hit = Contains(rect.Area, x, y);
                    return rect.Mode == SelectionMode.Add ? hit || doc.Selected(x, y) : !hit && doc.Selected(x, y);
                });
            case "selection.lasso":
                var lasso = command.Read<LassoArgs>();
                return Lasso(doc, lasso);
            case "selection.translate":
                var shift = command.Read<PointD>();
                return TranslateSelection(doc, PixelDelta(shift.X), PixelDelta(shift.Y));
            case "selection.resize":
                return ResizeSelection(doc, command.Read<int>());
            case "selection.colorRange":
                var query = command.Read<ColorSelection>();
                return WithSelection(doc, PixelSelection.ColorRange(backend.Composite(doc), query, doc));
            case "document.resizeCanvas":
                var canvas = command.Read<ResizeCanvasArgs>();
                Format.Dimensions(canvas.Width, canvas.Height);
                if (canvas.AnchorX is < 0 or > 2 || canvas.AnchorY is < 0 or > 2)
                    throw new InvalidDataException("Choose a canvas anchor.");
                int sx = (int)((long)(canvas.Width - doc.Width) * canvas.AnchorX / 2);
                int sy = (int)((long)(canvas.Height - doc.Height) * canvas.AnchorY / 2);
                return Reframe(doc, canvas.Width, canvas.Height, sx, sy, 1, 1);
            case "document.resizeImage":
                var size = command.Read<ResizeImageArgs>();
                Format.Dimensions(size.Width, size.Height);
                double scaleX = (double)size.Width / doc.Width, scaleY = (double)size.Height / doc.Height;
                return Reframe(doc, size.Width, size.Height, 0, 0, scaleX, scaleY) with
                { PixelsPerInch = Range(size.PixelsPerInch ?? doc.PixelsPerInch, 1, 100000) };
            case "document.chop":
            case "document.crop":
                var chop = Clip(command.Read<Area>(), doc);
                return Reframe(doc, chop.Width, chop.Height, -chop.X, -chop.Y, 1, 1) with { Selection = null, SelectionMask = null };
            case "guide.lock": return doc with { GuidesLocked = command.Read<bool>() };
            case "guide.clear": return doc.Guides.Length == 0 ? doc : doc with { Guides = [] };
            case "guide.set":
                if (doc.GuidesLocked) throw new InvalidDataException("Unlock guides before moving or adding them.");
                var guide = command.Read<Guide>();
                if (!Format.ValidId(guide.Id) || !Enum.IsDefined(guide.Axis) || guide.Position < 0 ||
                    guide.Position > (guide.Axis == GuideAxis.Vertical ? doc.Width : doc.Height))
                    throw new InvalidDataException("The guide must be on a canvas pixel boundary.");
                if (doc.Guides.FirstOrDefault(item => item.Id == guide.Id) == guide) return doc;
                return doc with { Guides = [.. doc.Guides.Where(item => item.Id != guide.Id), guide] };
            case "guide.remove":
                if (doc.GuidesLocked) throw new InvalidDataException("Unlock guides before removing them.");
                var guideId = command.Read<string>();
                return doc.Guides.Any(item => item.Id == guideId)
                    ? doc with { Guides = doc.Guides.Where(item => item.Id != guideId).ToArray() } : doc;
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
                    Color = create.Color, Width = create.Kind is LayerKind.Raster or LayerKind.Fill ? doc.Width : create.Width,
                    Height = create.Kind is LayerKind.Raster or LayerKind.Fill ? doc.Height : create.Height }));
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
                return With(Insert(layers, duplicateParent, source.Id, Copy(source, duplicateId) with { Name = source.Name + " (copy)" }));
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
            case "layer.transformsEnabled":
                return Edit(layer => layer with { TransformsEnabled = command.Read<bool>() });
            case "layer.move":
                var movement = command.Read<PointD>();
                Range(movement.X, -1000000, 1000000);
                Range(movement.Y, -1000000, 1000000);
                return Edit(layer =>
                {
                    var moved = layer.TranslatePlacement(movement);
                    if (layer.Kind != LayerKind.Raster || layer.PixelShifts.Length == 0 ||
                        backend.ResolveRaster(layer) is not { } raster) return moved;
                    return moved with { Pixels = raster.Pixels, RasterX = raster.X, RasterY = raster.Y, PixelShifts = [] };
                });
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
            case "layer.fill":
                var fillColor = command.Read<string>();
                Format.Color(fillColor);
                return Edit(layer => layer.Kind == LayerKind.Fill ? layer with { Color = fillColor }
                    : throw new InvalidDataException("Select a fill layer."));
            case "layer.shape":
                var shape = command.Read<ShapeArgs>();
                Format.Color(shape.Color);
                Format.Dimensions(shape.Width, shape.Height);
                return Edit(layer => layer.Kind is LayerKind.Rectangle or LayerKind.Ellipse ? layer with
                    { Width = shape.Width, Height = shape.Height, Color = shape.Color }
                    : throw new InvalidDataException("Select a shape layer."));
            case "layer.scale":
                var scale = command.Read<ScaleArgs>();
                Range(scale.X, .001, 1000);
                Range(scale.Y, .001, 1000);
                if (scale.AnchorX is < 0 or > 2 || scale.AnchorY is < 0 or > 2)
                    throw new InvalidDataException("Choose a transform anchor.");
                var scaleArea = doc.Selection ?? new Area(0, 0, doc.Width, doc.Height);
                return ApplySpatial(Projective.Anchored(scale.X, scale.Y,
                    scaleArea.X + scaleArea.Width * scale.AnchorX / 2d, scaleArea.Y + scaleArea.Height * scale.AnchorY / 2d));
            case "layer.flip":
                bool horizontal = command.Read<FlipArgs>().Horizontal;
                var flipArea = doc.Selection ?? new Area(0, 0, doc.Width, doc.Height);
                return ApplySpatial(Projective.Anchored(horizontal ? -1 : 1, horizontal ? 1 : -1,
                        flipArea.X + flipArea.Width / 2d, flipArea.Y + flipArea.Height / 2d));
            case "layer.warp":
                var warp = command.Read<WarpArgs>();
                if (warp.Kind is not (SpatialKind.FreeTransform or SpatialKind.Rotate or SpatialKind.Skew))
                    throw new InvalidDataException("Unknown transform.");
                return ApplySpatial(Projective.FromQuad(warp.Source, warp.Corners));
            case "layer.resetTransform":
                return Edit(layer => layer with { Transform = new Transform() });
            case "layer.bucket":
                var bucket = command.Read<BucketArgs>();
                Range(bucket.Tolerance, 0, 255);
                uint bucketColor = Format.Color(bucket.Color);
                return Edit(layer =>
                {
                    if (layer.Kind != LayerKind.Raster) throw new InvalidDataException("Select a raster layer for the paint bucket.");
                    var editable = backend.PrepareRasterEditing(doc, layer);
                    var working = With(Update(layers, layer.Id, _ => editable));
                    if (backend.BucketRegion(working, editable, bucket) is not { } region) return layer;
                    return ApplyInk(working, editable, region.Bounds, region.Contains, bucketColor);
                });
            case "layer.fillPixels":
                var fillInk = Format.Color(command.Read<string>());
                var fillBounds = doc.Selection ?? new Area(0, 0, doc.Width, doc.Height);
                return Edit(layer => ApplyInk(doc, layer, fillBounds, doc.Selection is null ? (_, _) => true : doc.Selected, fillInk));
            case "layer.strokeSelection":
                var outline = command.Read<OutlineArgs>();
                uint strokeInk = Format.Color(outline.Color);
                int thickness = (int)Range(outline.Thickness, 1, 16384);
                if (!Enum.IsDefined(outline.Alignment) || doc.Selection is null)
                    throw new InvalidDataException("A selection and stroke alignment are required.");
                int outside = outline.Alignment switch { StrokeAlignment.Outside => thickness, StrokeAlignment.Center => thickness / 2, _ => 0 };
                int inside = thickness - outside;
                var outer = ResizeSelection(doc, outside);
                var inner = ResizeSelection(doc, -inside);
                return outer.Selection is { } outlineBounds
                    ? Edit(layer => ApplyInk(doc, layer, outlineBounds, (x, y) => outer.Selected(x, y) && !inner.Selected(x, y), strokeInk))
                    : doc;
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
                if (delta is { } selectionMove)
                {
                    var dx = PixelDelta(selectionMove.X);
                    var dy = PixelDelta(selectionMove.Y);
                    if (dx == 0 && dy == 0) return doc;
                    var moved = backend.MoveSelection(doc, selectedLayer, selectedArea, dx, dy);
                    if (moved is null) return doc;
                    var movedDoc = Edit(layer => moved);
                    return TranslateSelection(movedDoc, dx, dy);
                }
                var edited = backend.ClearSelection(doc, selectedLayer);
                if (edited.Shift is not { } pixelEdit) return doc;
                return Edit(layer => layer with { PixelShifts = [.. layer.PixelShifts, pixelEdit] });
            case "mask.add":
                return Edit(layer => layer.Mask is not null ? layer : layer with
                {
                    Mask = new AlphaMask(doc.Width, doc.Height, Enumerable.Repeat((byte)255, doc.Width * doc.Height).ToArray())
                });
            case "mask.remove": return Edit(layer => layer with { Mask = null, MaskEnabled = true });
            case "mask.enable": return Edit(layer => layer with { MaskEnabled = command.Read<bool>() });
            case "mask.paste":
                var pasted = command.Read<AlphaMask>();
                Format.Dimensions(pasted.Width, pasted.Height);
                if (pasted.Alpha?.Length != pasted.Width * pasted.Height) throw new InvalidDataException("Invalid mask data.");
                return Edit(layer => layer with { Mask = pasted, MaskEnabled = true });
            case "layer.bake":
            case "mask.bake":
                return Edit(layer =>
                {
                    bool maskOnly = command.Name == "mask.bake";
                    if (maskOnly && layer.Mask is null) return layer;
                    RequireEditableSubtree(layers, [layer]);
                    var activeBlend = layer.TransformsEnabled ? EffectiveBlend(layer) : layer.Blend;
                    var source = layer with
                    {
                        Opacity = 1, Blend = Blend.Normal, Visible = true,
                        MaskEnabled = maskOnly || layer.MaskEnabled,
                        Effects = layer.Effects.Where(effect => effect.Kind != EffectKind.BlendMode).ToArray()
                    };
                    var baked = backend.RasterizeLayer(doc, source);
                    return new Layer(layer.Id, layer.Name, LayerKind.Raster)
                    {
                        Pixels = baked.Pixels, RasterX = baked.X, RasterY = baked.Y, Visible = layer.Visible,
                        Opacity = layer.Opacity, Blend = activeBlend, Width = baked.Pixels.Width, Height = baked.Pixels.Height
                    };
                });
            case "mask.paintStroke":
            case "mask.maskerStroke":
            case "layer.paintStroke":
                var stroke = command.Read<StrokeArgs>();
                Range(stroke.Size, 1, 2048);
                Range(stroke.Hardness, 0, 1);
                Range(stroke.Opacity, 0, 1);
                Format.Color(stroke.Color);
                if (stroke.Points is not { Length: > 0 and <= 100000 } ||
                    stroke.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    throw new InvalidDataException("Invalid stroke points.");
                if (command.Name == "mask.maskerStroke")
                    return Edit(layer =>
                    {
                        var masked = layer with { Mask = layer.Mask ?? new AlphaMask(doc.Width, doc.Height,
                            Enumerable.Repeat((byte)255, doc.Width * doc.Height).ToArray()), MaskEnabled = true };
                        return masked with { Mask = backend.PaintMask(doc, masked, stroke with { Erase = false }) };
                    });
                if (command.Name == "mask.paintStroke")
                    return Edit(layer => layer.Mask is not null ? layer with { Mask = backend.PaintMask(doc, layer, stroke) }
                        : throw new InvalidDataException("Add a mask before painting it."));
                return Edit(layer => layer.Kind == LayerKind.Raster
                    ? PaintRaster(doc, layer, stroke)
                    : throw new InvalidDataException("Select a raster layer to paint."));
            case "layer.reorder":
                var moving = Wanted();
                var order = command.Read<ReorderArgs>();
                EnsureEditable(layers, moving.Id);
                CheckParent(order.Parent);
                if (moving.Id == order.Before && ParentOf(layers, moving.Id) == order.Parent) return doc;
                if (order.Parent == moving.Id || new Document("", 1, 1) { Layers = moving.Children }.AllLayers()
                    .Any(child => child.Id == order.Parent)) return doc;
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

    private static Document Reframe(Document doc, int width, int height, int x, int y, double sx, double sy)
    {
        var framing = new Projective(sx, 0, x, 0, sy, y);
        var inverse = framing.Inverse();
        var result = doc with
        {
            Width = width, Height = height,
            Guides = doc.Guides.Select(guide => guide with { Position = (int)Math.Round(
                guide.Axis == GuideAxis.Vertical ? guide.Position * sx + x : guide.Position * sy + y) })
                .Where(guide => guide.Position >= 0 && guide.Position <= (guide.Axis == GuideAxis.Vertical ? width : height)).ToArray(),
            Layers = doc.Layers.Select(layer => layer with
            { Transform = layer.Transform.Warp is { } warp ? layer.Transform with { Warp = inverse.Then(warp).Then(framing) } : layer.Transform,
                Placement = new(
                layer.Placement.X * sx + x, layer.Placement.Y * sy + y,
                layer.Placement.ScaleX * sx, layer.Placement.ScaleY * sy) }).ToArray()
        };
        if (doc.Selection is null) return result;
        return SelectPixels(result, (px, py) => doc.Selected((int)Math.Floor((px - x + .5) / sx),
            (int)Math.Floor((py - y + .5) / sy)));
    }

    private Document TransformSelected(Document doc, Layer layer, Area area, Projective transform)
    {
        if (layer.Kind != LayerKind.Raster) throw new InvalidDataException("Select a raster layer to transform selected pixels.");
        var edit = backend.TransformPixels(doc, layer, area, transform);
        var changed = edit?.Shift is { } patch
            ? doc with { Layers = Update(doc.Layers, layer.Id, item => item with { PixelShifts = [.. item.PixelShifts, patch] }) }
            : doc;
        var inverse = transform.Inverse();
        return SelectPixels(changed, (x, y) =>
        {
            var previous = inverse.Map(x + .5, y + .5);
            return double.IsFinite(previous.X) && double.IsFinite(previous.Y) &&
                doc.Selected((int)Math.Floor(previous.X), (int)Math.Floor(previous.Y));
        });
    }

    private static Document Lasso(Document doc, LassoArgs args)
    {
        if (args.Points is not { Length: >= 3 and <= 20000 } || !Enum.IsDefined(args.Mode) ||
            args.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new InvalidDataException("Draw a closed lasso region.");
        var points = args.Points;
        var crossings = new List<double>();
        int row = -1, edge = 0;
        bool inside = false;
        return SelectPixels(doc, (x, y) =>
        {
            if (row != y)
            {
                row = y; edge = 0; inside = false; crossings.Clear();
                double py = y + .5;
                for (int i = 0; i < points.Length; i++)
                {
                    var a = points[i]; var b = points[(i + 1) % points.Length];
                    if ((a.Y > py) == (b.Y > py)) continue;
                    crossings.Add(a.X + (b.X - a.X) * (py - a.Y) / (b.Y - a.Y));
                }
                crossings.Sort();
            }
            while (edge < crossings.Count && x + .5 >= crossings[edge]) { inside = !inside; edge++; }
            return args.Mode switch
            {
                SelectionMode.Add => inside || doc.Selected(x, y),
                SelectionMode.Subtract => !inside && doc.Selected(x, y),
                _ => inside
            };
        });
    }

    private Layer ApplyInk(Document doc, Layer layer, Area bounds, Func<int, int, bool> contains, uint color)
    {
        if (layer.Kind != LayerKind.Raster) throw new InvalidDataException("Select a raster layer to fill or stroke pixels.");
        var edit = backend.PaintArea(doc, layer, bounds, contains, color);
        return edit.Shift is null ? layer : layer.Pixels is null
            ? layer with { Pixels = edit.Pixels, RasterX = edit.X, RasterY = edit.Y }
            : layer with { PixelShifts = [.. layer.PixelShifts, edit.Shift] };
    }

    public static Blend EffectiveBlend(Layer layer) =>
        layer.Effects.LastOrDefault(effect => effect.Enabled && effect.Kind == EffectKind.BlendMode)?.Mode ?? layer.Blend;

    private Layer PaintRaster(Document doc, Layer layer, StrokeArgs stroke)
    {
        var pixels = backend.Stroke(doc, layer, stroke);
        if (layer.PixelShifts.Length == 0) return layer with { Pixels = pixels };
        var effective = backend.ResolveRaster(layer)!;
        var prior = effective.Pixels.Rgba;
        int left = pixels.Width, top = pixels.Height, right = -1, bottom = -1;
        for (int y = 0; y < pixels.Height; y++)
        for (int x = 0; x < pixels.Width; x++)
        {
            int i = (y * pixels.Width + x) * 4;
            if (prior.AsSpan(i, 4).SequenceEqual(pixels.Rgba.AsSpan(i, 4))) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
        if (right < left) return layer;
        int width = right - left + 1, height = bottom - top + 1;
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            Array.Copy(pixels.Rgba, ((top + y) * pixels.Width + left) * 4, rgba, y * width * 4, width * 4);
        var patch = new Pixels(width, height, rgba);
        var area = new Area(effective.X + left, effective.Y + top, width, height);
        return layer with { PixelShifts = [.. layer.PixelShifts, new(area, [], 0, 0, Patch: patch)] };
    }

    private static int PixelDelta(double value) => (int)Math.Round(Range(value, -32768, 32768));

    private static bool Contains(Area area, int x, int y) => x >= area.X && y >= area.Y &&
        x < (long)area.X + area.Width && y < (long)area.Y + area.Height;

    private static Document WithSelection(Document doc, (Area? Bounds, PixelSelection? Mask) selection) =>
        doc with { Selection = selection.Bounds, SelectionMask = selection.Mask };

    private static Document SelectPixels(Document doc, Func<int, int, bool> test) =>
        WithSelection(doc, PixelSelection.Create(doc.Width, doc.Height, test));

    private static Document TranslateSelection(Document doc, int dx, int dy)
    {
        if (doc.Selection is not { } selection || (dx == 0 && dy == 0)) return doc;
        if (doc.SelectionMask is not null) return SelectPixels(doc, (x, y) => doc.Selected(x - dx, y - dy));
        var shifted = selection with { X = selection.X + dx, Y = selection.Y + dy };
        return WithSelection(doc, (Intersects(shifted, doc) ? Clip(shifted, doc) : null, null));
    }

    private static Document ResizeSelection(Document doc, int amount)
    {
        if (doc.Selection is not { } selection || amount == 0) return doc;
        Range(amount, -16384, 16384);
        if (doc.SelectionMask is null)
        {
            var area = new Area(selection.X - amount, selection.Y - amount, selection.Width + 2 * amount, selection.Height + 2 * amount);
            return WithSelection(doc, (area.Width > 0 && area.Height > 0 && Intersects(area, doc) ? Clip(area, doc) : null, null));
        }

        // A separable sliding window grows or shrinks arbitrary selections in linear time.
        int width = doc.Width, height = doc.Height, radius = Math.Abs(amount);
        bool expand = amount > 0;
        int window = radius * 2 + 1;
        var horizontal = new byte[width * height];
        var result = new byte[horizontal.Length];
        for (int y = 0; y < height; y++)
        {
            int count = 0;
            for (int x = 0; x <= Math.Min(radius, width - 1); x++) if (doc.Selected(x, y)) count++;
            for (int x = 0; x < width; x++)
            {
                horizontal[y * width + x] = (byte)((expand ? count > 0 : count == window) ? 1 : 0);
                if (x - radius >= 0 && doc.Selected(x - radius, y)) count--;
                if (x + radius + 1 < width && doc.Selected(x + radius + 1, y)) count++;
            }
        }
        for (int x = 0; x < width; x++)
        {
            int count = 0;
            for (int y = 0; y <= Math.Min(radius, height - 1); y++) count += horizontal[y * width + x];
            for (int y = 0; y < height; y++)
            {
                result[y * width + x] = (byte)((expand ? count > 0 : count == window) ? 1 : 0);
                if (y - radius >= 0) count -= horizontal[(y - radius) * width + x];
                if (y + radius + 1 < height) count += horizontal[(y + radius + 1) * width + x];
            }
        }
        return SelectPixels(doc, (x, y) => result[y * width + x] != 0);
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
        Range(Math.Abs(t.ScaleX), 0.001, 1000);
        Range(Math.Abs(t.ScaleY), 0.001, 1000);
        Range(t.Rotation, -360_000, 360_000);
        t.Warp?.Inverse();
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
            case EffectKind.MotionBlur: Range(effect.A, 0, 200); Range(effect.B, -180, 180); break;
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
