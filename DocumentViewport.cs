using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PhotoChop;

internal sealed class DocumentViewport : Grid
{
    private readonly ScrollBar horizontal = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollBar vertical = new() { Orientation = Orientation.Vertical };
    private bool updating;

    public EditorCanvas Canvas { get; } = new();
    public event Action<Point>? Scrolled;

    public DocumentViewport()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetColumn(vertical, 1);
        SetRow(horizontal, 1);
        Children.Add(Canvas);
        Children.Add(vertical);
        Children.Add(horizontal);
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
