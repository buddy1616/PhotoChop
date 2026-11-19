using System.Windows;
using Chop.Core;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PhotoChop;

internal sealed class DocumentViewport : Grid
{
    private readonly ScrollBar horizontal = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollBar vertical = new() { Orientation = Orientation.Vertical };
    private bool updating;

    public IReadOnlyList<DocumentRuler> Rulers { get; } =
    [
        new(GuideAxis.Horizontal, false), new(GuideAxis.Horizontal, true),
        new(GuideAxis.Vertical, false), new(GuideAxis.Vertical, true)
    ];

    public EditorCanvas Canvas { get; } = new();
    public event Action<Point>? Scrolled;

    public DocumentViewport()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(Canvas, 1, 1);
        Place(vertical, 3, 1);
        Place(horizontal, 1, 3);
        Place(Rulers[0], 1, 0);
        Place(Rulers[1], 1, 2);
        Place(Rulers[2], 0, 1);
        Place(Rulers[3], 2, 1);

        void Place(UIElement item, int column, int row)
        {
            SetColumn(item, column);
            SetRow(item, row);
            Children.Add(item);
        }
        vertical.ValueChanged += (_, _) => Notify();
        horizontal.ValueChanged += (_, _) => Notify();
    }

    public void UpdateBars(Point offset, double zoom, int width, int height)
    {
        updating = true;
        try
        {
            Update(horizontal, -offset.X, Canvas.ActualWidth, width * zoom);
            Update(vertical, -offset.Y, Canvas.ActualHeight, height * zoom);
        }
        finally { updating = false; }
    }

    public void UpdateRulers(bool visible, RulerUnit unit, Point offset, double zoom, double ppi)
    {
        foreach (var ruler in Rulers)
        {
            ruler.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ruler.Unit = unit;
            ruler.Offset = ruler.Axis == GuideAxis.Horizontal ? offset.X : offset.Y;
            ruler.Zoom = zoom;
            ruler.PixelsPerInch = ppi;
            ruler.InvalidateVisual();
        }
    }

    private static void Update(ScrollBar bar, double position, double viewport, double content)
    {
        double margin = Math.Min(64, viewport / 4);
        double center = (content - viewport) / 2;
        double padding = Math.Max(0, viewport - content);
        bar.Minimum = Math.Min(position, center - padding / 2 - Math.Max(0, content - viewport) / 2 - margin);
        bar.Maximum = Math.Max(position, center + padding / 2 + Math.Max(0, content - viewport) / 2 + margin);
        bar.ViewportSize = viewport;
        bar.SmallChange = Math.Max(8, viewport / 20);
        bar.LargeChange = Math.Max(8, viewport * .8);
        bar.Value = position;
    }

    private void Notify()
    {
        if (!updating) Scrolled?.Invoke(new Point(-horizontal.Value, -vertical.Value));
    }
}
