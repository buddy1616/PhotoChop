using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class SelectionCommandTests
{
    [Fact]
    public void AllInvertAndOutsideGesturesStayInsideCanvas()
    {
        var engine = NewEngine();
        engine.Execute(Command.Of("selection.invert", 0));
        Assert.Equal(new Area(0, 0, 9, 9), engine.Current!.Selection);
        engine.Execute(Command.Of("selection.invert", 0));
        Assert.Null(engine.Current!.Selection);

        engine.Execute(Command.Of("selection.rectangle", new RectangleSelection(new Area(-3, -2, 6, 5), SelectionMode.Replace)));
        Assert.Equal(new Area(0, 0, 3, 3), engine.Current!.Selection);
        engine.Execute(Command.Of("selection.invert", 0));
        Assert.False(engine.Current!.Selected(0, 0));
        Assert.True(engine.Current.Selected(8, 8));

        engine.Execute(Command.Of("selection.lasso", new LassoArgs([new(-3, -3), new(5, -2), new(-2, 5)], SelectionMode.Replace)));
        Assert.True(engine.Current!.Selected(0, 0));
        Assert.False(engine.Current.Selected(8, 8));
        engine.Execute(Command.Of("selection.all", 0));
        Assert.Equal(new Area(0, 0, 9, 9), engine.Current!.Selection);
        Assert.Null(engine.Current.SelectionMask);
    }

    [Fact]
    public void SmoothRoundsCornersAndFeatherControlsCopyAndClearOpacity()
    {
        var renderer = new SkiaRenderer();
        var engine = NewEngine(renderer);
        engine.Execute(Command.Of("layer.fillPixels", "#FFFFFFFF", "ink"));
        engine.Execute(Command.Of("selection.rectangle", new RectangleSelection(new Area(2, 2, 5, 5), SelectionMode.Replace)));
        engine.Execute(Command.Of("selection.smooth", 1));
        Assert.False(engine.Current!.Selected(2, 2));
        Assert.True(engine.Current.Selected(3, 2));

        engine.Execute(Command.Of("selection.rectangle", new RectangleSelection(new Area(2, 2, 5, 5), SelectionMode.Replace)));
        engine.Execute(Command.Of("selection.feather", 1));
        var feather = engine.Current!;
        Assert.Equal((byte)255, feather.SelectionAmount(4, 4));
        Assert.Equal((byte)113, feather.SelectionAmount(2, 2));
        Assert.Equal((byte)28, feather.SelectionAmount(1, 1));
        Assert.Equal((byte)0, feather.SelectionAmount(0, 0));
        var copied = renderer.CopyPixels(feather, "ink")!;
        Assert.Equal((byte)28, copied.Pixels.Rgba[3]);

        engine.Execute(Command.Of("layer.clearSelection", 0, "ink"));
        var raster = renderer.Composite(engine.Current!);
        Assert.Equal((byte)227, raster.Rgba[(9 + 1) * 4 + 3]);
        Assert.Equal((byte)0, raster.Rgba[(4 * 9 + 4) * 4 + 3]);

        engine.Execute(Command.Of("history.undo", 0));
        Assert.Same(feather, engine.Current);
        engine.Execute(Command.Of("selection.invert", 0));
        Assert.Equal((byte)142, engine.Current!.SelectionAmount(2, 2));
    }

    private static CommandEngine NewEngine(SkiaRenderer? renderer = null)
    {
        var engine = new CommandEngine(renderer ?? new SkiaRenderer());
        engine.Execute(Command.Of("document.create", new NewArgs("selection", 9, 9, Color: PixelBuffer.Rgba8)));
        engine.Execute(Command.Of("layer.create", new LayerArgs("ink", "Ink", LayerKind.Raster)));
        return engine;
    }
}
