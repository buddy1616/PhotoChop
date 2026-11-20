using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Chop.Core;

namespace PhotoChop;

internal static class StyleFields
{
    public static (DockPanel Row, Slider Slider, TextBox Input) Range(double value, double min, double max)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var input = new TextBox { Text = value.ToString("0.###"), Width = 70 };
        NumericWheel.Enable(input, min, max);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(input, Dock.Right);
        row.Children.Add(input);
        row.Children.Add(slider);
        bool syncing = false;
        slider.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            input.Text = slider.Value.ToString("0.###");
            syncing = false;
        };
        input.TextChanged += (_, _) =>
        {
            if (syncing || !double.TryParse(input.Text, out double number) || !double.IsFinite(number) || number < min || number > max) return;
            syncing = true;
            slider.Value = number;
            syncing = false;
        };
        return (row, slider, input);
    }

    public static ComboBox Contour(StyleContour current)
    {
        var field = new ComboBox();
        foreach (var contour in Enum.GetValues<StyleContour>())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var graph = new Canvas { Width = 44, Height = 24, Background = Brushes.DimGray, Margin = new Thickness(0, 0, 8, 0) };
            var line = new Polyline { Stroke = Brushes.White, StrokeThickness = 1.5 };
            for (int i = 0; i <= 40; i++) line.Points.Add(new Point(i + 2, 22 - 20 * ContourCurve.Sample(contour, i / 40d)));
            graph.Children.Add(line);
            row.Children.Add(graph);
            row.Children.Add(new TextBlock { Text = contour.ToString(), VerticalAlignment = VerticalAlignment.Center });
            var item = new ComboBoxItem { Content = row, Tag = contour };
            field.Items.Add(item);
            if (contour == current) field.SelectedItem = item;
        }
        return field;
    }
}
