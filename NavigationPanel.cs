using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoChop;

internal sealed class NavigationPanel : FrameworkElement
{
    public BitmapSource? Image { get; set; }
    public Rect Viewport { get; set; }
    public event Action<Point>? Navigate;
    public NavigationPanel() => ClipToBounds = true;

    private Rect ImageBounds()
    {
        if (Image is null || ActualWidth <= 12 || ActualHeight <= 12) return Rect.Empty;
        double scale = Math.Min((ActualWidth - 12) / Image.PixelWidth, (ActualHeight - 12) / Image.PixelHeight);
        double width = Image.PixelWidth * scale, height = Image.PixelHeight * scale;
        return new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var area = ImageBounds();
        if (Image is null || area.IsEmpty) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(73, 77, 85)), null, area);
        dc.DrawImage(Image, area);
        dc.PushClip(new RectangleGeometry(area));
        dc.DrawRectangle(null, new Pen(Brushes.OrangeRed, 1.5), new Rect(
            area.X + area.Width * Viewport.X, area.Y + area.Height * Viewport.Y,
            area.Width * Viewport.Width, area.Height * Viewport.Height));
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Send(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Send(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Send(Point point)
    {
        var bounds = ImageBounds();
        if (!bounds.IsEmpty) Navigate?.Invoke(new Point(Math.Clamp((point.X - bounds.X) / bounds.Width, 0, 1),
            Math.Clamp((point.Y - bounds.Y) / bounds.Height, 0, 1)));
    }
}

public partial class MainWindow
{
    private readonly NavigationPanel navigation = new() { MinHeight = 96 };
    private readonly TextBox navigationZoom = new() { Width = 76, Text = "100", VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider navigationSlider = new() { Minimum = 0, Maximum = Math.Log10(6400), Value = 2, MinWidth = 75 };
    private bool updatingNavigation;

    private FrameworkElement BuildNavigation()
    {
        navigation.Navigate += point =>
        {
            if (active?.Engine.Current is not { } doc) return;
            active.Offset = new(canvas.ActualWidth / 2 - doc.Width * point.X * active.Zoom,
                canvas.ActualHeight / 2 - doc.Height * point.Y * active.Zoom);
            UpdateView();
        };
        var container = new DockPanel { Margin = new Thickness(6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(Button("−", () => ZoomBy(.8)));
        NumericWheel.Enable(navigationZoom, 1, 6400, value =>
        {
            if (!updatingNavigation && active is not null) ZoomBy(value / (active.Zoom * 100));
        });
        navigationSlider.ValueChanged += (_, _) =>
        {
            if (!updatingNavigation && active is not null) ZoomBy(Math.Pow(10, navigationSlider.Value) / (active.Zoom * 100));
        };
        buttons.Children.Add(navigationSlider);
        buttons.Children.Add(navigationZoom);
        buttons.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center });
        buttons.Children.Add(Button("+", () => ZoomBy(1.25)));
        buttons.Children.Add(Button("Fit", Fit));
        DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        container.Children.Add(buttons);
        container.Children.Add(navigation);
        return container;
    }

    private void RefreshNavigation()
    {
        navigation.Image = active?.View.Frame;
        updatingNavigation = true;
        navigationZoom.IsEnabled = navigationSlider.IsEnabled = active is not null;
        if (active is not null)
        {
            if (!navigationZoom.IsKeyboardFocused) navigationZoom.Text = (active.Zoom * 100).ToString("0.##");
            navigationSlider.Value = Math.Log10(active.Zoom * 100);
        }
        updatingNavigation = false;
        if (active?.Engine.Current is { } doc)
            navigation.Viewport = new Rect(-active.Offset.X / (doc.Width * active.Zoom),
                -active.Offset.Y / (doc.Height * active.Zoom),
                canvas.ActualWidth / (doc.Width * active.Zoom), canvas.ActualHeight / (doc.Height * active.Zoom));
        navigation.InvalidateVisual();
    }
}
