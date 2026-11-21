using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class LayerStyleTests
{
    [Fact]
    public void OuterMultiplyGlowBlendsWithTheUnderlyingLayer()
    {
        var renderer = new SkiaRenderer();
        var background = new Layer("background", "Background", LayerKind.Rectangle) { Width = 32, Height = 32, Color = "#FF808080" };
        var shape = new Layer("shape", "Shape", LayerKind.Rectangle) { Width = 12, Height = 12, Transform = new(10, 10) };
        var glow = new Effect("glow", EffectKind.Glow, Size: 8, Ink: "#FFC08040");
        byte[] Render(Blend blend) => renderer.Composite(new Document("test", 32, 32)
            { Layers = [background, shape with { Effects = [glow with { Mode = blend }] }] }).Rgba;
        int pixel = (16 * 32 + 8) * 4;
        Assert.True(Render(Blend.Normal)[pixel] > 128);
        Assert.True(Render(Blend.Multiply)[pixel] < 128);
    }

    [Fact]
    public void ImportedCmykColorOverlayKeepsDarkTextureFromClippingToOneHue()
    {
        var renderer = new SkiaRenderer();
        var neutral = new Pixels(2, 1, [65, 65, 65, 255, 100, 100, 100, 255]);
        var effect = new Effect("tint", EffectKind.ColorOverlay, Mode: Blend.Color, Ink: "#FFF8A022",
            CmykInk: new(.0039, .4314, .9725, 0));
        var layer = new Layer("metal", "Metal", LayerKind.Raster) { Pixels = neutral, Effects = [effect] };
        byte[] result = renderer.Composite(new Document("test", 2, 1) { Layers = [layer] }).Rgba;
        Assert.True(result[0] > result[1] && result[1] > result[2]);
        Assert.True(result[2] > 0 && result[6] > result[2]);
    }

    [Fact]
    public void DeepRoundBevelGraduallyTurnsAwayFromTheLight()
    {
        var renderer = new SkiaRenderer();
        var bevel = new Effect("bevel", EffectKind.BevelEmboss, Size: 26, Alignment: StrokeAlignment.Inside,
            Lighting: new(Depth: 10, Technique: BevelTechnique.ChiselHard));
        var disk = new Layer("art", "Art", LayerKind.Ellipse)
            { Width = 120, Height = 120, Color = "#FF808080", Effects = [bevel] };
        var rendered = renderer.Composite(new Document("test", 120, 120) { Layers = [disk] });
        double ShadeAt(double degrees)
        {
            double sum = 0;
            double a = degrees * Math.PI / 180;
            for (int radius = 43; radius <= 47; radius++)
            {
                int x = (int)Math.Round(59.5 + radius * Math.Cos(a)), y = (int)Math.Round(59.5 + radius * Math.Sin(a));
                sum += rendered.Rgba[(y * 120 + x) * 4];
            }
            return sum / 5;
        }

        Assert.True(ShadeAt(0) > ShadeAt(45) + 10);
        Assert.True(ShadeAt(45) > ShadeAt(75) + 10);
        Assert.InRange(ShadeAt(90), 121, 135);
    }

    [Fact]
    public void NormalCmykLayersCompositeTheirInkChannelsBeforeDisplayConversion()
    {
        var settings = new ColorSettings(ChannelColorMode.Cmyk, ChannelDepth.Bit8);
        Pixels Sample(byte cyan, byte magenta, byte yellow, byte black, byte alpha) =>
            PixelBuffer.FromNative(new(settings, 1, 1, [[cyan], [magenta], [yellow], [black], [alpha]]));
        var backdrop = new Layer("bottom", "Bottom", LayerKind.Raster) { Pixels = Sample(120, 20, 60, 90, 255) };
        var foreground = new Layer("top", "Top", LayerKind.Raster) { Pixels = Sample(0, 200, 120, 30, 128),
            Effects = [new("overlay", EffectKind.ColorOverlay, Ink: "#FFFF0000", Opacity: .5, CmykInk: new(0, 1, 1, 0))] };
        var doc = new Document("test", 1, 1) { Color = settings, IccProfile = new byte[128], Layers = [backdrop, foreground] };
        var actual = new SkiaRenderer().CompositeForStorage(doc);
        Assert.Equal(new byte[] { 60, 124, 124, 52, 255 }, actual.Native!.Planes.Select(plane => plane[0]).ToArray());
    }

    [Fact]
    public void OuterBevelDoesNotBevelAnEarlierGlow()
    {
        var renderer = new SkiaRenderer();
        var layer = new Layer("art", "Art", LayerKind.Rectangle)
            { Width = 24, Height = 24, Transform = new(20, 20), Color = "#FF808080" };
        var doc = new Document("test", 64, 64);
        var glow = new Effect("glow", EffectKind.Glow, Size: 15, Ink: "#FFFFFFFF");
        var bevel = new Effect("bevel", EffectKind.BevelEmboss, Size: 5,
            Lighting: new(Technique: BevelTechnique.ChiselHard));
        Pixels Render(params Effect[] effects) => renderer.Composite(doc with { Layers = [layer with { Effects = effects }] });
        var glowOnly = Render(glow);
        var styled = Render(glow, bevel);
        int distant = (32 * 64 + 9) * 4;
        Assert.True(glowOnly.Rgba[distant + 3] > 0);
        Assert.Equal(glowOnly.Rgba[distant..(distant + 4)], styled.Rgba[distant..(distant + 4)]);
        Assert.False(glowOnly.Rgba.SequenceEqual(styled.Rgba));
    }

    [Fact]
    public void OuterChiselDoesNotTurnAFaintDistantPixelIntoARidge()
    {
        var rgba = new byte[40 * 12 * 4];
        for (int y = 2; y < 10; y++)
        for (int x = 2; x < 10; x++)
            rgba[(y * 40 + x) * 4 + 3] = 255;
        rgba[(6 * 40 + 30) * 4 + 3] = 12;
        var layer = new Layer("art", "Art", LayerKind.Raster) { Pixels = new Pixels(40, 12, rgba),
            Effects = [new("bevel", EffectKind.BevelEmboss, Size: 4, Alignment: StrokeAlignment.Outside,
                Lighting: new(Technique: BevelTechnique.ChiselHard))] };
        var image = new SkiaRenderer().Composite(new Document("test", 40, 12) { Layers = [layer] });
        Assert.Equal(0, image.Rgba[(6 * 40 + 31) * 4 + 3]);
        Assert.Equal(12, image.Rgba[(6 * 40 + 30) * 4 + 3]);
    }

    [Fact]
    public void SharedLightUpdatesLinkedEffectsAndLeavesLocalAnglesAlone()
    {
        var engine = new CommandEngine(new SkiaRenderer());
        engine.Execute(Command.Of("document.create", new NewArgs("test", 100, 100)));
        engine.Execute(Command.Of("layer.create", new LayerArgs("art", "Art", LayerKind.Rectangle)));
        var bevel = new Effect("bevel", EffectKind.BevelEmboss, Size: 4, A: 225, UseGlobalLight: true);
        engine.Execute(Command.Of("effect.add", bevel, "art"));
        engine.Execute(Command.Of("effect.add", new Effect("drop", EffectKind.DropShadow, A: 45, UseGlobalLight: true), "art"));
        engine.Execute(Command.Of("effect.add", new Effect("local", EffectKind.InnerShadow, A: 90, Size: 4), "art"));
        engine.Execute(Command.Of("effect.update", bevel with { A = 30, Contour = StyleContour.Cone }, "art"));
        var document = engine.Current!;
        Assert.Equal(30, document.GlobalLightAngle);
        Assert.Equal(new double[] { 30, 210, 90 }, document.Layers[0].Effects.Select(effect => effect.A));
        Assert.Equal(StyleContour.Cone, document.Layers[0].Effects[0].Contour);
    }

    [Fact]
    public void ChiselHardHasAFlatFacetAndChiselSoftRoundsItsShoulder()
    {
        var renderer = new SkiaRenderer();
        var layer = new Layer("art", "Art", LayerKind.Rectangle) { Width = 80, Height = 80, Color = "#FF808080" };
        Pixels Render(BevelTechnique technique) => renderer.Composite(new Document("test", 80, 80)
        {
            Layers = [layer with { Effects = [new("bevel", EffectKind.BevelEmboss, Size: 16,
                Alignment: StrokeAlignment.Inside, Lighting: new(Technique: technique))] }]
        });
        int Sample(Pixels pixels, int x) => pixels.Rgba[(40 * pixels.Width + x) * 4];
        var hard = Render(BevelTechnique.ChiselHard);
        var soft = Render(BevelTechnique.ChiselSoft);
        var smooth = Render(BevelTechnique.Smooth);
        Assert.Equal(Sample(hard, 4), Sample(hard, 10));
        Assert.NotEqual(Sample(hard, 10), Sample(hard, 18));
        Assert.InRange(Sample(soft, 16), Sample(hard, 10) + 1, Sample(hard, 18) - 1);
        Assert.NotEqual(Sample(smooth, 4), Sample(smooth, 10));
    }

    [Fact]
    public void TextArcIsAReplayableEditAndChangesTheRenderedBaseline()
    {
        var renderer = new SkiaRenderer();
        var engine = new CommandEngine(renderer);
        engine.Execute(Command.Of("document.create", new NewArgs("test", 280, 140)));
        engine.Execute(Command.Of("layer.create", new LayerArgs("title", "Title", LayerKind.Text, Text: "INFERNO")));
        byte[] straight = renderer.Composite(engine.Current!).Rgba;
        engine.Execute(Command.Of("layer.text", new TextArgs("INFERNO", "Liberation Sans", 48, "#FFFFFFFF", .5), "title"));
        var curved = renderer.Composite(engine.Current!).Rgba;
        Assert.Equal(.5, engine.Current!.Layers[0].TextBend);
        Assert.Contains((byte)255, curved);
        Assert.False(straight.SequenceEqual(curved));
    }

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
