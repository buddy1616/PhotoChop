using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class V11FeedbackTests
{
    [Fact]
    public void FillCoversResizedCanvasAndRendersItsEffects()
    {
        var renderer = new SkiaRenderer();
        var engine = new CommandEngine(renderer);
        engine.Execute(Command.Of("document.create", new NewArgs("fill", 4, 4, Color: PixelBuffer.Rgba8)));
        engine.Execute(Command.Of("layer.create", new LayerArgs("color", "Fill", LayerKind.Fill, Color: "#FF112233")));
        Assert.Throws<InvalidDataException>(() => engine.Execute(Command.Of("layer.move", new PointD(1, 0), "color")));
        Assert.Throws<InvalidDataException>(() => engine.Execute(Command.Of("layer.transform", new Transform(2, 3), "color")));
        engine.Execute(Command.Of("document.resizeCanvas", new ResizeCanvasArgs(7, 6)));
        engine.Execute(Command.Of("effect.add", new Effect("overlay", EffectKind.ColorOverlay, Ink: "#FF882200"), "color"));
        var pixels = renderer.Composite(engine.Current!);
        Assert.Equal(new byte[] { 136, 34, 0, 255 }, pixels.Rgba[..4]);
        Assert.Equal(new byte[] { 136, 34, 0, 255 }, pixels.Rgba[^4..]);
    }

    [Fact]
    public void ClippedPassThroughGroupBlendsChildrenAgainstTheClippingBase()
    {
        var renderer = new SkiaRenderer();
        var baseLayer = new Layer("base", "Base", LayerKind.Rectangle) { Width = 1, Height = 1, Color = "#FF808080" };
        var group = new Layer("group", "Group", LayerKind.Group)
        {
            PassThrough = true, Clipped = true,
            Children = [new("blend", "Blend", LayerKind.Rectangle)
                { Width = 2, Height = 1, Color = "#FF808080", Blend = Blend.Multiply }]
        };
        var pixels = renderer.Composite(new Document("test", 2, 1) { Layers = [baseLayer, group] });
        Assert.InRange(pixels.Rgba[0], (byte)63, (byte)65);
        Assert.Equal((byte)255, pixels.Rgba[3]);
        Assert.Equal((byte)0, pixels.Rgba[7]);
    }
}
