using System.Buffers.Binary;

namespace Chop.Core;

public sealed record PsdImportArgs(string Id, string? Path = null, byte[]? Data = null, bool Flattened = false);
public sealed record PsdImportResult(Document Document, IReadOnlyList<string> Warnings);

/// <summary>Imports Adobe Photoshop documents using the public PSD file format specification.</summary>
public static class PsdImport
{
    private const int MaxInputBytes = 512 * 1024 * 1024;
    private const long MaxDecodedBytes = 512L * 1024 * 1024;

    public static byte[] ReadFile(string path)
    {
        using var input = File.OpenRead(path);
        return ReadInput(input);
    }

    public static PsdImportResult Read(Stream input, string documentId, bool flattened = false) =>
        Read(ReadInput(input), documentId, flattened);

    public static PsdImportResult Read(byte[] bytes, string documentId, bool flattened = false)
    {
        if (!Format.ValidId(documentId)) throw new InvalidDataException("Choose a valid document ID.");
        if (bytes.Length > MaxInputBytes) throw new InvalidDataException("The PSD exceeds the supported file size.");
        var file = PsdParser.Parse(bytes);
        return new Importer(file).Read(documentId, flattened);
    }

    private static byte[] ReadInput(Stream input)
    {
        if (input.CanSeek)
        {
            long length = input.Length - input.Position;
            if (length < 0 || length > MaxInputBytes) throw new InvalidDataException("The PSD exceeds the supported file size.");
            var buffer = new byte[(int)length];
            input.ReadExactly(buffer);
            return buffer;
        }
        using var copy = new MemoryStream();
        var chunk = new byte[81920];
        for (int count; (count = input.Read(chunk)) != 0;)
        {
            if (copy.Length + count > MaxInputBytes) throw new InvalidDataException("The PSD exceeds the supported file size.");
            copy.Write(chunk, 0, count);
        }
        return copy.ToArray();
    }

    private sealed class Importer(PsdFile file)
    {
        private readonly List<string> warnings = [];
        private long decodedBytes;
        private int layerNumber;
        private byte[][]? composite;
        private int BaseChannels => file.Mode switch { 0 or 1 or 2 or 8 => 1, 3 or 9 => 3, 4 => 4, _ => 0 };
        private ColorSettings Settings => new(file.Mode switch
        {
            0 or 1 or 8 => ChannelColorMode.Grayscale, 4 => ChannelColorMode.Cmyk, _ => ChannelColorMode.Rgb
        }, file.Depth == 1 ? ChannelDepth.Bit8 : (ChannelDepth)file.Depth);

        public PsdImportResult Read(string id, bool flattened)
        {
            Format.Dimensions(file.Width, file.Height);
            ChannelSamples.Validate(Settings);
            if (BaseChannels == 0 || file.ChannelCount < BaseChannels)
                throw new InvalidDataException("This PSD color mode is unsupported. Convert the source to RGB, Grayscale, or CMYK.");
            if (file.Mode == 2 && (file.Depth != 8 || file.ColorModeData.Length < 768))
                throw new InvalidDataException("The indexed PSD palette is missing or invalid.");
            if (file.Mode == 8) Warn("Duotone ink curves were converted to grayscale.");
            if (file.Mode == 9) Warn("Lab colors were converted to RGB.");
            if (file.IccProfile.Length > 16 * 1024 * 1024) Warn("An oversized embedded ICC profile was not imported.");
            else if (!file.IccProfile.IsEmpty)
                Warn("The embedded ICC profile was preserved. PhotoChop previews color without applying the profile.");

            var layers = flattened || file.Layers.Count == 0 ? [] : ReadLayers();
            if (layers.Length == 0 || flattened)
            {
                if (file.HasComposite == false)
                    throw new InvalidDataException("This PSD was saved without a merged image. Re-save it with Maximize Compatibility, or import its editable layers.");
                var planes = CompositeChannels();
                var pixels = ReadPixels(planes, file.Width, file.Height, HasMergedAlpha, isMerged: true);
                layers = [new Layer("psd-composite", "PSD Composite", LayerKind.Raster)
                    { Width = file.Width, Height = file.Height, Pixels = pixels }];
            }
            else if (!layers.SelectMany(Branch).Any(layer => layer.Pixels is not null || layer.Kind == LayerKind.Fill) && file.HasComposite != false)
            {
                Warn("The PSD contains no editable pixel layers. Its saved composite was imported.");
                var pixels = ReadPixels(CompositeChannels(), file.Width, file.Height, HasMergedAlpha, isMerged: true);
                layers = [new Layer("psd-composite", "PSD Composite", LayerKind.Raster)
                    { Width = file.Width, Height = file.Height, Pixels = pixels }];
            }

            var result = new Document(id, file.Width, file.Height)
            {
                Color = Settings,
                PixelsPerInch = file.Dpi is { } dpi && double.IsFinite(dpi) && dpi is >= 1 and <= 100000 ? dpi : 72,
                Layers = layers,
                Channels = ReadExtraChannels(),
                IccProfile = file.IccProfile.Length is > 0 and <= 16 * 1024 * 1024 ? file.IccProfile.ToArray() : null,
                Guides = file.Guides.Where(guide => double.IsFinite(guide.Position) && guide.Position >= 0 &&
                    guide.Position <= (guide.Vertical ? file.Width : file.Height)).Select((guide, index) =>
                    new Guide($"psd-guide-{index + 1}", guide.Vertical ? GuideAxis.Vertical : GuideAxis.Horizontal,
                        (int)Math.Round(guide.Position))).ToArray(),
                Metadata = new() { ["source.format"] = "PSD" }
            };
            return new(result, warnings.ToArray());
        }

        private static IEnumerable<Layer> Branch(Layer root)
        {
            yield return root;
            foreach (var child in root.Children)
                foreach (var item in Branch(child)) yield return item;
        }

        private Layer[] ReadLayers()
        {
            var stack = new Stack<List<Layer>>();
            stack.Push([]);
            foreach (var source in file.Layers)
            {
                if (source.SectionType == 3)
                {
                    if (stack.Count >= 24) throw new InvalidDataException("The PSD contains too many nested layer groups.");
                    stack.Push([]);
                    continue;
                }

                bool group = source.SectionType is 1 or 2;
                Layer[] children = [];
                if (group)
                {
                    if (stack.Count > 1) children = stack.Pop().ToArray();
                    else Warn("An unmatched group marker was imported as an empty group.");
                }
                var tier = stack.Peek();
                var layer = ReadLayer(source, group, children, tier.Count != 0);
                tier.Add(layer);
            }

            while (stack.Count > 1)
            {
                Warn("An unmatched group marker was imported without its group.");
                var children = stack.Pop();
                stack.Peek().AddRange(children);
            }
            return stack.Pop().ToArray();
        }

        private Layer ReadLayer(PsdLayer source, bool group, Layer[] children, bool hasBelow)
        {
            var key = group ? source.SectionBlendKey ?? source.BlendKey : source.BlendKey;
            bool passThrough = group && key == "pass";
            var blend = ReadBlend(key);
            string name = string.IsNullOrWhiteSpace(source.Name) ? (group ? "Group" : "Layer") : source.Name;
            string id = $"psd-layer-{++layerNumber}";
            Pixels? pixels = null;
            if (!group && source.Width > 0 && source.Height > 0)
            {
                var numbered = source.Channels.Where(channel => channel.Id >= 0 && channel.Id < BaseChannels)
                    .GroupBy(channel => channel.Id).ToDictionary(channels => (int)channels.Key, channels => channels.First());
                if (numbered.Count == BaseChannels)
                {
                    Format.Dimensions(source.Width, source.Height);
                    var alpha = source.Channels.FirstOrDefault(channel => channel.Id == -1 && channel.Data.Remaining > 2);
                    var planes = new byte[BaseChannels + (alpha is null ? 0 : 1)][];
                    for (int i = 0; i < BaseChannels; i++)
                        planes[i] = ReadChannel(numbered[i], source.Width, source.Height);
                    if (alpha is not null) planes[^1] = ReadChannel(alpha, source.Width, source.Height);
                    pixels = ReadPixels(planes, source.Width, source.Height, alpha is not null);
                }
                else if (source.Channels.Any(channel => channel.Id >= 0))
                    Warn("Some layers had incomplete color channels and were imported without pixels.");
            }
            string? fillColor = !group && (pixels is null || source.PixelDataIrrelevant)
                ? PsdSolidFill.Read(source) : null;
            if (fillColor is not null) pixels = null;
            WarnAboutTags(source, pixels is not null, fillColor is not null);
            var mask = ReadMask(source, out bool realMaskUsed, out bool maskEnabled);
            var vectorMask = realMaskUsed ? null : ReadVectorMask(source);
            if (passThrough && (source.Opacity != 255 || mask is not null && maskEnabled ||
                vectorMask is { Enabled: true })) Warn("Pass-through groups with masks or reduced opacity use isolated compositing.");
            if (source.Clipped && !hasBelow) Warn("Clipping was released on layers with no base layer below.");

            return new(id, name, group ? LayerKind.Group : fillColor is not null ? LayerKind.Fill : LayerKind.Raster)
            {
                Color = fillColor ?? "#FFFFFFFF",
                Width = pixels?.Width ?? file.Width, Height = pixels?.Height ?? file.Height,
                RasterX = pixels is null ? 0 : source.X, RasterY = pixels is null ? 0 : source.Y,
                Pixels = pixels, Children = children,
                Visible = source.Visible, Locked = source.Locked, Opacity = source.Opacity / 255d,
                Clipped = source.Clipped && hasBelow, PassThrough = passThrough, Blend = blend,
                Mask = mask, MaskEnabled = maskEnabled, VectorMask = vectorMask
            };
        }

        private void WarnAboutTags(PsdLayer source, bool hasPixels, bool editableFill)
        {
            var tags = source.Tags;
            if (tags.ContainsKey("TySh") || tags.ContainsKey("tySh") || tags.ContainsKey("SoLd") ||
                tags.ContainsKey("SoLE") || tags.ContainsKey("plLd") || (!editableFill &&
                (tags.ContainsKey("SoCo") || tags.ContainsKey("GdFl") || tags.ContainsKey("PtFl") ||
                    tags.ContainsKey("vscg") || tags.ContainsKey("vogk"))))
                Warn(hasPixels ? "Text, Smart Objects, and Photoshop-specific fills or shapes use their saved raster pixels." :
                    "Some Photoshop-specific layers had no editable raster and were kept as empty layers.");
            if (tags.ContainsKey("lrFX") || tags.ContainsKey("lfx2") || tags.ContainsKey("lmfx") || tags.ContainsKey("lfxs"))
                Warn("Photoshop layer styles were not translated. Import the saved composite for their rendered appearance.");
            if (tags.Keys.Any(key => key is "brit" or "levl" or "curv" or "expA" or "vibA" or "hue " or "hue2" or
                "blnc" or "blwh" or "phfl" or "mixr" or "clrL" or "nvrt" or "post" or "thrs" or "grdm" or "selc"))
                Warn("Photoshop adjustment layers were kept as placeholders. Import the saved composite for their rendered appearance.");
            if (tags.TryGetValue("iOpa", out var fill) && fill.Length > 0 && fill.Span[0] != 255)
                Warn("Separate Photoshop fill opacity was not translated.");
        }

        private Blend ReadBlend(string key) => key switch
        {
            "norm" or "pass" => Blend.Normal, "mul " => Blend.Multiply, "scrn" => Blend.Screen,
            "over" => Blend.Overlay, "lddg" or "add " => Blend.Add, "dark" => Blend.Darken,
            "lite" => Blend.Lighten, "idiv" => Blend.ColorBurn, "div " => Blend.ColorDodge,
            "hLit" => Blend.HardLight, "sLit" => Blend.SoftLight, "diff" => Blend.Difference,
            "smud" => Blend.Exclusion, "hue " => Blend.Hue, "sat " => Blend.Saturation,
            "colr" => Blend.Color, "lum " => Blend.Luminosity,
            _ => UnsupportedBlend()
        };

        private Blend UnsupportedBlend()
        {
            Warn("Unsupported Photoshop blend modes were converted to Normal.");
            return Blend.Normal;
        }

        private AlphaMask? ReadMask(PsdLayer layer, out bool realMaskUsed, out bool enabled)
        {
            var channel = layer.Channels.FirstOrDefault(item => item.Id == -2 && item.Data.Remaining > 2);
            var mask = layer.Mask;
            realMaskUsed = channel is null || mask is null || mask.Width <= 0 || mask.Height <= 0;
            if (realMaskUsed)
            {
                channel = layer.Channels.FirstOrDefault(item => item.Id == -3 && item.Data.Remaining > 2);
                mask = layer.RealMask;
                realMaskUsed = channel is not null && mask is { Width: > 0, Height: > 0 };
            }

            enabled = mask?.Enabled ?? true;
            if (channel is null || mask is null || mask.Width <= 0 || mask.Height <= 0) return null;
            var samples = ReadChannel(channel, mask.Width, mask.Height);
            var alpha = ToBytePlane(samples, mask.Width, mask.Height);
            double density = (mask.Density ?? 255) / 255d;
            byte Coverage(byte value) => (byte)Math.Round(255 - density * (255 - (mask.Inverted ? 255 - value : value)));
            for (int i = 0; i < alpha.Length; i++) alpha[i] = Coverage(alpha[i]);
            if (mask.Feather is > 0) Warn("Photoshop raster mask feathering was not translated.");
            return new(mask.Width, mask.Height, alpha, checked(mask.X + (mask.Relative ? layer.X : 0)),
                checked(mask.Y + (mask.Relative ? layer.Y : 0)), Coverage(mask.Outside));
        }

        private VectorMask? ReadVectorMask(PsdLayer source)
        {
            if (!source.Tags.TryGetValue("vmsk", out var tag) && !source.Tags.TryGetValue("vsms", out tag)) return null;
            try
            {
                var mask = PsdVectorMasks.Parse(tag, file.Width, file.Height, out bool unlinked);
                if (unlinked) Warn("Unlinked vector masks follow the layer when it is transformed in PhotoChop.");
                return mask;
            }
            catch (InvalidDataException)
            {
                Warn("Some vector masks used unsupported path records and were skipped.");
                return null;
            }
        }

        private bool HasMergedAlpha
        {
            get
            {
                if (file.ChannelCount <= BaseChannels) return false;
                string? firstName = file.AlphaNames.FirstOrDefault();
                if (file.MergedTransparency || file.AlphaIds.FirstOrDefault(uint.MaxValue) == 0 ||
                    string.Equals(firstName, "Transparency", StringComparison.OrdinalIgnoreCase)) return true;
                return file.Layers.Count == 0 && file.AlphaNames.Count == 0 && file.AlphaIds.Count == 0
                    && file.ChannelCount == BaseChannels + 1;
            }
        }

        private ChannelPlane[] ReadExtraChannels()
        {
            int first = BaseChannels + (HasMergedAlpha ? 1 : 0);
            if (first >= file.ChannelCount) return [];
            if (file.HasComposite == false)
            {
                Warn("Additional alpha and spot channels could not be read because the PSD omitted its merged image.");
                return [];
            }
            var planes = CompositeChannels();
            var result = new List<ChannelPlane>();
            int count = file.ChannelCount - first;
            int nameStart = HasMergedAlpha && file.AlphaNames.Count > count ? 1 : 0;
            int spotStart = HasMergedAlpha && file.AlphaSpots.Count > count ? 1 : 0;
            for (int index = 0; index < count; index++)
            {
                var name = file.AlphaNames.ElementAtOrDefault(index + nameStart);
                bool spot = file.AlphaSpots.ElementAtOrDefault(index + spotStart);
                byte[] samples = file.Depth == 1 ? ToBytePlane(planes[first + index], file.Width, file.Height) : planes[first + index];
                result.Add(new(new($"psd-channel-{index + 1}", string.IsNullOrWhiteSpace(name) ? $"Alpha {index + 1}" : name,
                    Spot: spot), file.Width, file.Height, Settings.Depth, samples));
            }
            return result.ToArray();
        }

        private byte[][] CompositeChannels()
        {
            if (composite is not null) return composite;
            Reserve((long)PsdCompression.PlaneLength(file.Width, file.Height, file.Depth) * file.ChannelCount);
            return composite = PsdCompression.ReadChannels(file.CompositeData, file.Width, file.Height, file.Depth, file.ChannelCount);
        }

        private byte[] ReadChannel(PsdChannel channel, int width, int height)
        {
            Reserve(PsdCompression.PlaneLength(width, height, file.Depth));
            return PsdCompression.ReadChannels(channel.Data, width, height, file.Depth, 1)[0];
        }

        private byte[] ToBytePlane(byte[] samples, int width, int height)
        {
            if (file.Depth == 8) return samples;
            var result = new byte[checked(width * height)];
            if (file.Depth == 1)
            {
                int stride = (width + 7) / 8;
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    result[y * width + x] = (samples[y * stride + x / 8] & (1 << (7 - x % 8))) != 0 ? (byte)255 : (byte)0;
            }
            else
            {
                for (int i = 0; i < result.Length; i++)
                    result[i] = ChannelSamples.Display(ChannelSamples.Read(samples, Settings.Depth, i));
            }
            return result;
        }

        private Pixels ReadPixels(byte[][] channels, int width, int height, bool alpha, bool isMerged = false)
        {
            var planes = channels.Take(BaseChannels + (alpha ? 1 : 0)).ToArray();
            int count = checked(width * height);
            Reserve((long)count * 4); // Display proxy for the decoded working planes.
            if (file.Mode == 0)
            {
                planes = planes.Select(plane => ToBytePlane(plane, width, height)).ToArray();
                for (int i = 0; i < count; i++) planes[0][i] ^= 255; // A set bitmap bit is black.
            }
            else if (file.Mode == 2)
            {
                var palette = file.ColorModeData.Span;
                byte[][] colors = [new byte[count], new byte[count], new byte[count], new byte[count]];
                for (int i = 0; i < count; i++)
                {
                    int entry = planes[0][i];
                    colors[0][i] = palette[entry];
                    colors[1][i] = palette[256 + entry];
                    colors[2][i] = palette[512 + entry];
                    colors[3][i] = entry == file.TransparencyIndex ? (byte)0 : alpha ? planes[1][i] : (byte)255;
                }
                planes = colors;
            }
            else if (file.Mode == 4)
            {
                for (int channel = 0; channel < BaseChannels; channel++)
                {
                    planes[channel] = (byte[])planes[channel].Clone();
                    for (int i = 0; i < count; i++)
                    {
                        if (file.Depth is 8 or 16)
                        {
                            int size = ChannelSamples.Bytes(Settings.Depth);
                            for (int offset = 0; offset < size; offset++) planes[channel][i * size + offset] ^= 255;
                        }
                        else ChannelSamples.Write(planes[channel], Settings.Depth, i,
                            1 - ChannelSamples.Read(planes[channel], Settings.Depth, i));
                    }
                }
            }
            else if (file.Mode == 9)
                return ConvertLab(planes, width, height, alpha);

            if (isMerged && alpha && file.Mode is 1 or 3 && file.Writer?.Contains("Adobe", StringComparison.OrdinalIgnoreCase) == true)
            {
                for (int channel = 0; channel < BaseChannels; channel++)
                {
                    planes[channel] = (byte[])planes[channel].Clone();
                    for (int i = 0; i < count; i++)
                    {
                        float opacity = ChannelSamples.Read(planes[BaseChannels], Settings.Depth, i);
                        if (opacity <= 0 || opacity >= 1) continue;
                        float value = ChannelSamples.Read(planes[channel], Settings.Depth, i);
                        ChannelSamples.Write(planes[channel], Settings.Depth, i, (value + opacity - 1) / opacity);
                    }
                }
            }

            return PixelBuffer.Convert(PixelBuffer.FromNative(new(Settings, width, height, planes)), Settings);
        }

        private Pixels ConvertLab(byte[][] planes, int width, int height, bool alpha)
        {
            var buffer = new PixelBuffer(width, height, Settings);
            for (int i = 0; i < width * height; i++)
            {
                double l = ChannelSamples.Read(planes[0], Settings.Depth, i) * 100;
                double a = ChannelSamples.Read(planes[1], Settings.Depth, i) * 255 - 128;
                double b = ChannelSamples.Read(planes[2], Settings.Depth, i) * 255 - 128;
                double f = (l + 16) / 116;
                static double Inverse(double t) => t > 6d / 29 ? t * t * t : (t - 4d / 29) * 108 / 841;
                // D50 Lab to linear sRGB through a D50-adapted sRGB matrix.
                double x = .96422 * Inverse(f + a / 500), y = Inverse(f), z = .82521 * Inverse(f - b / 200);
                static double Transfer(double t) => t <= .0031308 ? 12.92 * t : 1.055 * Math.Pow(t, 1 / 2.4) - .055;
                buffer.Set(i, new(Transfer(3.1338561 * x - 1.6168667 * y - .4906146 * z),
                    Transfer(-.9787684 * x + 1.9161415 * y + .0334540 * z),
                    Transfer(.0719453 * x - .2289914 * y + 1.4052427 * z),
                    alpha ? ChannelSamples.Read(planes[3], Settings.Depth, i) : 1));
            }
            return buffer.Finish();
        }

        private void Reserve(long bytes)
        {
            decodedBytes += bytes;
            if (decodedBytes > MaxDecodedBytes) throw new InvalidDataException("The PSD layers exceed the supported decoded size.");
        }

        private void Warn(string warning)
        {
            if (!warnings.Contains(warning)) warnings.Add(warning);
        }
    }
}
