using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoChop;

internal sealed class HueSpectrum : FrameworkElement
{
    private static readonly Brush spectrum = CreateSpectrum();
    private double value;
    public event EventHandler? ValueChanged;
    public double Value
    {
        get => value;
        set
        {
            var next = Math.Clamp(value, 0, 359.999);
            if (next == this.value) return;
            this.value = next;
            InvalidateVisual();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public HueSpectrum()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, "Hue spectrum");
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(spectrum, new Pen(IsKeyboardFocused ? Brushes.DeepSkyBlue : Brushes.Gray, 1), new Rect(RenderSize));
        double x = Math.Clamp(Value / 360 * ActualWidth, 2, Math.Max(2, ActualWidth - 2));
        dc.DrawLine(new Pen(Brushes.Black, 5), new Point(x, 2), new Point(x, ActualHeight - 2));
        dc.DrawLine(new Pen(Brushes.White, 2), new Point(x, 2), new Point(x, ActualHeight - 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        SetFrom(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) SetFrom(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
        {
            Value += e.Key == Key.Left ? -1 : 1;
            e.Handled = true;
        }
    }

    private void SetFrom(Point point) => Value = point.X / Math.Max(1, ActualWidth) * 360;

    private static Brush CreateSpectrum()
    {
        Color[] colors = [Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red];
        var stops = new GradientStopCollection(colors.Select((color, index) => new GradientStop(color, index / 6.0)));
        var brush = new LinearGradientBrush(stops, new Point(0, .5), new Point(1, .5));
        brush.Freeze();
        return brush;
    }
}
