using System.Globalization;
using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private sealed record PositionedGlyph(string Text, CharacterStyle Style, float Width, float Height, float Ascent, float Descent, float Scale, float Before);

    private static Pixels RasterizeRichText(Layer layer, RichText text)
    {
        var faces = new Dictionary<(string Font, int Weight, bool Italic), SKTypeface>();
        SKTypeface Face(CharacterStyle style)
        {
            var key = (style.Font, style.Weight, style.Italic);
            if (!faces.TryGetValue(key, out var face))
            {
                string family = RichText.ResolveFamily(style.Font, SKFontManager.Default.FontFamilies);
                face = SKTypeface.FromFamilyName(family, new SKFontStyle(style.Weight, (int)SKFontStyleWidth.Normal,
                    style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright)) ?? CreateTypeface(style.Font);
                faces.Add(key, face);
            }
            return face;
        }
        var fonts = new Dictionary<(CharacterStyle Style, float Scale), SKFont>();
        SKFont Font(CharacterStyle s, float scale)
        {
            var key = (s, scale);
            if (!fonts.TryGetValue(key, out var font))
            {
                font = new(Face(s), (float)s.Size * scale, (float)s.HorizontalScale,
                    s.FauxItalic ? -.22f : 0) { Embolden = s.FauxBold };
                fonts.Add(key, font);
            }
            return font;
        }
        static float Rise(CharacterStyle style) => (float)style.BaselineShift + (style.Script switch
        {
            TextScript.Superscript => (float)style.Size * .34f, TextScript.Subscript => -(float)style.Size * .18f, _ => 0
        });
        Span<ushort> pair = stackalloc ushort[2];
        Span<int> shift = stackalloc int[1];

        var lines = new List<(PositionedGlyph[] Glyphs, ParagraphStyle Style, float Left, float Y, float Height, float Ascent, bool Last)>();
        float y = 2, widest = 0;
        try
        {
            foreach (var paragraph in text.Paragraphs)
            {
                var p = paragraph.Style;
                y += (float)p.SpaceBefore;
                var glyphs = new List<PositionedGlyph>();
                foreach (var run in paragraph.Runs)
                {
                    var s = run.Style;
                    var elements = StringInfo.GetTextElementEnumerator(run.Text);
                    string previous = "";
                    while (elements.MoveNext())
                    {
                        string item = elements.GetTextElement();
                        float scale = s.Script != TextScript.Normal ? .67f : s.Case == TextCase.SmallCaps &&
                            item != item.ToUpperInvariant() ? .8f : 1;
                        if (s.Case != TextCase.Normal) item = item.ToUpperInvariant();
                        var font = Font(s, scale);
                        float kern = previous.Length == 0 ? 0 : (float)(s.KerningOffset * s.Size / 1000);
                        var face = Face(s);
                        if (s.Kerning && previous.Length == 1 && item.Length == 1 && face.UnitsPerEm > 0)
                        {
                            pair[0] = face.GetGlyph(previous[0]);
                            pair[1] = face.GetGlyph(item[0]);
                            if (face.GetKerningPairAdjustments(pair, shift))
                                kern += shift[0] * font.Size * (float)s.HorizontalScale / face.UnitsPerEm;
                        }
                        float advance = font.MeasureText(item) + kern + (float)(s.Tracking * s.Size / 1000);
                        float rise = Rise(s), vertical = (float)s.VerticalScale;
                        glyphs.Add(new(item, s, Math.Max(0, advance), font.Spacing * vertical,
                            Math.Max(0, -font.Metrics.Ascent * vertical + rise),
                            Math.Max(0, font.Metrics.Descent * vertical - rise), scale, kern));
                        previous = item;
                    }
                }

                int first = 0;
                do
                {
                    float left = (float)Math.Max(0, p.LeftIndent + (first == 0 ? p.FirstLineIndent : 0));
                    double available = text.Width > 0 ? Math.Max(1, text.Width - left - p.RightIndent - 4) : double.PositiveInfinity;
                    int end = first, breakAt = first;
                    double length = 0;
                    while (end < glyphs.Count && (end == first || length + glyphs[end].Width <= available))
                    {
                        length += glyphs[end].Width;
                        if (string.IsNullOrWhiteSpace(glyphs[end].Text)) breakAt = end + 1;
                        end++;
                    }
                    if (end < glyphs.Count && breakAt > first) end = breakAt;
                    var row = glyphs.GetRange(first, end - first).ToArray();
                    float ascent = row.Length == 0 ? (float)layer.Size : row.Max(g => g.Ascent);
                    float height = (float)(p.Leading > 0 ? p.Leading : row.Length == 0 ? layer.Size * 1.2 :
                        Math.Max(row.Max(g => g.Height), ascent + row.Max(g => g.Descent)));
                    lines.Add((row, p, left, y, height, ascent, end == glyphs.Count));
                    widest = Math.Max(widest, left + row.Sum(g => g.Width) + (float)p.RightIndent);
                    y += height;
                    first = end;
                } while (first < glyphs.Count);
                y += (float)p.SpaceAfter;
            }

            int width = Math.Clamp((int)Math.Ceiling(Math.Max(text.Width, widest + 6)), 1, 16384);
            int heightPx = Math.Clamp((int)Math.Ceiling(y + 4), 1, Math.Min(16384, 32_000_000 / width));
            using var bitmap = Surface(width, heightPx);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true };
            foreach (var line in lines)
            {
                if (line.Y >= heightPx) break;
                float advance = line.Glyphs.Sum(g => g.Width);
                float gap = Math.Max(0, width - 6 - line.Left - (float)line.Style.RightIndent - advance);
                float x = 2 + line.Left + (line.Style.Alignment switch
                {
                    TextAlignmentMode.Right => gap, TextAlignmentMode.Center => gap / 2, _ => 0
                });
                int spaces = line.Style.Alignment == TextAlignmentMode.Justify && !line.Last
                    ? line.Glyphs.Count(g => string.IsNullOrWhiteSpace(g.Text)) : 0;
                foreach (var glyph in line.Glyphs)
                {
                    var style = glyph.Style;
                    var font = Font(style, glyph.Scale);
                    paint.Color = new SKColor(Format.Color(style.Color));
                    float baseline = line.Y + line.Ascent - Rise(style);
                    canvas.Save();
                    canvas.Translate(x + glyph.Before, baseline);
                    canvas.Scale(1, (float)style.VerticalScale);
                    canvas.DrawText(glyph.Text, 0, 0, SKTextAlign.Left, font, paint);
                    float stroke = Math.Max(1, font.Size / 16);
                    if (style.Underline) canvas.DrawRect(0, stroke, glyph.Width, stroke, paint);
                    if (style.Strike) canvas.DrawRect(0, -font.Size * .3f, glyph.Width, stroke, paint);
                    canvas.Restore();
                    x += glyph.Width + (spaces > 0 && string.IsNullOrWhiteSpace(glyph.Text) ? gap / spaces : 0);
                }
            }
            canvas.Flush();
            return text.Warp is { Style: not TextWarpStyle.None } warp ? WarpTypography(bitmap, warp) : Unpremultiply(bitmap);
        }
        finally
        {
            foreach (var font in fonts.Values) font.Dispose();
            foreach (var face in faces.Values) face.Dispose();
        }
    }

    private static Pixels WarpTypography(SKBitmap bitmap, TextWarp warp)
    {
        const int columns = 40, rows = 20;
        var points = new SKPoint[(columns + 1) * (rows + 1)];
        var uv = new SKPoint[points.Length];
        var triangles = new List<ushort>(columns * rows * 6);
        float width = bitmap.Width, height = bitmap.Height;
        for (int y = 0; y <= rows; y++)
        for (int x = 0; x <= columns; x++)
        {
            double u = x / (double)columns, v = y / (double)rows;
            int i = y * (columns + 1) + x;
            uv[i] = new((float)(u * width), (float)(v * height));
            double a = warp.Vertical ? v : u, b = warp.Vertical ? u : v;
            double t = a * 2 - 1, q = b * 2 - 1, dome = 1 - t * t;
            double bend = warp.Bend, shift = 0, stretch = 0;
            switch (warp.Style)
            {
                case TextWarpStyle.Arc: shift = -dome * .24; break;
                case TextWarpStyle.ArcLower: shift = dome * b * .22; break;
                case TextWarpStyle.ArcUpper: shift = -dome * (1 - b) * .22; break;
                case TextWarpStyle.Arch: shift = -Math.Sin(a * Math.PI) * .22; stretch = dome * q * .10; break;
                case TextWarpStyle.Bulge: stretch = dome * q * .21; break;
                case TextWarpStyle.ShellLower: stretch = dome * b * b * .30; break;
                case TextWarpStyle.ShellUpper: stretch = -dome * (1 - b) * (1 - b) * .30; break;
                case TextWarpStyle.Flag: shift = Math.Sin(a * Math.PI * 2) * (1 - a * .6) * .18; break;
                case TextWarpStyle.Wave: shift = Math.Sin(a * Math.PI * 2) * .20; break;
                case TextWarpStyle.Fish: stretch = Math.Sin(a * Math.PI * 2) * q * .22; break;
                case TextWarpStyle.Rise: shift = -a * a * .23; break;
                case TextWarpStyle.Fisheye: a += bend * t * dome * .16; stretch = dome * q * .22; break;
                case TextWarpStyle.Inflate: a += bend * t * (1 - q * q) * .12; stretch = q * dome * .23; break;
                case TextWarpStyle.Squeeze: a -= bend * t * (1 - q * q) * .16; stretch = -dome * q * .18; break;
                case TextWarpStyle.Twist: shift = t * q * .23; a += bend * q * dome * .13; break;
            }
            double across = warp.Vertical ? width : height, along = warp.Vertical ? height : width;
            b += bend * (shift + stretch) * Math.Max(1, along / across * .65);
            (u, v) = warp.Vertical ? (b, a) : (a, b);
            u = .5 + (u - .5) * (1 + warp.HorizontalDistortion * (v - .5) * .9);
            v = .5 + (v - .5) * (1 + warp.VerticalDistortion * (u - .5) * .9);
            points[i] = new((float)(u * width), (float)(v * height));
            if (x < columns && y < rows)
            {
                int below = i + columns + 1;
                triangles.AddRange([(ushort)i, (ushort)(i + 1), (ushort)below, (ushort)(i + 1), (ushort)(below + 1), (ushort)below]);
            }
        }
        float left = Math.Min(0, points.Min(p => p.X)), top = Math.Min(0, points.Min(p => p.Y));
        int w = (int)Math.Ceiling(points.Max(p => p.X) - left), h = (int)Math.Ceiling(points.Max(p => p.Y) - top);
        if (w < 1 || h < 1 || w > 16384 || h > 16384 || (long)w * h > 32_000_000) return Unpremultiply(bitmap);
        using var output = Surface(w, h);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-left, -top);
        using var shader = bitmap.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, points, uv, null, triangles.ToArray());
        canvas.DrawVertices(vertices, SKBlendMode.SrcOver, paint);
        canvas.Flush();
        return Unpremultiply(output);
    }
}
