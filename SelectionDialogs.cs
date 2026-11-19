using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private void ModifySelection(bool expand)
    {
        if (active?.Engine.Current?.Selection is null) return;
        string action = expand ? "Expand" : "Contract";
        var values = Form(action + " selection", ("Pixels", "1"));
        if (values is not null) Guard(() =>
        {
            int pixels = Int(values[0]);
            if (pixels is < 1 or > 16384) throw new ArgumentException("Enter 1 to 16,384 pixels.");
            Run("selection.resize", expand ? pixels : -pixels);
        });
    }

    private void SelectColorRange()
    {
        if (active?.Engine.Current is not { } document) return;
        Guard(() =>
        {
            var pixels = ReferenceEquals(document, active.Rendered) ? active.Composite! : renderer.Composite(document);
            if (ColorRangeDialog(pixels) is { } query) Run("selection.colorRange", query);
        });
    }

    private ColorSelection? ColorRangeDialog(Pixels pixels)
    {
        var dialog = new Window { Owner = this, Title = "Color Range", Width = 475, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var root = new StackPanel { Margin = new Thickness(14) };
        dialog.Content = root;
        var choices = new Dictionary<string, string?>
        {
            ["Sampled Color"] = null, ["Reds"] = "#FFFF0000", ["Yellows"] = "#FFFFFF00", ["Greens"] = "#FF00FF00",
            ["Cyans"] = "#FF00FFFF", ["Blues"] = "#FF0000FF", ["Magentas"] = "#FFFF00FF"
        };
        var list = new ComboBox { ItemsSource = choices.Keys, SelectedIndex = 0, Margin = new Thickness(0, 3, 0, 6) };
        root.Children.Add(new TextBlock { Text = "Select" });
        root.Children.Add(list);
        var fuzziness = new Slider { Minimum = 0, Maximum = 200, Value = 30 };
        var amount = new TextBlock { Margin = new Thickness(0, 4, 0, 1) };
        root.Children.Add(amount);
        root.Children.Add(fuzziness);
        var invert = new CheckBox { Content = "Invert", Margin = new Thickness(0, 5, 0, 7) };
        root.Children.Add(invert);
        var grid = new Grid { Height = 170, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var source = new Image { Source = EditorCanvas.Bitmap(pixels), Stretch = Stretch.Uniform, Cursor = ToolCursors.Eyedropper };
        var matching = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(matching, 1);
        grid.Children.Add(source);
        grid.Children.Add(matching);
        root.Children.Add(grid);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = Button("OK", () => dialog.DialogResult = true);
        ok.IsDefault = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(3), Padding = new Thickness(8, 3, 8, 3) });
        root.Children.Add(buttons);

        string sampled = foreground;
        ColorSelection Query() => new(choices[(string)list.SelectedItem] ?? sampled, fuzziness.Value, invert.IsChecked == true);
        void Update()
        {
            amount.Text = $"Fuzziness  {fuzziness.Value:0}";
            var (area, mask) = PixelSelection.ColorRange(pixels, Query());
            int width = Math.Min(240, pixels.Width), height = Math.Min(240, pixels.Height);
            var preview = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int px = x * pixels.Width / width, py = y * pixels.Height / height;
                bool hit = mask?.Contains(px, py) ?? (area is { } a && px >= a.X && px < a.X + a.Width && py >= a.Y && py < a.Y + a.Height);
                int i = (y * width + x) * 4;
                preview[i] = preview[i + 1] = preview[i + 2] = (byte)(hit ? 255 : 0);
                preview[i + 3] = 255;
            }
            matching.Source = EditorCanvas.Bitmap(new Pixels(width, height, preview));
            source.Cursor = list.SelectedIndex == 0 ? ToolCursors.Eyedropper : Cursors.Arrow;
        }
        source.MouseLeftButtonDown += (_, e) =>
        {
            if (list.SelectedIndex != 0) return;
            var point = e.GetPosition(source);
            double scale = Math.Min(source.ActualWidth / pixels.Width, source.ActualHeight / pixels.Height);
            int x = (int)Math.Floor((point.X - (source.ActualWidth - pixels.Width * scale) / 2) / scale);
            int y = (int)Math.Floor((point.Y - (source.ActualHeight - pixels.Height * scale) / 2) / scale);
            if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height) return;
            int i = (y * pixels.Width + x) * 4;
            sampled = $"#FF{pixels.Rgba[i]:X2}{pixels.Rgba[i + 1]:X2}{pixels.Rgba[i + 2]:X2}";
            Update();
        };
        list.SelectionChanged += (_, _) => Update();
        fuzziness.ValueChanged += (_, _) => Update();
        invert.Click += (_, _) => Update();
        Update();
        return dialog.ShowDialog() == true ? Query() : null;
    }
}
