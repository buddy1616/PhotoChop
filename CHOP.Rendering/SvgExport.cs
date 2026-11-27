using System.Globalization;
using System.Text;
using System.Xml;
using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    public byte[] ExportImage(Document document, ImageExportFormat format) => format switch
    {
        ImageExportFormat.Png => EncodePng(Composite(document), document.PixelsPerInch),
        ImageExportFormat.Jpeg => EncodeImage(document, SKEncodedImageFormat.Jpeg),
        ImageExportFormat.WebP => EncodeImage(document, SKEncodedImageFormat.Webp),
        ImageExportFormat.Svg => EncodeSvg(document),
        ImageExportFormat.Ico => EncodeIco(document),
        ImageExportFormat.Psd => EncodePsd(document),
        _ => throw new InvalidDataException("Unknown export format.")
    };

    public byte[] EncodeImage(Document document, SKEncodedImageFormat format)
    {
        var pixels = Composite(document);
        if (format == SKEncodedImageFormat.Jpeg)
        {
            // JPEG has no transparency. Composite over white before encoding.
            pixels = pixels with { Rgba = (byte[])pixels.Rgba.Clone() };
            for (int i = 0; i < pixels.Rgba.Length; i += 4)
            {
                int alpha = pixels.Rgba[i + 3];
                for (int channel = 0; channel < 3; channel++)
                    pixels.Rgba[i + channel] = (byte)((pixels.Rgba[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                pixels.Rgba[i + 3] = 255;
            }
        }
        using var bitmap = Bitmap(pixels);
        using var encoded = bitmap.Encode(format, format == SKEncodedImageFormat.Webp ? 100 : 95) ??
            throw new InvalidDataException($"Could not encode {format}.");
        return encoded.ToArray();
    }

    public byte[] EncodeSvg(Document document)
    {
        const string ns = "http://www.w3.org/2000/svg";
        using var output = new MemoryStream();
        using (var xml = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            xml.WriteStartElement("svg", ns);
            Attribute("width", Number(document.Width));
            Attribute("height", Number(document.Height));
            Attribute("viewBox", $"0 0 {document.Width} {document.Height}");

            if (document.Floating is null && document.Color.Mode != ChannelColorMode.Grayscale && document.Layers.All(Supported) &&
                !document.Layers.Any(layer => layer.Children.Any(ContainsFill)))
                foreach (var layer in document.Layers) WriteLayer(layer);
            else WriteImage(Composite(document), 0, 0);

            xml.WriteEndElement();

            void Attribute(string name, string value) => xml.WriteAttributeString(name, value);
            void Paint(string attribute, string color)
            {
                uint argb = Format.Color(color);
                Attribute(attribute, $"#{argb & 0xFFFFFF:X6}");
                if ((argb >> 24) != 255) Attribute(attribute + "-opacity", Number((argb >> 24) / 255d));
            }
            void WriteImage(Pixels pixels, int x, int y)
            {
                xml.WriteStartElement("image", ns);
                Attribute("x", Number(x));
                Attribute("y", Number(y));
                Attribute("width", Number(pixels.Width));
                Attribute("height", Number(pixels.Height));
                Attribute("href", "data:image/png;base64," + Convert.ToBase64String(EncodePng(pixels)));
                xml.WriteEndElement();
            }
            void WriteLayer(Layer layer)
            {
                if (!layer.Visible || layer.Opacity == 0) return;
                var matrix = layer.Kind == LayerKind.Fill ? SKMatrix.Identity : ExportMatrix(layer);
                xml.WriteStartElement("g", ns);
                Attribute("aria-label", layer.Name);
                Attribute("opacity", Number(layer.Opacity));
                Attribute("transform", "matrix(" + string.Join(" ", new[] { matrix.ScaleX, matrix.SkewY,
                    matrix.SkewX, matrix.ScaleY, matrix.TransX, matrix.TransY }.Select(value => Number(value))) + ")");
                switch (layer.Kind)
                {
                    case LayerKind.Group:
                        foreach (var child in layer.Children) WriteLayer(child);
                        break;
                    case LayerKind.Rectangle:
                    case LayerKind.Fill:
                        xml.WriteStartElement("rect", ns);
                        Attribute("width", Number(layer.Kind == LayerKind.Fill ? document.Width : layer.Width));
                        Attribute("height", Number(layer.Kind == LayerKind.Fill ? document.Height : layer.Height));
                        Paint("fill", layer.Color);
                        xml.WriteEndElement();
                        break;
                    case LayerKind.Ellipse:
                        xml.WriteStartElement("ellipse", ns);
                        Attribute("cx", Number(layer.Width / 2d));
                        Attribute("cy", Number(layer.Height / 2d));
                        Attribute("rx", Number(layer.Width / 2d));
                        Attribute("ry", Number(layer.Height / 2d));
                        Paint("fill", layer.Color);
                        xml.WriteEndElement();
                        break;
                    case LayerKind.Path:
                        var styles = ActiveEffects(layer).ToArray();
                        var fill = styles.FirstOrDefault(effect => effect.Kind == EffectKind.PathFill);
                        var stroke = styles.FirstOrDefault(effect => effect.Kind == EffectKind.Stroke);
                        if (fill is not null) WritePath(layer.Contours.Where(contour => contour.Closed), fill, null);
                        if (stroke is not null) WritePath(layer.Contours, null, stroke);
                        break;
                    default:
                        if (PrepareLayer(layer, []) is { } frame) WriteImage(frame.Pixels, frame.X, frame.Y);
                        break;
                }
                xml.WriteEndElement();
            }
            void WritePath(IEnumerable<PathContour> contours, Effect? fill, Effect? stroke)
            {
                var data = new StringBuilder();
                foreach (var contour in contours)
                {
                    if (contour.Nodes.Length < 2) continue;
                    data.Append("M ").Append(Coordinates(contour.Nodes[0].Point));
                    int count = contour.Closed ? contour.Nodes.Length : contour.Nodes.Length - 1;
                    for (int i = 0; i < count; i++)
                    {
                        var from = contour.Nodes[i];
                        var to = contour.Nodes[(i + 1) % contour.Nodes.Length];
                        if (from.Outgoing is null && to.Incoming is null) data.Append(" L ").Append(Coordinates(to.Point));
                        else data.Append(" C ").Append(Coordinates(from.Outgoing ?? from.Point)).Append(' ')
                            .Append(Coordinates(to.Incoming ?? to.Point)).Append(' ').Append(Coordinates(to.Point));
                    }
                    if (contour.Closed) data.Append(" Z");
                    data.Append(' ');
                }
                if (data.Length == 0) return;
                xml.WriteStartElement("path", ns);
                Attribute("d", data.ToString().TrimEnd());
                if (fill is null) Attribute("fill", "none");
                else { Paint("fill", fill.Ink); Attribute("fill-rule", "evenodd"); }
                if (stroke is not null)
                {
                    Paint("stroke", stroke.Ink);
                    Attribute("stroke-width", Number(stroke.A));
                    Attribute("stroke-linecap", "round");
                    Attribute("stroke-linejoin", "round");
                }
                xml.WriteEndElement();
            }
        }
        return output.ToArray();
    }

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string Coordinates(PointD point) => $"{Number(point.X)} {Number(point.Y)}";
    private static IEnumerable<Effect> ActiveEffects(Layer layer) =>
        layer.TransformsEnabled ? layer.Effects.Where(effect => effect.Enabled) : [];

    private static SKMatrix ExportMatrix(Layer layer)
    {
        var matrix = SKMatrix.Concat(SpatialMatrix(layer), LayerMatrix(layer, !layer.TransformsEnabled));
        return layer.RasterGrid is { } grid ? SKMatrix.Concat(matrix, ToSkia(grid)) : matrix;
    }

    private static bool ContainsFill(Layer layer) => layer.Kind == LayerKind.Fill || layer.Children.Any(ContainsFill);

    private static bool Supported(Layer layer)
    {
        if (!layer.Visible || layer.Opacity == 0) return true;
        if (layer.FillOpacity != 1 || layer.Kind == LayerKind.Effect || layer.Clipped || ActiveVectorMask(layer.VectorMask) || (layer.Mask is not null && layer.MaskEnabled) ||
            (layer.TransformsEnabled ? CommandEngine.EffectiveBlend(layer) : layer.Blend) != Blend.Normal)
            return false;
        var matrix = ExportMatrix(layer);
        if (matrix.Persp0 != 0 || matrix.Persp1 != 0 || matrix.Persp2 != 1) return false;
        var effects = ActiveEffects(layer).ToArray();
        bool centeredStroke = layer.Contours.All(contour => !contour.Closed);
        return effects.All(effect => layer.Kind == LayerKind.Path &&
                (effect.Kind == EffectKind.PathFill || effect.Kind == EffectKind.Stroke &&
                    (centeredStroke || effect.Alignment == StrokeAlignment.Center))) &&
            effects.Count(effect => effect.Kind == EffectKind.Stroke) <= 1 &&
            layer.Children.All(Supported);
    }
}
