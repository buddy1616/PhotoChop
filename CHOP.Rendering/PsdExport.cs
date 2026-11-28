using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private sealed record PsdExportLayer(Layer Layer, int Section, Func<RasterEdit>? Render,
        AlphaMask? EditableMask = null, Effect[]? EditableStyles = null, bool SolidFill = false, VectorMask? EditableVectorMask = null);
    private sealed record PsdExportRecord(PsdExportLayer Entry, long Bounds, long[] ChannelLengths);

    private byte[] EncodePsd(Document document)
    {
        using var output = new MemoryStream();
        ExportPsd(document, output);
        return output.ToArray();
    }

    public void ExportPsd(Document document, Stream output, bool flattenLayers = true, bool keepLayersSeparate = true)
    {
        if (!output.CanWrite || !output.CanSeek)
            throw new ArgumentException("PSD export requires a writable, seekable stream.", nameof(output));
        if (document.Width > 30_000 || document.Height > 30_000)
            throw new InvalidDataException("PSD supports a maximum of 30,000 pixels per dimension.");
        document = ResolveFloating(document);
        var area = new Area(0, 0, document.Width, document.Height);
        Pixels? merged = null;
        Pixels CompositeImage() => merged ??= Composite(document);

        var entries = new List<PsdExportLayer>();
        if ((flattenLayers && !keepLayersSeparate) || document.Layers.Length == 0)
            entries.Add(new(new Layer("composite", "Composite", LayerKind.Raster), 0, () => new(CompositeImage(), 0, 0)));
        else
        {
            var ancestors = new List<Layer>();
            AddTier(document.Layers, ancestors);
        }

        var writer = new PsdOutput(output);
        writer.Key("8BPS");
        writer.Short(1);
        writer.Int(0);
        writer.Short(0);
        writer.Short(4);
        writer.Int(document.Height);
        writer.Int(document.Width);
        writer.Short(8);
        writer.Short(3);
        writer.Int(0); // No indexed color data.
        writer.Block(() =>
        {
            writer.Key("8BIM");
            writer.Short(1005); // ResolutionInfo
            writer.Short(0); // Empty, even-padded resource name.
            writer.Int(16);
            int dpi = unchecked((int)Math.Round(Math.Clamp(document.PixelsPerInch, 1, 65535) * 65536));
            for (int axis = 0; axis < 2; axis++)
            {
                writer.Int(dpi);
                writer.Short(1); // Pixels per inch.
                writer.Short(1); // Display inches.
            }
            if (!flattenLayers)
            {
                writer.Key("8BIM");
                writer.Short(1037); // Global Lighting Angle
                writer.Short(0);
                writer.Int(4);
                writer.Int((int)Math.Round(PsdLightAngle(document.GlobalLightAngle)));
            }
        });

        var row = new byte[document.Width];
        writer.Block(() =>
        {
            writer.Block(() =>
            {
                writer.Short((short)-entries.Count); // Negative count marks the merged alpha channel.
                var records = entries.Select(WriteRecord).ToArray();
                foreach (var record in records)
                {
                    var raster = record.Entry.Render?.Invoke();
                    var bounds = raster is null ? default : AlphaBounds(raster.Pixels);
                    if (bounds.Width > row.Length) row = new byte[bounds.Width];
                    if (raster is not null)
                        writer.Patch(record.Bounds, () => WriteBounds(new(raster.X + bounds.X, raster.Y + bounds.Y, bounds.Width, bounds.Height)));
                    for (int channel = 0; channel < 4; channel++)
                    {
                        long start = output.Position;
                        if (raster is null || bounds.Width == 0) writer.Short(0);
                        else
                        {
                            writer.Short(2); // ZIP, independently compressed for each layer channel.
                            using var zip = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true);
                            WritePlane(zip, raster.Pixels, bounds, channel);
                        }
                        int length = checked((int)(output.Position - start));
                        writer.Patch(record.ChannelLengths[channel], () => writer.Int(length));
                    }
                    if (record.Entry.EditableMask is { } mask)
                    {
                        long start = output.Position;
                        writer.Short(2);
                        using (var zip = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
                            zip.Write(mask.Alpha);
                        int length = checked((int)(output.Position - start));
                        writer.Patch(record.ChannelLengths[4], () => writer.Int(length));
                    }
                    CheckSize();
                }
            }, even: true);
            writer.Int(0); // No global layer mask.
        });

        writer.Short(0); // Raw, planar composite for broad reader compatibility.
        CheckSize();
        var image = CompositeImage();
        for (int channel = 0; channel < 4; channel++) WritePlane(output, image, area, channel);

        void CheckSize()
        {
            if (output.Position + (long)area.Width * area.Height * 4 > int.MaxValue)
                throw new InvalidDataException("The export exceeds the 2 GB PSD limit. Try exporting a single merged layer or reducing the document size.");
        }

        void AddTier(Layer[] layers, List<Layer> parents)
        {
            for (int index = 0; index < layers.Length; index++)
            {
                var layer = layers[index];
                if (layer.Kind == LayerKind.Group)
                {
                    Add(new(new Layer("section", "</Layer group>", LayerKind.Group), 3, null));
                    parents.Add(layer);
                    AddTier(layer.Children, parents);
                    parents.RemoveAt(parents.Count - 1);
                    Add(new(layer, layer.InitiallyCollapsed ? 2 : 1, null));
                    continue;
                }
                var branch = parents.ToArray();
                var below = layer.Kind == LayerKind.Effect ? layers[..index] : [];
                var source = layer;
                AlphaMask? mask = null;
                Effect[]? styles = null;
                bool fill = false;
                VectorMask? vector = null;
                if (!flattenLayers && layer.Kind is not LayerKind.Effect && EditablePsdBranch(branch) &&
                    TryPsdTranslation(layer, branch, out int dx, out int dy))
                {
                    var activeEffects = layer.TransformsEnabled ? layer.Effects.Where(ProcessesPixels).ToArray() : [];
                    bool editableStyles = activeEffects.Length > 0 && PsdStylesCanRemainEditable(activeEffects);
                    bool effectsAllowMasks = activeEffects.Length == 0 || editableStyles;
                    if (layer.VectorMask is { } originalVector && (effectsAllowMasks || !originalVector.Enabled) &&
                        TryPsdVectorMask(originalVector, area, dx, dy) is { } mappedVector)
                    {
                        vector = mappedVector;
                        source = source with { VectorMask = null };
                    }
                    if (layer.Mask is { } originalMask && layer.PixelShifts.Length == 0 &&
                        (effectsAllowMasks || !layer.MaskEnabled))
                    {
                        mask = originalMask with { X = originalMask.X + dx, Y = originalMask.Y + dy };
                        source = source with { Mask = null };
                    }
                    if (editableStyles)
                    {
                        styles = activeEffects;
                        source = source with { Effects = [] };
                    }
                    bool opaqueFill = Format.Color(layer.Color) >> 24 == 255 && effectsAllowMasks &&
                        (layer.Mask is null || mask is not null) && (layer.VectorMask is null || vector is not null);
                    fill = opaqueFill && layer.Kind == LayerKind.Fill;
                    if (opaqueFill && layer.VectorMask is null && layer.Kind is LayerKind.Rectangle or LayerKind.Ellipse)
                    {
                        vector = TryPsdVectorMask(PsdShapeMask(layer), area, dx, dy);
                        fill = vector is not null;
                    }
                }
                Add(new(layer, 0, () => RenderLayer(source, branch, below), mask, styles, fill, vector));
            }
        }

        void Add(PsdExportLayer entry)
        {
            if (entries.Count == short.MaxValue)
                throw new InvalidDataException("The document contains too many layers for PSD.");
            entries.Add(entry);
        }

        RasterEdit RenderLayer(Layer layer, Layer[] parents, Layer[] below)
        {
            // Keep the layer's opacity and fill out of the bake. Source colors and effects
            // still contribute their own alpha, which Photoshop multiplies by those settings.
            var target = layer with { Opacity = 1, FillOpacity = 1, Visible = true, Clipped = false,
                Blend = layer.Kind == LayerKind.Effect ? layer.Blend : Blend.Normal,
                Effects = layer.Kind == LayerKind.Effect ? layer.Effects :
                    layer.Effects.Where(effect => effect.Kind != EffectKind.BlendMode).ToArray() };
            if (target.Kind == LayerKind.Effect && (below.Length == 0 || !target.TransformsEnabled ||
                (CommandEngine.EffectiveBlend(target) == Blend.Normal && !target.Effects.Any(ProcessesPixels))))
                return new(new Pixels(1, 1, new byte[4]), 0, 0);

            // Bounded RGB layers can render directly, keeping pixels beyond the canvas too.
            // Use the composite path for effects that are drawn separately against the canvas.
            if (parents.Length == 0 && document.Color.Mode == ChannelColorMode.Rgb &&
                target.Kind is not (LayerKind.Fill or LayerKind.Effect) && SpatialMatrix(target) == SKMatrix.Identity &&
                target.Effects.LastOrDefault(ProcessesPixels) is not
                    { Kind: EffectKind.Glow, Alignment: StrokeAlignment.Outside, Mode: not Blend.Normal })
                return RasterizeLayer(document, target);

            Layer[] branch = layer.Kind == LayerKind.Effect ? [.. below, target] : [target];
            for (int index = parents.Length - 1; index >= 0; index--)
                branch = [parents[index] with { Children = branch, Visible = true, Opacity = 1, FillOpacity = 1,
                    Clipped = false, Blend = Blend.Normal,
                    Effects = parents[index].Effects.Where(effect => effect.Kind != EffectKind.BlendMode).ToArray() }];
            var source = document with { Layers = branch };
            return new(Composite(source), 0, 0);
        }

        PsdExportRecord WriteRecord(PsdExportLayer entry)
        {
            var layer = entry.Layer;
            long bounds = output.Position;
            WriteBounds(default);
            short channelCount = entry.EditableMask is null ? (short)4 : (short)5;
            writer.Short(channelCount);
            var lengths = new long[channelCount];
            for (short channel = 0; channel < channelCount; channel++)
            {
                writer.Short(channel switch { 3 => (short)-1, 4 => (short)-2, _ => channel });
                lengths[channel] = output.Position;
                writer.Int(0);
            }
            string blend = layer.Kind == LayerKind.Effect ? "norm" :
                PsdBlendKey(layer.TransformsEnabled ? CommandEngine.EffectiveBlend(layer) : layer.Blend);
            writer.Key("8BIM");
            writer.Key(blend);
            writer.Byte(Byte(layer.Opacity));
            writer.Byte(layer.Clipped ? (byte)1 : (byte)0);
            writer.Byte((byte)((layer.Visible ? 0 : 2) | (entry.Section == 0 && !entry.SolidFill ? 8 : 24)));
            writer.Byte(0);
            writer.Block(() =>
            {
                writer.Block(() =>
                {
                    if (entry.EditableMask is not { } mask) return;
                    WriteBounds(new(mask.X, mask.Y, mask.Width, mask.Height));
                    writer.Byte(mask.Outside);
                    writer.Byte(layer.MaskEnabled ? (byte)0 : (byte)2);
                    writer.Short(0);
                });
                writer.Int(0); // No custom blending ranges.
                var name = Encoding.ASCII.GetBytes(layer.Name[..Math.Min(layer.Name.Length, 255)]);
                writer.Byte((byte)name.Length);
                output.Write(name);
                for (int pad = (name.Length + 1) % 4; pad != 0 && pad < 4; pad++) writer.Byte(0);
                writer.Tag("luni", () =>
                {
                    writer.Int(layer.Name.Length);
                    output.Write(Encoding.BigEndianUnicode.GetBytes(layer.Name));
                });
                if (entry.SolidFill)
                    writer.Tag("SoCo", () => WritePsdSolidFill(writer, layer.Color));
                if (entry.EditableVectorMask is { } vector)
                    writer.Tag("vmsk", () => WritePsdVectorMask(writer, vector, area));
                if (entry.EditableStyles is { } styles)
                    writer.Tag("lfx2", () => WritePsdStyles(writer, styles));
                if (layer.FillOpacity != 1)
                    writer.Tag("iOpa", () => { writer.Byte(Byte(layer.FillOpacity)); writer.Byte(0); writer.Short(0); });
                if (layer.Locked)
                    writer.Tag("lspf", () => writer.Int(unchecked((int)0x80000000)));
                if (entry.Section != 0)
                    writer.Tag("lsct", () =>
                    {
                        writer.Int(entry.Section);
                        if (entry.Section == 3) return;
                        writer.Key("8BIM");
                        writer.Key(IsPassThroughGroup(layer, withinClippingStack: true) ? "pass" : blend);
                    });
            });
            return new(entry, bounds, lengths);
        }

        void WriteBounds(Area bounds)
        {
            writer.Int(bounds.Y);
            writer.Int(bounds.X);
            writer.Int(bounds.Y + bounds.Height);
            writer.Int(bounds.X + bounds.Width);
        }

        void WritePlane(Stream stream, Pixels pixels, Area bounds, int channel)
        {
            for (int y = bounds.Y; y < bounds.Y + bounds.Height; y++)
            {
                int offset = (y * pixels.Width + bounds.X) * 4 + channel;
                for (int x = 0; x < bounds.Width; x++, offset += 4) row[x] = pixels.Rgba[offset];
                stream.Write(row.AsSpan(0, bounds.Width));
            }
        }
    }

    private static Area AlphaBounds(Pixels pixels)
    {
        int left = pixels.Width, top = pixels.Height, right = -1, bottom = -1;
        for (int y = 0; y < pixels.Height; y++)
        for (int x = 0; x < pixels.Width; x++)
        {
            if (pixels.Rgba[(y * pixels.Width + x) * 4 + 3] == 0) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
        return right < 0 ? new(0, 0, 1, 1) : new(left, top, right - left + 1, bottom - top + 1);
    }

    private static string PsdBlendKey(Blend blend) => blend switch
    {
        Blend.Multiply => "mul ", Blend.Screen => "scrn", Blend.Overlay => "over", Blend.Add => "lddg",
        Blend.Darken => "dark", Blend.Lighten => "lite", Blend.ColorBurn => "idiv", Blend.ColorDodge => "div ",
        Blend.HardLight => "hLit", Blend.SoftLight => "sLit", Blend.Difference => "diff", Blend.Exclusion => "smud",
        Blend.Hue => "hue ", Blend.Saturation => "sat ", Blend.Color => "colr", Blend.Luminosity => "lum ",
        Blend.LinearBurn => "lbrn", Blend.LinearLight => "lLit", Blend.VividLight => "vLit", Blend.PinLight => "pLit",
        Blend.HardMix => "hMix", Blend.Subtract => "fsub", Blend.Divide => "fdiv", _ => "norm"
    };

    private sealed class PsdOutput(Stream stream)
    {
        public void Byte(byte value) => stream.WriteByte(value);
        public void Key(string value) => stream.Write(Encoding.ASCII.GetBytes(value));

        public void Short(short value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        public void Double(double value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
            stream.Write(bytes);
        }

        public void Int(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        public void Unicode(string value)
        {
            Int(value.Length);
            stream.Write(Encoding.BigEndianUnicode.GetBytes(value));
        }

        public void Patch(long position, Action write)
        {
            long end = stream.Position;
            stream.Position = position;
            write();
            stream.Position = end;
        }

        public void Block(Action write, bool even = false)
        {
            long length = stream.Position;
            Int(0);
            write();
            if (even && (stream.Position - length) % 2 != 0) Byte(0);
            int size = checked((int)(stream.Position - length - 4));
            Patch(length, () => Int(size));
        }

        public void Tag(string key, Action write)
        {
            Key("8BIM");
            Key(key);
            Block(write, even: true);
        }
    }
}
