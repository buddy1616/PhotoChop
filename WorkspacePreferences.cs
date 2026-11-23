using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Chop.Core;
using Chop.Rendering;
using SkiaSharp;

namespace PhotoChop;

public partial class MainWindow
{
    private bool blockRenderInput;

    private double? baseMenuFontSize;

    private void UpdateGpuPreference()
    {
        if (!preferences.ExperimentalGpu)
        {
            gpu?.Dispose();
            gpu = null;
        }
        foreach (var session in sessions) session.Rendered = null;
        GpuStatus.Visibility = preferences.ExperimentalGpu ? Visibility.Visible : Visibility.Collapsed;
        GpuStatus.Content = "CPU";
    }

    private void ApplyControlScale()
    {
        App.ControlScale = double.IsFinite(preferences.UiScale) ? Math.Clamp(preferences.UiScale, .75, 1.25) : 1;
        preferences.UiScale = App.ControlScale;
        Menus.FontSize = (baseMenuFontSize ??= Menus.FontSize) * App.ControlScale;
        App.ScaleControls(ToolOptions);
        App.ScaleControls(WorkspaceStatus);
        foreach (var control in panelContent.Values) App.ScaleControls(control);
    }

    private void EditPreferences()
    {
        var dialog = new Window { Owner = this, Title = "Preferences", Width = 500, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(14) };
        var pages = new TabControl { MinHeight = 210 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(pages);
        StackPanel Page(string name)
        {
            var content = new StackPanel { Margin = new Thickness(12) };
            pages.Items.Add(new TabItem { Header = name, Content = content });
            return content;
        }
        static CheckBox Check(Panel page, string name, bool value)
        {
            var field = new CheckBox { Content = name, IsChecked = value, Margin = new Thickness(0, 6, 0, 6) };
            page.Children.Add(field);
            return field;
        }
        var appearance = Page("Interface");
        var uiScale = StyleFields.Range(preferences.UiScale * 100, 75, 125);
        appearance.Children.Add(new Label { Content = "UI control scale (%)", Target = uiScale.Input });
        appearance.Children.Add(uiScale.Row);
        var performance = Page("Performance");
        var useGpu = Check(performance, "Use experimental GPU compositing", preferences.ExperimentalGpu);
        var rulers = Page("Units and Rulers");
        rulers.Children.Add(new Label { Content = "Ruler units" });
        var units = new ComboBox { ItemsSource = Enum.GetValues<RulerUnit>(), SelectedItem = preferences.RulerUnit };
        rulers.Children.Add(units);
        var showRulers = Check(rulers, "Show rulers", preferences.ShowRulers);
        var horizontal = Check(rulers, "Center horizontal ruler", preferences.CenterHorizontal);
        var vertical = Check(rulers, "Center vertical ruler", preferences.CenterVertical);
        var guides = Page("Guides and Grids");
        var showGuides = Check(guides, "Show guides", preferences.ShowGuides);
        string ink = preferences.GuideColor;
        guides.Children.Add(new Label { Content = "Guide color" });
        guides.Children.Add(StyleFields.Swatch(dialog, "Guide color", ink, value => ink = value));
        var pixelGrid = Check(guides, "Show pixel grid at high zoom", preferences.ShowPixelGrid);
        var size = new TextBox { Text = Math.Clamp(preferences.CheckerSize, 2, 64).ToString(), MaxWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Left };
        NumericWheel.Enable(size, 2, 64);
        guides.Children.Add(new Label { Content = "Transparency checker size", Target = size });
        guides.Children.Add(size);
        var apply = Button("OK", () =>
        {
            if (!int.TryParse(size.Text, out int tile) || tile is < 2 or > 64) return;
            bool gpuChanged = preferences.ExperimentalGpu != (useGpu.IsChecked == true);
            preferences.ExperimentalGpu = useGpu.IsChecked == true;
            preferences.RulerUnit = (RulerUnit)units.SelectedItem;
            preferences.ShowRulers = showRulers.IsChecked == true;
            preferences.CenterHorizontal = horizontal.IsChecked == true;
            preferences.CenterVertical = vertical.IsChecked == true;
            preferences.ShowGuides = showGuides.IsChecked == true;
            preferences.GuideColor = ink;
            preferences.ShowPixelGrid = pixelGrid.IsChecked == true;
            preferences.CheckerSize = tile;
            preferences.UiScale = uiScale.Slider.Value / 100;
            ApplyControlScale();
            Guard(SavePreferences);
            if (gpuChanged) UpdateGpuPreference();
            dialog.DialogResult = true;
        });
        apply.IsDefault = true;
        actions.Children.Add(apply);
        actions.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(3) });
        dialog.Content = root;
        if (dialog.ShowDialog() == true)
        {
            UpdateRulers();
            UpdateCanvas();
        }
    }

    private TextBlock info = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };

    private FrameworkElement BuildInfo()
    {
        info.Text = "Move across the canvas to inspect pixels.";
        return new ScrollViewer { Content = info, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void ShowPixelInfo(Session session, Point point)
    {
        if (session.Engine.Current is not { } doc) return;
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        string color = "—";
        if (session.Composite is { } image && x >= 0 && y >= 0 && x < image.Width && y < image.Height)
        {
            int i = (y * image.Width + x) * 4;
            color = $"R {image.Rgba[i]}   G {image.Rgba[i + 1]}   B {image.Rgba[i + 2]}   A {image.Rgba[i + 3]}\n#{image.Rgba[i]:X2}{image.Rgba[i + 1]:X2}{image.Rgba[i + 2]:X2}";
        }
        string native = "";
        if (doc.Color.Mode == ChannelColorMode.Cmyk &&
            doc.AllLayers().FirstOrDefault(layer => layer.Id == session.ActiveId) is { } selected &&
            renderer.ResolveRaster(selected) is { Pixels.Native: { Settings.Mode: ChannelColorMode.Cmyk } planes } raster &&
            SkiaRenderer.WorldMatrix(doc.Layers, selected.Id, SKMatrix.Identity) is { } world &&
            world.TryInvert(out var inverse))
        {
            var local = inverse.MapPoint((float)point.X, (float)point.Y);
            int sx = (int)Math.Floor(local.X) - raster.X, sy = (int)Math.Floor(local.Y) - raster.Y;
            if (sx >= 0 && sy >= 0 && sx < planes.Width && sy < planes.Height)
            {
                float Ink(int channel) => ChannelSamples.Read(planes.Planes[channel], planes.Settings.Depth, sy * planes.Width + sx);
                native = $"\nLayer ink   C {Ink(0):P1}   M {Ink(1):P1}   Y {Ink(2):P1}   K {Ink(3):P1}";
            }
        }
        string selection = doc.Selection is { } area ? $"\nSelection   {area.Width} × {area.Height} px" : "";
        info.Text = $"X {x}   Y {y} px\nDisplay   {color}{native}\nDocument   {doc.Width} × {doc.Height} px · {doc.Color.Mode} / {(int)doc.Color.Depth}-bit{selection}";
    }
}
