using System.IO.Compression;
using System.Text.Json;

namespace Chop.Core;

/// <summary>Versioned ZIP document with a JSON manifest and embedded RGBA resources.</summary>
public static class ProjectFile
{
    private const int MaxProfileBytes = 16 * 1024 * 1024;
    private const string ProfilePath = "color/source.icc";
    private const int MaxManifestBytes = 8_000_000;
    private const int MaxRasterBytes = 512 * 1024 * 1024;
    private sealed record Manifest(int Version, Document Document, Dictionary<string, Resource> Resources, Dictionary<string, Resource>? Masks = null,
        Dictionary<string, ShiftResource[]>? Shifts = null, Dictionary<string, ColorResource>? LayerChannels = null,
        ExtraChannelResource[]? Channels = null, bool HasIccProfile = false);
    private sealed record ColorResource(ColorSettings Settings, Resource[] Planes);
    private sealed record ExtraChannelResource(ChannelInfo Info, ChannelDepth Depth, Resource Plane);
    private sealed record ShiftResource(Area Source, int Dx, int Dy, string? Map, bool Clear = false, string? Patch = null, Resource? NativePatch = null);
    private sealed record Resource(string Path, int Width, int Height, int X = 0, int Y = 0, byte Outside = 255, ColorSettings? Color = null, int Planes = 0);

    public static void Save(string path, Document document)
    {
        if (document.Floating is not null) throw new InvalidDataException("Resolve the floating pixels before saving the document.");
        var full = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var resources = new Dictionary<string, Resource>();
                var masks = new Dictionary<string, Resource>();
                var shifts = new Dictionary<string, ShiftResource[]>();
                var layerChannels = new Dictionary<string, ColorResource>();
                long rasterBytes = 0;
                void WriteResource(Dictionary<string, Resource> map, string id, Resource resource, byte[] bytes)
                {
                    rasterBytes += bytes.LongLength;
                    if (rasterBytes > MaxRasterBytes) throw new InvalidDataException("Image resources exceed the supported size.");
                    map.Add(id, resource);
                    using var output = archive.CreateEntry(resource.Path).Open();
                    output.Write(bytes);
                }
                Resource WritePlane(string name, int width, int height, byte[] bytes)
                {
                    rasterBytes += bytes.LongLength;
                    if (rasterBytes > MaxRasterBytes) throw new InvalidDataException("Image resources exceed the supported size.");
                    using var output = archive.CreateEntry(name).Open();
                    output.Write(bytes);
                    return new(name, width, height);
                }
                Resource WritePixels(string name, Pixels source)
                {
                    var pixels = PixelBuffer.Convert(source, document.Color);
                    if (pixels.Native is not { } native) return WritePlane(name + ".rgba", pixels.Width, pixels.Height, pixels.Rgba);
                    ChannelSamples.Validate(native);
                    rasterBytes += native.Planes.Sum(plane => plane.LongLength);
                    if (rasterBytes > MaxRasterBytes) throw new InvalidDataException("Image resources exceed the supported size.");
                    string path = name + ".planes";
                    using var target = archive.CreateEntry(path).Open();
                    foreach (var plane in native.Planes) target.Write(plane);
                    return new(path, pixels.Width, pixels.Height, Color: native.Settings, Planes: native.Planes.Length);
                }
                foreach (var layer in document.AllLayers())
                {
                    if (layer.Pixels is { } pixels)
                        resources.Add(layer.Id, WritePixels($"pixels/{layer.Id}", pixels));
                    if (layer.SourceChannels is { } source)
                    {
                        ChannelSamples.Validate(source);
                        layerChannels.Add(layer.Id, new(source.Settings, source.Planes.Select((samples, i) =>
                            WritePlane($"channels/layers/{layer.Id}/{i}.bin", source.Width, source.Height, samples)).ToArray()));
                    }
                    if (layer.Mask is { } mask)
                        WriteResource(masks, layer.Id, new($"masks/{layer.Id}.alpha", mask.Width, mask.Height, mask.X, mask.Y, mask.Outside), mask.Alpha);
                    if (layer.PixelShifts.Length != 0)
                        shifts[layer.Id] = layer.PixelShifts.Select((shift, i) =>
                        {
                            string? map = null;
                            if (shift.Bits.Length != 0)
                            {
                                map = $"shifts/{layer.Id}/{i}.bits";
                                rasterBytes += shift.Bits.LongLength;
                                if (rasterBytes > MaxRasterBytes) throw new InvalidDataException("Image resources exceed the supported size.");
                                using var output = archive.CreateEntry(map).Open();
                                output.Write(shift.Bits);
                            }
                            Resource? patchResource = shift.Patch is { } patch ? WritePixels($"shifts/{layer.Id}/{i}", patch) : null;
                            return new ShiftResource(shift.Source, shift.Dx, shift.Dy, map, shift.Clear,
                                patchResource is { Color: null } ? patchResource.Path : null,
                                patchResource?.Color is null ? null : patchResource);
                        }).ToArray();
                }
                var extraChannels = document.Channels.Select(channel =>
                {
                    ChannelSamples.Validate(channel.Width, channel.Height, channel.Depth, channel.Samples);
                    return new ExtraChannelResource(channel.Info, channel.Depth, WritePlane(
                        $"channels/document/{channel.Info.Id}.bin", channel.Width, channel.Height, channel.Samples));
                }).ToArray();
                if (document.IccProfile is { } profile)
                {
                    if (profile.Length is 0 or > MaxProfileBytes) throw new InvalidDataException("Invalid ICC profile size.");
                    using var output = archive.CreateEntry(ProfilePath).Open();
                    output.Write(profile);
                }
                var json = JsonSerializer.SerializeToUtf8Bytes(new Manifest(14, document, resources, masks, shifts, layerChannels,
                    extraChannels, document.IccProfile is not null), Format.Json);
                if (json.Length > MaxManifestBytes) throw new InvalidDataException("CHOP manifest exceeds the supported size.");
                using var manifest = archive.CreateEntry("manifest.json").Open();
                manifest.Write(json);
            }
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static Document Open(string path)
    {
        using var input = File.OpenRead(path);
        return Open(input);
    }

    public static Document Open(Stream input)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Missing CHOP manifest.");
        if (entry.Length > MaxManifestBytes) throw new InvalidDataException("CHOP manifest exceeds the supported size.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(stream, Format.Json)
            ?? throw new InvalidDataException("Invalid CHOP manifest.");
        if (manifest.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14)) throw new InvalidDataException($"Unsupported CHOP version {manifest.Version}.");
        var doc = manifest.Document ?? throw new InvalidDataException("Missing document.");
        if (manifest.Resources is null || doc.Layers is null || doc.Metadata is null ||
            !Format.ValidId(doc.Id) || doc.ColorSpace != "sRGB" || doc.PixelsPerInch is < 1 or > 100000 ||
            !double.IsFinite(doc.GlobalLightAngle) || doc.GlobalLightAngle is < 0 or >= 360)
            throw new InvalidDataException("Invalid document metadata.");
        Format.Dimensions(doc.Width, doc.Height);
        doc.ImageOrientation?.Inverse();
        doc.ImageFlip?.Inverse();
        ChannelSamples.Validate(doc.Color);
        if (doc.Guides is null || doc.Guides.Any(guide => guide is null || !Format.ValidId(guide.Id) ||
            !Enum.IsDefined(guide.Axis) || guide.Position < 0 || guide.Position > (guide.Axis == GuideAxis.Vertical ? doc.Width : doc.Height)) ||
            doc.Guides.Select(guide => guide.Id).Distinct().Count() != doc.Guides.Length)
            throw new InvalidDataException("Invalid guide data.");
        var ids = new HashSet<string>();
        long totalBytes = 0;
        byte[] ReadResource(Resource resource, string expected, int channels)
        {
            if (resource is null || resource.Path != expected) throw new InvalidDataException("Invalid image resource reference.");
            Format.Dimensions(resource.Width, resource.Height);
            int size = checked(resource.Width * resource.Height * channels);
            totalBytes += size;
            if (totalBytes > MaxRasterBytes) throw new InvalidDataException("Image resources exceed the supported size.");
            var imageEntry = archive.GetEntry(expected);
            if (imageEntry?.Length != size) throw new InvalidDataException("Missing or invalid image resource.");
            using var source = imageEntry.Open();
            var bytes = new byte[size];
            source.ReadExactly(bytes);
            return bytes;
        }
        Pixels ReadPixels(Resource resource, string stem)
        {
            if (resource.Color is not { } settings)
                return new(resource.Width, resource.Height, ReadResource(resource, stem + ".rgba", 4));
            ChannelSamples.Validate(settings);
            Format.Dimensions(resource.Width, resource.Height);
            int components = ChannelSamples.ComponentCount(settings.Mode);
            int length = checked(resource.Width * resource.Height * ChannelSamples.Bytes(settings.Depth));
            long total = (long)length * resource.Planes;
            string expected = stem + ".planes";
            var imageEntry = resource.Path == expected ? archive.GetEntry(expected) : null;
            if (settings != doc.Color || resource.Planes < components || resource.Planes > components + 1 ||
                total > MaxRasterBytes || imageEntry?.Length != total)
                throw new InvalidDataException("Missing or invalid native pixel resource.");
            totalBytes += total;
            if (totalBytes > MaxRasterBytes) throw new InvalidDataException("Image resources exceed the supported size.");
            using var source = imageEntry.Open();
            var planes = new byte[resource.Planes][];
            for (int i = 0; i < planes.Length; i++)
            {
                planes[i] = new byte[length];
                source.ReadExactly(planes[i]);
            }
            return PixelBuffer.FromNative(new(settings, resource.Width, resource.Height, planes));
        }
        Layer Restore(Layer layer)
        {
            if (layer is null || !Format.ValidId(layer.Id) || !ids.Add(layer.Id) ||
                !Enum.IsDefined(layer.Kind) || !Enum.IsDefined(layer.Blend) ||
                layer.Children is null || layer.Effects is null || layer.Transform is null ||
                layer.Placement is not { ScaleX: > 0, ScaleY: > 0 } placement ||
                !double.IsFinite(placement.X) || !double.IsFinite(placement.Y) ||
                !double.IsFinite(placement.ScaleX) || !double.IsFinite(placement.ScaleY) ||
                string.IsNullOrWhiteSpace(layer.Name) || layer.Font is null || layer.Text is null ||
                layer.Opacity is < 0 or > 1 || layer.FillOpacity is < 0 or > 1 || !double.IsFinite(layer.FillOpacity) || layer.Size is < 1 or > 512 ||
                (layer.Kind != LayerKind.Group && layer.Children.Length != 0))
                throw new InvalidDataException("Invalid layer metadata.");
            CommandEngine.CheckTransform(layer.Transform);
            if (layer.VectorMask is { } vectorMask) CommandEngine.CheckVectorMask(vectorMask);
            if (layer.PassThrough && layer.Kind != LayerKind.Group)
                throw new InvalidDataException("Only groups can use pass-through blending.");
            CommandEngine.CheckContours(layer.Contours);
            if (layer.Kind != LayerKind.Path && layer.Contours.Length != 0)
                throw new InvalidDataException("Only Path layers can contain vector paths.");
            if (layer.Kind == LayerKind.Chop && (string.IsNullOrWhiteSpace(layer.ChopSource) ||
                !string.Equals(Path.GetExtension(layer.ChopSource), ".chop", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Invalid Chop Layer source.");
            layer.ImagePlacement?.Inverse();
            if (layer.RasterGrid is { } grid)
            {
                if (layer.Kind != LayerKind.Raster) throw new InvalidDataException("Only raster layers can have a pixel grid.");
                grid.Inverse();
            }
            if (layer.SpatialEdits is { } legacy)
            {
                var matrix = layer.Transform.Warp ?? Projective.Identity;
                foreach (var spatial in legacy)
                {
                    if (spatial is null || !Enum.IsDefined(spatial.Kind)) throw new InvalidDataException("Invalid spatial edit.");
                    spatial.Matrix.Inverse();
                    if (spatial.Enabled) matrix = matrix.Then(spatial.Matrix);
                }
                matrix.Inverse();
                layer = layer with { Transform = layer.Transform with { Warp = matrix == Projective.Identity ? null : matrix }, SpatialEdits = null };
            }
            Format.Color(layer.Color);
            RichText.Check(layer.Typography);
            if (!double.IsFinite(layer.TextBend) || layer.TextBend is < -1 or > 1)
                throw new InvalidDataException("Invalid text bend.");
            if (layer.Kind is LayerKind.Raster or LayerKind.Rectangle or LayerKind.Ellipse or LayerKind.Fill) Format.Dimensions(layer.Width, layer.Height);
            var effects = new HashSet<string>();
            foreach (var effect in layer.Effects)
            {
                CommandEngine.CheckEffect(effect);
                if (effect.Kind == EffectKind.PathFill && layer.Kind != LayerKind.Path)
                    throw new InvalidDataException("The Fill effect requires a Path layer.");
                if (!effects.Add(effect.Id)) throw new InvalidDataException("Duplicate effect ID.");
            }
            Pixels? pixels = null;
            if (manifest.Resources.TryGetValue(layer.Id, out var resource))
            {
                if (layer.Kind is not (LayerKind.Raster or LayerKind.Chop or LayerKind.Text)) throw new InvalidDataException("Unexpected raster resource.");
                pixels = PixelBuffer.Convert(ReadPixels(resource, $"pixels/{layer.Id}"), doc.Color);
            }
            if (layer.Kind == LayerKind.Chop && pixels is null) throw new InvalidDataException("Missing Chop Layer snapshot.");
            AlphaMask? mask = null;
            if (manifest.Masks?.TryGetValue(layer.Id, out var alpha) == true)
            {
                var bytes = ReadResource(alpha, $"masks/{layer.Id}.alpha", 1);
                mask = new AlphaMask(alpha.Width, alpha.Height, bytes, alpha.X, alpha.Y, alpha.Outside);
            }
            PixelShift[] shifts = [];
            if (manifest.Shifts?.TryGetValue(layer.Id, out var moves) == true)
            {
                if (pixels is null || moves is null) throw new InvalidDataException("Pixel offsets require a raster source.");
                int left = layer.RasterX, top = layer.RasterY, right = checked(left + pixels.Width), bottom = checked(top + pixels.Height);
                shifts = moves.Select((move, i) =>
                {
                    var area = move.Source;
                    bool hasPatch = move.Patch is not null || move.NativePatch is not null;
                    if (area.Width <= 0 || area.Height <= 0 || !hasPatch && (area.X < left || area.Y < top ||
                        (long)area.X + area.Width > right || (long)area.Y + area.Height > bottom))
                        throw new InvalidDataException("Invalid pixel offset area.");
                    if ((move.Clear || hasPatch) && (move.Dx != 0 || move.Dy != 0) ||
                        (hasPatch && (move.Clear || move.Map is not null)) ||
                        (move.Patch is not null && move.NativePatch is not null) ||
                        (move.NativePatch is { } nativePatch && (nativePatch.Color is null ||
                            nativePatch.Width != area.Width || nativePatch.Height != area.Height)))
                        throw new InvalidDataException("Invalid raster edit resource.");
                    int x = checked(area.X + move.Dx), y = checked(area.Y + move.Dy);
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, checked(x + area.Width));
                    bottom = Math.Max(bottom, checked(y + area.Height));
                    Format.Dimensions(checked(right - left), checked(bottom - top));
                    byte[] bits = move.Map is null ? [] : ReadResource(new(move.Map, area.Width, area.Height), $"shifts/{layer.Id}/{i}.bits", 1);
                    var patchResource = move.NativePatch ?? (move.Patch is { } path ? new Resource(path, area.Width, area.Height) : null);
                    Pixels? patch = patchResource is null ? null :
                        PixelBuffer.Convert(ReadPixels(patchResource, $"shifts/{layer.Id}/{i}"), doc.Color);
                    return new PixelShift(area, bits, move.Dx, move.Dy, move.Clear, patch);
                }).ToArray();
            }
            ColorPlanes? source = null;
            if (manifest.LayerChannels?.TryGetValue(layer.Id, out var channelData) == true)
            {
                if (layer.Kind != LayerKind.Raster || pixels is null || channelData.Planes is not { Length: > 0 and <= 5 })
                    throw new InvalidDataException("Invalid layer color channels.");
                var plane = channelData.Planes[0];
                var samples = channelData.Planes.Select((value, i) =>
                {
                    if (value.Width != plane.Width || value.Height != plane.Height)
                        throw new InvalidDataException("Layer channel dimensions differ.");
                    return ReadResource(value, $"channels/layers/{layer.Id}/{i}.bin", ChannelSamples.Bytes(channelData.Settings.Depth));
                }).ToArray();
                source = new(channelData.Settings, plane.Width, plane.Height, samples);
                ChannelSamples.Validate(source);
                if (manifest.Version <= 10 && source.Settings == doc.Color && pixels is not null &&
                    source.Width == pixels.Width && source.Height == pixels.Height)
                {
                    var originalProxy = PixelBuffer.FromNative(source);
                    if (originalProxy.Rgba.AsSpan().SequenceEqual(pixels.Rgba)) pixels = originalProxy;
                }
            }
            return layer with { Pixels = pixels, Mask = mask, PixelShifts = shifts, SourceChannels = source,
                Children = layer.Children.Select(Restore).ToArray() };
        }
        var channelIds = new HashSet<string>();
        var extra = (manifest.Channels ?? []).Select(channel =>
        {
            if (!Format.ValidId(channel.Info.Id) || !channelIds.Add(channel.Info.Id) || string.IsNullOrWhiteSpace(channel.Info.Name))
                throw new InvalidDataException("Invalid document channel.");
            var plane = channel.Plane;
            return new ChannelPlane(channel.Info, plane.Width, plane.Height, channel.Depth,
                ReadResource(plane, $"channels/document/{channel.Info.Id}.bin", ChannelSamples.Bytes(channel.Depth)));
        }).ToArray();
        byte[]? profile = null;
        if (manifest.HasIccProfile)
        {
            var profileEntry = archive.GetEntry(ProfilePath);
            if (profileEntry is null || profileEntry.Length is 0 or > MaxProfileBytes)
                throw new InvalidDataException("Missing or invalid ICC profile.");
            using var profileData = profileEntry.Open();
            profile = new byte[(int)profileEntry.Length];
            profileData.ReadExactly(profile);
        }
        var result = doc with { Channels = extra, Layers = doc.Layers.Select(Restore).ToArray(), IccProfile = profile };
        if (manifest.Resources.Keys.Any(id => !ids.Contains(id)) || manifest.Masks?.Keys.Any(id => !ids.Contains(id)) == true ||
            manifest.Shifts?.Keys.Any(id => !ids.Contains(id)) == true || manifest.LayerChannels?.Keys.Any(id => !ids.Contains(id)) == true)
            throw new InvalidDataException("Unreferenced raster resource.");
        return result;
    }
}
