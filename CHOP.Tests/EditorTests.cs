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
    public void RepeatedLayerTransformsComposeIntoTheSamePropertyAndSaveWithoutAStack()
    {
        var engine = NewEngine(20, 10);
        Layer(engine, new("art", "Art", LayerKind.Rectangle, Width: 6, Height: 4));
        engine.Execute(Command.Of("layer.scale", new ScaleArgs(.5, .5, 0, 0), "art"));
        engine.Execute(Command.Of("layer.flip", new FlipArgs(true), "art"));
        var layer = engine.Current!.Layers[0];
        Assert.Equal(new PointD(19, 1), layer.Transform.Warp!.Value.Map(2, 2));
        Assert.Null(layer.SpatialEdits);
        var path = Path.Combine(folder, "transform.chop");
        ProjectFile.Save(path, engine.Current);
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        Assert.DoesNotContain("spatialEdits", reader.ReadToEnd());
        Assert.Equal(Composite(engine), renderer.Composite(ProjectFile.Open(path)).Rgba);
    }

    [Fact]
    public void LegacyTransformStackMigratesEnabledEditsInOrder()
    {
        var doc = new Document("doc", 16, 12) { Layers = [new Layer("art", "Artwork", LayerKind.Rectangle) {
            Width = 2, Height = 2, SpatialEdits = [
                new("one", SpatialKind.Scale, Projective.Anchored(2, 2, 0, 0)),
                new("off", SpatialKind.FlipHorizontal, Projective.Anchored(-1, 1, 8, 6), false),
                new("two", SpatialKind.FreeTransform, new Projective(1, 0, 3, 0, 1, 2))
            ]
        }] };
        var path = Path.Combine(folder, "legacy.chop");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var manifest = zip.CreateEntry("manifest.json").Open())
            JsonSerializer.Serialize(manifest, new { version = 6, document = doc, resources = new Dictionary<string, object>() }, Format.Json);
        var restored = ProjectFile.Open(path);
        var layer = restored.Layers[0];
        Assert.Null(layer.SpatialEdits);
        Assert.Equal(new PointD(5, 4), layer.Transform.Warp!.Value.Map(1, 1));
    }

    [Fact]
    public void BucketFillsBeyondTransformedImportedPixelsAndKeepsTheLayerTransform()
    {
        var engine = NewEngine(16, 10);
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("art", "Imported", renderer.EncodePng(
            new Pixels(2, 2, [255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255])))));
        engine.Execute(Command.Of("layer.scale", new ScaleArgs(2, 2, 0, 0), "art"));
        var transform = engine.Current!.Layers[0].Transform;
        engine.Execute(Command.Of("layer.bucket", new BucketArgs(new(12, 7), "#FF0080FF"), "art"));
        var result = Composite(engine);
        Assert.Equal(new byte[] { 0, 128, 255, 255 }, result[((7 * 16 + 12) * 4)..((7 * 16 + 13) * 4)]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, result[..4]);
        Assert.Equal(transform, engine.Current.Layers[0].Transform);
        string path = Path.Combine(folder, "bucket.chop");
        ProjectFile.Save(path, engine.Current);
        Assert.Equal(result, renderer.Composite(ProjectFile.Open(path)).Rgba);
    }

    [Fact]
    public void LassoMovesOnlySelectedDisplayedPixelsAfterFlipAndScale()
    {
        var engine = NewEngine(12, 8);
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("art", "Imported", renderer.EncodePng(
            new Pixels(2, 2, [255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255])))));
        engine.Execute(Command.Of("layer.scale", new ScaleArgs(2, 2, 0, 0), "art"));
        engine.Execute(Command.Of("layer.flip", new FlipArgs(true), "art"));
        engine.Execute(Command.Of("selection.lasso", new LassoArgs(
            [new(8, 0), new(10, 0), new(8, 2)], SelectionMode.Replace)));
        Assert.True(engine.Current!.Selected(8, 0));
        Assert.False(engine.Current.Selected(9, 1));
        var before = Composite(engine);
        engine.Execute(Command.Of("layer.moveSelection", new PointD(-3, 3), "art"));
        var after = Composite(engine);
        byte Alpha(byte[] rgba, int x, int y) => rgba[(y * 12 + x) * 4 + 3];
        Assert.Equal(0, Alpha(after, 8, 0));
        Assert.Equal(Alpha(before, 9, 1), Alpha(after, 9, 1));
        Assert.Equal(255, Alpha(after, 5, 3));
        Assert.True(engine.Current.Selected(5, 3));
        Assert.False(engine.Current.Selected(6, 4));
        Assert.Empty(engine.Current.Layers[0].PixelShifts);
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Equal(before, Composite(engine));
    }

    [Fact]
    public void MovingWholeLayerCommitsPlacementIndependentOfTheUnbakedTransformToggle()
    {
        var engine = NewEngine(20, 12);
        Layer(engine, new("art", "Artwork", LayerKind.Rectangle, Width: 3, Height: 2, Color: "#FFFF0000"));
        engine.Execute(Command.Of("layer.move", new PointD(5, 4), "art"));
        engine.Execute(Command.Of("layer.transformsEnabled", false, "art"));
        Assert.Equal(255, Composite(engine)[(4 * 20 + 5) * 4 + 3]);
        Assert.Equal(new Area(5, 4, 3, 2), renderer.VisibleLayerBounds(engine.Current!, "art"));
        Assert.Null(engine.Current!.Layers[0].Transform.Warp);
    }

    [Fact]
    public void MovingAFlippedLayerStillFollowsTheMoveDirection()
    {
        var engine = NewEngine(12, 6);
        Layer(engine, new("art", "Artwork", LayerKind.Rectangle, Width: 2, Height: 2, Transform: new(3, 1), Color: "#FFFF0000"));
        engine.Execute(Command.Of("layer.flip", new FlipArgs(true), "art"));
        Assert.Equal(new Area(7, 1, 2, 2), renderer.VisibleLayerBounds(engine.Current!, "art"));
        engine.Execute(Command.Of("layer.move", new PointD(2, 0), "art"));
        Assert.Equal(new Area(9, 1, 2, 2), renderer.VisibleLayerBounds(engine.Current!, "art"));
    }

    [Fact]
    public void WholeLayerFlipUsesCanvasCenterAndReplaysFromTheSingleTransform()
    {
        var engine = NewEngine(10, 6);
        Layer(engine, new("art", "Rectangle", LayerKind.Rectangle, Transform: new(1, 2),
            Width: 2, Height: 1, Color: "#FFFF0000"));
        var before = Composite(engine);
        engine.Execute(Command.Of("layer.flip", new FlipArgs(true), "art"));
        var layer = engine.Current!.Layers[0];
        Assert.Equal(LayerKind.Rectangle, layer.Kind);
        Assert.Equal(new Transform(1, 2), layer.Transform with { Warp = null });
        Assert.Equal(Projective.Anchored(-1, 1, 5, 3), layer.Transform.Warp);
        Assert.Equal(255, Composite(engine)[(2 * 10 + 7) * 4 + 3]);
        Assert.Equal(0, Composite(engine)[(2 * 10 + 1) * 4 + 3]);
        string path = Path.Combine(folder, "flipped.chop");
        ProjectFile.Save(path, engine.Current);
        Assert.Equal(Composite(engine), renderer.Composite(ProjectFile.Open(path)).Rgba);
        engine.Execute(Command.Of("layer.transform", new Transform(1, 2), "art"));
        Assert.Equal(before, Composite(engine));
        engine.Execute(Command.Of("layer.resetTransform", 0, "art"));
        Assert.Equal(new Transform(), engine.Current.Layers[0].Transform);
    }

    [Fact]
    public void ProjectiveSkewMapsAllCornersAndKeepsWholeLayerEditable()
    {
        var area = new Area(0, 0, 12, 8);
        PointD[] corners = [new(2, 0), new(12, 0), new(11, 8), new(0, 8)];
        var matrix = Projective.FromQuad(area, corners);
        var inverse = matrix.Inverse();
        PointD[] source = [new(0, 0), new(12, 0), new(12, 8), new(0, 8)];
        for (int i = 0; i < 4; i++)
        {
            var mapped = matrix.Map(source[i].X, source[i].Y);
            Assert.Equal(corners[i].X, mapped.X, 8);
            Assert.Equal(corners[i].Y, mapped.Y, 8);
            Assert.Equal(source[i].X, inverse.Map(mapped.X, mapped.Y).X, 8);
        }
        var engine = NewEngine(12, 8);
        Layer(engine, new("art", "Rectangle", LayerKind.Rectangle, Width: 4, Height: 6, Color: "#FFFF0000"));
        var before = Composite(engine);
        engine.Execute(Command.Of("layer.warp", new WarpArgs(SpatialKind.Skew, area, corners), "art"));
        Assert.NotEqual(before, Composite(engine));
        Assert.Null(engine.Current!.Layers[0].Pixels);
        engine.Execute(Command.Of("layer.transformsEnabled", false, "art"));
        Assert.Equal(before, Composite(engine));
    }

    [Fact]
    public void RotateSelectedPixelsLeavesUnselectedPixelsInPlace()
    {
        var engine = NewEngine(8, 8);
        var raster = new Pixels(3, 1, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255]);
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("art", "Artwork", renderer.EncodePng(raster))));
        engine.Execute(Command.Of("layer.transform", new Transform(2, 2), "art"));
        var selected = new Area(2, 2, 2, 1);
        engine.Execute(Command.Of("document.selection", selected));
        PointD[] corners = [new(3, 2), new(3, 4), new(2, 4), new(2, 2)];
        engine.Execute(Command.Of("layer.warp", new WarpArgs(SpatialKind.Rotate, selected, corners), "art"));
        var rgba = Composite(engine);
        Assert.Equal(255, rgba[(2 * 8 + 2) * 4]);
        Assert.Equal(255, rgba[(3 * 8 + 2) * 4 + 1]);
        Assert.Equal(255, rgba[(2 * 8 + 4) * 4 + 2]);
        Assert.Equal(0, rgba[(2 * 8 + 3) * 4 + 3]);
        Assert.Equal(new Area(2, 2, 1, 2), engine.Current!.Selection);
    }

    [Fact]
    public void LassoClipsBucketAndMaskerCreatesAndEditsOnlyTheMask()
    {
        var engine = NewEngine(8, 6);
        Layer(engine, new("art", "Raster", LayerKind.Raster));
        engine.Execute(Command.Of("selection.lasso", new LassoArgs([new(0, 0), new(6, 0), new(0, 6)], SelectionMode.Replace)));
        Assert.True(engine.Current!.Selected(1, 1));
        Assert.False(engine.Current.Selected(5, 5));
        engine.Execute(Command.Of("layer.bucket", new BucketArgs(new(1, 1), "#FFFF0000"), "art"));
        byte[] filled = Composite(engine);
        int At(int x, int y) => (y * 8 + x) * 4;
        Assert.Equal(255, filled[At(1, 1)]);
        Assert.Equal(0, filled[At(5, 5) + 3]);
        var original = engine.Current.Layers[0].Pixels;
        var brush = new StrokeArgs([new(1.5, 1.5)], 2, 1, 1, "#FF000000", false);
        engine.Execute(Command.Of("mask.maskerStroke", brush, "art"));
        Assert.Equal(0, Composite(engine)[At(1, 1) + 3]);
        Assert.Same(original, engine.Current.Layers[0].Pixels);
        engine.Execute(Command.Of("mask.maskerStroke", brush with { Color = "#FFFFFFFF" }, "art"));
        Assert.Equal(filled, Composite(engine));
    }

    [Fact]
    public void BucketStopsAtNonMatchingPixelsAndDoesNotFillDisconnectedMatches()
    {
        var engine = NewEngine(5, 1);
        var line = new Pixels(5, 1, [0, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0]);
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("art", "Artwork", renderer.EncodePng(line))));
        engine.Execute(Command.Of("layer.bucket", new BucketArgs(new(0, 0), "#FF00FF00"), "art"));
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 255, 0, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0 }, Composite(engine));
    }

    [Fact]
    public void LockPreventsGuideMovesAndClearIsUndoable()
    {
        var engine = NewEngine(8, 6);
        engine.Execute(Command.Of("guide.set", new Guide("a", GuideAxis.Horizontal, 2)));
        engine.Execute(Command.Of("guide.lock", true));
        Assert.Throws<InvalidDataException>(() => engine.Execute(Command.Of("guide.set", new Guide("a", GuideAxis.Horizontal, 3))));
        Assert.Throws<InvalidDataException>(() => engine.Execute(Command.Of("guide.remove", "a")));
        Assert.Equal(2, Assert.Single(engine.Current!.Guides).Position);
        engine.Execute(Command.Of("guide.clear", 0));
        Assert.Empty(engine.Current.Guides);
        engine.Execute(Command.Of("history.undo", 0));
        Assert.True(engine.Current.GuidesLocked);
        Assert.Single(engine.Current.Guides);
    }

    [Fact]
    public void FlipAffectsOnlySelectedPixelsAndRetainsTheRasterSource()
    {
        var engine = NewEngine(10, 6);
        var original = new Pixels(4, 1,
            [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]);
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("art", "Artwork", renderer.EncodePng(original))));
        engine.Execute(Command.Of("layer.transform", new Transform(2, 2), "art"));
        engine.Execute(Command.Of("document.selection", new Area(3, 2, 2, 1)));
        engine.Execute(Command.Of("layer.flip", new FlipArgs(true), "art"));
        var after = Composite(engine);
        int At(int x) => (2 * 10 + x) * 4;
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, after[At(2)..At(3)]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, after[At(3)..At(4)]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, after[At(4)..At(5)]);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, after[At(5)..At(6)]);
        Assert.Equal(original.Rgba, engine.Current!.Layers[0].Pixels!.Rgba);
        Assert.Equal(new Transform(2, 2), engine.Current.Layers[0].Transform);
        string path = Path.Combine(folder, "selected-flip.chop");
        ProjectFile.Save(path, engine.Current);
        Assert.Equal(after, renderer.Composite(ProjectFile.Open(path)).Rgba);
    }

    [Fact]
    public void SelectedScaleUsesAnchorAndUpdatesTheOutlineWithoutMovingOtherPixels()
    {
        var engine = NewEngine(10, 8);
        var rgba = Enumerable.Repeat((byte)255, 8 * 4 * 4).ToArray();
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("art", "Artwork", renderer.EncodePng(new Pixels(8, 4, rgba)))));
        engine.Execute(Command.Of("document.selection", new Area(2, 0, 4, 4)));
        engine.Execute(Command.Of("layer.scale", new ScaleArgs(.5, .5, AnchorX: 2, AnchorY: 2), "art"));
        var after = Composite(engine);
        byte Alpha(int x, int y) => after[(y * 10 + x) * 4 + 3];
        Assert.Equal(new Area(4, 2, 2, 2), engine.Current!.Selection);
        Assert.Equal(0, Alpha(2, 0));
        Assert.Equal(0, Alpha(4, 1));
        Assert.Equal(255, Alpha(4, 2));
        Assert.Equal(255, Alpha(0, 0));
        Assert.Equal(255, Alpha(7, 3));
        Assert.Equal(rgba, engine.Current.Layers[0].Pixels!.Rgba);
    }

    [Fact]
    public void WholeLayerScaleUsesTheCanvasAnchorAndKeepsTheOriginalTransform()
    {
        var engine = NewEngine(40, 30);
        Layer(engine, new("art", "Rectangle", LayerKind.Rectangle, Transform: new(16, 10, 2, 2),
            Width: 4, Height: 4, Color: "#FFFFFFFF"));
        engine.Execute(Command.Of("layer.scale", new ScaleArgs(.5, .5), "art"));
        Assert.Equal(new Transform(16, 10, 2, 2), engine.Current!.Layers[0].Transform with { Warp = null });
        Assert.Equal(new PointD(18, 12.5), engine.Current.Layers[0].Transform.Warp!.Value.Map(16, 10));
        var pixels = Composite(engine);
        Assert.Equal(255, pixels[(13 * 40 + 18) * 4 + 3]);
        Assert.Equal(0, pixels[(10 * 40 + 16) * 4 + 3]);

        engine.Execute(Command.Of("history.undo", 0));
        engine.Execute(Command.Of("layer.scale", new ScaleArgs(.5, .5, 2, 2), "art"));
        Assert.Equal(new PointD(28, 20), engine.Current!.Layers[0].Transform.Warp!.Value.Map(16, 10));
    }

    [Fact]
    public void GuidesUsePixelBoundariesAndFollowAnchorResizeAndHistory()
    {
        var engine = NewEngine(10, 8);
        engine.Execute(Command.Of("guide.set", new Guide("vertical", GuideAxis.Vertical, 3)));
        engine.Execute(Command.Of("guide.set", new Guide("horizontal", GuideAxis.Horizontal, 4)));
        engine.Execute(Command.Of("guide.set", new Guide("vertical", GuideAxis.Vertical, 6)));
        Assert.Equal(2, engine.Current!.Guides.Length);
        Assert.True(engine.Dirty);
        Assert.Throws<InvalidDataException>(() => engine.Execute(Command.Of("guide.set", new Guide("bad", GuideAxis.Horizontal, 9))));
        engine.Execute(Command.Of("document.resizeCanvas", new ResizeCanvasArgs(14, 12, AnchorX: 2, AnchorY: 1)));
        Assert.Equal(10, engine.Current.Guides.Single(guide => guide.Id == "vertical").Position);
        Assert.Equal(6, engine.Current.Guides.Single(guide => guide.Id == "horizontal").Position);

        string path = Path.Combine(folder, "guides.chop");
        engine.Execute(Command.Of("document.save", new PathArgs(path)));
        Assert.False(engine.Dirty);
        Assert.Equal(engine.Current.Guides, ProjectFile.Open(path).Guides);
        engine.Execute(Command.Of("guide.remove", "horizontal"));
        Assert.True(engine.Dirty);
        engine.Execute(Command.Of("history.undo", 0));
        Assert.False(engine.Dirty);
        Assert.Equal(10, GuideSnap.Nearest(9.75, [6, 10], .5));
        Assert.Equal(9.75, GuideSnap.Nearest(9.75, [6, 10], .2));
    }

    [Fact]
    public void GuideSnappingUsesInternalEdgesOfNonRectangularSelections()
    {
        var doc = new Document("doc", 8, 8) { Selection = new Area(2, 2, 3, 2),
            SelectionMask = new PixelSelection(new Area(2, 2, 3, 2), [1, 0, 1, 1, 0, 1]) };
        var edges = GuideSnap.SelectionEdges(doc);
        Assert.Equal(new[] { 2, 3, 4, 5 }, edges.Vertical);
        Assert.Equal(new[] { 2, 4 }, edges.Horizontal);
        Assert.Equal(3, GuideSnap.Nearest(3.1, edges.Vertical, .25));
    }

    [Fact]
    public void ResizeCanvasUsesAnchorWithoutScalingThenImageSizeScalesEditableLayers()
    {
        var engine = NewEngine(6, 4);
        Layer(engine, new("art", "Artwork", LayerKind.Rectangle, Width: 2, Height: 1, Color: "#FFFF0000"));
        engine.Execute(Command.Of("document.resizeCanvas", new ResizeCanvasArgs(10, 8, 2, 1)));
        var moved = engine.Current!;
        Assert.Equal(new Placement(4, 2), moved.Layers[0].Placement);
        Assert.Equal((6, 4), (engine.History[^2].Document.Width, engine.History[^2].Document.Height));
        Assert.Equal(0, Composite(engine)[3]);
        Assert.Equal(255, Composite(engine)[(2 * 10 + 4) * 4 + 3]);
        engine.Execute(Command.Of("document.resizeImage", new ResizeImageArgs(20, 16, 144)));
        var sized = engine.Current!;
        Assert.Equal((20, 16, 144d), (sized.Width, sized.Height, sized.PixelsPerInch));
        Assert.Equal(LayerKind.Rectangle, sized.Layers[0].Kind);
        Assert.Equal(2, sized.Layers[0].Width);
        Assert.Equal(255, Composite(engine)[(4 * 20 + 8) * 4 + 3]);
        string path = Path.Combine(folder, "sized.chop");
        ProjectFile.Save(path, sized);
        Assert.Equal(Composite(engine), renderer.Composite(ProjectFile.Open(path)).Rgba);
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Equal(10, engine.Current!.Width);
    }

    [Fact]
    public void ResizingDocumentScalesGroupTransformEffectsAfterCompositing()
    {
        var engine = NewEngine(12, 8);
        Layer(engine, new("g", "Group", LayerKind.Group));
        Layer(engine, new("shape", "Shape", LayerKind.Rectangle, Parent: "g", Width: 1, Height: 1, Color: "#FFFF0000"));
        engine.Execute(Command.Of("effect.add", new Effect("shift", EffectKind.Transform, Spatial: new(3, 2)), "g"));
        engine.Execute(Command.Of("document.resizeImage", new ResizeImageArgs(24, 16)));
        var scaled = Composite(engine);
        Assert.Equal(255, scaled[(4 * 24 + 6) * 4]);
        Assert.Equal(0, scaled[(2 * 24 + 3) * 4 + 3]);
        Assert.Equal(3, engine.Current!.Layers[0].Effects[0].Spatial!.X);
    }

    [Fact]
    public void FlipReflectsPixelsAroundTheLayerCenterAndIsUndoable()
    {
        var engine = NewEngine(8, 6);
        var original = new Pixels(2, 2,
            [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]);
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("art", "Artwork", renderer.EncodePng(original))));
        engine.Execute(Command.Of("layer.transform", new Transform(3, 2), "art"));
        var before = Composite(engine);
        int At(int x, int y) => (y * 8 + x) * 4;
        engine.Execute(Command.Of("layer.flip", new FlipArgs(true), "art"));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Composite(engine)[At(3, 2)..(At(3, 2) + 4)]);
        engine.Execute(Command.Of("layer.flip", new FlipArgs(false), "art"));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Composite(engine)[At(3, 2)..(At(3, 2) + 4)]);
        Assert.Equal(original.Rgba, engine.Current!.Layers[0].Pixels!.Rgba);
        engine.Execute(Command.Of("history.undo", 0));
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Equal(before, Composite(engine));
    }

    [Theory]
    [InlineData(StrokeAlignment.Inside, 2, 0)]
    [InlineData(StrokeAlignment.Outside, 0, 2)]
    [InlineData(StrokeAlignment.Center, 1, 1)]
    public void StrokeUsesItsAlignmentAndDoesNotFillTheInterior(StrokeAlignment alignment, int inside, int outside)
    {
        var engine = NewEngine(12, 10);
        Layer(engine, new("art", "Art", LayerKind.Raster));
        engine.Execute(Command.Of("document.selection", new Area(3, 2, 6, 6)));
        engine.Execute(Command.Of("layer.strokeSelection", new OutlineArgs("#FF0044FF", 2, alignment), "art"));
        var rgba = Composite(engine);
        for (int x = 0; x < 6; x++)
        {
            bool selected = x >= 3 - outside && x < 3 + inside;
            Assert.Equal(selected ? 255 : 0, rgba[(4 * 12 + x) * 4 + 3]);
        }
        Assert.Equal(0, rgba[(4 * 12 + 6) * 4 + 3]);
    }

    [Fact]
    public void FillAndColorRangeCombineSelectPixelsWithoutOverwritingSource()
    {
        var engine = NewEngine(7, 4);
        var original = new Pixels(1, 1, [255, 0, 0, 255]);
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("art", "Artwork", renderer.EncodePng(original))));
        engine.Execute(Command.Of("document.selection", new Area(4, 2, 2, 1)));
        engine.Execute(Command.Of("layer.fillPixels", "#FF0000FF", "art"));
        engine.Execute(Command.Of("selection.colorRange", new ColorSelection("#FFFF0000", 0, Mode: SelectionMode.Add)));
        Assert.True(engine.Current!.Selected(0, 0));
        Assert.True(engine.Current.Selected(5, 2));
        Assert.False(engine.Current.Selected(3, 2));
        engine.Execute(Command.Of("selection.colorRange", new ColorSelection("#FFFF0000", 0, Mode: SelectionMode.Subtract)));
        Assert.False(engine.Current.Selected(0, 0));
        Assert.True(engine.Current.Selected(5, 2));
        Assert.Equal(original.Rgba, engine.Current.Layers[0].Pixels!.Rgba);
        string path = Path.Combine(folder, "fill.chop");
        ProjectFile.Save(path, engine.Current);
        Assert.Equal(Composite(engine), renderer.Composite(ProjectFile.Open(path)).Rgba);
    }

    [Fact]
    public void HistoryNamesTextAndEraserAndMotionBlurIsDirectional()
    {
        var engine = NewEngine(15, 15);
        Layer(engine, new("text", "Text", LayerKind.Text));
        Assert.Equal("Create text layer", engine.History[^1].Name);
        Layer(engine, new("art", "Artwork", LayerKind.Raster));
        var brush = new StrokeArgs([new(7.5, 7.5)], 1, 1, 1, "#FFFFFFFF", false);
        engine.Execute(Command.Of("layer.paintStroke", brush, "art"));
        engine.Execute(Command.Of("layer.paintStroke", brush with { Erase = true }, "art"));
        Assert.Equal("Erase pixels", engine.History[^1].Name);
        engine.Execute(Command.Of("history.undo", 0));
        engine.Execute(Command.Of("layer.visibility", false, "text"));
        engine.Execute(Command.Of("effect.add", new Effect("blur", EffectKind.MotionBlur, A: 8), "art"));
        Assert.Equal("Add Motion Blur", engine.History[^1].Name);
        var rgba = Composite(engine);
        Assert.True(rgba[(7 * 15 + 4) * 4 + 3] > 0);
        Assert.Equal(0, rgba[(4 * 15 + 7) * 4 + 3]);
        engine.Execute(Command.Of("layer.transformsEnabled", false, "art"));
        Assert.Equal(0, Composite(engine)[(7 * 15 + 4) * 4 + 3]);
    }

    [Fact]
    public void GroupBypassPreservesDescendantEffectsAndTransforms()
    {
        var engine = NewEngine(10, 8);
        Layer(engine, new("group", "Group", LayerKind.Group, Transform: new(1, 0)));
        Layer(engine, new("art", "Art", LayerKind.Rectangle, Parent: "group", Width: 2, Height: 1,
            Color: "#FFFF0000", Transform: new(1, 1)));
        engine.Execute(Command.Of("effect.add", new Effect("shift", EffectKind.Transform, Spatial: new(2, 0)), "art"));
        engine.Execute(Command.Of("effect.add", new Effect("gray", EffectKind.Grayscale), "art"));
        var transformed = Composite(engine);
        engine.Execute(Command.Of("layer.transformsEnabled", false, "art"));
        var bypassed = Composite(engine);
        int At(int x, int y) => (y * 10 + x) * 4;
        Assert.Equal(0, bypassed[At(4, 1) + 3]);
        Assert.Equal(0, bypassed[At(0, 0) + 3]);
        Assert.Equal(255, bypassed[At(1, 0) + 3]);
        Assert.Equal(255, bypassed[At(1, 0)]);
        Assert.Equal(0, bypassed[At(1, 0) + 1]);
        string path = Path.Combine(folder, "transforms.chop");
        ProjectFile.Save(path, engine.Current!);
        var restored = ProjectFile.Open(path);
        Assert.False(restored.Layers[0].Children[0].TransformsEnabled);
        Assert.Equal(bypassed, renderer.Composite(restored).Rgba);
        engine.Execute(Command.Of("layer.transformsEnabled", true, "art"));
        Assert.Equal(transformed, Composite(engine));
        engine.Execute(Command.Of("layer.transformsEnabled", false, "group"));
        var plainGroup = Composite(engine);
        Assert.Equal(0, plainGroup[At(0, 0) + 3]);
        Assert.Equal(255, plainGroup[At(3, 1) + 3]);
        Assert.Equal(plainGroup[At(3, 1)], plainGroup[At(3, 1) + 1]);
        Assert.True(engine.Current!.Layers[0].Children[0].TransformsEnabled);
        engine.Execute(Command.Of("layer.transformsEnabled", true, "group"));
        Assert.Equal(transformed, Composite(engine));
    }

    [Fact]
    public void BakeGroupRetainsNameAndAppearanceAndReplacesChildrenWithPixels()
    {
        var engine = NewEngine(12, 8);
        Layer(engine, new("group", "Artwork", LayerKind.Group, Transform: new(2, 1)));
        Layer(engine, new("left", "Left", LayerKind.Rectangle, Parent: "group", Width: 2, Height: 2, Color: "#FFFF2080"));
        Layer(engine, new("right", "Right", LayerKind.Rectangle, Parent: "group", Width: 3, Height: 2,
            Transform: new(3, 0), Color: "#FF30FF80"));
        engine.Execute(Command.Of("effect.add", new Effect("gray", EffectKind.Grayscale), "group"));
        engine.Execute(Command.Of("layer.opacity", .8, "group"));
        var before = Composite(engine);
        engine.Execute(Command.Of("layer.bake", 0, "group"));
        var baked = Assert.Single(engine.Current!.Layers);
        Assert.Equal("Artwork", baked.Name);
        Assert.Equal("group", baked.Id);
        Assert.Equal(LayerKind.Raster, baked.Kind);
        Assert.NotNull(baked.Pixels);
        Assert.Empty(baked.Children);
        Assert.Empty(baked.Effects);
        Assert.Equal(new Transform(), baked.Transform);
        Assert.Equal(before, Composite(engine));
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Equal(LayerKind.Group, engine.Current.Layers[0].Kind);
    }

    [Fact]
    public void BakeLayerRetainsPixelsOutsideCanvasAndClearsEditableTransforms()
    {
        var engine = NewEngine(8, 6);
        Layer(engine, new("art", "Artwork", LayerKind.Rectangle, Width: 3, Height: 2,
            Transform: new(-2, 1, 2, 1), Color: "#FF95CAFF"));
        engine.Execute(Command.Of("effect.add", new Effect("shift", EffectKind.Transform, Spatial: new(-1, 0)), "art"));
        var before = Composite(engine);
        engine.Execute(Command.Of("layer.bake", 0, "art"));
        var baked = Assert.Single(engine.Current!.Layers);
        Assert.Equal(-4, baked.RasterX);
        Assert.Equal(6, baked.Pixels!.Width);
        Assert.Empty(baked.Effects);
        Assert.Equal(before, Composite(engine));
    }

    [Fact]
    public void BakeNestedGroupUsesTheVisibleAreaInParentCoordinates()
    {
        var engine = NewEngine(12, 8);
        Layer(engine, new("parent", "Parent", LayerKind.Group, Transform: new(-16, 0)));
        Layer(engine, new("nested", "Nested", LayerKind.Group, Parent: "parent", Transform: new(2, 1)));
        Layer(engine, new("art", "Artwork", LayerKind.Rectangle, Parent: "nested", Width: 2, Height: 2,
            Transform: new(16, 0), Color: "#FFFF8040"));
        var before = Composite(engine);
        Assert.Contains((byte)255, before);
        engine.Execute(Command.Of("layer.bake", 0, "nested"));
        Assert.Equal(16, engine.Current!.Layers[0].Children[0].RasterX);
        Assert.Equal(before, Composite(engine));
    }

    [Fact]
    public void DuplicateGroupAppendsCopyAndClonesChildIdentities()
    {
        var engine = NewEngine();
        Layer(engine, new("group", "Artwork", LayerKind.Group));
        Layer(engine, new("art", "Child", LayerKind.Raster, Parent: "group"));
        engine.Execute(Command.Of("layer.duplicate", "copy", "group"));
        var copied = engine.Current!.Layers.Single(layer => layer.Id == "copy");
        Assert.Equal("Artwork (copy)", copied.Name);
        Assert.Equal("copy-1", Assert.Single(copied.Children).Id);
    }

    [Fact]
    public void RasterMovesCommitPixelsAndLaterEditsRoundTrip()
    {
        var engine = NewEngine(12, 6);
        var source = new Pixels(3, 1, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255]);
        engine.Execute(Command.Of("layer.importPng", new ImportArgs("pixels", "Pixels", renderer.EncodePng(source))));
        engine.Execute(Command.Of("selection.rectangle", new RectangleSelection(new Area(0, 0, 1, 1), SelectionMode.Replace)));
        engine.Execute(Command.Of("selection.rectangle", new RectangleSelection(new Area(2, 0, 1, 1), SelectionMode.Add)));
        engine.Execute(Command.Of("layer.moveSelection", new PointD(4, 2), "pixels"));
        var moved = engine.Current!.Layers.Single();
        Assert.NotEqual(source.Rgba, moved.Pixels!.Rgba);
        Assert.Empty(moved.PixelShifts);
        Assert.True(engine.Current.Selected(4, 2));
        Assert.False(engine.Current.Selected(5, 2));
        int At(int x, int y) => (y * 12 + x) * 4;
        var composite = Composite(engine);
        Assert.Equal(0, composite[At(0, 0) + 3]);
        Assert.Equal(255, composite[At(1, 0) + 1]);
        Assert.Equal(255, composite[At(4, 2)]);
        Assert.Equal(255, composite[At(6, 2) + 2]);

        engine.Execute(Command.Of("layer.paintStroke", new StrokeArgs([new(4.5, 2.5)], 1, 1, 1, "#FFFFFFFF", false), "pixels"));
        engine.Execute(Command.Of("selection.rectangle", new RectangleSelection(new Area(4, 2, 1, 1), SelectionMode.Subtract)));
        engine.Execute(Command.Of("layer.clearSelection", 0, "pixels"));
        string path = Path.Combine(folder, "offsets.chop");
        ProjectFile.Save(path, engine.Current!);
        var restored = ProjectFile.Open(path);
        Assert.All(restored.Layers[0].PixelShifts, shift => Assert.True(shift.Clear || shift.Patch is not null));
        Assert.NotEqual(source.Rgba, restored.Layers[0].Pixels!.Rgba);
        Assert.Equal(Composite(engine), renderer.Composite(restored).Rgba);
        Assert.Equal(0, Composite(engine)[At(6, 2) + 3]);
    }

    [Fact]
    public void PixelSelectionsSupportColorRangeBooleanEditsAndExpansion()
    {
        var engine = NewEngine(7, 5);
        Layer(engine, new("red1", "Red", LayerKind.Rectangle, Color: "#FFFF0000", Width: 1, Height: 1, Transform: new(1, 2)));
        Layer(engine, new("red2", "Nearby red", LayerKind.Rectangle, Color: "#FFF40000", Width: 1, Height: 1, Transform: new(5, 2)));
        engine.Execute(Command.Of("selection.colorRange", new ColorSelection("#FFFF0000", 0)));
        Assert.True(engine.Current!.Selected(1, 2));
        Assert.False(engine.Current.Selected(5, 2));
        engine.Execute(Command.Of("selection.colorRange", new ColorSelection("#FFFF0000", 10)));
        Assert.True(engine.Current.Selected(5, 2));
        Assert.False(engine.Current.Selected(3, 2));
        engine.Execute(Command.Of("selection.resize", 1));
        Assert.True(engine.Current.Selected(0, 1));
        Assert.True(engine.Current.Selected(6, 3));
        Assert.False(engine.Current.Selected(3, 2));
        engine.Execute(Command.Of("selection.resize", -1));
        Assert.True(engine.Current.Selected(1, 2));
        Assert.False(engine.Current.Selected(0, 1));
        engine.Execute(Command.Of("selection.rectangle", new RectangleSelection(new Area(0, 0, 2, 3), SelectionMode.Subtract)));
        Assert.False(engine.Current.Selected(1, 2));
        engine.Execute(Command.Of("selection.translate", new PointD(-1, 1)));
        Assert.True(engine.Current.Selected(4, 3));
    }

    [Fact]
    public void HistoryCanDisableAndReorderCommandsAndReplayTheSameResult()
    {
        var engine = new CommandEngine(renderer);
        var log = new List<Command>();
        engine.Executed += entry => { if (entry.Success) log.Add(entry.Command); };
        engine.Execute(Command.Of("document.create", new NewArgs("history", 12, 10)));
        Layer(engine, new("art", "Original", LayerKind.Fill));
        engine.Execute(Command.Of("layer.rename", "First", "art"));
        engine.Execute(Command.Of("layer.rename", "Second", "art"));
        engine.Execute(Command.Of("history.change", new HistoryChange(3, 2)));
        Assert.Equal("First", engine.Current!.Layers.Single().Name);
        engine.Execute(Command.Of("history.change", new HistoryChange(3, Enabled: false)));
        Assert.Equal("Second", engine.Current.Layers.Single().Name);
        engine.Execute(Command.Of("history.change", new HistoryChange(1, Enabled: false)));
        Assert.Empty(engine.Current.Layers);
        Assert.NotNull(engine.History[2].Error);
        engine.Execute(Command.Of("history.change", new HistoryChange(1, Enabled: true)));
        Assert.Null(engine.History[2].Error);
        var replay = new CommandEngine(renderer);
        foreach (var command in log) replay.Execute(command);
        Assert.Equal("Second", replay.Current!.Layers.Single().Name);
        Assert.Equal(engine.History.Select(entry => entry.Enabled), replay.History.Select(entry => entry.Enabled));
    }

    [Fact]
    public void FillMaskCanBeDisabledPastedViewedAndBaked()
    {
        var engine = NewEngine(4, 3);
        Layer(engine, new("fill", "Fill", LayerKind.Fill, Color: "#FF00FF00"));
        engine.Execute(Command.Of("layer.fill", "#FF0000FF", "fill"));
        var mask = new AlphaMask(4, 3, [0, 128, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255]);
        engine.Execute(Command.Of("mask.paste", mask, "fill"));
        var masked = Composite(engine);
        Assert.Equal(0, masked[3]);
        Assert.Equal(128, masked[7]);
        Assert.Equal(255, masked[^2]);
        var gray = renderer.MaskView(engine.Current!, engine.Current!.Layers[0]).Rgba;
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, gray[4..8]);
        engine.Execute(Command.Of("mask.enable", false, "fill"));
        Assert.Equal(255, Composite(engine)[3]);
        engine.Execute(Command.Of("mask.enable", true, "fill"));
        engine.Execute(Command.Of("mask.bake", 0, "fill"));
        Assert.Equal(LayerKind.Raster, engine.Current.Layers[0].Kind);
        Assert.Null(engine.Current.Layers[0].Mask);
        Assert.Equal(masked, Composite(engine));
    }

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
