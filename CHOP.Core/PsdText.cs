using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chop.Core;

internal static partial class PsdText
{
    internal sealed record Settings(string Text, string Font, double Size, double Bend, RichText Typography);

    public static Settings? Read(PsdLayer layer, CmykProfile? profile)
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
            string? key = warp.Enum("warpStyle");
            var style = TextWarpStyle.None;
            if (key is not null && (!key.StartsWith("warp", StringComparison.Ordinal) ||
                !Enum.TryParse(key[4..], ignoreCase: true, out style))) return null;
            var deformation = new TextWarp(style, Math.Clamp(warp.Number("warpValue", 0) / 100, -1, 1),
                warp.Enum("warpRotate") == "Vrtc", Math.Clamp(warp.Number("warpPerspective", 0) / 100, -1, 1),
                Math.Clamp(warp.Number("warpPerspectiveOther", 0) / 100, -1, 1));
            var dataBytes = text.Get("EngineData") as byte[];
            var (family, size) = ReadFont(dataBytes);
            double heightScale = Math.Sqrt(scale * scale + shear * shear);
            if (!double.IsFinite(heightScale) || heightScale <= 0) heightScale = 1;
            size = Math.Clamp(size * heightScale, 1, 512);
            string plain = content.TrimEnd('\0').Replace('\r', '\n');
            var typography = ReadRuns(plain, dataBytes, new CharacterStyle(family, size), deformation, heightScale, profile);
            return new(plain, family, size, deformation.Style == TextWarpStyle.Arc ? deformation.Bend : 0, typography);
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

    private static RichText ReadRuns(string content, byte[]? data, CharacterStyle fallback, TextWarp warp, double scale, CmykProfile? profile)
    {
        if (data is null || content.Length > 32768) return RichText.FromPlain(content, fallback, warp);
        string engine = Encoding.Latin1.GetString(data);
        static string[] Blocks(string source, string name, string marker, out int[] lengths)
        {
            lengths = [];
            int start = source.IndexOf(name, StringComparison.Ordinal);
            if (start < 0) return [];
            int items = source.IndexOf("/RunArray", start, StringComparison.Ordinal);
            int count = items < 0 ? -1 : source.IndexOf("/RunLengthArray", items, StringComparison.Ordinal);
            if (count < 0) return [];
            var match = Regex.Match(source[count..], @"^/RunLengthArray\s*\[([\s\d]+)\]", RegexOptions.NonBacktracking);
            if (!match.Success) return [];
            lengths = Regex.Matches(match.Groups[1].Value, @"\d+").Select(v => int.TryParse(v.Value, out int n) ? n : 0).ToArray();
            return source[items..count].Split(marker).Skip(1).ToArray();
        }
        static string? Field(string data, string key)
        {
            int start = data.IndexOf("/" + key + " ", StringComparison.Ordinal);
            if (start < 0) return null;
            start += key.Length + 2;
            int end = start;
            while (end < data.Length && !char.IsWhiteSpace(data[end])) end++;
            return data[start..end];
        }
        static double Number(string data, string key, double fallback = 0) =>
            double.TryParse(Field(data, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : fallback;
        static bool Flag(string data, string key, bool fallback = false) => Field(data, key) switch
        {
            "true" => true, "false" => false, _ => fallback
        };
        int fontsStart = engine.IndexOf("/FontSet", StringComparison.Ordinal);
        int fontsEnd = fontsStart < 0 ? -1 : engine.IndexOf(']', fontsStart);
        string[] names = fontsEnd < 0 ? [] : FontName().Matches(engine[fontsStart..fontsEnd])
            .Select(m => Encoding.BigEndianUnicode.GetString(Encoding.Latin1.GetBytes(m.Groups[1].Value)).Trim()).ToArray();
        var blocks = Blocks(engine, "/StyleRun", "/StyleSheetData", out var lengths);
        var paragraphs = Blocks(engine, "/ParagraphRun", "/Properties", out var paragraphLengths);
        CharacterStyle ReadStyle(string block)
        {
            int index = (int)Number(block, "Font", -1);
            string font = index >= 0 && index < names.Length && names[index].Length > 0 ? names[index] : fallback.Font;
            string color = fallback.Color;
            int fill = block.IndexOf("/FillColor", StringComparison.Ordinal);
            if (fill >= 0)
            {
                var match = Regex.Match(block[fill..], @"/Values\s*\[([^\]]+)\]");
                var samples = match.Success ? match.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? Math.Clamp(n, 0, 1) : 0).ToArray() : [];
                static int Byte(double v) => (int)Math.Round(v * 255);
                if (samples.Length == 4) color = $"#{Byte(samples[0]):X2}{Byte(samples[1]):X2}{Byte(samples[2]):X2}{Byte(samples[3]):X2}";
                else if (samples.Length == 5 && profile is not null)
                {
                    var rgb = new ushort[3];
                    if (profile.Convert(samples.Skip(1).Select(v => (ushort)Math.Round(v * 65535)).ToArray(), rgb, 1))
                        color = $"#{Byte(samples[0]):X2}{(rgb[0] + 128) / 257:X2}{(rgb[1] + 128) / 257:X2}{(rgb[2] + 128) / 257:X2}";
                }
            }
            return new(font, Math.Clamp(Number(block, "FontSize", fallback.Size / scale) * scale, 1, 512), color,
                font.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? 700 : 400,
                font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase),
                Flag(block, "FauxBold"), Flag(block, "FauxItalic"), Flag(block, "Underline"), Flag(block, "Strikethrough"),
                (int)Number(block, "FontCaps") switch { 1 => TextCase.SmallCaps, 2 => TextCase.Uppercase, _ => TextCase.Normal },
                (int)Number(block, "FontBaseline") switch { 1 => TextScript.Superscript, 2 => TextScript.Subscript, _ => TextScript.Normal },
                Math.Clamp(Number(block, "Tracking"), -500, 2000), Math.Clamp(Number(block, "HorizontalScale", 1), .1, 5),
                Math.Clamp(Number(block, "VerticalScale", 1), .1, 5), Math.Clamp(Number(block, "BaselineShift") * scale, -512, 512),
                Flag(block, "AutoKerning", true), Math.Clamp(Number(block, "Kerning"), -500, 2000));
        }
        var resolved = new List<(int End, CharacterStyle Style, string Block)>();
        int position = 0;
        for (int i = 0; i < Math.Min(blocks.Length, lengths.Length) && position < content.Length; i++)
        {
            position += Math.Clamp(lengths[i], 0, content.Length - position);
            if (lengths[i] > 0) resolved.Add((position, ReadStyle(blocks[i]), blocks[i]));
        }
        resolved.Add((content.Length + 1, fallback, ""));
        var result = new List<TextParagraph>();
        int cursor = 0, run = 0, paragraphIndex = 0, paragraphEnd = paragraphLengths.FirstOrDefault();
        foreach (string line in content.Split('\n'))
        {
            if (result.Count >= 4096) break;
            while (paragraphIndex + 1 < paragraphLengths.Length && cursor >= paragraphEnd)
                paragraphEnd += Math.Clamp(paragraphLengths[++paragraphIndex], 0, content.Length + 1);
            while (cursor >= resolved[run].End) run++;
            string block = paragraphIndex < paragraphs.Length ? paragraphs[paragraphIndex] : "";
            double Measure(string key, double min = 0) => Math.Clamp(Number(block, key) * scale, min, 4096);
            var p = new ParagraphStyle((int)Number(block, "Justification") switch
            {
                1 => TextAlignmentMode.Right, 2 => TextAlignmentMode.Center, >= 3 => TextAlignmentMode.Justify, _ => TextAlignmentMode.Left
            }, Flag(resolved[run].Block, "AutoLeading", true) ? 0 : Math.Clamp(Number(resolved[run].Block, "Leading") * scale, 0, 4096),
                Measure("StartIndent"), Measure("EndIndent"), Measure("FirstLineIndent", -4096), Measure("SpaceBefore"), Measure("SpaceAfter"));
            var runs = new List<TextRun>();
            int end = cursor + line.Length;
            while (cursor < end)
            {
                while (cursor >= resolved[run].End) run++;
                int next = Math.Min(end, resolved[run].End);
                runs.Add(new(content[cursor..next], resolved[run].Style));
                cursor = next;
            }
            if (runs.Count == 0) runs.Add(new("", resolved[run].Style));
            result.Add(new(runs.ToArray(), p));
            cursor++;
        }
        var rich = new RichText(result.ToArray(), warp);
        RichText.Check(rich);
        return rich;
    }

    [GeneratedRegex(@"/Font\s+(\d+)\s+/FontSize\s+(\d+(?:\.\d+)?)", RegexOptions.NonBacktracking)]
    private static partial Regex FontStyle();

    [GeneratedRegex(@"/Name\s*\(\xFE\xFF([^)]{1,512})\)", RegexOptions.NonBacktracking)]
    private static partial Regex FontName();
}
