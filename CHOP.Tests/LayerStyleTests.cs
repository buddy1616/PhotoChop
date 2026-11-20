using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class LayerStyleTests
{
    [Fact]
    public void BevelFollowsTheMaskedEdgeOfAnOpaqueLayer()
    {
        var renderer = new SkiaRenderer();
        var layer = new Layer("art", "Art", LayerKind.Rectangle)
        {
            Width = 24, Height = 16, Color = "#FF808080",
            Mask = new(12, 16, Enumerable.Repeat((byte)255, 12 * 16).ToArray(), X: 6, Outside: 0),
            Effects = [new("bevel", EffectKind.BevelEmboss, A: 0, B: 30, Size: 3, Alignment: StrokeAlignment.Inside)]
        };
        var image = renderer.Composite(new Document("test", 24, 16) { Layers = [layer] });
        Assert.Equal(0, image.Rgba[(8 * 24 + 5) * 4 + 3]);
        Assert.NotEqual(image.Rgba[(8 * 24 + 12) * 4], image.Rgba[(8 * 24 + 6) * 4]);
    }

    [Fact]
    public void GradientHonorsAnInternalStopInTheFillToolAndOverlay()
    {
        var renderer = new SkiaRenderer();
        GradientStop[] stops = [new(0, "#FFFF0000"), new(.25, "#FF00FF00"), new(1, "#FF0000FF")];
        var layer = new Layer("paint", "Paint", LayerKind.Raster) { Width = 5, Height = 1 };
        var document = new Document("test", 5, 1) { Layers = [layer] };
        var painted = renderer.PaintGradient(document, layer, new Area(0, 0, 5, 1),
            new GradientArgs(new(.5, .5), new(4.5, .5), stops[0].Color, stops[^1].Color, stops));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, painted.Pixels.Rgba[4..8]);
        var image = renderer.Composite(document with { Layers = [layer with
        {
            Pixels = painted.Pixels,
            Effects = [new("ramp", EffectKind.GradientOverlay, Stops: stops)]
        }] });
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, image.Rgba[4..8]);
    }

    [Fact]
    public void InnerShadowIsDirectionalAndGlowDoesNotRequireAColorShift()
    {
        var renderer = new SkiaRenderer();
        var art = new Layer("art", "Art", LayerKind.Rectangle)
            { Width = 14, Height = 12, Transform = new(4, 4), Color = "#FFFFFFFF" };
        var document = new Document("test", 24, 20);
        Pixels Render(Effect effect) => renderer.Composite(document with { Layers = [art with { Effects = [effect] }] });
        var shadow = Render(new("shadow", EffectKind.InnerShadow, A: 0, B: 4, Size: 1, Ink: "#FF000000"));
        Assert.True(shadow.Rgba[(10 * 24 + 15) * 4] < shadow.Rgba[(10 * 24 + 6) * 4]);
        Assert.Equal(0, shadow.Rgba[(10 * 24 + 18) * 4 + 3]);
        var glow = Render(new("glow", EffectKind.Glow, Alignment: StrokeAlignment.Outside, Size: 2, Ink: "#FFFF0000"));
        Assert.True(glow.Rgba[(10 * 24 + 18) * 4 + 3] > 0);
    }

    [Fact]
    public void EmbeddedPatternIsReplayableAndBevelRespondsToRelief()
    {
        var renderer = new SkiaRenderer();
        byte[] tile = renderer.EncodePng(new Pixels(2, 1, [255, 0, 0, 255, 0, 255, 0, 255]));
        var engine = new CommandEngine(renderer);
        engine.Execute(Command.Of("document.create", new NewArgs("style", 24, 24, Color: new())));
        engine.Execute(Command.Of("layer.create", new LayerArgs("art", "Art", LayerKind.Rectangle, Width: 20, Height: 20)));
        engine.Execute(Command.Of("effect.add", new Effect("tile", EffectKind.PatternOverlay, Texture: new(Png: tile)), "art"));
        byte[] baseline = renderer.Composite(engine.Current!).Rgba;
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 255, 0, 0, 255 }, baseline[..12]);
        engine.Execute(Command.Of("effect.add", new Effect("bevel", EffectKind.BevelEmboss, A: 45, B: 30,
            Size: 3, Alignment: StrokeAlignment.Inside, Texture: new(TexturePreset.Weave)), "art"));
        Assert.False(baseline.SequenceEqual(renderer.Composite(engine.Current!).Rgba));
        string path = Path.Combine(Path.GetTempPath(), $"chop-style-{Guid.NewGuid():N}.chop");
        try
        {
            ProjectFile.Save(path, engine.Current!);
            var restored = ProjectFile.Open(path);
            Assert.Equal(tile, restored.Layers[0].Effects[0].Texture!.Png);
            Assert.Equal(renderer.Composite(engine.Current!).Rgba, renderer.Composite(restored).Rgba);
        }
        finally { File.Delete(path); }
    }
}
