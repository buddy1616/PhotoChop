using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private Effect? EditStyleEffect(Effect effect)
    {
        bool shadow = effect.Kind == EffectKind.DropShadow;
        bool overlay = effect.Kind == EffectKind.ColorOverlay;
        var dialog = new Window { Owner = effectDialog ?? EditorOwner, Title = EffectName(effect.Kind), Width = 420,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(14) };
        dialog.Content = panel;
        TextBox Numeric(string title, double initial, double min, double max)
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 6, 0, 3) });
            var field = StyleFields.Range(initial, min, max);
            panel.Children.Add(field.Row);
            return field.Input;
        }
        var fields = new List<TextBox>();
        ComboBox? alignment = null, blend = null;
        static double Opposite(double angle) => (angle + 180) % 360;
        var shared = new CheckBox { Content = "Use global light", IsChecked = effect.UseGlobalLight };
        var contour = StyleFields.Contour(effect.Contour);
        if (shadow)
        {
            var angle = new TextBox { Text = Opposite(effect.A).ToString("0.###"), Width = 66, HorizontalAlignment = HorizontalAlignment.Left };
            NumericWheel.Enable(angle, 0, 360);
            panel.Children.Add(new Label { Content = "Light angle (degrees)", Target = angle });
            panel.Children.Add(AngleRow(angle, shared: shared));
            fields.Add(angle);
            shared.Checked += (_, _) => angle.Text = (active?.Engine.Current?.GlobalLightAngle ?? 225).ToString("0.###");
            panel.Children.Add(new Label { Content = "Contour", Target = contour });
            panel.Children.Add(contour);
            fields.Add(Numeric("Distance (px)", effect.B, 0, 512));
            fields.Add(Numeric("Size (px)", effect.Size, 0, 100));
            fields.Add(Numeric("Spread (%)", effect.Spread, 0, 100));
            fields.Add(Numeric("Opacity (%)", effect.Opacity * 100, 0, 100));
        }
        else if (overlay)
        {
            panel.Children.Add(new TextBlock { Text = "Blend mode", Margin = new Thickness(0, 6, 0, 3) });
            blend = new ComboBox { ItemsSource = Enum.GetValues<Blend>(), SelectedItem = effect.Mode };
            panel.Children.Add(blend);
            fields.Add(Numeric("Opacity (%)", effect.Opacity * 100, 0, 100));
        }
        else
        {
            fields.Add(Numeric("Thickness (px)", effect.A, 1, 512));
            panel.Children.Add(new TextBlock { Text = "Position", Margin = new Thickness(0, 6, 0, 3) });
            alignment = new ComboBox { ItemsSource = Enum.GetValues<StrokeAlignment>(), SelectedItem = effect.Alignment };
            panel.Children.Add(alignment);
        }
        string ink = effect.Ink;
        var live = new CheckBox { Content = "Live preview", IsChecked = true, Margin = new Thickness(3, 8, 3, 8) };
        var errorLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };
        Effect Read()
        {
            var v = fields.Select(field => Number(field.Text)).ToArray();
            if (v.Any(value => !double.IsFinite(value))) throw new InvalidDataException("Enter finite values.");
            if (shadow && v[0] is < 0 or > 360) throw new InvalidDataException("Enter a light angle between 0 and 360.");
            if (overlay) return effect with { Mode = (Blend)blend!.SelectedItem, Opacity = v[0] / 100, Ink = ink };
            return shadow ? effect with { A = Opposite(v[0]), B = v[1], Size = v[2], Spread = v[3], Opacity = v[4] / 100, Ink = ink, UseGlobalLight = shared.IsChecked == true, Contour = (StyleContour)((ComboBoxItem)contour.SelectedItem).Tag }
                : effect with { A = v[0], Alignment = (StrokeAlignment)alignment!.SelectedItem, Ink = ink };
        }
        var engine = active?.Engine;
        var layer = ActiveLayer();
        Command Make(Effect edited) => Command.Of(layer!.Effects.Any(e => e.Id == effect.Id) ? "effect.update" : "effect.add", edited, layer.Id);
        void RefreshPreview()
        {
            preview = null;
            try
            {
                if (live.IsChecked == true && engine is not null && layer is not null) preview = engine.Preview(Make(Read()));
                errorLabel.Text = "";
            }
            catch (Exception e) when (e is InvalidDataException or FormatException) { errorLabel.Text = e.Message; }
            UpdateCanvas();
        }
        var color = StyleFields.Swatch(dialog, "Effect color", ink, selected => { ink = selected; RefreshPreview(); });
        panel.Children.Add(new Label { Content = "Color", Target = color });
        panel.Children.Add(color);
        panel.Children.Add(live);
        panel.Children.Add(errorLabel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Effect? result = null;
        var apply = Button("Apply", () =>
        {
            try
            {
                var edited = Read();
                if (engine is not null && layer is not null) engine.Preview(Make(edited));
                result = edited;
                dialog.DialogResult = true;
            }
            catch (Exception e) when (e is InvalidDataException or FormatException) { errorLabel.Text = e.Message; }
        });
        apply.IsDefault = true;
        buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(3) });
        panel.Children.Add(buttons);
        foreach (var field in fields) field.TextChanged += (_, _) => RefreshPreview();
        if (alignment is not null) alignment.SelectionChanged += (_, _) => RefreshPreview();
        if (blend is not null) blend.SelectionChanged += (_, _) => RefreshPreview();
        shared.Click += (_, _) => RefreshPreview();
        contour.SelectionChanged += (_, _) => RefreshPreview();
        live.Click += (_, _) => RefreshPreview();
        dialog.Loaded += (_, _) => RefreshPreview();
        dialog.Closed += (_, _) => { preview = null; UpdateCanvas(); };
        return dialog.ShowDialog() == true ? result : null;
    }
}

internal sealed class AngleDial : FrameworkElement
{
    private double angle;
    public event Action<double>? Changed;
    public double Angle
    {
        get => angle;
        set { angle = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 3);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(43, 48, 57)), new Pen(Brushes.LightSlateGray, 1), center, radius, radius);
        double radians = angle * Math.PI / 180;
        var end = center + new Vector(Math.Cos(radians), Math.Sin(radians)) * (radius - 4);
        dc.DrawLine(new Pen(Brushes.LightSkyBlue, 2), center, end);
        dc.DrawEllipse(Brushes.White, null, end, 3, 3);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Pick(e.GetPosition(this));
    }

    private void Pick(Point p)
    {
        Angle = (Math.Atan2(p.Y - ActualHeight / 2, p.X - ActualWidth / 2) * 180 / Math.PI + 360) % 360;
        Changed?.Invoke(Angle);
    }
}
