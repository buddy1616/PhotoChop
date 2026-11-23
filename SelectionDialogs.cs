using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private void SelectAllPixels() => Run("selection.all", 0);
    private void InvertSelection() => Run("selection.invert", 0);

    private void FilterSelection(bool feather)
    {
        if (active?.Engine.Current?.Selection is null) return;
        string title = feather ? "Feather Selection" : "Smooth Selection";
        if (Form(title, ("Radius (pixels)", "3")) is { } values)
            Guard(() => Run(feather ? "selection.feather" : "selection.smooth", Int(values[0])));
    }

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

    private Window? colorRangeWindow;
    private Func<Session, Point, bool>? sampleColorRange;
    private Session? colorRangeSession;
    private bool samplingCanvas;
    private bool IsSamplingCanvas(Session session) => samplingCanvas && ReferenceEquals(colorRangeSession, session);

    private void SelectColorRange()
    {
        if (colorRangeWindow is { } existing) { existing.Activate(); return; }
        if (active is not { } session || session.Engine.Current is not { } document) return;
        Guard(() =>
        {
            var pixels = ReferenceEquals(document, session.Rendered) ? session.Composite! : renderer.Composite(document);
            ShowColorRange(session, pixels);
        });
    }

    private void ShowColorRange(Session session, Pixels pixels)
    {
        var dialog = new Window { Owner = EditorOwner, Title = "Color Range", Topmost = true, Width = 330, SizeToContent = SizeToContent.Height,
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
        var combine = new ComboBox { ItemsSource = new[] { "Add to selection", "Subtract from selection" },
            SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(combine);
        var invert = new CheckBox { Content = "Invert", Margin = new Thickness(0, 5, 0, 7) };
        root.Children.Add(invert);
        var sampleHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 5) };
        root.Children.Add(sampleHint);
        var matching = new Image { Height = 145, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 6) };
        root.Children.Add(matching);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        string sampled = foreground;
        ColorSelection Query() => new(choices[(string)list.SelectedItem] ?? sampled, fuzziness.Value, invert.IsChecked == true,
            combine.SelectedIndex == 1 ? Chop.Core.SelectionMode.Subtract : Chop.Core.SelectionMode.Add);
        var ok = Button("OK", () =>
        {
            var query = Query();
            dialog.Close();
            if (sessions.Contains(session))
            {
                SelectTab(session);
                Run("selection.colorRange", query);
            }
        });
        ok.IsDefault = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(Button("Cancel", dialog.Close));
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
        root.Children.Add(buttons);

        void Update()
        {
            amount.Text = $"Fuzziness  {fuzziness.Value:0}";
            var (area, mask) = PixelSelection.ColorRange(pixels, Query(), session.Engine.Current);
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
            sampleHint.Text = list.SelectedIndex == 0 ? "Click the document canvas to sample a color." : "White areas show the color range.";
            samplingCanvas = list.SelectedIndex == 0;
            session.View.Cursor = samplingCanvas ? ToolCursors.Eyedropper : ToolCursor(tool);
        }
        sampleColorRange = (target, point) =>
        {
            if (!ReferenceEquals(target, session) || list.SelectedIndex != 0) return false;
            Guard(() =>
            {
                var current = session.Engine.Current!;
                pixels = ReferenceEquals(current, session.Rendered) ? session.Composite! : renderer.Composite(current);
                int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
                if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height) return;
                int i = (y * pixels.Width + x) * 4;
                sampled = $"#FF{pixels.Rgba[i]:X2}{pixels.Rgba[i + 1]:X2}{pixels.Rgba[i + 2]:X2}";
                Update();
            });
            return true;
        };
        dialog.Closed += (_, _) =>
        {
            sampleColorRange = null;
            colorRangeWindow = null;
            colorRangeSession = null;
            samplingCanvas = false;
            session.View.Cursor = ToolCursor(tool);
        };
        list.SelectionChanged += (_, _) => Update();
        fuzziness.ValueChanged += (_, _) => Update();
        invert.Click += (_, _) => Update();
        combine.SelectionChanged += (_, _) => Update();
        colorRangeSession = session;
        Update();
        colorRangeWindow = dialog;
        dialog.Show();
    }
}
