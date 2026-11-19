using System.IO.Compression;
using System.Text.Json;

namespace Chop.Core;

/// <summary>Versioned ZIP document with a JSON manifest and embedded RGBA resources.</summary>
public static class ProjectFile
{
    private const int MaxManifestBytes = 8_000_000;
    private const int MaxRasterBytes = 512 * 1024 * 1024;
    private sealed record Manifest(int Version, Document Document, Dictionary<string, Resource> Resources, Dictionary<string, Resource>? Masks = null,
        Dictionary<string, ShiftResource[]>? Shifts = null);
    private sealed record ShiftResource(Area Source, int Dx, int Dy, string? Map, bool Clear = false, string? Patch = null);
    private sealed record Resource(string Path, int Width, int Height);

    public static void Save(string path, Document document)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var resources = new Dictionary<string, Resource>();
                var masks = new Dictionary<string, Resource>();
                var shifts = new Dictionary<string, ShiftResource[]>();
                long rasterBytes = 0;
                void WriteResource(Dictionary<string, Resource> map, string id, Resource resource, byte[] bytes)
                {
                    rasterBytes += bytes.LongLength;
                    if (rasterBytes > MaxRasterBytes) throw new InvalidDataException("Image resources exceed the supported size.");
                    map.Add(id, resource);
                    using var output = archive.CreateEntry(resource.Path).Open();
                    output.Write(bytes);
                }
                foreach (var layer in document.AllLayers())
                {
                    if (layer.Pixels is { } pixels)
                        WriteResource(resources, layer.Id, new($"pixels/{layer.Id}.rgba", pixels.Width, pixels.Height), pixels.Rgba);
                    if (layer.Mask is { } mask)
                        WriteResource(masks, layer.Id, new($"masks/{layer.Id}.alpha", mask.Width, mask.Height), mask.Alpha);
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
                            string? patchPath = null;
                            if (shift.Patch is { } patch)
                            {
                                patchPath = $"shifts/{layer.Id}/{i}.rgba";
                                rasterBytes += patch.Rgba.LongLength;
                                if (rasterBytes > MaxRasterBytes) throw new InvalidDataException("Image resources exceed the supported size.");
                                using var output = archive.CreateEntry(patchPath).Open();
                                output.Write(patch.Rgba);
                            }
                            return new ShiftResource(shift.Source, shift.Dx, shift.Dy, map, shift.Clear, patchPath);
                        }).ToArray();
                }
                var json = JsonSerializer.SerializeToUtf8Bytes(new Manifest(4, document, resources, masks, shifts), Format.Json);
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
        if (manifest.Version is not (1 or 2 or 3 or 4)) throw new InvalidDataException($"Unsupported CHOP version {manifest.Version}.");
        var doc = manifest.Document ?? throw new InvalidDataException("Missing document.");
        if (manifest.Resources is null || doc.Layers is null || doc.Metadata is null ||
            !Format.ValidId(doc.Id) || doc.ColorSpace != "sRGB" || doc.PixelsPerInch is < 1 or > 100000)
            throw new InvalidDataException("Invalid document metadata.");
        Format.Dimensions(doc.Width, doc.Height);
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
        Layer Restore(Layer layer)
        {
            if (layer is null || !Format.ValidId(layer.Id) || !ids.Add(layer.Id) ||
                !Enum.IsDefined(layer.Kind) || !Enum.IsDefined(layer.Blend) ||
                layer.Children is null || layer.Effects is null || layer.Transform is null ||
                layer.Placement is not { ScaleX: > 0, ScaleY: > 0 } placement ||
                !double.IsFinite(placement.X) || !double.IsFinite(placement.Y) ||
                !double.IsFinite(placement.ScaleX) || !double.IsFinite(placement.ScaleY) ||
                string.IsNullOrWhiteSpace(layer.Name) || layer.Font is null || layer.Text is null ||
                layer.Opacity is < 0 or > 1 || layer.Size is < 1 or > 512 ||
                (layer.Kind != LayerKind.Group && layer.Children.Length != 0))
                throw new InvalidDataException("Invalid layer metadata.");
            CommandEngine.CheckTransform(layer.Transform);
            Format.Color(layer.Color);
            if (layer.Kind is LayerKind.Raster or LayerKind.Rectangle or LayerKind.Ellipse or LayerKind.Fill) Format.Dimensions(layer.Width, layer.Height);
            var effects = new HashSet<string>();
            foreach (var effect in layer.Effects)
            {
                CommandEngine.CheckEffect(effect);
                if (!effects.Add(effect.Id)) throw new InvalidDataException("Duplicate effect ID.");
            }
            Pixels? pixels = null;
            if (manifest.Resources.TryGetValue(layer.Id, out var resource))
            {
                if (layer.Kind != LayerKind.Raster) throw new InvalidDataException("Unexpected raster resource.");
                var bytes = ReadResource(resource, $"pixels/{layer.Id}.rgba", 4);
                pixels = new Pixels(resource.Width, resource.Height, bytes);
            }
            AlphaMask? mask = null;
            if (manifest.Masks?.TryGetValue(layer.Id, out var alpha) == true)
            {
                var bytes = ReadResource(alpha, $"masks/{layer.Id}.alpha", 1);
                mask = new AlphaMask(alpha.Width, alpha.Height, bytes);
            }
            PixelShift[] shifts = [];
            if (manifest.Shifts?.TryGetValue(layer.Id, out var moves) == true)
            {
                if (pixels is null || moves is null) throw new InvalidDataException("Pixel offsets require a raster source.");
                int left = layer.RasterX, top = layer.RasterY, right = checked(left + pixels.Width), bottom = checked(top + pixels.Height);
                shifts = moves.Select((move, i) =>
                {
                    var area = move.Source;
                    if (area.Width <= 0 || area.Height <= 0 || move.Patch is null && (area.X < left || area.Y < top ||
                        (long)area.X + area.Width > right || (long)area.Y + area.Height > bottom))
                        throw new InvalidDataException("Invalid pixel offset area.");
                    if ((move.Clear || move.Patch is not null) && (move.Dx != 0 || move.Dy != 0) ||
                        (move.Patch is not null && (move.Clear || move.Map is not null)))
                        throw new InvalidDataException("Invalid raster edit resource.");
                    int x = checked(area.X + move.Dx), y = checked(area.Y + move.Dy);
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, checked(x + area.Width));
                    bottom = Math.Max(bottom, checked(y + area.Height));
                    Format.Dimensions(checked(right - left), checked(bottom - top));
                    byte[] bits = move.Map is null ? [] : ReadResource(new(move.Map, area.Width, area.Height), $"shifts/{layer.Id}/{i}.bits", 1);
                    Pixels? patch = move.Patch is null ? null : new Pixels(area.Width, area.Height,
                        ReadResource(new(move.Patch, area.Width, area.Height), $"shifts/{layer.Id}/{i}.rgba", 4));
                    return new PixelShift(area, bits, move.Dx, move.Dy, move.Clear, patch);
                }).ToArray();
            }
            return layer with { Pixels = pixels, Mask = mask, PixelShifts = shifts, Children = layer.Children.Select(Restore).ToArray() };
        }
        var result = doc with { Layers = doc.Layers.Select(Restore).ToArray() };
        if (manifest.Resources.Keys.Any(id => !ids.Contains(id)) || manifest.Masks?.Keys.Any(id => !ids.Contains(id)) == true ||
            manifest.Shifts?.Keys.Any(id => !ids.Contains(id)) == true)
            throw new InvalidDataException("Unreferenced raster resource.");
        return result;
    }
}
