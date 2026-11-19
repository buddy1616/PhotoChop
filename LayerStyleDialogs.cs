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
        var dialog = new Window { Owner = effectDialog ?? EditorOwner, Title = EffectName(effect.Kind), Width = 420,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(14) };
        dialog.Content = panel;
        TextBox Numeric(string title, double initial, double min, double max)
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 6, 0, 3) });
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var box = new TextBox { Text = initial.ToString("0.###"), Width = 72, Padding = new Thickness(3) };
            var slider = new Slider { Minimum = min, Maximum = max, Value = initial, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(box, System.Windows.Controls.Dock.Right);
            row.Children.Add(box);
            row.Children.Add(slider);
            panel.Children.Add(row);
            bool syncing = false;
            slider.ValueChanged += (_, _) =>
            {
                if (syncing) return;
                syncing = true; box.Text = slider.Value.ToString("0.###"); syncing = false;
            };
            box.TextChanged += (_, _) =>
            {
                if (syncing || !double.TryParse(box.Text, out var number) || !double.IsFinite(number)) return;
                syncing = true; slider.Value = number; syncing = false;
            };
            return box;
        }
        var fields = new List<TextBox>();
        ComboBox? alignment = null;
        AngleDial? dial = null;
        if (shadow)
        {
            var angleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            dial = new AngleDial { Width = 52, Height = 52, Angle = effect.A };
            angleRow.Children.Add(dial);
            angleRow.Children.Add(new Label { Content = "Angle (degrees)", VerticalAlignment = VerticalAlignment.Center });
            var angle = new TextBox { Text = effect.A.ToString("0.###"), Width = 66, VerticalAlignment = VerticalAlignment.Center };
            angleRow.Children.Add(angle);
            panel.Children.Add(angleRow);
            fields.Add(angle);
            bool syncing = false;
            dial.Changed += value =>
            {
                if (syncing) return;
                syncing = true; angle.Text = value.ToString("0.###"); syncing = false;
            };
            angle.TextChanged += (_, _) =>
            {
                if (syncing || !double.TryParse(angle.Text, out var value) || !double.IsFinite(value)) return;
                syncing = true; dial.Angle = Math.Clamp(value, 0, 360); syncing = false;
            };
            fields.Add(Numeric("Distance (px)", effect.B, 0, 512));
            fields.Add(Numeric("Size (px)", effect.Size, 0, 100));
            fields.Add(Numeric("Spread (%)", effect.Spread, 0, 100));
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
            return shadow ? effect with { A = v[0], B = v[1], Size = v[2], Spread = v[3], Opacity = v[4] / 100, Ink = ink }
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
        var color = Button("Choose Color…", () =>
        {
            if (ColorPickerDialog.Show(dialog, "Effect Color", ink) is { } selected) { ink = selected; RefreshPreview(); }
        });
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
