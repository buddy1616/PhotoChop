using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class PsdExportTests
{
    private readonly SkiaRenderer renderer = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExportsPsdThroughCommandAndPreservesTransparencyAndLayerMetadata(bool flattenLayers)
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
        engine.Execute(Command.Of("document.export", new ExportArgs(path, ImageExportFormat.Psd, flattenLayers)));
        Assert.Same(original, engine.Current);
        var data = File.ReadAllBytes(path);
        var result = PsdImport.Read(data, "roundtrip").Document;
        Assert.Equal((16, 12, 300d), (result.Width, result.Height, result.PixelsPerInch));
        if (flattenLayers)
            Assert.Equal(LayerKind.Raster, Assert.Single(result.Layers).Kind);
        else
        {
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

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("chop-psd-export-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
