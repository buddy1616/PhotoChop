using System.Windows;
using System.Windows.Media.Imaging;
using Chop.Core;
using SkiaSharp;

namespace PhotoChop;

// A screen-space proxy for the active stroke. The committed render evaluates the entire layer stack.
internal sealed class TonalDragPreview
{
    private readonly Document document;
    private readonly byte[] baseline;
    private readonly int width, height;
    private readonly RasterEdit raster;
    private readonly Layer layer;
    private readonly SKMatrix inverse;
    private readonly StrokeArgs stroke;
    private readonly byte[] bgra;
    private readonly byte[] coverage;
    private readonly byte[][] curves = new byte[256][];
    private PointD? previous;
    private double remaining;
    public WriteableBitmap Bitmap { get; }

    public TonalDragPreview(Document document, BitmapSource frame, Layer layer, RasterEdit raster, SKMatrix inverse, StrokeArgs stroke)
    {
        this.document = document;
        width = frame.PixelWidth;
        height = frame.PixelHeight;
        this.raster = raster;
        this.layer = layer;
        this.inverse = inverse;
        this.stroke = stroke;
        remaining = Math.Max(.5, stroke.Size / 6);
        Bitmap = new WriteableBitmap(frame);
        baseline = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        frame.CopyPixels(baseline, width * 4, 0);
        bgra = (byte[])baseline.Clone();
        coverage = new byte[width * height];
    }

    public void Add(PointD point, bool finish = false)
    {
        int left = width, top = height, right = -1, bottom = -1;
        double radius = stroke.Size / 2, feather = Math.Max(.5, radius * (1 - stroke.Hardness));
        void Stamp(PointD center)
        {
            int x0 = (int)Math.Clamp(Math.Floor(center.X - radius), 0, width);
            int y0 = (int)Math.Clamp(Math.Floor(center.Y - radius), 0, height);
            int x1 = (int)Math.Clamp(Math.Ceiling(center.X + radius), 0, width);
            int y1 = (int)Math.Clamp(Math.Ceiling(center.Y + radius), 0, height);
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int index = y * width + x;
                if (coverage[index] == 255) continue;
                double dx = x + .5 - center.X, dy = y + .5 - center.Y;
                double distance = dx * dx + dy * dy;
                if (distance >= radius * radius) continue;
                double selection = document.Selection is null ? 1 : document.SelectionAmount(x, y) / 255d;
                if (selection == 0) continue;
                int amount = (int)Math.Round(255 * selection * Math.Min(1, (radius - Math.Sqrt(distance)) / feather));
                if (amount <= coverage[index]) continue;
                var local = inverse.MapPoint(x + .5f, y + .5f);
                int rx = (int)Math.Floor(local.X) - raster.X, ry = (int)Math.Floor(local.Y) - raster.Y;
                if (rx < 0 || ry < 0 || rx >= raster.Pixels.Width || ry >= raster.Pixels.Height) continue;
                double visible = raster.Pixels.Rgba[(ry * raster.Pixels.Width + rx) * 4 + 3] / 255d *
                    layer.Opacity * layer.FillOpacity;
                if (layer.MaskEnabled && layer.Mask is { } mask) visible *= mask.At(rx + raster.X, ry + raster.Y) / 255d;
                if (visible == 0) continue;
                coverage[index] = (byte)amount;
                int previewAmount = (int)Math.Round(visible * amount);
                var curve = curves[previewAmount] ??= MakeCurve(previewAmount);
                int pixel = index * 4;
                bgra[pixel] = curve[baseline[pixel]];
                bgra[pixel + 1] = curve[baseline[pixel + 1]];
                bgra[pixel + 2] = curve[baseline[pixel + 2]];
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        }

        if (previous is { } start)
        {
            double dx = point.X - start.X, dy = point.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double offset = remaining;
            for (; offset <= length; offset += Math.Max(.5, stroke.Size / 6))
                Stamp(new(start.X + dx * offset / length, start.Y + dy * offset / length));
            remaining = offset - length;
        }
        else Stamp(point);
        if (finish) Stamp(point);
        previous = point;
        if (right >= left)
        {
            var rect = new Int32Rect(left, top, right - left + 1, bottom - top + 1);
            Bitmap.WritePixels(rect, bgra, width * 4, left, top);
        }
    }

    private byte[] MakeCurve(int amount)
    {
        double exponent = 1 + stroke.Opacity * amount / 255;
        var curve = new byte[256];
        for (int i = 0; i < curve.Length; i++)
        {
            double value = i / 255d;
            double toned = stroke.Tone == TonalStroke.Dodge ? 1 - Math.Pow(1 - value, exponent) : Math.Pow(value, exponent);
            curve[i] = (byte)Math.Round(toned * 255);
        }
        return curve;
    }
}
