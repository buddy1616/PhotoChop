using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class EditorTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("chop-tests-").FullName;
    private readonly SkiaRenderer renderer = new();

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private CommandEngine NewEngine(int width = 32, int height = 24)
    {
        var engine = new CommandEngine(renderer);
        engine.Execute(Command.Of("document.create", new NewArgs("test", width, height)));
        return engine;
    }

    private static void Layer(CommandEngine engine, LayerArgs args) => engine.Execute(Command.Of("layer.create", args));
    private byte[] Composite(CommandEngine engine) => renderer.Composite(engine.Current!).Rgba;

    [Fact]
    public void NewDesktopDocumentHasOneStarterGroupAndPreservesPrintResolution()
    {
        var engine = new CommandEngine(renderer);
        engine.Execute(Command.Of("document.create", new NewArgs("print", 32, 24, "#FF0044FF", 300, true)));
        var group = Assert.Single(engine.Current!.Layers);
        var raster = Assert.Single(group.Children);
        Assert.Equal("Group 1", group.Name);
        Assert.Equal("Layer 1", raster.Name);
        Assert.False(raster.Locked);
        Assert.Equal(new byte[] { 0, 68, 255, 255 }, Composite(engine)[..4]);

        string path = Path.Combine(folder, "print.chop");
        engine.Execute(Command.Of("document.save", new PathArgs(path)));
        Assert.Equal(300, ProjectFile.Open(path).PixelsPerInch);
        var png = renderer.EncodePng(renderer.Composite(engine.Current), engine.Current.PixelsPerInch);
        Assert.Equal("pHYs", System.Text.Encoding.ASCII.GetString(png, 37, 4));
        Assert.Equal(11811, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(41)));
        Assert.Equal(Composite(engine), renderer.DecodePng(png).Rgba);
    }

    [Fact]
    public void SelectionMoveMovesOnlySelectedPixelsOutsideOriginalRasterBoundsAndClearsWithUndo()
    {
        var engine = NewEngine(8, 6);
        var png = renderer.EncodePng(new Pixels(2, 1, [255, 0, 0, 255, 0, 255, 0, 255]));
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("photo", "Imported PNG", png)));
        engine.Execute(Command.Of("layer.transform", new Transform(2, 1), "photo"));
        engine.Execute(Command.Of("document.selection", new Area(2, 1, 1, 1)));
        var initial = Composite(engine);
        engine.Execute(Command.Of("layer.moveSelection", new PointD(-2, 2), "photo"));
        var moved = Composite(engine);
        int At(int x, int y) => (y * 8 + x) * 4;
        Assert.Equal(new byte[4], moved[At(2, 1)..(At(2, 1) + 4)]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, moved[At(3, 1)..(At(3, 1) + 4)]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, moved[At(0, 3)..(At(0, 3) + 4)]);
        Assert.Equal(new Area(0, 3, 1, 1), engine.Current!.Selection);
        engine.Execute(Command.Of("layer.clearSelection", 0, "photo"));
        Assert.Equal(0, Composite(engine)[At(0, 3) + 3]);
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Equal(moved, Composite(engine));
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Equal(initial, Composite(engine));
    }

    [Fact]
    public void GroupAndLayerMasksMultiplyOpacityAndRoundTripWithOriginalPixels()
    {
        var engine = NewEngine(12, 10);
        Layer(engine, new("g", "Group", LayerKind.Group, Transform: new(2, 1)));
        Layer(engine, new("a", "Red", LayerKind.Rectangle, Parent: "g", Color: "#FFFF0000", Width: 4, Height: 4));
        Layer(engine, new("b", "Blue", LayerKind.Rectangle, Parent: "g", Color: "#FF0000FF", Transform: new(4, 0), Width: 4, Height: 4));
        engine.Execute(Command.Of("mask.add", 0, "g"));
        engine.Execute(Command.Of("mask.add", 0, "a"));
        var erase = new StrokeArgs([new(5.5, 2.5)], 8, 1, .5, "#FF000000", true);
        engine.Execute(Command.Of("mask.paintStroke", erase, "g"));
        engine.Execute(Command.Of("mask.paintStroke", erase with { Points = [new(3.5, 2.5)] }, "a"));
        var pixels = Composite(engine);
        Assert.InRange(pixels[(2 * 12 + 3) * 4 + 3], 62, 65);
        Assert.InRange(pixels[(2 * 12 + 7) * 4 + 3], 126, 129);
        string path = Path.Combine(folder, "masked.chop");
        engine.Execute(Command.Of("document.save", new PathArgs(path)));
        var opened = ProjectFile.Open(path);
        Assert.Equal(pixels, renderer.Composite(opened).Rgba);
        Assert.Equal("#FFFF0000", opened.Layers[0].Children[0].Color);
        engine.Execute(Command.Of("mask.remove", 0, "g"));
        Assert.Equal(255, Composite(engine)[(2 * 12 + 7) * 4 + 3]);
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Equal(pixels, Composite(engine));
    }

    [Fact]
    public void GroupAtAnchorMovesLayersFromDifferentParentsIntoAnchorsLevel()
    {
        var engine = NewEngine();
        Layer(engine, new("g", "Group 1", LayerKind.Group));
        Layer(engine, new("a", "Nested", LayerKind.Raster, Parent: "g"));
        Layer(engine, new("b", "Root", LayerKind.Raster));
        engine.Execute(Command.Of("layer.group", new GroupArgs("nested", "Group 2", ["b", "a"], "a")));
        var parent = Assert.Single(engine.Current!.Layers);
        var grouped = Assert.Single(parent.Children);
        Assert.Equal("nested", grouped.Id);
        Assert.Equal(new[] { "a", "b" }, grouped.Children.Select(layer => layer.Id));
    }

    [Fact]
    public void NativeRoundTripRetainsEditableHierarchyAndExactSourcePixels()
    {
        string path = Path.Combine(folder, "layers.chop");
        var engine = NewEngine();
        Layer(engine, new("group", "Artwork", LayerKind.Group));
        Layer(engine, new("text", "Caption", LayerKind.Text, Parent: "group", Text: "CHOP", Size: 12,
            Transform: new(6, 2, 1.1, 1, 4)));
        var raw = new Pixels(2, 1, [200, 40, 70, 127, 8, 20, 40, 255]);
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("raster", "Source", renderer.EncodePng(raw), "group")));
        engine.Execute(Command.Of("layer.opacity", .65, "raster"));
        engine.Execute(Command.Of("layer.blend", Blend.Screen, "raster"));
        engine.Execute(Command.Of("effect.add", new Effect("sat", EffectKind.HueSaturation, A: 30, B: -.3), "raster"));
        engine.Execute(Command.Of("document.save", new PathArgs(path)));
        Assert.False(engine.Dirty);

        var opened = new CommandEngine(renderer);
        opened.Execute(Command.Of("document.open", new OpenArgs(path)));
        Assert.False(opened.Dirty);
        var group = Assert.Single(opened.Current!.Layers);
        Assert.Equal(new[] { "text", "raster" }, group.Children.Select(layer => layer.Id));
        Assert.Equal("CHOP", group.Children[0].Text);
        Assert.Equal(new Transform(6, 2, 1.1, 1, 4), group.Children[0].Transform);
        Assert.Equal(raw.Rgba, group.Children[1].Pixels!.Rgba);
        Assert.Equal(.65, group.Children[1].Opacity);
        Assert.Equal(Blend.Screen, group.Children[1].Blend);
        Assert.Equal(new Effect("sat", EffectKind.HueSaturation, A: 30, B: -.3), Assert.Single(group.Children[1].Effects));
        Assert.Equal(Composite(engine), Composite(opened));

        opened.Execute(Command.Of("layer.text", new TextArgs("Reopened", "Liberation Sans", 10, "#FF80C0FF"), "text"));
        Assert.True(opened.Dirty);
        Assert.NotEqual(Composite(engine), Composite(opened));
        opened.Execute(Command.Of("document.save", new PathArgs(path)));
        Assert.Equal("Reopened", ProjectFile.Open(path).Layers[0].Children[0].Text);
    }

    [Fact]
    public void CompositingRespectsOrderingTransparencyAndSourceOverAlpha()
    {
        var engine = NewEngine(3, 2);
        Layer(engine, new("bottom", "Red", LayerKind.Rectangle, Color: "#80FF0000", Width: 1, Height: 1));
        Layer(engine, new("top", "Blue", LayerKind.Rectangle, Color: "#800000FF", Width: 1, Height: 1));
        byte[] first = Composite(engine);
        Assert.InRange(first[0], 84, 86);
        Assert.Equal(0, first[1]);
        Assert.InRange(first[2], 169, 171);
        Assert.InRange(first[3], 191, 192);
        Assert.Equal(new byte[4], first[4..8]);
        engine.Execute(Command.Of("layer.reorder", new ReorderArgs(null, "bottom"), "top"));
        byte[] reversed = Composite(engine);
        Assert.InRange(reversed[0], 169, 171);
        Assert.InRange(reversed[2], 84, 86);
        engine.Execute(Command.Of("layer.visibility", false, "bottom"));
        Assert.Equal(new byte[] { 0, 0, 255, 128 }, Composite(engine)[..4]);
    }

    [Theory]
    [InlineData(Blend.Multiply, 48)]
    [InlineData(Blend.Screen, 208)]
    [InlineData(Blend.Overlay, 96)]
    [InlineData(Blend.Add, 255)]
    public void BlendModesEvaluateOpaqueChannels(Blend blend, int expected)
    {
        var engine = NewEngine(1, 1);
        Layer(engine, new("bottom", "Bottom", LayerKind.Rectangle, Color: "#FF404040", Width: 1, Height: 1));
        Layer(engine, new("top", "Top", LayerKind.Rectangle, Color: "#FFC0C0C0", Width: 1, Height: 1));
        engine.Execute(Command.Of("layer.blend", blend, "top"));
        var pixel = Composite(engine);
        Assert.InRange(pixel[0], expected - 1, expected + 1);
        Assert.Equal(255, pixel[3]);
    }

    [Fact]
    public void AddBlendUsesSourceOverAlphaForTranslucentLayers()
    {
        var engine = NewEngine(1, 1);
        Layer(engine, new("red", "Red", LayerKind.Rectangle, Color: "#80FF0000", Width: 1, Height: 1));
        Layer(engine, new("blue", "Blue", LayerKind.Rectangle, Color: "#800000FF", Width: 1, Height: 1));
        engine.Execute(Command.Of("layer.blend", Blend.Add, "blue"));
        var pixel = Composite(engine);
        Assert.InRange(pixel[0], 169, 171);
        Assert.InRange(pixel[2], 169, 171);
        Assert.InRange(pixel[3], 191, 192);
    }

    [Fact]
    public void CommandsValidateTargetsAndLocksAndBranchHistory()
    {
        var engine = NewEngine();
        var log = new List<LogEntry>();
        engine.Executed += log.Add;
        Layer(engine, new("group", "Group", LayerKind.Group));
        Layer(engine, new("art", "Art", LayerKind.Raster, Parent: "group"));
        engine.Execute(Command.Of("layer.lock", true, "group"));
        Assert.Throws<InvalidDataException>(() => engine.Execute(Command.Of("layer.rename", "Locked", "art")));
        Assert.False(log[^1].Success);
        engine.Execute(Command.Of("history.undo", 0));
        engine.Execute(Command.Of("layer.rename", "New name", "art"));
        engine.Execute(Command.Of("history.redo", 0));
        Assert.False(engine.Current!.Layers[0].Locked);
        Assert.Equal("New name", engine.Current.Layers[0].Children[0].Name);
        engine.Execute(Command.Of("history.go", 0));
        Assert.Empty(engine.Current.Layers);
        engine.Execute(Command.Of("history.redo", 0));
        Assert.Single(engine.Current.Layers);
    }

    [Fact]
    public void StrokeInterpolatesClipsSelectionAndErasesWithUndo()
    {
        var engine = NewEngine(20, 12);
        Layer(engine, new("art", "Art", LayerKind.Raster, Transform: new(1, 0)));
        engine.Execute(Command.Of("document.selection", new Area(4, 0, 8, 12)));
        var stroke = new StrokeArgs([new(2.5, 5.5), new(17.5, 5.5)], 4, 1, 1, "#FFFF0000", false);
        engine.Execute(Command.Of("layer.paintStroke", stroke, "art"));
        var painted = Composite(engine);
        for (int i = 0; i < 20; i++)
            Assert.Equal(i is >= 4 and < 12 ? 255 : 0, painted[(5 * 20 + i) * 4 + 3]);
        engine.Execute(Command.Of("layer.paintStroke", stroke with { Erase = true }, "art"));
        Assert.Equal(0, Composite(engine)[(5 * 20 + 7) * 4 + 3]);
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Equal(painted, Composite(engine));
        engine.Execute(Command.Of("history.redo", 0));
        Assert.Equal(0, Composite(engine)[(5 * 20 + 7) * 4 + 3]);
    }

    [Fact]
    public void EffectsCanBeEditedReorderedAndDisabledWithoutChangingSource()
    {
        var engine = NewEngine(3, 3);
        var source = new Pixels(3, 3, new byte[36]);
        source.Rgba[16] = 200;
        source.Rgba[19] = 255;
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("art", "Pixels", renderer.EncodePng(source))));
        var original = Composite(engine);
        var effect = new Effect("gray", EffectKind.Grayscale);
        engine.Execute(Command.Of("effect.add", effect, "art"));
        engine.Execute(Command.Of("effect.add", new Effect("tone", EffectKind.BrightnessContrast, A: .2), "art"));
        engine.Execute(Command.Of("effect.add", new Effect("blur", EffectKind.GaussianBlur, A: .6), "art"));
        byte[] changed = Composite(engine);
        Assert.NotEqual(original, changed);
        Assert.True(changed[3] > 0);
        engine.Execute(Command.Of("effect.reorder", new EffectOrder("blur", 0), "art"));
        engine.Execute(Command.Of("effect.update", new Effect("tone", EffectKind.BrightnessContrast, A: .7), "art"));
        Assert.NotEqual(changed, Composite(engine));
        engine.Execute(Command.Of("effect.update", effect with { Enabled = false }, "art"));
        engine.Execute(Command.Of("effect.remove", "tone", "art"));
        engine.Execute(Command.Of("effect.remove", "blur", "art"));
        Assert.Equal(original, Composite(engine));
        Assert.Equal(source.Rgba, engine.Current!.Layers[0].Pixels!.Rgba);
    }

    [Fact]
    public void ProceduralGeometryAndOpacityRespectOrderAndPreserveVectorSource()
    {
        var engine = NewEngine(12, 10);
        Layer(engine, new("shape", "Shape", LayerKind.Rectangle, Color: "#FFFF0000", Width: 2, Height: 2));
        engine.Execute(Command.Of("effect.add", new Effect("shift", EffectKind.Transform, Spatial: new(2, 1)), "shape"));
        engine.Execute(Command.Of("effect.add", new Effect("scale", EffectKind.Transform, Spatial: new(ScaleX: 2)), "shape"));
        engine.Execute(Command.Of("effect.add", new Effect("alpha", EffectKind.Opacity, A: .5), "shape"));
        var composite = Composite(engine);
        byte Alpha(byte[] pixels, int x, int y) => pixels[(y * 12 + x) * 4 + 3];
        Assert.Equal(0, Alpha(composite, 2, 1));
        Assert.InRange(Alpha(composite, 5, 1), 126, 129);
        engine.Execute(Command.Of("effect.reorder", new EffectOrder("scale", 0), "shape"));
        var reordered = Composite(engine);
        Assert.True(Alpha(reordered, 2, 1) > 0);
        Assert.Equal(0, Alpha(reordered, 7, 1));
        Assert.Equal(new Transform(), engine.Current!.Layers[0].Transform);
        Assert.Equal(2, engine.Current.Layers[0].Width);
    }

    [Fact]
    public void BrushOpacityIsAppliedOncePerGesture()
    {
        var engine = NewEngine(20, 12);
        Layer(engine, new("paint", "Paint", LayerKind.Raster));
        engine.Execute(Command.Of("layer.paintStroke", new StrokeArgs(
            [new(5.5, 5.5), new(15.5, 5.5), new(5.5, 5.5)], 5, 1, .5, "#FFFFFFFF", false), "paint"));
        Assert.InRange(Composite(engine)[(5 * 20 + 8) * 4 + 3], 126, 129);
    }

    [Fact]
    public void CropGroupUngroupMergeAndFlattenAreUndoable()
    {
        var engine = NewEngine();
        Layer(engine, new("a", "Box", LayerKind.Rectangle, Transform: new(4, 4), Color: "#FFFF8040", Width: 6, Height: 6));
        Layer(engine, new("b", "Oval", LayerKind.Ellipse, Transform: new(6, 4), Color: "#804080FF", Width: 6, Height: 6));
        var original = Composite(engine);
        engine.Execute(Command.Of("layer.group", new GroupArgs("g", "Shapes", ["a", "b"])));
        Assert.Equal(new[] { "a", "b" }, Assert.Single(engine.Current!.Layers).Children.Select(layer => layer.Id));
        engine.Execute(Command.Of("layer.ungroup", 0, "g"));
        Assert.Equal(original, Composite(engine));
        engine.Execute(Command.Of("layer.merge", new MergeArgs("merged", "Merged", ["a", "b"])));
        Assert.Equal(LayerKind.Raster, Assert.Single(engine.Current.Layers).Kind);
        Assert.Equal(original, Composite(engine));
        engine.Execute(Command.Of("document.crop", new Area(4, 4, 10, 10)));
        Assert.Equal((10, 10), (engine.Current.Width, engine.Current.Height));
        engine.Execute(Command.Of("document.flatten", "flat"));
        Assert.Equal("flat", Assert.Single(engine.Current.Layers).Id);
        engine.Execute(Command.Of("history.undo", 0));
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Equal((32, 24), (engine.Current.Width, engine.Current.Height));
        Assert.Equal(original, Composite(engine));
    }

    [Fact]
    public void RejectsUnsupportedNativeVersion()
    {
        string path = Path.Combine(folder, "bad.chop");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var output = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
            output.Write("""{"version":999,"document":null,"resources":{}}""");
        Assert.Contains("Unsupported CHOP version", Assert.Throws<InvalidDataException>(() => ProjectFile.Open(path)).Message);
    }

    [Fact]
    public async Task StandaloneCliReplaysGuiCommandsAndExportsMatchingPng()
    {
        string script = Path.Combine(folder, "replay.json");
        string png = Path.Combine(folder, "result.png");
        string project = Path.Combine(folder, "result.chop");
        var engine = new CommandEngine(renderer);
        var commands = new List<Command>();
        engine.Executed += entry => commands.Add(entry.Command);
        engine.Execute(Command.Of("document.create", new NewArgs("demo", 45, 32)));
        Layer(engine, new("paint", "Raster", LayerKind.Raster));
        engine.Execute(Command.Of("layer.paintStroke", new StrokeArgs([new(5, 5), new(30, 15)], 4, .5, .8, "#FF3C78F0", false), "paint"));
        var image = renderer.EncodePng(new Pixels(1, 1, [225, 50, 70, 255]));
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("png", "Imported", image)));
        Layer(engine, new("caption", "Letter", LayerKind.Text, Text: "C", Size: 15, Transform: new(15, 8)));
        engine.Execute(Command.Of("effect.add", new Effect("gray", EffectKind.Grayscale), "paint"));
        engine.Execute(Command.Of("history.undo", 0));
        engine.Execute(Command.Of("history.redo", 0));
        commands.Add(Command.Of("document.save", new { path = new Dictionary<string, string> { ["$param"] = "project" } }));
        commands.Add(Command.Of("document.export", new { path = new Dictionary<string, string> { ["$param"] = "png" } }));
        await File.WriteAllTextAsync(script, JsonSerializer.Serialize(commands, Format.Json));

        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(dotnet) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "cli", "CHOP.CLI.dll"),
            "run", script, "project=" + JsonSerializer.Serialize(project), "png=" + JsonSerializer.Serialize(png) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errors);
        Assert.Equal(Composite(engine), renderer.DecodePng(await File.ReadAllBytesAsync(png)).Rgba);
        Assert.Equal(Composite(engine), renderer.Composite(ProjectFile.Open(project)).Rgba);
    }
}
