using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chop.Core;

namespace PhotoChop;

internal static class TextLayerDialog
{
    public static TextArgs? Show(Window owner, string title, TextArgs initial, Action<TextArgs?> previewChanged)
    {
        var dialog = new Window
        {
            Title = title, Owner = owner, Width = 480, Height = 420, MinWidth = 390, MinHeight = 315,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var shell = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel();
        var font = new ComboBox
        {
            IsEditable = true, Text = initial.Font, Margin = new Thickness(0, 2, 0, 6),
            ItemsSource = Fonts.SystemFontFamilies.Select(family => family.Source).Append("Liberation Sans").Distinct().Order().ToArray()
        };
        var size = new TextBox { Text = initial.Size.ToString(), Width = 64, Padding = new Thickness(4) };
        NumericWheel.Enable(size, 1);
        var text = new TextBox { Text = initial.Text, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(7), VerticalContentAlignment = VerticalAlignment.Top };
        string color = initial.Color;
        var live = new CheckBox { Content = "Live preview", IsChecked = true, Margin = new Thickness(0, 6, 12, 0),
            VerticalAlignment = VerticalAlignment.Center };
        void Preview() => previewChanged(live.IsChecked == true ? Read() : null);
        var colorButton = new Button { Content = "Text color…", Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(10, 0, 0, 0) };
        colorButton.Click += (_, _) =>
        {
            if (ColorPickerDialog.Show(dialog, "Text color", color) is { } picked)
            {
                color = picked;
                colorButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
                Preview();
            }
        };
        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
        options.Children.Add(new Label { Content = "Size (px)", Target = size });
        options.Children.Add(size);
        options.Children.Add(colorButton);
        header.Children.Add(new Label { Content = "Font", Target = font });
        header.Children.Add(font);
        header.Children.Add(options);
        DockPanel.SetDock(header, Dock.Top);
        shell.Children.Add(header);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom);
        var apply = new Button { Content = "Apply", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(3) };
        footer.Children.Add(live);
        footer.Children.Add(apply);
        footer.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(3) });
        shell.Children.Add(footer);
        shell.Children.Add(text);
        dialog.Content = shell;

        TextArgs? Read() => double.TryParse(size.Text, out var value) && double.IsFinite(value) && value is >= 1 and <= 512 &&
            !string.IsNullOrWhiteSpace(font.Text) ? new TextArgs(text.Text, font.Text.Trim(), value, color) : null;
        TextArgs? result = null;
        apply.Click += (_, _) =>
        {
            result = Read();
            if (result is null)
            {
                MessageBox.Show(dialog, "Enter a font family and a size from 1 to 512 pixels.", title);
                return;
            }
            dialog.DialogResult = true;
        };
        live.Click += (_, _) => Preview();
        text.TextChanged += (_, _) => Preview();
        size.TextChanged += (_, _) => Preview();
        font.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Preview()));
        font.SelectionChanged += (_, _) => Preview();
        dialog.Closed += (_, _) => previewChanged(null);
        dialog.Loaded += (_, _) =>
        {
            text.Focus();
            if (title == "Create text layer") text.SelectAll();
            else text.CaretIndex = text.Text.Length;
            Preview();
        };
        return dialog.ShowDialog() == true ? result : null;
    }
}
