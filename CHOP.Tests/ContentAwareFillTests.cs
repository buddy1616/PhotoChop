using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class ContentAwareFillTests
{
    [Fact]
    public async Task PaintedSamplesFillOnlySelectedPixelsAndCreateOneUndoableLayer()
    {
        var renderer = new SkiaRenderer();
        var engine = new CommandEngine(renderer);
        engine.Execute(Command.Of("document.create", new NewArgs("test", 16, 12, Color: PixelBuffer.Rgba8)));
        var pixels = new Pixels(16, 12, new byte[16 * 12 * 4]);
        for (int i = 0; i < 16 * 12; i++)
        {
            pixels.Rgba[i * 4 + (i % 16 < 6 ? 2 : 0)] = 255;
            pixels.Rgba[i * 4 + 3] = 255;
        }
        engine.Execute(Command.Of("layer.importImage", new ImageImportArgs("base", "Base", Data: renderer.EncodePng(pixels))));
        engine.Execute(Command.Of("layer.create", new LayerArgs("above", "Above", LayerKind.Raster)));
        engine.Execute(Command.Of("selection.rectangle", new RectangleSelection(new Area(8, 4, 3, 3), SelectionMode.Replace)));
        engine.Execute(Command.Of("selection.rectangle", new RectangleSelection(new Area(9, 5, 1, 1), SelectionMode.Subtract)));
        var before = engine.Current!;
        var options = new ContentAwareFillArgs("fill", SampleAll: false, Strokes:
            [new([new(2.5, 2.5), new(2.5, 9.5)], 2, true)]);
        var command = Command.Of("layer.contentAwareFill", options, "base");
        var draft = engine.Preview(before, command);
        int history = engine.History.Count;
        await engine.ExecuteContentAwareFillAsync(options, "base");
        var after = engine.Current!;
        Assert.Equal(new[] { "base", "fill", "above" }, after.Layers.Select(layer => layer.Id));
        Assert.Same(before.Layers[0], after.Layers[0]);
        Assert.Equal(history + 1, engine.History.Count);
        var filled = after.Layers[1].Pixels!;
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, filled.Rgba[..4]);
        Assert.Equal((byte)0, filled.Rgba[4 * 4 + 3]);
        Assert.Equal(renderer.Composite(draft).Rgba, renderer.Composite(after).Rgba);
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Same(before, engine.Current);
    }

    [Fact]
    public void RemovingPaintedSourcesRejectsAnEmptySamplingArea()
    {
        var source = new Pixels(3, 1, [ 20, 30, 40, 255, 20, 30, 40, 255, 20, 30, 40, 255 ]);
        var doc = new Document("test", 3, 1) { Selection = new Area(1, 0, 1, 1) };
        Assert.Throws<InvalidDataException>(() => ContentAwareFill.Synthesize(doc, source,
            new("fill", Strokes: [new([new(1.5, .5)], 3, false)])));
    }
}
