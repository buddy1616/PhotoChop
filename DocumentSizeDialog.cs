using System.IO;
using System.Windows;
using System.Windows.Controls;
using Chop.Core;

namespace PhotoChop;

internal static class DocumentSizeDialog
{
    internal sealed record SizeOptions(int Width, int Height, double PixelsPerInch, int AnchorX, int AnchorY);
    public static SizeOptions? Show(Window owner, Document current, bool canvasOnly)
    {
        var dialog = new Window { Owner = owner, Title = canvasOnly ? "Canvas Size" : "Image Size", Width = 404,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(16) };
        dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = $"Current size   {current.Width:N0} × {current.Height:N0} px", Margin = new Thickness(0, 0, 0, 8) });
        var units = canvasOnly ? new[] { "Pixels", "Inches", "Millimeters" } : new[] { "Pixels", "Inches", "Millimeters", "Percent" };
        (TextBox Value, ComboBox Unit) Field(string label, string value, string[] options)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
            var unit = new ComboBox { ItemsSource = options, SelectedIndex = 0, MinWidth = 128, Margin = new Thickness(6, 0, 0, 0) };
            var input = new TextBox { Text = value, Padding = new Thickness(4) };
            NumericWheel.Enable(input, 0);
            panel.Children.Add(new Label { Content = label, Target = input, Padding = new Thickness(0) });
            DockPanel.SetDock(unit, Dock.Right);
            row.Children.Add(unit);
            row.Children.Add(input);
            panel.Children.Add(row);
            return (input, unit);
        }
        var width = Field("Width", current.Width.ToString(), units);
        var height = Field("Height", current.Height.ToString(), units);
        (TextBox? Value, ComboBox? Unit) resolution = canvasOnly ? default :
            Field("Resolution", current.PixelsPerInch.ToString(), ["Pixels/inch (DPI)", "Pixels/cm"]);
        double Ppi() => canvasOnly ? current.PixelsPerInch :
            double.Parse(resolution.Value!.Text) * (resolution.Unit!.SelectedIndex == 1 ? 2.54 : 1);
        double Scale(int unit, int original) => unit switch { 1 => Ppi(), 2 => Ppi() / 25.4, 3 => original / 100.0, _ => 1 };
        void BindUnits((TextBox Value, ComboBox Unit) field, Func<int, double> scale)
        {
            int previous = field.Unit.SelectedIndex;
            field.Unit.SelectionChanged += (_, _) =>
            {
                if (double.TryParse(field.Value.Text, out var value))
                {
                    try
                    {
                        double converted = value * scale(previous) / scale(field.Unit.SelectedIndex);
                        if (double.IsFinite(converted)) field.Value.Text = converted.ToString("0.########");
                    }
                    catch (FormatException) { }
                }
                previous = field.Unit.SelectedIndex;
            };
        }
        BindUnits(width, unit => Scale(unit, current.Width));
        BindUnits(height, unit => Scale(unit, current.Height));
        if (resolution.Value is { } resValue && resolution.Unit is { } resUnit)
            BindUnits((resValue, resUnit), unit => unit == 1 ? 2.54 : 1);

        if (!canvasOnly)
        {
            var linked = new CheckBox { Content = "Maintain proportions", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(linked);
            bool updating = false;
            void Sync((TextBox Value, ComboBox Unit) source, int originalSource, (TextBox Value, ComboBox Unit) target, int originalTarget)
            {
                if (updating || linked.IsChecked != true || !double.TryParse(source.Value.Text, out var value)) return;
                try
                {
                    var next = value * Scale(source.Unit.SelectedIndex, originalSource) / originalSource
                        * originalTarget / Scale(target.Unit.SelectedIndex, originalTarget);
                    if (!double.IsFinite(next)) return;
                    updating = true;
                    target.Value.Text = next.ToString("0.########");
                }
                catch (FormatException) { }
                finally { updating = false; }
            }
            width.Value.TextChanged += (_, _) => Sync(width, current.Width, height, current.Height);
            height.Value.TextChanged += (_, _) => Sync(height, current.Height, width, current.Width);
        }

        var anchor = new AnchorPicker();
        if (canvasOnly)
        {
            panel.Children.Add(new Label { Content = "Anchor", Padding = new Thickness(0, 6, 0, 4) });
            panel.Children.Add(anchor);
        }

        var information = new TextBlock { Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(information);
        SizeOptions Values()
        {
            double ppi = Ppi();
            if (!double.IsFinite(ppi) || ppi is < 1 or > 100000) throw new InvalidDataException("Enter a valid resolution.");
            int Pixels((TextBox Value, ComboBox Unit) field, int original)
            {
                double pixels = double.Parse(field.Value.Text) * Scale(field.Unit.SelectedIndex, original);
                if (!double.IsFinite(pixels) || pixels < .5 || pixels >= 16384.5)
                    throw new InvalidDataException("Each dimension must be between 1 and 16,384 pixels.");
                return (int)Math.Round(pixels, MidpointRounding.AwayFromZero);
            }
            int w = Pixels(width, current.Width), h = Pixels(height, current.Height);
            Format.Dimensions(w, h);
            return new(w, h, ppi, anchor.X, anchor.Y);
        }
        void Refresh()
        {
            try { var size = Values(); information.Text = $"New size   {size.Width:N0} × {size.Height:N0} px"; }
            catch (Exception e) when (e is FormatException or InvalidDataException) { information.Text = e.Message; }
        }
        foreach (var (value, unit) in new[] { width, height, resolution })
        {
            if (value is not null) value.TextChanged += (_, _) => Refresh();
            if (unit is not null) unit.SelectionChanged += (_, _) => Refresh();
        }
        SizeOptions? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 74, Margin = new Thickness(3), Padding = new Thickness(5) };
        ok.Click += (_, _) =>
        {
            try { result = Values(); dialog.DialogResult = true; }
            catch (Exception e) when (e is FormatException or InvalidDataException) { information.Text = e.Message; }
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 74, Margin = new Thickness(3), Padding = new Thickness(5) });
        panel.Children.Add(buttons);
        Refresh();
        return dialog.ShowDialog() == true ? result : null;
    }
}
