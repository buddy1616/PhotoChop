using System.Buffers.Binary;
using System.IO.Compression;

namespace Chop.Core;

internal static class PsdPatterns
{
    public static Dictionary<string, byte[]> Read(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> blocks)
    {
        var patterns = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in new[] { "Patt", "Pat2", "Pat3" })
        {
            if (!blocks.TryGetValue(key, out var data)) continue;
            var reader = new PsdReader(data);
            try
            {
                for (int i = 0; i < 64 && reader.Remaining >= 4; i++)
                {
                    uint length = reader.ReadUInt32();
                    var entry = reader.ReadSection(length);
                    reader.Skip(Math.Min(reader.Remaining, (4 - length % 4) % 4));
                    try
                    {
                        if (ReadTile(entry) is { } tile) patterns.TryAdd(tile.Id, tile.Png);
                    }
                    catch (InvalidDataException) { }
                }
            }
            catch (InvalidDataException) { }
        }
        return patterns;
    }

    private static (string Id, byte[] Png)? ReadTile(PsdReader reader)
    {
        if (reader.ReadUInt32() != 1) return null;
        int mode = reader.ReadInt32();
        int count = mode switch { 1 or 7 => 1, 3 => 3, _ => 0 };
        if (count == 0) return null;
        int height = reader.ReadUInt16(), width = reader.ReadUInt16();
        if (width is < 1 or > 1024 || height is < 1 or > 1024) return null;
        reader.Skip(2L * reader.ReadUInt32());
        string id = reader.ReadAscii(reader.ReadByte()).TrimEnd('\0');
        if (reader.ReadUInt32() != 3) return null;
        var planes = reader.ReadSection(reader.ReadUInt32());
        planes.Skip(16);
        if (planes.ReadUInt32() < count) return null;
        var channels = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            if (planes.ReadUInt32() == 0) return null;
            var channel = planes.ReadSection(planes.ReadUInt32());
            if (channel.ReadUInt32() != 8 || channel.ReadInt32() != 0 || channel.ReadInt32() != 0 ||
                channel.ReadInt32() != height || channel.ReadInt32() != width || channel.ReadUInt16() != 8) return null;
            ushort compression = channel.ReadByte();
            channels[i] = PsdCompression.ReadChannels(channel, width, height, 8, 1, compressionCode: compression)[0];
        }
        return (id, EncodePng(width, height, channels));
    }

    private static byte[] EncodePng(int width, int height, byte[][] channels)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = channels.Length == 3 ? (byte)2 : (byte)0;
        WriteChunk(output, "IHDR"u8, header);
        using var payload = new MemoryStream();
        using (var zip = new ZLibStream(payload, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[1 + width * channels.Length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    for (int c = 0; c < channels.Length; c++) row[1 + x * channels.Length + c] = channels[c][y * width + x];
                zip.Write(row);
            }
        }
        WriteChunk(output, "IDAT"u8, payload.GetBuffer().AsSpan(0, (int)payload.Length));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);
        uint crc = UpdateCrc(UpdateCrc(uint.MaxValue, type), data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        output.Write(word);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }
        return crc;
    }
}
