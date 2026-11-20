using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chop.Core;

internal static partial class PsdText
{
    internal sealed record Settings(string Text, string Font, double Size, double Bend);

    public static Settings? Read(PsdLayer layer)
    {
        if (!layer.Tags.TryGetValue("TySh", out var data)) return null;
        try
        {
            var reader = new PsdReader(data);
            if (reader.ReadUInt16() != 1) return null;
            reader.Skip(16);
            double shear = BitConverter.Int64BitsToDouble(reader.ReadInt64());
            double scale = BitConverter.Int64BitsToDouble(reader.ReadInt64());
            reader.Skip(16);
            if (reader.ReadUInt16() != 50 || reader.ReadUInt32() != 16) return null;
            var text = PsdDescriptorStyles.ReadDescriptor(reader, 0);
            if (text.Get("Txt ") is not string content || reader.ReadUInt16() != 1 || reader.ReadUInt32() != 16) return null;
            var warp = PsdDescriptorStyles.ReadDescriptor(reader, 0);
            string? style = warp.Enum("warpStyle");
            if (style is not ("warpNone" or "warpArc") ||
                style == "warpArc" && (warp.Enum("warpRotate") != "Hrzn" || warp.Number("warpPerspective", 0) != 0 ||
                    warp.Number("warpPerspectiveOther", 0) != 0)) return null;
            var (family, size) = ReadFont(text.Get("EngineData") as byte[]);
            double heightScale = Math.Sqrt(scale * scale + shear * shear);
            if (double.IsFinite(heightScale) && heightScale > 0) size *= heightScale;
            return new(content.TrimEnd('\0').Replace('\r', '\n'), family, Math.Clamp(size, 1, 512),
                style == "warpArc" ? Math.Clamp(warp.Number("warpValue", 0) / 100, -1, 1) : 0);
        }
        catch (InvalidDataException) { return null; }
    }

    private static (string Font, double Size) ReadFont(byte[]? data)
    {
        const string fallback = "Liberation Sans";
        if (data is null) return (fallback, 48);
        string engine = Encoding.Latin1.GetString(data);
        var style = FontStyle().Match(engine);
        if (!style.Success || !int.TryParse(style.Groups[1].Value, out int index) ||
            !double.TryParse(style.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double size) ||
            !double.IsFinite(size)) return (fallback, 48);
        int start = engine.IndexOf("/FontSet", StringComparison.Ordinal);
        if (start < 0) return (fallback, size);
        int end = engine.IndexOf(']', start);
        if (end < 0) return (fallback, size);
        var names = FontName().Matches(engine[start..end]);
        if (index >= names.Count) return (fallback, size);
        byte[] encoded = Encoding.Latin1.GetBytes(names[index].Groups[1].Value);
        string font = Encoding.BigEndianUnicode.GetString(encoded).Trim();
        return (font.Length == 0 ? fallback : font, size);
    }

    [GeneratedRegex(@"/Font\s+(\d+)\s+/FontSize\s+(\d+(?:\.\d+)?)", RegexOptions.NonBacktracking)]
    private static partial Regex FontStyle();

    [GeneratedRegex(@"/Name\s*\(\xFE\xFF([^)]{1,512})\)", RegexOptions.NonBacktracking)]
    private static partial Regex FontName();
}
