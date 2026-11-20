using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public class ToolAndFidelityTests
{
    [Fact]
    public void PencilIsScriptableAndFullyCoversItsPixelWithoutAntialiasing()
    {
        var renderer = new SkiaRenderer();
        var engine = new CommandEngine(renderer);
        engine.Execute(Command.Of("document.create", new NewArgs("paper", 6, 6, Color: new())));
        engine.Execute(Command.Of("layer.create", new LayerArgs("ink", "Ink", LayerKind.Raster)));
        engine.Execute(Command.Of("layer.paintStroke", new StrokeArgs([new(2.98, 2.98)], 1, 0, 1, "#FFFFFFFF",
            false, Pencil: true), "ink"));
        var pixels = renderer.Composite(engine.Current!).Rgba;
        Assert.Equal(255, pixels[(2 * 6 + 2) * 4 + 3]);
        Assert.Equal(1, pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha != 0));
    }

    [Theory]
    [InlineData(Blend.LinearBurn)]
    [InlineData(Blend.LinearLight)]
    [InlineData(Blend.VividLight)]
    [InlineData(Blend.PinLight)]
    [InlineData(Blend.HardMix)]
    [InlineData(Blend.Subtract)]
    [InlineData(Blend.Divide)]
    public void ExtendedBlendModesRenderAndKeepFillOpacitySeparate(Blend blend)
    {
        var renderer = new SkiaRenderer();
        var bottom = new Layer("bottom", "Bottom", LayerKind.Rectangle) { Width = 2, Height = 2, Color = "#FF996699" };
        var top = new Layer("top", "Top", LayerKind.Rectangle) { Width = 2, Height = 2, Color = "#FFDD9999", Blend = blend };
        var document = new Document("test", 2, 2);
        var composited = renderer.Composite(document with { Layers = [bottom, top] }).Rgba;
        var basePixels = renderer.Composite(document with { Layers = [bottom] }).Rgba;
        Assert.False(composited.SequenceEqual(basePixels));
        Assert.Equal(basePixels, renderer.Composite(document with { Layers = [bottom, top with { FillOpacity = 0 }] }).Rgba);
    }
}
