using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Chop.Core;
using Chop.Rendering;
using Xunit;

namespace Chop.Tests;

public sealed class PsdImportTests
{
    private readonly SkiaRenderer renderer = new();

    [Fact]
    public void PreservesEditableGroupsStackingClippingAndRasterMaskOffsets()
    {
        var group = new LayerFixture("Folder", 0, 0, 0, 0, [], Section: 1, UnicodeName: "Grüppe");
        var top = new LayerFixture("Clipped", 0, 0, 7, 6, [.. Rgb(7, 6, 223, 22, 41), new(-1, Enumerable.Repeat((byte)190, 42).ToArray())], Clipping: 1, Opacity: 128);
        var mask = new MaskFixture(2, 2, 1, 2, [255, 128], Default: 0);
        var basis = new LayerFixture("Base", 1, 1, 4, 4, [.. Rgb(4, 4, 31, 76, 219), new(-2, mask.Data)], Mask: mask);
        var divider = new LayerFixture("</Layer group>", 0, 0, 0, 0, [], Section: 3);
        var bottom = new LayerFixture("Hidden", 0, 0, 1, 1, Rgb(1, 1, 10, 160, 10), Hidden: true);

        var document = Read(Document(7, 6, layers: [bottom, divider, basis, top, group], dpi: 300));
        Assert.Equal((7, 6), (document.Width, document.Height));
        Assert.Equal(300, document.PixelsPerInch);
        Assert.Collection(document.Layers,
            layer => { Assert.Equal("Hidden", layer.Name); Assert.False(layer.Visible); },
            layer =>
            {
                Assert.Equal(LayerKind.Group, layer.Kind);
                Assert.Equal("Grüppe", layer.Name);
                Assert.Collection(layer.Children,
                    child => { Assert.Equal("Base", child.Name); Assert.Equal((4, 4), (child.Pixels!.Width, child.Pixels.Height)); Assert.NotNull(child.Mask); },
                    child => { Assert.Equal("Clipped", child.Name); Assert.True(child.Clipped); Assert.Equal(128 / 255d, child.Opacity, 6); Assert.Equal(190, child.Pixels!.Rgba[3]); });
            });

        byte[] composite = renderer.Composite(document).Rgba;
        Assert.Equal(0, composite[(1 * 7 + 1) * 4 + 3]);
        Assert.Equal(255, composite[(2 * 7 + 2) * 4 + 3]);
        Assert.InRange(composite[(3 * 7 + 2) * 4 + 3], 127, 129);
        Assert.Equal(0, composite[(4 * 7 + 2) * 4 + 3]);
        Assert.True(composite[(2 * 7 + 2) * 4] > 31);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ImportsFlattenedCompositeInEverySupportedCompression(int compression)
    {
        byte[][] rgb = [[23, 99, 141, 254, 12, 3], [88, 65, 22, 54, 71, 12], [5, 19, 26, 24, 33, 44]];
        var doc = Read(Document(3, 2, composite: rgb, compression: compression));
        Assert.Equal(new ColorSettings(ChannelColorMode.Rgb, ChannelDepth.Bit8), doc.Color);
        var pixels = renderer.Composite(doc).Rgba;
        for (int i = 0; i < 6; i++)
            Assert.Equal(new byte[] { rgb[0][i], rgb[1][i], rgb[2][i], 255 }, pixels.AsSpan(i * 4, 4).ToArray());
    }

    [Fact]
    public void ReadsSixteenBitLayerInfoTagAndPreservesNativeSamplesAfterProjectSave()
    {
        var color = new LayerFixture("Precise", 0, 0, 2, 1,
            [new(0, [0x12, 0x34, 0x98, 0x76]), new(1, [0x65, 0x43, 0x35, 0x13]), new(2, [0x0f, 0x0a, 0x39, 0xf1])]);
        var doc = Read(Document(2, 1, depth: 16, layers: [color]));
        Assert.Equal(ChannelDepth.Bit16, doc.Color.Depth);
        var layer = Assert.Single(doc.Layers);
        Assert.Equal("Precise", layer.Name);
        var native = layer.Pixels!.Native!;
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(native.Planes[0]));
        Assert.Equal(0x9876, BinaryPrimitives.ReadUInt16LittleEndian(native.Planes[0].AsSpan(2)));
        Assert.Equal(0x3513, BinaryPrimitives.ReadUInt16LittleEndian(native.Planes[1].AsSpan(2)));

        var path = Path.Combine(Path.GetTempPath(), $"chop-psd-{Guid.NewGuid():N}.chop");
        try
        {
            ProjectFile.Save(path, doc);
            var reopened = Assert.Single(ProjectFile.Open(path).Layers);
            Assert.Equal(native.Planes[0], reopened.Pixels!.Native!.Planes[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PreservesFloatCompositeSamplesWhenUsingZipPrediction()
    {
        static byte[] Samples(params float[] values) => Bytes(output =>
        {
            foreach (float value in values) output.I32(BitConverter.SingleToInt32Bits(value));
        });
        var document = Read(Document(3, 2, depth: 32, compression: 3, composite:
            [Samples(.125f, .25f, .375f, .5f, .625f, .75f), Samples(0, .1f, .2f, .3f, .4f, .5f), Samples(.9f, .8f, .7f, .6f, .5f, .4f)]));
        var pixels = Assert.Single(document.Layers).Pixels!;
        Assert.Equal(ChannelDepth.Bit32, document.Color.Depth);
        Assert.Equal(.125f, ChannelSamples.Read(pixels.Native!.Planes[0], ChannelDepth.Bit32, 0));
        Assert.Equal(.75f, ChannelSamples.Read(pixels.Native.Planes[0], ChannelDepth.Bit32, 5));
        Assert.Equal(.7f, ChannelSamples.Read(pixels.Native.Planes[2], ChannelDepth.Bit32, 2));
    }

    [Fact]
    public void ImportsGrayscaleAndCmykCompositeChannelsWithCorrectPolarity()
    {
        var gray = Read(Document(1, 1, mode: 1, composite: [[77]]));
        Assert.Equal(ChannelColorMode.Grayscale, gray.Color.Mode);
        Assert.Equal(new byte[] { 77, 77, 77, 255 }, renderer.Composite(gray).Rgba);
        var cmyk = Read(Document(1, 1, mode: 4, composite: [[0], [255], [255], [255]]));
        Assert.Equal(ChannelColorMode.Cmyk, cmyk.Color.Mode);
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, renderer.Composite(cmyk).Rgba);
    }

    [Fact]
    public void KeepsNamedExtraChannelsSeparateFromCompositeTransparency()
    {
        var document = Read(Document(2, 1, alphaName: "Saved selection", composite:
            [[25, 80], [40, 120], [65, 190], [0, 255]]));
        var pixels = Assert.Single(document.Layers).Pixels!;
        Assert.Equal(new byte[] { 25, 40, 65, 255, 80, 120, 190, 255 }, pixels.Rgba);
        var alpha = Assert.Single(document.Channels);
        Assert.Equal("Saved selection", alpha.Info.Name);
        Assert.Equal(new byte[] { 0, 255 }, alpha.Samples);
    }

    [Fact]
    public async Task CliImportsLayeredPsdAndSavesNativeProjectAndPng()
    {
        string folder = Directory.CreateTempSubdirectory("chop-psd-cli-").FullName;
        try
        {
            string sourcePath = Path.Combine(folder, "source.psd");
            string scriptPath = Path.Combine(folder, "import.json");
            string projectPath = Path.Combine(folder, "saved.chop");
            string pngPath = Path.Combine(folder, "saved.png");
            var red = new LayerFixture("Red pixel", 1, 0, 1, 1, Rgb(1, 1, 240, 0, 8));
            byte[] profile = new byte[132];
            BinaryPrimitives.WriteInt32BigEndian(profile, profile.Length);
            Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
            await File.WriteAllBytesAsync(sourcePath, Document(3, 2, layers: [red], profile: profile));

            Command[] commands =
            [
                Command.Of("document.importPsd", new { id = "psd-cli", path = new Dictionary<string, string> { ["$param"] = "source" } }),
                Command.Of("document.save", new { path = new Dictionary<string, string> { ["$param"] = "project" } }),
                Command.Of("document.export", new { path = new Dictionary<string, string> { ["$param"] = "png" } })
            ];
            await File.WriteAllTextAsync(scriptPath, JsonSerializer.Serialize(commands, Format.Json));

            string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var start = new ProcessStartInfo(dotnet) { RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in new[] { Path.Combine(AppContext.BaseDirectory, "cli", "CHOP.CLI.dll"), "run", scriptPath,
                "source=" + JsonSerializer.Serialize(sourcePath), "project=" + JsonSerializer.Serialize(projectPath),
                "png=" + JsonSerializer.Serialize(pngPath) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await errors);

            File.Delete(sourcePath);
            var project = ProjectFile.Open(projectPath);
            Assert.Equal("Red pixel", Assert.Single(project.Layers).Name);
            Assert.Equal(profile, project.IccProfile);
            var png = renderer.DecodePng(await File.ReadAllBytesAsync(pngPath));
            Assert.Equal((3, 2), (png.Width, png.Height));
            Assert.Equal(new byte[] { 240, 0, 8, 255 }, png.Rgba.AsSpan(4, 4).ToArray());
            Assert.Equal(renderer.Composite(project).Rgba, png.Rgba);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ImportsClosedBezierVectorMask()
    {
        var rectangle = new (double X, double Y)[] { (.25, .25), (.75, .25), (.75, .75), (.25, .75) };
        var layer = new LayerFixture("Vector Mask", 0, 0, 8, 8, Rgb(8, 8, 30, 60, 210), Vector: rectangle);
        var document = Read(Document(8, 8, layers: [layer]));
        var mask = Assert.Single(document.Layers).VectorMask;
        Assert.NotNull(mask);
        var contour = Assert.Single(mask.Contours);
        Assert.True(contour.Closed);
        Assert.Equal(4, contour.Nodes.Length);
        var pixels = renderer.Composite(document).Rgba;
        Assert.Equal(0, pixels[3]);
        Assert.Equal(255, pixels[(4 * 8 + 4) * 4 + 3]);
    }

    [Fact]
    public void RejectsUnsupportedVersionsAndTruncatedPayloads()
    {
        byte[] valid = Document(3, 2);
        byte[] wrongVersion = (byte[])valid.Clone();
        wrongVersion[5] = 3;
        Assert.Throws<InvalidDataException>(() => Read(wrongVersion));
        var truncated = Record.Exception(() => Read(valid[..(valid.Length - 2)]));
        Assert.True(truncated is IOException or InvalidDataException, truncated?.ToString());
    }

    private static Document Read(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        return PsdImport.Read(input, "psd-import").Document;
    }

    private static byte[] Document(int width, int height, int depth = 8, LayerFixture[]? layers = null,
        byte[][]? composite = null, int compression = 0, int dpi = 0, int mode = 3, string? alphaName = null, byte[]? profile = null) => Bytes(file =>
    {
        file.Key("8BPS");
        file.U16(1);
        file.Data(new byte[6]);
        int channelCount = composite?.Length ?? (mode == 1 ? 1 : mode == 4 ? 4 : 3);
        file.U16(channelCount);
        file.I32(height);
        file.I32(width);
        file.U16(depth);
        file.U16(mode);
        file.I32(0); // Color mode data
        file.Block(resources =>
        {
            if (dpi != 0)
                resources.Resource(1005, resolution =>
                {
                    resolution.I32(dpi << 16);
                    resolution.U16(1);
                    resolution.U16(1);
                    resolution.I32(dpi << 16);
                    resolution.U16(1);
                    resolution.U16(1);
                });
            if (profile is not null) resources.Resource(1039, icc => icc.Data(profile));
            if (alphaName is not null)
                resources.Resource(1006, alpha =>
                {
                    var bytes = Encoding.ASCII.GetBytes(alphaName);
                    alpha.U8(bytes.Length);
                    alpha.Data(bytes);
                });
        });
        file.Block(layerAndMask =>
        {
            if (layers is null) return;
            if (depth == 8) layerAndMask.Block(data => WriteLayerInfo(data, layers));
            else layerAndMask.I32(0);
            layerAndMask.I32(0); // Global layer mask
            if (depth != 8) layerAndMask.Tag(depth == 16 ? "Lr16" : "Lr32", data => WriteLayerInfo(data, layers));
        });
        var planes = composite ?? Enumerable.Range(0, channelCount).Select(_ => new byte[width * height * (depth / 8)]).ToArray();
        file.U16(compression);
        file.Data(Compressed(planes, width, height, depth, compression));
    });

    private static void WriteLayerInfo(BigEndian output, LayerFixture[] layers)
    {
        output.U16(layers.Length);
        foreach (LayerFixture layer in layers)
        {
            output.I32(layer.Y);
            output.I32(layer.X);
            output.I32(layer.Y + layer.Height);
            output.I32(layer.X + layer.Width);
            output.U16(layer.Channels.Length);
            foreach (SamplePlane channel in layer.Channels)
            {
                output.U16(channel.Id);
                output.I32(channel.Data.Length + 2);
            }
            output.Key("8BIM");
            output.Key("norm");
            output.U8(layer.Opacity);
            output.U8(layer.Clipping);
            output.U8(layer.Hidden ? 2 : 0);
            output.U8(0);
            output.Block(extra =>
            {
                extra.Block(maskData =>
                {
                    if (layer.Mask is not { } mask) return;
                    maskData.I32(mask.Y);
                    maskData.I32(mask.X);
                    maskData.I32(mask.Y + mask.Height);
                    maskData.I32(mask.X + mask.Width);
                    maskData.U8(mask.Default);
                    maskData.U8(0);
                    maskData.U16(0);
                });
                extra.I32(0); // Blending ranges
                extra.PascalName(layer.Name);
                if (layer.Section != 0) extra.Tag("lsct", divider => divider.I32(layer.Section));
                if (layer.Vector is { } points) extra.Tag("vmsk", vector =>
                {
                    vector.I32(3); // Vector mask version
                    vector.I32(0); // Enabled, linked and not inverted
                    vector.U16(6); // Path fill rule
                    vector.Data(new byte[24]);
                    vector.U16(8); // Initial fill is outside
                    vector.Data(new byte[24]);
                    vector.U16(0); // Closed subpath
                    vector.U16(points.Length);
                    vector.U16(1); // Combine
                    vector.Data(new byte[20]);
                    foreach (var point in points)
                    {
                        vector.U16(2); // Closed unlinked knot with coincident handles
                        for (int i = 0; i < 3; i++)
                        {
                            vector.I32((int)(point.Y * 16777216));
                            vector.I32((int)(point.X * 16777216));
                        }
                    }
                });
                if (layer.UnicodeName is { } name) extra.Tag("luni", unicode =>
                {
                    unicode.I32(name.Length);
                    unicode.Data(Encoding.BigEndianUnicode.GetBytes(name));
                });
            });
        }
        foreach (SamplePlane channel in layers.SelectMany(layer => layer.Channels))
        {
            output.U16(0);
            output.Data(channel.Data);
        }
        output.Pad(2);
    }

    private static byte[] Compressed(byte[][] planes, int width, int height, int depth, int compression)
    {
        byte[] data = planes.SelectMany(plane => plane).ToArray();
        if (compression == 0) return data;
        if (compression == 1)
        {
            return Bytes(rle =>
            {
                int rowSize = width * (depth / 8);
                int rows = height * planes.Length;
                for (int row = 0; row < rows; row++) rle.U16(rowSize + 1);
                for (int row = 0; row < rows; row++)
                {
                    rle.U8(rowSize - 1); // A literal PackBits packet
                    rle.Data(data.AsSpan(row * rowSize, rowSize));
                }
            });
        }
        if (compression == 3)
        {
            int bytes = depth / 8;
            int rowSize = width * bytes;
            for (int offset = 0; offset < data.Length; offset += rowSize)
            {
                Span<byte> row = data.AsSpan(offset, rowSize);
                if (depth == 16)
                {
                    for (int x = width - 1; x > 0; x--)
                    {
                        ushort a = BinaryPrimitives.ReadUInt16BigEndian(row[(x * 2)..]);
                        ushort b = BinaryPrimitives.ReadUInt16BigEndian(row[((x - 1) * 2)..]);
                        BinaryPrimitives.WriteUInt16BigEndian(row[(x * 2)..], unchecked((ushort)(a - b)));
                    }
                    continue;
                }
                if (depth == 32)
                {
                    byte[] ordered = row.ToArray();
                    for (int x = 0; x < width; x++)
                    for (int b = 0; b < 4; b++)
                        row[b * width + x] = ordered[x * 4 + b];
                }
                for (int x = rowSize - 1; x > 0; x--)
                    row[x] = unchecked((byte)(row[x] - row[x - 1]));
            }
        }
        using var encoded = new MemoryStream();
        using (var zip = new ZLibStream(encoded, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(data);
        return encoded.ToArray();
    }

    private static SamplePlane[] Rgb(int width, int height, byte r, byte g, byte b) =>
        new[] { r, g, b }.Select((value, index) => new SamplePlane(index, Enumerable.Repeat(value, width * height).ToArray())).ToArray();

    private sealed record SamplePlane(int Id, byte[] Data);
    private sealed record MaskFixture(int X, int Y, int Width, int Height, byte[] Data, byte Default);
    private sealed record LayerFixture(string Name, int X, int Y, int Width, int Height, SamplePlane[] Channels,
        int Opacity = 255, int Clipping = 0, bool Hidden = false, int Section = 0, string? UnicodeName = null, MaskFixture? Mask = null, (double X, double Y)[]? Vector = null);

    private static byte[] Bytes(Action<BigEndian> write)
    {
        using var stream = new MemoryStream();
        write(new BigEndian(stream));
        return stream.ToArray();
    }

    private sealed class BigEndian(Stream stream)
    {
        public void U8(int value) => stream.WriteByte(unchecked((byte)value));

        public void U16(int value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, unchecked((ushort)value));
            stream.Write(bytes);
        }

        public void I32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        public void Key(string value) => Data(Encoding.ASCII.GetBytes(value));
        public void Data(ReadOnlySpan<byte> value) => stream.Write(value);

        public void Block(Action<BigEndian> write)
        {
            byte[] content = Bytes(write);
            I32(content.Length);
            Data(content);
        }

        public void Resource(int id, Action<BigEndian> write)
        {
            Key("8BIM");
            U16(id);
            U16(0); // Empty Pascal name and two-byte alignment
            Block(write);
            Pad(2);
        }

        public void Tag(string key, Action<BigEndian> write)
        {
            Key("8BIM");
            Key(key);
            Block(write);
        }

        public void PascalName(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            int first = bytes.Length + 1;
            U8(bytes.Length);
            Data(bytes);
            for (int pad = (4 - first % 4) % 4; pad > 0; pad--) U8(0);
        }

        public void Pad(int multiple)
        {
            while (stream.Position % multiple != 0) U8(0);
        }
    }
}
