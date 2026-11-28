using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class PsdExportTests
{
    private readonly SkiaRenderer renderer = new();

    [Theory]
    [InlineData(null, null)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ExportsPsdThroughCommandAndPreservesTransparencyAndLayerMetadata(bool? flattenLayers, bool? keepLayersSeparate)
    {
        var engine = new CommandEngine(renderer);
        engine.Execute(Command.Of("document.create", new NewArgs("psd", 16, 12, PixelsPerInch: 300)));
        engine.Execute(Command.Of("layer.create", new LayerArgs("group", "Group Ω", LayerKind.Group, Transform: new(1, 0))));
        engine.Execute(Command.Of("layer.create", new LayerArgs("shape", "Rectangle", LayerKind.Rectangle,
            Parent: "group", Transform: new(3, 2), Width: 4, Height: 4, Color: "#80FF0000")));
        engine.Execute(Command.Of("effect.add", new Effect("tint", EffectKind.ColorOverlay, Ink: "#FF00FF00"), "shape"));
        engine.Execute(Command.Of("layer.opacity", .6, "shape"));
        engine.Execute(Command.Of("layer.fillOpacity", .5, "shape"));
        engine.Execute(Command.Of("layer.create", new LayerArgs("hidden", "Hidden", LayerKind.Rectangle,
            Width: 3, Height: 3, Color: "#FFFFFFFF")));
        engine.Execute(Command.Of("layer.visibility", false, "hidden"));

        using var folder = new TemporaryFolder();
        string path = Path.Combine(folder.Path, "sample.psd");
        var original = engine.Current!;
        var command = flattenLayers is { } setting
            ? Command.Of("document.export", new ExportArgs(path, ImageExportFormat.Psd, setting, keepLayersSeparate!.Value))
            : Command.Of("document.export", new { path, format = "psd" });
        engine.Execute(command);
        Assert.Same(original, engine.Current);
        var data = File.ReadAllBytes(path);
        var result = PsdImport.Read(data, "roundtrip").Document;
        Assert.Equal((16, 12, 300d), (result.Width, result.Height, result.PixelsPerInch));
        if (flattenLayers == true && keepLayersSeparate == false)
            Assert.Equal(LayerKind.Raster, Assert.Single(result.Layers).Kind);
        else
        {
            Assert.Equal(2, result.Layers.Length);
            var group = result.Layers[0];
            Assert.Equal("Group Ω", group.Name);
            Assert.Equal(LayerKind.Group, group.Kind);
            var layer = Assert.Single(group.Children);
            Assert.Equal(LayerKind.Raster, layer.Kind);
            Assert.Equal((4, 2), (layer.RasterX, layer.RasterY));
            Assert.Equal(.6, layer.Opacity);
            Assert.Equal(128 / 255d, layer.FillOpacity);
            Assert.InRange(layer.Pixels!.Rgba[3], 126, 129);
            Assert.False(result.Layers[1].Visible);
            if (flattenLayers == false)
                Assert.NotNull(result.Layers[1].VectorMask);
            else
                Assert.NotNull(result.Layers[1].Pixels);
        }

        var expected = renderer.Composite(original);
        var merged = renderer.Composite(PsdImport.Read(data, "merged", flattened: true).Document);
        Assert.Equal(expected.Rgba, merged.Rgba);
        var actual = renderer.Composite(result);
        int pixel = (2 * original.Width + 4) * 4;
        Assert.Equal(255, actual.Rgba[pixel + 1]);
        Assert.InRange(actual.Rgba[pixel + 3], expected.Rgba[pixel + 3] - 1, expected.Rgba[pixel + 3] + 1);
        Assert.Equal(0, actual.Rgba[3]);
    }

    [Fact]
    public void EditablePsdRetainsRasterMasksShapePathsAndSupportedStyles()
    {
        var mask = new AlphaMask(2, 2, [0, 128, 255, 64], X: 1, Y: 1);
        var style = new Effect("outline", EffectKind.Stroke, A: 2, Ink: "#FF123456", Opacity: .8);
        var shape = new Layer("shape", "Ellipse", LayerKind.Ellipse)
        {
            Width = 8, Height = 6, Color = "#FFCC6600", Transform = new(3, 2),
            Mask = mask, Effects = [style]
        };
        var unsupported = new Layer("blurred", "Blurred", LayerKind.Rectangle)
        {
            Width = 4, Height = 4, Effects = [new("blur", EffectKind.GaussianBlur, A: 1)], Mask = mask
        };
        var document = new Document("source", 20, 15) { Layers = [shape, unsupported] };
        using var output = new MemoryStream();
        renderer.ExportPsd(document, output, flattenLayers: false, keepLayersSeparate: false);

        var imported = PsdImport.Read(output.ToArray(), "editable").Document;
        var editable = imported.Layers[0];
        Assert.Equal(LayerKind.Fill, editable.Kind);
        Assert.Equal(shape.Color, editable.Color);
        Assert.Equal((4, 3), (editable.Mask!.X, editable.Mask.Y));
        Assert.Equal(mask.Alpha, editable.Mask.Alpha);
        var path = Assert.Single(editable.VectorMask!.Contours);
        Assert.True(path.Closed);
        Assert.Equal(4, path.Nodes.Length);
        Assert.Equal(7, path.Nodes[0].Point.X, precision: 5);
        Assert.Equal(2, path.Nodes[0].Point.Y, precision: 5);
        var exportedStyle = Assert.Single(editable.Effects);
        Assert.Equal((style.Kind, style.A, style.Ink, style.Opacity),
            (exportedStyle.Kind, exportedStyle.A, exportedStyle.Ink, exportedStyle.Opacity));
        Assert.Equal(LayerKind.Raster, imported.Layers[1].Kind);
        Assert.NotNull(imported.Layers[1].Pixels);
        Assert.Null(imported.Layers[1].Mask);
        Assert.Empty(imported.Layers[1].Effects);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("chop-psd-export-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
