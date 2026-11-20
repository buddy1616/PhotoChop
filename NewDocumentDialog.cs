using System.IO;
using System.Windows;
using System.Windows.Controls;
using Chop.Core;

namespace PhotoChop;

internal static class NewDocumentDialog
{
    public static NewArgs? Show(Window owner, int defaultWidth, int defaultHeight, string background)
    {
        var dialog = new Window { Owner = owner, Title = "New document", Width = 410, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(16) };
        dialog.Content = panel;

        (TextBox Field, ComboBox Unit) Input(string label, string value, string[] units, Action change)
        {
            panel.Children.Add(new Label { Content = label });
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var unit = new ComboBox { ItemsSource = units, SelectedIndex = 0, MinWidth = 120, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(unit, Dock.Right);
            var field = new TextBox { Text = value, Padding = new Thickness(4) };
            NumericWheel.Enable(field, 0);
            row.Children.Add(unit);
            row.Children.Add(field);
            panel.Children.Add(row);
            field.TextChanged += (_, _) => change();
            unit.SelectionChanged += (_, _) => change();
            return (field, unit);
        }

        var estimate = new TextBlock { Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap };
        (TextBox Field, ComboBox Unit) width = default, height = default, resolution = default;
        void Update()
        {
            try
            {
                var values = Dimensions();
                estimate.Text = $"{values.Width:N0} × {values.Height:N0} pixels";
            }
            catch (Exception error) when (error is FormatException or InvalidDataException or OverflowException)
            { estimate.Text = error.Message; }
        }

        double Resolution() => double.Parse(resolution.Field!.Text) * (resolution.Unit!.SelectedIndex == 1 ? 2.54 : 1);
        (int Width, int Height, double Ppi) Dimensions()
        {
            double ppi = Resolution();
            if (!double.IsFinite(ppi) || ppi is < 1 or > 100000) throw new InvalidDataException("Enter a valid resolution.");
            int Pixels((TextBox Field, ComboBox Unit) dimension)
            {
                double size = double.Parse(dimension.Field.Text);
                double pixels = dimension.Unit.SelectedIndex switch { 1 => size * ppi, 2 => size * ppi / 25.4, _ => size };
                if (!double.IsFinite(pixels) || pixels <= 0 || pixels > 16384.49)
                    throw new InvalidDataException("Each dimension must be between 1 and 16,384 pixels.");
                return (int)Math.Round(pixels, MidpointRounding.AwayFromZero);
            }
            int w = Pixels(width), h = Pixels(height);
            Format.Dimensions(w, h);
            return (w, h, ppi);
        }

        width = Input("Width", defaultWidth.ToString(), ["Pixels", "Inches", "Millimeters"], Update);
        height = Input("Height", defaultHeight.ToString(), ["Pixels", "Inches", "Millimeters"], Update);
        resolution = Input("Resolution", "72", ["Pixels/inch (DPI)", "Pixels/cm"], Update);
        double PixelScale(int unit) => unit switch { 1 => Resolution(), 2 => Resolution() / 25.4, _ => 1 };
        PreserveValueOnUnitChange(width, PixelScale);
        PreserveValueOnUnitChange(height, PixelScale);
        PreserveValueOnUnitChange(resolution, unit => unit == 1 ? 2.54 : 1);

        var modes = new ComboBox { ItemsSource = new[] { "RGB", "Grayscale", "CMYK" }, SelectedIndex = 0 };
        var depths = new ComboBox { ItemsSource = new[] { "8 Bits/Channel", "16 Bits/Channel", "32 Bits/Channel" }, SelectedIndex = 0 };
        panel.Children.Add(new Label { Content = "Color mode", Target = modes });
        var colorRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(depths, Dock.Right);
        depths.Margin = new Thickness(6, 0, 0, 0);
        colorRow.Children.Add(depths);
        colorRow.Children.Add(modes);
        panel.Children.Add(colorRow);

        var transparent = new CheckBox { Content = "Transparent background", IsChecked = true, Margin = new Thickness(0, 6, 0, 4) };
        var color = new TextBox { Text = background, IsEnabled = false, ToolTip = "Background color, #AARRGGBB" };
        transparent.Click += (_, _) => color.IsEnabled = transparent.IsChecked != true;
        panel.Children.Add(transparent);
        panel.Children.Add(color);
        panel.Children.Add(estimate);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var create = new Button { Content = "Create", IsDefault = true, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4) };
        buttons.Children.Add(create);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4) });
        panel.Children.Add(buttons);
        NewArgs? settings = null;
        create.Click += (_, _) =>
        {
            try
            {
                var (w, h, ppi) = Dimensions();
                string? fill = transparent.IsChecked == true ? null : color.Text.Trim();
                if (fill is not null) Format.Color(fill);
                var mode = modes.SelectedIndex switch { 1 => ChannelColorMode.Grayscale, 2 => ChannelColorMode.Cmyk, _ => ChannelColorMode.Rgb };
                var depth = depths.SelectedIndex switch { 1 => ChannelDepth.Bit16, 2 => ChannelDepth.Bit32, _ => ChannelDepth.Bit8 };
                settings = new NewArgs(Format.Id(), w, h, fill, ppi, StarterLayers: true, Color: new(mode, depth));
                dialog.DialogResult = true;
            }
            catch (Exception error) when (error is FormatException or InvalidDataException or OverflowException)
            { estimate.Text = error.Message; }
        };
        Update();
        dialog.Loaded += (_, _) => { width.Field.Focus(); width.Field.SelectAll(); };
        return dialog.ShowDialog() == true ? settings : null;
    }
    private static void PreserveValueOnUnitChange((TextBox Field, ComboBox Unit) input, Func<int, double> scale)
    {
        int previous = input.Unit.SelectedIndex;
        input.Unit.SelectionChanged += (_, _) =>
        {
            int next = input.Unit.SelectedIndex;
            if (double.TryParse(input.Field.Text, out double value))
            {
                try
                {
                    double converted = value * scale(previous) / scale(next);
                    if (double.IsFinite(converted)) input.Field.Text = converted.ToString("0.########");
                }
                catch (FormatException) { }
            }
            previous = next;
        };
    }

}
