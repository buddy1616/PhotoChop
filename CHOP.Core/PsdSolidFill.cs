namespace Chop.Core;

internal static class PsdSolidFill
{
    public static string? Read(PsdLayer layer)
    {
        if (!layer.Tags.TryGetValue("SoCo", out var block)) return null;
        try
        {
            var reader = new PsdReader(block);
            if (reader.ReadUInt32() != 16) return null;
            SkipName(reader);
            ReadId(reader);
            if (reader.ReadUInt32() != 1 || ReadId(reader) != "Clr " || reader.ReadAscii(4) != "Objc") return null;
            SkipName(reader);
            if (ReadId(reader) != "RGBC" || reader.ReadUInt32() != 3) return null;
            var channels = new Dictionary<string, byte>();
            for (int i = 0; i < 3; i++)
            {
                string key = ReadId(reader);
                if (reader.ReadAscii(4) != "doub") return null;
                double value = BitConverter.Int64BitsToDouble(reader.ReadInt64());
                if (!double.IsFinite(value) || !channels.TryAdd(key, (byte)Math.Round(Math.Clamp(value, 0, 255))))
                    return null;
            }
            return channels.TryGetValue("Rd  ", out byte r) && channels.TryGetValue("Grn ", out byte g) &&
                channels.TryGetValue("Bl  ", out byte b) ? $"#FF{r:X2}{g:X2}{b:X2}" : null;
        }
        catch (InvalidDataException) { return null; }
    }

    private static void SkipName(PsdReader reader) => reader.Skip(reader.ReadUInt32() * 2L);

    private static string ReadId(PsdReader reader)
    {
        uint length = reader.ReadUInt32();
        if (length > 64) throw new InvalidDataException("Unsupported Photoshop color descriptor ID.");
        return reader.ReadAscii(length == 0 ? 4 : (int)length);
    }
}
