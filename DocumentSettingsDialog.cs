using System.Windows;
using System.Windows.Controls;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private void EditDocumentSettings()
    {
        if (active?.Engine.Current is not { } document) return;
        var dialog = new Window { Owner = EditorOwner, Title = "Document Settings", Width = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height };
        var panel = new StackPanel { Margin = new Thickness(16) };
        var modes = new ComboBox { ItemsSource = new[] { "RGB", "Grayscale", "CMYK" }, SelectedIndex = (int)document.Color.Mode };
        var depths = new ComboBox { ItemsSource = new[] { "8 Bits/Channel", "16 Bits/Channel", "32 Bits/Channel (Float)" },
            SelectedIndex = document.Color.Depth switch { ChannelDepth.Bit8 => 0, ChannelDepth.Bit16 => 1, _ => 2 } };
        var resolution = new TextBox { Text = document.PixelsPerInch.ToString(), Padding = new Thickness(4) };
        NumericWheel.Enable(resolution, 1, 100000);
        void Field(string name, Control field)
        {
            panel.Children.Add(new Label { Content = name, Target = field });
            panel.Children.Add(field);
        }
        Field("Color mode", modes);
        Field("Color depth", depths);
        Field("Resolution (pixels/inch)", resolution);
        var conversion = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) };
        panel.Children.Add(conversion);
        void Explain()
        {
            var depth = new[] { ChannelDepth.Bit8, ChannelDepth.Bit16, ChannelDepth.Bit32 }[depths.SelectedIndex];
            conversion.Text = (modes.SelectedIndex != (int)document.Color.Mode || depth < document.Color.Depth)
                ? "Converting the document can discard color or precision. You can undo this change." : "";
        }
        modes.SelectionChanged += (_, _) => Explain();
        depths.SelectionChanged += (_, _) => Explain();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DocumentSettingsArgs? result = null;
        var apply = Button("Apply", () =>
        {
            if (!double.TryParse(resolution.Text, out double dpi) || !double.IsFinite(dpi) || dpi is < 1 or > 100000)
            {
                MessageBox.Show(dialog, "Enter a resolution from 1 to 100,000 pixels per inch.");
                return;
            }
            var depth = new[] { ChannelDepth.Bit8, ChannelDepth.Bit16, ChannelDepth.Bit32 }[depths.SelectedIndex];
            result = new(new((ChannelColorMode)modes.SelectedIndex, depth), dpi);
            dialog.DialogResult = true;
        });
        apply.IsDefault = true;
        buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(2) });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        if (dialog.ShowDialog() == true && result is not null) Run("document.settings", result);
    }
}
