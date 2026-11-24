using System.Buffers.Binary;
using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class IcoAndUndoTests
{
    [Fact]
    public void WindowsIconExportReimportsTheLargestFrameWithTransparency()
    {
        var renderer = new SkiaRenderer();
        var engine = new CommandEngine(renderer);
        engine.Execute(Command.Of("document.create", new NewArgs("icon", 64, 32, Background: "#FFFF0000")));
        byte[] ico = renderer.ExportImage(engine.Current!, ImageExportFormat.Ico);
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));
        var pixels = renderer.DecodeImage(ico);
        Assert.Equal(256, pixels.Width);
        Assert.Equal(256, pixels.Height);
        Assert.Equal(0, pixels.Rgba[3]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.Rgba.AsSpan((128 * 256 + 128) * 4, 4).ToArray());

        engine.Execute(Command.Of("document.importImage", new ImageImportArgs("opened", "Icon", Data: ico)));
        Assert.Equal(256, engine.Current!.Width);
        Assert.Equal("opened", engine.Current.Layers.Single().Id);
    }

    [Fact]
    public void UndoFirstMoveRestoresTheImportedImage()
    {
        var renderer = new SkiaRenderer();
        var engine = new CommandEngine(renderer);
        var png = renderer.EncodePng(new Pixels(2, 1, [255, 0, 0, 255, 0, 0, 0, 0]));
        engine.Execute(Command.Of("document.importImage", new ImageImportArgs("art", "Image", Data: png)));
        var imported = engine.Current;
        Assert.Equal("Imported image", engine.History[0].Name);
        engine.Execute(Command.Of("layer.move", new PointD(1, 0), "art"));
        engine.Execute(Command.Of("history.undo", 0));
        Assert.Same(imported, engine.Current);
    }
}
