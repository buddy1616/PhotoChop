using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Chop.Core;

/// <summary>Big-endian reader bounded to a PSD section. Child sections share the original input bytes.</summary>
internal sealed class PsdReader(ReadOnlyMemory<byte> bytes)
{
    private int position;

    public long Position => position;
    public long Remaining => bytes.Length - position;

    public ReadOnlyMemory<byte> ReadMemory(int count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException("A PSD field extends beyond its section.");
        var result = bytes.Slice(position, count);
        position += count;
        return result;
    }

    public PsdReader ReadSection(long length)
    {
        if (length < 0 || length > Remaining)
            throw new InvalidDataException("A PSD section extends beyond its enclosing data.");
        return new PsdReader(ReadMemory((int)length));
    }

    public void Skip(long count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException("A PSD field extends beyond its section.");
        position += (int)count;
    }

    public byte ReadByte() => ReadMemory(1).Span[0];
    public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(ReadMemory(2).Span);
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(ReadMemory(2).Span);
    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(ReadMemory(4).Span);
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadMemory(4).Span);
    public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadMemory(8).Span);
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(ReadMemory(8).Span);
    public string ReadAscii(int count) => Encoding.ASCII.GetString(ReadMemory(count).Span);
    public byte[] ReadBytes(int count) => ReadMemory(count).ToArray();
    public void ReadExactly(Span<byte> destination) => ReadMemory(destination.Length).Span.CopyTo(destination);
}

/// <summary>Decodes the image data compression shared by PSD composite and layer channels.</summary>
internal static class PsdCompression
{
    private const long MaxChannelBytes = 4L * 1024 * 1024 * 1024;

    public static int PlaneLength(int width, int height, int depth)
    {
        Format.Dimensions(width, height);
        if (depth is not (1 or 8 or 16 or 32)) throw new InvalidDataException($"Unsupported PSD bit depth {depth}.");
        return checked((width * depth + 7) / 8 * height);
    }

    /// <summary>Reads planar data and converts 16-bit words and 32-bit floats to little endian. One-bit rows stay packed.</summary>
    public static byte[][] ReadChannels(PsdReader reader, int width, int height, int depth, int channelCount, bool psb = false, ushort? compressionCode = null)
    {
        int planeLength = PlaneLength(width, height, depth);
        if (channelCount is < 1 or > 56 || (long)planeLength * channelCount > MaxChannelBytes)
            throw new InvalidDataException("PSD decoded channel data exceeds the supported size.");

        ushort compression = compressionCode ?? reader.ReadUInt16();
        var channels = new byte[channelCount][];
        switch (compression)
        {
            case 0:
                if (reader.Remaining < (long)planeLength * channelCount)
                    throw new InvalidDataException("PSD raw channel data is truncated.");
                for (int i = 0; i < channelCount; i++) channels[i] = reader.ReadBytes(planeLength);
                break;
            case 1:
                ReadRle(reader, channels, height, planeLength, psb);
                break;
            case 2:
                ReadZip(reader, channels, planeLength);
                break;
            case 3:
                if (depth == 1) throw new InvalidDataException("PSD ZIP prediction is unavailable for one-bit pixels.");
                ReadZip(reader, channels, planeLength);
                foreach (var plane in channels) UndoPrediction(plane, width, height, depth);
                return channels;
            default:
                throw new InvalidDataException($"Unsupported PSD compression {compression}.");
        }

        foreach (var plane in channels)
        {
            if (depth == 16)
            {
                var words = MemoryMarshal.Cast<byte, ushort>(plane.AsSpan());
                BinaryPrimitives.ReverseEndianness(words, words);
            }
            else if (depth == 32)
            {
                var words = MemoryMarshal.Cast<byte, uint>(plane.AsSpan());
                BinaryPrimitives.ReverseEndianness(words, words);
            }
        }
        return channels;
    }

    private static void ReadRle(PsdReader reader, byte[][] channels, int height, int planeLength, bool psb)
    {
        int rowCount = height * channels.Length;
        if (reader.Remaining < rowCount * (psb ? 4L : 2L))
            throw new InvalidDataException("PSD RLE row table is truncated.");

        var lengths = new int[rowCount];
        long compressedLength = 0;
        for (int i = 0; i < rowCount; i++)
        {
            uint length = psb ? reader.ReadUInt32() : reader.ReadUInt16();
            if (length > int.MaxValue) throw new InvalidDataException("A PSD RLE row exceeds the supported size.");
            lengths[i] = (int)length;
            compressedLength += length;
        }
        if (compressedLength > reader.Remaining)
            throw new InvalidDataException("PSD RLE pixel data is truncated.");

        int rowBytes = planeLength / height;
        int rowIndex = 0;
        for (int channel = 0; channel < channels.Length; channel++)
        {
            var plane = new byte[planeLength];
            channels[channel] = plane;
            for (int y = 0; y < height; y++)
                DecodePackBits(reader.ReadMemory(lengths[rowIndex++]).Span, plane.AsSpan(y * rowBytes, rowBytes));
        }
    }

    private static void DecodePackBits(ReadOnlySpan<byte> packed, Span<byte> row)
    {
        int source = 0, target = 0;
        while (source < packed.Length)
        {
            int control = (sbyte)packed[source++];
            if (control == -128) continue;
            int count = control >= 0 ? control + 1 : 1 - control;
            if (count > row.Length - target)
                throw new InvalidDataException("A PSD RLE run exceeds its row.");
            var run = row.Slice(target, count);
            if (control >= 0)
            {
                if (count > packed.Length - source)
                    throw new InvalidDataException("A PSD RLE literal is truncated.");
                packed.Slice(source, count).CopyTo(run);
                source += count;
            }
            else
            {
                if (source == packed.Length)
                    throw new InvalidDataException("A PSD RLE repeat is truncated.");
                run.Fill(packed[source++]);
            }
            target += count;
        }
        if (target != row.Length) throw new InvalidDataException("PSD RLE data does not fill its row.");
    }

    private static void ReadZip(PsdReader reader, byte[][] channels, int planeLength)
    {
        var packed = reader.ReadMemory((int)reader.Remaining);
        if (packed.Length < 6) throw new InvalidDataException("PSD ZIP pixel data is truncated.");
        using var input = MemoryMarshal.TryGetArray(packed, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(packed.ToArray(), writable: false);
        using var zip = new ZLibStream(input, CompressionMode.Decompress);
        try
        {
            uint sum = 1, accumulated = 0;
            for (int i = 0; i < channels.Length; i++)
            {
                channels[i] = new byte[planeLength];
                zip.ReadExactly(channels[i]);
                AccumulateAdler(channels[i], ref sum, ref accumulated);
            }
            if (zip.ReadByte() != -1)
                throw new InvalidDataException("PSD ZIP pixel data expands beyond its declared dimensions.");
            // ZLibStream can report a clean EOF after all pixels when just the checksum is missing.
            if (BinaryPrimitives.ReadUInt32BigEndian(packed.Span[^4..]) != (accumulated << 16 | sum))
                throw new InvalidDataException("The PSD ZIP checksum is missing or invalid.");
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("PSD ZIP pixel data is truncated.", error);
        }
    }

    private static void AccumulateAdler(ReadOnlySpan<byte> bytes, ref uint sum, ref uint accumulated)
    {
        while (!bytes.IsEmpty)
        {
            // 5,552 is the largest block for which the Adler accumulators cannot overflow 32 bits.
            int count = Math.Min(bytes.Length, 5_552);
            foreach (byte sample in bytes[..count])
            {
                sum += sample;
                accumulated += sum;
            }
            sum %= 65_521;
            accumulated %= 65_521;
            bytes = bytes[count..];
        }
    }

    private static void UndoPrediction(byte[] plane, int width, int height, int depth)
    {
        int rowBytes = plane.Length / height;
        byte[]? planar = depth == 32 ? ArrayPool<byte>.Shared.Rent(rowBytes) : null;
        try
        {
            for (int y = 0; y < height; y++)
            {
                var row = plane.AsSpan(y * rowBytes, rowBytes);
                if (depth == 16)
                {
                    ushort previous = 0;
                    for (int x = 0; x < width; x++)
                    {
                        var word = row.Slice(x * 2, 2);
                        previous = unchecked((ushort)(previous + BinaryPrimitives.ReadUInt16BigEndian(word)));
                        BinaryPrimitives.WriteUInt16LittleEndian(word, previous);
                    }
                    continue;
                }

                for (int x = 1; x < row.Length; x++) row[x] = unchecked((byte)(row[x] + row[x - 1]));
                if (planar is null) continue;

                // Photoshop predicts each row across four blocks of byte positions.
                // Put the bytes back together in little-endian IEEE single order.
                row.CopyTo(planar);
                for (int x = 0; x < width; x++)
                {
                    row[x * 4] = planar[3 * width + x];
                    row[x * 4 + 1] = planar[2 * width + x];
                    row[x * 4 + 2] = planar[width + x];
                    row[x * 4 + 3] = planar[x];
                }
            }
        }
        finally
        {
            if (planar is not null) ArrayPool<byte>.Shared.Return(planar);
        }
    }
}
