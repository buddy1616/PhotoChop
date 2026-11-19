using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;

namespace PhotoChop;

internal enum RulerUnit { Pixels, Inches, Millimeters }

internal sealed class DocumentRuler : FrameworkElement
{
    private static readonly Brush surface = new SolidColorBrush(Color.FromRgb(38, 42, 50));
    private static readonly Brush ink = new SolidColorBrush(Color.FromRgb(195, 205, 222));
    private static readonly Pen tick = new(ink, 1);
    private static readonly Typeface face = new("Segoe UI");

    public GuideAxis Axis { get; }
    private readonly bool farEdge;
    public double Zoom { get; set; } = 1;
    public double Offset { get; set; }
    public double PixelsPerInch { get; set; } = 72;
    public RulerUnit Unit { get; set; }

    public DocumentRuler(GuideAxis axis, bool farEdge)
    {
        Axis = axis;
        this.farEdge = farEdge;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        if (axis == GuideAxis.Horizontal) Height = 25;
        else Width = 28;
    }

    protected override void OnRender(DrawingContext dc)
    {
        bool horizontal = Axis == GuideAxis.Horizontal;
        double length = horizontal ? ActualWidth : ActualHeight;
        double thickness = horizontal ? ActualHeight : ActualWidth;
        dc.DrawRectangle(surface, null, new Rect(RenderSize));
        double unitPixels = Unit switch { RulerUnit.Inches => PixelsPerInch, RulerUnit.Millimeters => PixelsPerInch / 25.4, _ => 1 };
        double stride = unitPixels * Zoom;
        if (length <= 1 || thickness <= 1 || stride <= 0 || !double.IsFinite(stride)) return;

        double desired = 62 / stride;
        double power = Math.Pow(10, Math.Floor(Math.Log10(desired)));
        if (Unit == RulerUnit.Pixels) power = Math.Max(1, power);
        double major = (desired / power) switch { <= 1 => power, <= 2 => 2 * power, <= 5 => 5 * power, _ => 10 * power };
        int divisions = Unit == RulerUnit.Pixels && major < 5 ? (int)major : 5;
        double interval = major / divisions;
        double step = interval * stride;
        double first = Math.Floor(-Offset / step);
        double edge = farEdge ? 0 : thickness;
        double innerDirection = farEdge ? 1 : -1;
        Point At(double along, double across) => horizontal ? new Point(along, across) : new Point(across, along);
        dc.DrawLine(tick, At(0, edge - (farEdge ? -.5 : .5)), At(length, edge - (farEdge ? -.5 : .5)));
        for (int i = 0; i <= Math.Min(4096, length / step + 2); i++)
        {
            double index = first + i;
            double position = Offset + index * step;
            bool majorTick = Math.Abs(index % divisions) < .001;
            dc.DrawLine(tick, At(position, edge), At(position, edge + innerDirection * (majorTick ? 8 : 4)));
            if (!majorTick || position < -1) continue;
            double value = index * interval;
            var label = new FormattedText(value.ToString("0.###", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, face, 9, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (horizontal)
                dc.DrawText(label, new Point(position + 2, farEdge ? 8 : 1));
            else
            {
                double x = farEdge ? thickness - 1 : 1;
                dc.PushTransform(new RotateTransform(90, x, position + 2));
                dc.DrawText(label, new Point(x, position + 2 - (farEdge ? 0 : label.Height)));
                dc.Pop();
            }
        }
    }
}
