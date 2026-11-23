namespace Chop.Core;

public enum TextAlignmentMode { Left, Center, Right, Justify }
public enum TextCase { Normal, Uppercase, SmallCaps }
public enum TextScript { Normal, Subscript, Superscript }
public enum TextWarpStyle { None, Arc, ArcLower, ArcUpper, Arch, Bulge, ShellLower, ShellUpper, Flag, Wave, Fish, Rise, Fisheye, Inflate, Squeeze, Twist }

public sealed record TextWarp(TextWarpStyle Style = TextWarpStyle.None, double Bend = 0, bool Vertical = false,
    double HorizontalDistortion = 0, double VerticalDistortion = 0);

public sealed record CharacterStyle(string Font = "Liberation Sans", double Size = 48, string Color = "#FFFFFFFF",
    int Weight = 400, bool Italic = false, bool FauxBold = false, bool FauxItalic = false, bool Underline = false,
    bool Strike = false, TextCase Case = TextCase.Normal, TextScript Script = TextScript.Normal,
    double Tracking = 0, double HorizontalScale = 1, double VerticalScale = 1, double BaselineShift = 0, bool Kerning = true,
    double KerningOffset = 0);
public sealed record TextRun(string Text, CharacterStyle Style);
public sealed record ParagraphStyle(TextAlignmentMode Alignment = TextAlignmentMode.Left, double Leading = 0,
    double LeftIndent = 0, double RightIndent = 0, double FirstLineIndent = 0, double SpaceBefore = 0, double SpaceAfter = 0);
public sealed record TextParagraph(TextRun[] Runs, ParagraphStyle Style);
public sealed record RichText(TextParagraph[] Paragraphs, TextWarp? Warp = null, double Width = 0, double Height = 0, PointD[][]? Shape = null)
{
    public const int MaxBoxSide = 16384;

    public string PlainText => string.Join("\n", Paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Text))));

    public static RichText FromPlain(string text, CharacterStyle style, TextWarp? warp = null) =>
        new(text.Replace("\r", "").Split('\n').Select(line => new TextParagraph([new(line, style)], new())).ToArray(), warp);

    public static string ResolveFamily(string requested, IEnumerable<string> installed)
    {
        static string Key(string family) => string.Concat(family.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        string name = Key(requested);
        string? candidate = installed.FirstOrDefault(family => Key(family) == name);
        if (candidate is not null) return candidate;
        var suffixes = new[] { "BOLDITALIC", "BOLDOBLIQUE", "REGULAR", "ITALIC", "OBLIQUE", "BOLD" };
        var suffix = suffixes.FirstOrDefault(value => name.EndsWith(value, StringComparison.Ordinal) && name.Length > value.Length);
        if (suffix is null) return requested;
        string baseName = name[..^suffix.Length];
        return installed.FirstOrDefault(family => Key(family) == baseName) ?? requested;
    }

    public static void Check(RichText? value)
    {
        if (value is null) return;
        static bool Valid(double v, double min, double max) => double.IsFinite(v) && v >= min && v <= max;
        if (value.Paragraphs is not { Length: > 0 and <= 4096 } || !Valid(value.Width, 0, MaxBoxSide) ||
            !Valid(value.Height, 0, MaxBoxSide) || value.Height > 0 && (value.Width < 1 || value.Width * value.Height > 32_000_000) ||
            value.Shape is { } shape && (value.Height < 1 || shape.Length == 0 ||
                shape.Sum(loop => (long)(loop?.Length ?? 0)) > 8192 || shape.Any(loop => loop is not { Length: >= 3 } ||
                    loop.Any(p => !Valid(p.X, 0, value.Width) || !Valid(p.Y, 0, value.Height)))) ||
            value.Warp is { } warp && (!Enum.IsDefined(warp.Style) || !Valid(warp.Bend, -1, 1) ||
                !Valid(warp.HorizontalDistortion, -1, 1) || !Valid(warp.VerticalDistortion, -1, 1)))
            throw new InvalidDataException("Invalid text layout.");
        long length = 0, count = 0;
        foreach (var paragraph in value.Paragraphs)
        {
            if (paragraph?.Style is not { } p || paragraph.Runs is null || !Enum.IsDefined(p.Alignment) ||
                !Valid(p.Leading, 0, 4096) || !Valid(p.LeftIndent, 0, 4096) || !Valid(p.RightIndent, 0, 4096) ||
                !Valid(p.FirstLineIndent, -4096, 4096) || !Valid(p.SpaceBefore, 0, 4096) || !Valid(p.SpaceAfter, 0, 4096))
                throw new InvalidDataException("Invalid paragraph.");
            foreach (var run in paragraph.Runs)
            {
                if (++count > 32768 || run?.Text is null || (length += run.Text.Length) > 32768 ||
                    run.Style is not { } s || string.IsNullOrWhiteSpace(s.Font) || s.Font.Length > 256 ||
                    !Valid(s.Size, 1, 512) || s.Weight is < 100 or > 950 || !Enum.IsDefined(s.Case) ||
                    !Enum.IsDefined(s.Script) || !Valid(s.Tracking, -500, 2000) || !Valid(s.KerningOffset, -500, 2000) ||
                    !Valid(s.HorizontalScale, .1, 5) || !Valid(s.VerticalScale, .1, 5) || !Valid(s.BaselineShift, -512, 512))
                    throw new InvalidDataException("Invalid text run.");
                Format.Color(s.Color);
            }
        }
    }
}
