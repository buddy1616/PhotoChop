using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PhotoChop;

internal static class ColorPickerDialog
{
    public static string? Show(Window owner, string title, string initial)
    {
        var original = (Color)ColorConverter.ConvertFromString(initial)!;
        var dialog = new Window { Owner = owner, Title = title, Width = 428, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var root = new StackPanel { Margin = new Thickness(16) };
        dialog.Content = root;
        var area = new Grid { Height = 210, Cursor = Cursors.Cross, ClipToBounds = true };
        var hueFill = new SolidColorBrush();
        area.Background = hueFill;
        area.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0)
        });
        area.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90)
        });
        var indicator = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        area.Children.Add(indicator);
        root.Children.Add(area);
        var hue = new HueSpectrum { Height = 22, Margin = new Thickness(0, 4, 0, 4) };
        var opacity = StyleFields.Range(original.A * 100 / 255d, 0, 100);
        var alpha = opacity.Slider;
        root.Children.Add(new TextBlock { Text = "Hue" });
        root.Children.Add(hue);
        root.Children.Add(new TextBlock { Text = "Opacity (%)" });
        root.Children.Add(opacity.Row);
        var colorRow = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        var swatch = new Border { Width = 52, Height = 28, Margin = new Thickness(0, 0, 8, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        DockPanel.SetDock(swatch, Dock.Left);
        colorRow.Children.Add(swatch);
        var hex = new TextBox { Padding = new Thickness(4) };
        colorRow.Children.Add(hex);
        root.Children.Add(colorRow);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 76, Margin = new Thickness(3), Padding = new Thickness(6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(3), Padding = new Thickness(6) };
        ok.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(ok);
        actions.Children.Add(cancel);
        root.Children.Add(actions);

        double saturation = 0, value = 0;
        bool updating = false;
        Color current = original;
        void Redraw(bool writeHex)
        {
            indicator.Margin = new Thickness(saturation * area.ActualWidth - 6, (1 - value) * area.ActualHeight - 6, 0, 0);
            hueFill.Color = FromHsv(hue.Value, 1, 1, 255);
            current = FromHsv(hue.Value, saturation, value, (byte)Math.Round(alpha.Value * 255 / 100));
            swatch.Background = new SolidColorBrush(current);
            if (writeHex) hex.Text = current.ToString();
            ok.IsEnabled = true;
        }
        void Read(Color color)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double high = Math.Max(r, Math.Max(g, b)), low = Math.Min(r, Math.Min(g, b)), delta = high - low;
            value = high;
            saturation = high == 0 ? 0 : delta / high;
            if (delta != 0)
            {
                double sector = high == r ? (g - b) / delta : high == g ? 2 + (b - r) / delta : 4 + (r - g) / delta;
                hue.Value = (sector * 60 + 360) % 360;
            }
            alpha.Value = color.A * 100 / 255d;
        }
        void ApplyControls()
        {
            if (updating) return;
            updating = true;
            Redraw(true);
            updating = false;
        }
        void Sample(Point point)
        {
            saturation = Math.Clamp(point.X / Math.Max(1, area.ActualWidth), 0, 1);
            value = 1 - Math.Clamp(point.Y / Math.Max(1, area.ActualHeight), 0, 1);
            ApplyControls();
        }
        area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); Sample(e.GetPosition(area)); };
        area.MouseMove += (_, e) => { if (area.IsMouseCaptured) Sample(e.GetPosition(area)); };
        area.MouseLeftButtonUp += (_, _) => area.ReleaseMouseCapture();
        area.SizeChanged += (_, _) => ApplyControls();
        hue.ValueChanged += (_, _) => ApplyControls();
        alpha.ValueChanged += (_, _) => ApplyControls();
        hex.TextChanged += (_, _) =>
        {
            if (updating) return;
            try
            {
                var text = hex.Text.Trim();
                Chop.Core.Format.Color(text);
                var color = (Color)ColorConverter.ConvertFromString(text)!;
                updating = true;
                Read(color);
                Redraw(false);
            }
            catch (Exception) { ok.IsEnabled = false; }
            finally { updating = false; }
        };
        updating = true;
        Read(original);
        Redraw(true);
        updating = false;
        return dialog.ShowDialog() == true ? current.ToString() : null;
    }

    private static Color FromHsv(double hue, double saturation, double value, byte alpha)
    {
        double chroma = value * saturation, sector = (hue % 360) / 60, secondary = chroma * (1 - Math.Abs(sector % 2 - 1));
        var (r, g, b) = sector switch
        {
            < 1 => (chroma, secondary, 0d), < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary), < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma), _ => (chroma, 0d, secondary)
        };
        double floor = value - chroma;
        byte Channel(double component) => (byte)Math.Round((component + floor) * 255);
        return Color.FromArgb(alpha, Channel(r), Channel(g), Channel(b));
    }
}
