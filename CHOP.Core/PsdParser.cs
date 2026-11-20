using System.Buffers.Binary;
using System.Text;

namespace Chop.Core;

internal sealed record PsdFile(int Width, int Height, int Depth, int Mode, int ChannelCount, bool MergedTransparency,
    double? Dpi, IReadOnlyList<PsdLayer> Layers, PsdReader CompositeData, ReadOnlyMemory<byte> IccProfile,
    IReadOnlyList<string> AlphaNames, bool IsPsb)
{
    public bool? HasComposite { get; init; }
    public double? GlobalAngle { get; init; }
    public double? GlobalAltitude { get; init; }
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> GlobalTags { get; init; } =
        new Dictionary<string, ReadOnlyMemory<byte>>();
    public string? Writer { get; init; }
    public ReadOnlyMemory<byte> ColorModeData { get; init; }
    public int? TransparencyIndex { get; init; }
    public IReadOnlyList<bool> AlphaSpots { get; init; } = [];
    public IReadOnlyList<uint> AlphaIds { get; init; } = [];
    public IReadOnlyList<PsdGuide> Guides { get; init; } = [];
}

internal sealed record PsdLayer(string Name, int X, int Y, int Width, int Height, byte Opacity, bool Clipped, bool Visible,
    bool Locked, string BlendKey, int SectionType, string? SectionBlendKey, PsdMask? Mask, PsdMask? RealMask,
    IReadOnlyList<PsdChannel> Channels, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Tags)
{
    public bool PixelDataIrrelevant { get; init; }
}

internal sealed record PsdMask(int X, int Y, int Width, int Height, byte Outside, bool Enabled, bool Inverted)
{
    public bool Relative { get; init; }
    public byte? Density { get; init; }
    public double? Feather { get; init; }
}

internal sealed record PsdChannel(short Id, PsdReader Data);
internal sealed record PsdGuide(bool Vertical, double Position);

/// <summary>Reads PSD layer records in file order, from the bottom of the stack to the top.</summary>
internal static class PsdParser
{
    private static readonly Encoding MacRoman = CodePagesEncodingProvider.Instance.GetEncoding(10000) ?? Encoding.Latin1;

    public static PsdFile Parse(byte[] bytes)
    {
        var file = new PsdReader(bytes);
        if (file.ReadAscii(4) != "8BPS") throw new InvalidDataException("This file does not have a Photoshop document header.");
        int version = file.ReadUInt16();
        if (version == 2) throw new InvalidDataException("Photoshop Large Document Format (PSB) is not supported.");
        if (version != 1) throw new InvalidDataException($"Unsupported Photoshop document version {version}.");
        file.Skip(6);
        int channels = file.ReadUInt16();
        uint height = file.ReadUInt32(), width = file.ReadUInt32();
        int depth = file.ReadUInt16(), mode = file.ReadUInt16();
        if (channels is < 1 or > 56 || width is < 1 or > 30_000 || height is < 1 or > 30_000 ||
            depth is not (1 or 8 or 16 or 32))
            throw new InvalidDataException("Invalid Photoshop document dimensions, depth, or channel count.");

        var colorMode = BlockMemory(file, file.ReadUInt32());
        var resources = ReadResources(file.ReadSection(file.ReadUInt32()));
        var layerAndMask = file.ReadSection(file.ReadUInt32());
        var layers = new List<PsdLayer>();
        bool transparency = false;
        Dictionary<string, ReadOnlyMemory<byte>> global = new();
        if (layerAndMask.Remaining > 0)
        {
            (layers, transparency) = ReadLayers(layerAndMask.ReadSection(layerAndMask.ReadUInt32()));
            var trailer = ReadRemaining(layerAndMask);
            if (trailer.Length >= 4)
            {
                var tail = new PsdReader(trailer);
                if (!HasTagSignature(trailer.Span)) tail.Skip(tail.ReadUInt32());
                global = ReadTaggedBlocks(ReadRemaining(tail), alignment: 4);
                string? alternate = depth switch { 16 => "Lr16", 32 => "Lr32", _ => null };
                if ((alternate is not null && global.TryGetValue(alternate, out var info)) ||
                    (layers.Count == 0 && global.TryGetValue("Layr", out info)))
                {
                    var (otherLayers, otherTransparency) = ReadLayers(new PsdReader(info));
                    if (otherLayers.Count != 0) layers = otherLayers;
                    transparency |= otherTransparency;
                }
                transparency |= global.ContainsKey("Mtrn") || global.ContainsKey("Mt16") || global.ContainsKey("Mt32");
            }
        }

        return new PsdFile((int)width, (int)height, depth, mode, channels, transparency, resources.Dpi, layers,
            file.ReadSection(file.Remaining), resources.Icc, resources.UnicodeNames.Count != 0 ? resources.UnicodeNames : resources.PascalNames,
            IsPsb: false)
        {
            HasComposite = resources.HasComposite,
            GlobalAngle = resources.GlobalAngle,
            GlobalAltitude = resources.GlobalAltitude,
            GlobalTags = global,
            Writer = resources.Writer,
            ColorModeData = colorMode,
            TransparencyIndex = resources.TransparencyIndex,
            AlphaSpots = resources.Spots,
            AlphaIds = resources.AlphaIds,
            Guides = resources.Guides
        };
    }

    private sealed class ResourceInfo
    {
        public bool? HasComposite;
        public string? Writer;
        public double? Dpi;
        public double? GlobalAngle;
        public double? GlobalAltitude;
        public ReadOnlyMemory<byte> Icc;
        public int? TransparencyIndex;
        public readonly List<string> PascalNames = [];
        public readonly List<string> UnicodeNames = [];
        public readonly List<bool> Spots = [];
        public readonly List<uint> AlphaIds = [];
        public readonly List<PsdGuide> Guides = [];
    }

    private static ResourceInfo ReadResources(PsdReader resource)
    {
        var info = new ResourceInfo();
        while (resource.Remaining >= 12)
        {
            var signature = resource.ReadAscii(4);
            if (signature is not ("8BIM" or "MeSa" or "PHUT" or "AgHg" or "DCSR" or "8B64")) break;
            int id = resource.ReadUInt16();
            ReadPascal(resource, 2);
            uint length = resource.ReadUInt32();
            var data = resource.ReadSection(length);
            if ((length & 1) != 0 && resource.Remaining > 0) resource.Skip(1);
            switch (id)
            {
                case 1005 when data.Remaining >= 16:
                    double dpi = data.ReadUInt32() / 65536d;
                    if (dpi is >= 1 and <= 100_000) info.Dpi = dpi;
                    break;
                case 1006:
                    while (data.Remaining > 0 && info.PascalNames.Count < 56) info.PascalNames.Add(ReadPascal(data, 1));
                    break;
                case 1037 when data.Remaining >= 4:
                    info.GlobalAngle = data.ReadInt32();
                    break;
                case 1049 when data.Remaining >= 4:
                    info.GlobalAltitude = data.ReadInt32();
                    break;
                case 1032 when data.Remaining >= 16:
                    data.Skip(12);
                    uint count = data.ReadUInt32();
                    for (int i = 0; i < Math.Min(count, 4096) && data.Remaining >= 5; i++)
                    {
                        double position = data.ReadUInt32() / 32d;
                        int direction = data.ReadByte();
                        if (direction <= 1) info.Guides.Add(new(direction == 0, position));
                    }
                    break;
                case 1039:
                    info.Icc = ReadRemaining(data);
                    break;
                case 1045:
                    while (data.Remaining >= 4 && info.UnicodeNames.Count < 56) info.UnicodeNames.Add(ReadUnicode(data));
                    break;
                case 1047 when data.Remaining >= 2:
                    info.TransparencyIndex = data.ReadUInt16();
                    break;
                case 1053:
                    while (data.Remaining >= 4 && info.AlphaIds.Count < 56) info.AlphaIds.Add(data.ReadUInt32());
                    break;
                case 1057 when data.Remaining >= 5:
                    data.Skip(4);
                    info.HasComposite = data.ReadByte() != 0;
                    if (data.Remaining >= 4) info.Writer = ReadUnicode(data);
                    break;
                case 1077 when data.Remaining >= 4:
                    data.Skip(4);
                    info.Spots.Clear();
                    while (data.Remaining >= 13 && info.Spots.Count < 56)
                    {
                        data.Skip(12);
                        info.Spots.Add(data.ReadByte() == 2);
                    }
                    break;
            }
        }
        return info;
    }

    private readonly record struct ChannelRecord(short Id, uint Size);

    private static (List<PsdLayer> Layers, bool Transparency) ReadLayers(PsdReader info)
    {
        if (info.Remaining == 0) return ([], false);
        short count = info.ReadInt16();
        var records = new List<(PsdLayer Layer, ChannelRecord[] Channels)>(Math.Abs((int)count));
        for (int i = 0; i < Math.Abs((int)count); i++)
        {
            var bounds = ReadBounds(info);
            int channelCount = info.ReadUInt16();
            if (channelCount > 64) throw new InvalidDataException("Too many channels in a Photoshop layer.");
            var channels = new ChannelRecord[channelCount];
            for (int c = 0; c < channelCount; c++) channels[c] = new(info.ReadInt16(), info.ReadUInt32());
            if (info.ReadAscii(4) != "8BIM") throw new InvalidDataException("Invalid Photoshop layer blend signature.");
            var blend = info.ReadAscii(4);
            var opacity = info.ReadByte();
            bool clipped = info.ReadByte() == 1;
            byte flags = info.ReadByte();
            info.Skip(1);

            var extra = info.ReadSection(info.ReadUInt32());
            var (mask, real) = ReadMasks(extra.ReadSection(extra.ReadUInt32()), channels.Any(c => c.Id == -3));
            extra.Skip(extra.ReadUInt32());
            string name = ReadPascal(extra, 4);
            var tags = ReadTaggedBlocks(ReadRemaining(extra), alignment: 2);
            if (tags.TryGetValue("luni", out var unicode) && unicode.Length >= 4)
                name = ReadUnicode(new PsdReader(unicode));
            int sectionType = 0;
            string? sectionBlend = null;
            if ((tags.TryGetValue("lsct", out var divider) || tags.TryGetValue("lsdk", out divider)) && divider.Length >= 4)
            {
                var section = new PsdReader(divider);
                var type = section.ReadUInt32();
                sectionType = type <= 3 ? (int)type : 0;
                if (section.Remaining >= 8 && section.ReadAscii(4) == "8BIM") sectionBlend = section.ReadAscii(4);
            }
            bool locked = tags.TryGetValue("lspf", out var lockBytes) && lockBytes.Length >= 4 &&
                (BinaryPrimitives.ReadUInt32BigEndian(lockBytes.Span) & 0x80000000u) != 0;
            records.Add((new PsdLayer(name, bounds.X, bounds.Y, bounds.Width, bounds.Height, opacity, clipped,
                Visible: (flags & 2) == 0, Locked: locked, blend, sectionType, sectionBlend, mask, real, [], tags)
                { PixelDataIrrelevant = (flags & 0x18) == 0x18 }, channels));
        }

        var layers = new List<PsdLayer>(records.Count);
        foreach (var (layer, channels) in records)
        {
            var data = new PsdChannel[channels.Length];
            for (int i = 0; i < channels.Length; i++)
                data[i] = new(channels[i].Id, info.ReadSection(channels[i].Size));
            layers.Add(layer with { Channels = data });
        }
        return (layers, count < 0);
    }

    private static (PsdMask? Mask, PsdMask? RealMask) ReadMasks(PsdReader reader, bool hasRealMask)
    {
        if (reader.Remaining == 0) return (null, null);
        var bounds = ReadBounds(reader);
        byte outside = reader.ReadByte(), flags = reader.ReadByte();
        var mask = MakeMask(bounds, outside, flags);
        PsdMask? actual = null;
        if (hasRealMask && reader.Remaining >= 18)
        {
            byte realFlags = reader.ReadByte(), realOutside = reader.ReadByte();
            actual = MakeMask(ReadBounds(reader), realOutside, realFlags);
        }
        if ((flags & 16) != 0 && reader.Remaining >= 1)
        {
            byte settings = reader.ReadByte();
            if ((settings & 1) != 0 && reader.Remaining >= 1) mask = mask with { Density = reader.ReadByte() };
            if ((settings & 2) != 0 && reader.Remaining >= 8)
            {
                double feather = BitConverter.Int64BitsToDouble(reader.ReadInt64());
                if (feather >= 0 && double.IsFinite(feather)) mask = mask with { Feather = feather };
            }
        }
        return (mask, actual);
    }

    private static PsdMask MakeMask(Area bounds, byte outside, byte flags) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height, outside, Enabled: (flags & 2) == 0, Inverted: (flags & 4) != 0)
        {
            Relative = (flags & 1) != 0
        };

    private static Area ReadBounds(PsdReader reader)
    {
        int top = reader.ReadInt32(), left = reader.ReadInt32(), bottom = reader.ReadInt32(), right = reader.ReadInt32();
        long width = Math.Max((long)right - left, 0), height = Math.Max((long)bottom - top, 0);
        if (width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("Invalid Photoshop layer bounds.");
        return new(left, top, (int)width, (int)height);
    }

    private static bool HasTagSignature(ReadOnlySpan<byte> span) => span.StartsWith("8BIM"u8) || span.StartsWith("8B64"u8);

    private static Dictionary<string, ReadOnlyMemory<byte>> ReadTaggedBlocks(ReadOnlyMemory<byte> data, int alignment)
    {
        var tags = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        while (data.Length >= 12 && HasTagSignature(data.Span))
        {
            string code = Encoding.ASCII.GetString(data.Span.Slice(4, 4));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(8, 4));
            int available = data.Length - 12;
            if (length > available) throw new InvalidDataException($"Truncated Photoshop layer record {code}.");
            tags[code] = data.Slice(12, (int)length);
            data = data[(12 + (int)length)..];
            // Padding differs between tagged-block writers. Preserve an immediately following signature.
            int padding = (alignment - (int)(length % alignment)) % alignment;
            if (padding > 0 && data.Length >= padding && !HasTagSignature(data.Span))
            {
                int skip = 0;
                while (skip < padding && data.Span[skip] == 0)
                {
                    skip++;
                    if (HasTagSignature(data.Span[skip..])) break;
                }
                data = data[skip..];
            }
        }
        return tags;
    }

    private static string ReadUnicode(PsdReader reader)
    {
        uint length = reader.ReadUInt32();
        if (length > reader.Remaining / 2) throw new InvalidDataException("Truncated Photoshop Unicode string.");
        return Encoding.BigEndianUnicode.GetString(reader.ReadMemory(checked((int)length * 2)).Span).TrimEnd('\0');
    }

    private static string ReadPascal(PsdReader reader, int alignment)
    {
        int length = reader.ReadByte();
        var name = MacRoman.GetString(reader.ReadMemory(length).Span).TrimEnd('\0');
        reader.Skip((alignment - (length + 1) % alignment) % alignment);
        return name;
    }

    private static ReadOnlyMemory<byte> BlockMemory(PsdReader reader, long length)
    {
        if (length > int.MaxValue) throw new InvalidDataException("A Photoshop section exceeds the supported size.");
        return reader.ReadMemory((int)length);
    }

    private static ReadOnlyMemory<byte> ReadRemaining(PsdReader reader) => BlockMemory(reader, reader.Remaining);
}

internal static class PsdVectorMasks
{
    public static VectorMask Parse(ReadOnlyMemory<byte> bytes, int docWidth, int docHeight, out bool unlinked)
    {
        var reader = new PsdReader(bytes);
        if (reader.ReadUInt32() != 3) throw new InvalidDataException("Unsupported Photoshop vector mask version.");
        uint flags = reader.ReadUInt32();
        unlinked = (flags & 2) != 0;
        bool initialFill = false;
        var contours = new List<PathContour>();
        var operations = new List<VectorMaskOperation>();
        int count = 0;
        while (reader.Remaining >= 26)
        {
            var record = reader.ReadSection(26);
            int selector = record.ReadUInt16();
            if (selector == 8)
            {
                initialFill = record.ReadUInt16() != 0;
                continue;
            }
            if (selector is not (0 or 3)) continue;
            bool closed = selector == 0;
            int knots = record.ReadUInt16();
            var operation = (VectorMaskOperation)record.ReadInt16();
            if (!Enum.IsDefined(operation)) throw new InvalidDataException("Unsupported Photoshop vector mask path operation.");
            if ((long)knots * 26 > reader.Remaining || (count += knots) > 100_000 || contours.Count >= 2_000)
                throw new InvalidDataException("Photoshop vector mask exceeds the supported path size.");

            var nodes = new PathNode[knots];
            for (int i = 0; i < nodes.Length; i++)
            {
                var knot = reader.ReadSection(26);
                int kind = knot.ReadUInt16();
                if (closed ? kind is not (1 or 2) : kind is not (4 or 5))
                    throw new InvalidDataException("Invalid Photoshop vector mask knot.");
                var incoming = Coordinate(knot, docWidth, docHeight);
                var point = Coordinate(knot, docWidth, docHeight);
                var outgoing = Coordinate(knot, docWidth, docHeight);
                nodes[i] = new(point, incoming == point ? null : incoming, outgoing == point ? null : outgoing);
            }
            if (nodes.Length != 0)
            {
                contours.Add(new(nodes, closed && nodes.Length >= 2));
                operations.Add(operation);
            }
        }
        // Photoshop pads this tagged block to a four-byte boundary.
        if (reader.Remaining > 3 || reader.ReadMemory((int)reader.Remaining).Span.IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Truncated Photoshop vector mask.");
        return new VectorMask(contours.ToArray(), Enabled: (flags & 4) == 0, Inverted: (flags & 1) != 0,
            Operations: operations.ToArray(), InitialFill: initialFill);
    }

    private static PointD Coordinate(PsdReader reader, int width, int height)
    {
        const double Scale = 16_777_216;
        double y = reader.ReadInt32() / Scale * height;
        double x = reader.ReadInt32() / Scale * width;
        return new(x, y);
    }
}
