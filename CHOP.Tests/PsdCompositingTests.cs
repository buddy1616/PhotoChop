using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class PsdCompositingTests
{
    private readonly SkiaRenderer renderer = new();

    [Fact]
    public void PassThroughLetsChildrenBlendWithTheBackdrop()
    {
        var backdrop = Rectangle("background", "#FFFF0000");
        var child = Rectangle("child", "#FF00FF00") with { Blend = Blend.Multiply };
        var group = new Layer("group", "Group", LayerKind.Group)
        {
            Children = [child],
            PassThrough = true
        };
        var document = new Document("composite", 8, 8) { Layers = [backdrop, group] };

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, renderer.Composite(document).Rgba[..4]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 },
            renderer.Composite(document with { Layers = [backdrop, group with { PassThrough = false }] }).Rgba[..4]);
    }

    [Fact]
    public void DifferenceBlendIsAppliedToLayersAndToColorOverlays()
    {
        var backdrop = Rectangle("background", "#FF800000");
        var foreground = Rectangle("foreground", "#FFFF0000") with { Blend = Blend.Difference };
        var document = new Document("composite", 8, 8) { Layers = [backdrop, foreground] };
        Assert.Equal(new byte[] { 127, 0, 0, 255 }, renderer.Composite(document).Rgba[..4]);

        var overlay = new Effect("overlay", EffectKind.ColorOverlay, Mode: Blend.Difference, Ink: "#FFFF0000");
        document = document with { Layers = [backdrop with { Effects = [overlay] }] };
        Assert.Equal(new byte[] { 127, 0, 0, 255 }, renderer.Composite(document).Rgba[..4]);
    }

    [Fact]
    public void VectorMaskSubtractionAndIntersectionRespectPathOrder()
    {
        var mask = new VectorMask(
            [Contour(2, 2, 5, 5), Contour(1, 1, 7, 7)],
            Operations: [VectorMaskOperation.Subtract, VectorMaskOperation.Intersect]);
        var layer = Rectangle("masked", "#FFFF0000") with { VectorMask = mask };
        var document = new Document("mask", 8, 8) { Layers = [layer] };
        var image = renderer.Composite(document);
        byte Alpha(int x, int y) => image.Rgba[(y * image.Width + x) * 4 + 3];
        Assert.Equal(0, Alpha(0, 0));
        Assert.Equal(255, Alpha(1, 1));
        Assert.Equal(0, Alpha(3, 3));
    }

    [Fact]
    public void VectorMaskContinueCombinesContoursWithEvenOddFill()
    {
        var mask = new VectorMask(
            [Contour(1, 1, 7, 7), Contour(2, 2, 5, 5)],
            Operations: [VectorMaskOperation.Union, VectorMaskOperation.Continue]);
        var layer = Rectangle("masked", "#FFFF0000") with { VectorMask = mask };
        var image = renderer.Composite(new Document("mask", 8, 8) { Layers = [layer] });
        byte Alpha(int x, int y) => image.Rgba[(y * image.Width + x) * 4 + 3];
        Assert.Equal(0, Alpha(0, 0));
        Assert.Equal(255, Alpha(1, 1));
        Assert.Equal(0, Alpha(3, 3));
    }

    private static Layer Rectangle(string id, string color) =>
        new(id, id, LayerKind.Rectangle) { Width = 8, Height = 8, Color = color };

    private static PathContour Contour(int left, int top, int right, int bottom) =>
        new([new(new(left, top)), new(new(right, top)), new(new(right, bottom)), new(new(left, bottom))], Closed: true);
}
