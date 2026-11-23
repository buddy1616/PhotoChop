using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class InvertTests
{
    [Fact]
    public void RasterInvertPreservesNativeAlphaAndUsesFeatheredDocumentCoordinates()
    {
        var engine = new CommandEngine(new SkiaRenderer());
        var format = new ColorSettings(ChannelColorMode.Rgb, ChannelDepth.Bit16);
        engine.Execute(Command.Of("document.create", new NewArgs("invert", 8, 8, Color: format)));
        var buffer = new PixelBuffer(4, 1, format);
        for (int i = 0; i < 4; i++) buffer.Set(i, new(.2, .4, .6, .7));
        engine.Execute(Command.Of("layer.pastePixels",
            new PastePixelsArgs("ink", "Ink", X: 2, Y: 3, Native: buffer.Finish().Native)));
        engine.Execute(Command.Of("document.selection", new Area(3, 3, 1, 1)));
        engine.Execute(Command.Of("layer.invertPixels", 0, "ink"));
        var prefix = engine.Current!;
        var current = prefix.Layers.Single().Pixels!;
        Assert.Equal(.2, PixelBuffer.Get(current, 0).R, 4);
        Assert.Equal(.8, PixelBuffer.Get(current, 1).R, 4);
        Assert.Equal(.7, PixelBuffer.Alpha(current, 1), 4);
        Assert.Equal(format, current.Native!.Settings);

        engine.Execute(Command.Of("history.undo", 0));
        engine.Execute(Command.Of("selection.feather", 1));
        engine.Execute(Command.Of("layer.invertPixels", 0, "ink"));
        current = engine.Current!.Layers.Single().Pixels!;
        Assert.Equal(.2 + .6 * 28 / 255, PixelBuffer.Get(current, 1).R, 4);
        Assert.Equal(.2, PixelBuffer.Get(current, 3).R, 4);
    }

    [Fact]
    public void WholeRasterInvertPreservesAlphaAndCanBeReversedExactly()
    {
        var renderer = new SkiaRenderer();
        foreach (var settings in new[] { PixelBuffer.Rgba8, new ColorSettings(ChannelColorMode.Rgb, ChannelDepth.Bit16) })
        {
            var input = new PixelBuffer(37, 4, settings);
            var random = new Random(19);
            for (int i = 0; i < 37 * 4; i++)
                input.Set(i, new(random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble()));
            var original = input.Finish();
            var layer = new Layer("pixels", "Pixels", LayerKind.Raster) { Pixels = original };
            var doc = new Document("invert", 37, 4) { Color = settings, Layers = [layer] };
            var inverted = renderer.InvertPixels(doc, layer);
            for (int i = 0; i < original.Rgba.Length; i++)
            {
                byte expected = i % 4 == 3 ? original.Rgba[i] : (byte)(255 - original.Rgba[i]);
                Assert.Equal(expected, inverted.Pixels!.Rgba[i]);
            }
            var restored = renderer.InvertPixels(doc with { Layers = [inverted] }, inverted).Pixels!;
            Assert.Equal(original.Rgba, restored.Rgba);
            if (original.Native is { } native)
                for (int c = 0; c < native.Planes.Length; c++) Assert.Equal(native.Planes[c], restored.Native!.Planes[c]);
        }
    }

    [Fact]
    public void MaskInvertExtendsSelectedAreaAndKeepsOutsideDefaultUntilWholeMaskIsInverted()
    {
        var engine = new CommandEngine(new SkiaRenderer());
        engine.Execute(Command.Of("document.create", new NewArgs("invert", 8, 8)));
        engine.Execute(Command.Of("layer.create", new LayerArgs("ink", "Ink", LayerKind.Raster, Transform: new(2, 1))));
        engine.Execute(Command.Of("mask.paste", new AlphaMask(1, 1, [20], Outside: 180), "ink"));
        engine.Execute(Command.Of("document.selection", new Area(3, 1, 1, 1)));
        engine.Execute(Command.Of("mask.invert", 0, "ink"));
        var mask = engine.Current!.Layers.Single().Mask!;
        Assert.Equal(20, mask.At(0, 0));
        Assert.Equal(75, mask.At(1, 0));
        Assert.Equal(180, mask.At(2, 0));
        engine.Execute(Command.Of<Area?>("document.selection", null));
        engine.Execute(Command.Of("mask.invert", 0, "ink"));
        mask = engine.Current!.Layers.Single().Mask!;
        Assert.Equal(235, mask.At(0, 0));
        Assert.Equal(180, mask.At(1, 0));
        Assert.Equal(75, mask.At(2, 0));
    }
}
