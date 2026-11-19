using System.Windows;
using System.Windows.Controls;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private void FillPixels()
    {
        if (ActiveLayer()?.Kind == LayerKind.Raster && ColorPickerDialog.Show(EditorOwner, "Fill color", foreground) is { } ink)
            RunLayer("layer.fillPixels", ink);
    }

    private void StrokeSelection()
    {
        if (ActiveLayer()?.Kind != LayerKind.Raster || active?.Engine.Current?.Selection is null) return;
        var dialog = new Window { Title = "Stroke Selection", Owner = EditorOwner, Width = 320, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var thickness = new TextBox { Text = "1", Margin = new Thickness(0, 4, 0, 8) };
        var alignment = new ComboBox { ItemsSource = new[] { StrokeAlignment.Inside, StrokeAlignment.Outside, StrokeAlignment.Center },
            SelectedItem = StrokeAlignment.Center, Margin = new Thickness(0, 4, 0, 8) };
        string ink = foreground;
        panel.Children.Add(new Label { Content = "Thickness (pixels)", Target = thickness });
        panel.Children.Add(thickness);
        panel.Children.Add(new Label { Content = "Position", Target = alignment });
        panel.Children.Add(alignment);
        panel.Children.Add(Button("Choose Color…", () =>
        {
            if (ColorPickerDialog.Show(dialog, "Stroke color", ink) is { } color) ink = color;
        }));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        OutlineArgs? result = null;
        var ok = Button("Apply", () =>
        {
            if (!int.TryParse(thickness.Text, out int pixels) || pixels is < 1 or > 16384)
            {
                MessageBox.Show(dialog, "Enter a thickness from 1 to 16,384 pixels.");
                return;
            }
            result = new OutlineArgs(ink, pixels, (StrokeAlignment)alignment.SelectedItem);
            dialog.DialogResult = true;
        });
        ok.IsDefault = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(2) });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        if (dialog.ShowDialog() == true && result is not null) RunLayer("layer.strokeSelection", result);
    }

    private void ResizeDocument(bool canvasOnly)
    {
        if (active?.Engine.Current is not { } doc ||
            DocumentSizeDialog.Show(EditorOwner, doc, canvasOnly) is not { } result) return;
        bool changed = canvasOnly
            ? Run("document.resizeCanvas", new ResizeCanvasArgs(result.Width, result.Height, result.AnchorX, result.AnchorY))
            : Run("document.resizeImage", new ResizeImageArgs(result.Width, result.Height, result.PixelsPerInch));
        if (changed) Fit();
    }
}
