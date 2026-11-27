using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chop.Core;
using Microsoft.Win32;

namespace PhotoChop;

public partial class MainWindow
{
    private static Chop.Core.GradientStop[] DefaultRamp(string first, string last) => [new(0, first), new(1, last)];

    private Chop.Core.GradientStop[]? EditGradientStops(Window owner, Chop.Core.GradientStop[] initial)
    {
        var dialog = new Window { Owner = owner, Title = "Gradient Stops", Width = 460, Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var shell = new DockPanel { Margin = new Thickness(14) };
        var list = new StackPanel();
        var swatch = new Border { Height = 20, Margin = new Thickness(0, 0, 0, 8) };
        var actions = new WrapPanel();
        var draft = initial.ToList();
        Chop.Core.GradientStop[]? output = null;
        void Draw()
        {
            list.Children.Clear();
            var brush = new LinearGradientBrush { StartPoint = new(0, .5), EndPoint = new(1, .5) };
            foreach (var stop in draft)
                brush.GradientStops.Add(new System.Windows.Media.GradientStop((Color)ColorConverter.ConvertFromString(stop.Color), stop.Position));
            swatch.Background = brush;
            for (int i = 0; i < draft.Count; i++)
            {
                int index = i;
                var stop = draft[i];
                var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                bool endpoint = i == 0 || i == draft.Count - 1;
                var percent = new TextBlock { Text = $"{stop.Position:P1}", Width = 53, VerticalAlignment = VerticalAlignment.Center };
                if (!endpoint)
                {
                    double min = Math.BitIncrement(draft[i - 1].Position);
                    double max = Math.BitDecrement(draft[i + 1].Position);
                    var slider = new Slider { Minimum = min, Maximum = max, Value = stop.Position, Width = 105 };
                    slider.ValueChanged += (_, _) =>
                    {
                        draft[index] = draft[index] with { Position = slider.Value };
                        percent.Text = $"{slider.Value:P1}";
                        brush.GradientStops[index].Offset = slider.Value;
                        if (index > 1 && list.Children[index - 1] is WrapPanel left)
                            left.Children.OfType<Slider>().Single().Maximum = Math.BitDecrement(slider.Value);
                        if (index + 2 < draft.Count && list.Children[index + 1] is WrapPanel right)
                            right.Children.OfType<Slider>().Single().Minimum = Math.BitIncrement(slider.Value);
                    };
                    row.Children.Add(slider);
                }
                var color = StyleFields.Swatch(dialog, "Stop Color", stop.Color, ink =>
                { draft[index] = draft[index] with { Color = ink }; Draw(); });
                row.Children.Add(percent);
                row.Children.Add(color);
                if (!endpoint) row.Children.Add(Button("Remove", () => { draft.RemoveAt(index); Draw(); }));
                list.Children.Add(row);
            }
        }
        actions.Children.Add(Button("Add Stop", () =>
        {
            if (draft.Count >= 64) return;
            int index = Enumerable.Range(0, draft.Count - 1).MaxBy(i => draft[i + 1].Position - draft[i].Position);
            double middle = (draft[index].Position + draft[index + 1].Position) / 2;
            var color = GradientRamp.Sampler(draft.ToArray())(middle);
            string hex = $"#{ChannelSamples.Display((float)color.A):X2}{ChannelSamples.Display((float)color.R):X2}{ChannelSamples.Display((float)color.G):X2}{ChannelSamples.Display((float)color.B):X2}";
            draft.Insert(index + 1, new(middle, hex));
            Draw();
        }));
        actions.Children.Add(Button("Apply", () => { output = draft.ToArray(); dialog.DialogResult = true; }));
        actions.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(3) });
        DockPanel.SetDock(swatch, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(actions, System.Windows.Controls.Dock.Bottom);
        shell.Children.Add(swatch);
        shell.Children.Add(actions);
        shell.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        LiveComboNavigation.Enable(dialog);
        dialog.Content = shell;
        Draw();
        return dialog.ShowDialog() == true ? output : null;
    }

    private Effect? EditAdvancedStyle(Effect effect)
    {
        bool glow = effect.Kind == EffectKind.Glow, shadow = effect.Kind == EffectKind.InnerShadow, bevel = effect.Kind == EffectKind.BevelEmboss;
        bool gradient = effect.Kind == EffectKind.GradientOverlay, pattern = effect.Kind == EffectKind.PatternOverlay;
        var workArea = SystemParameters.WorkArea;
        var dialog = new Window { Owner = effectDialog ?? EditorOwner, Title = EffectName(effect.Kind),
            Width = Math.Min(workArea.Width, 760 * preferences.UiScale), MaxHeight = workArea.Height * .85,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var shell = new DockPanel { Margin = new Thickness(12) };
        var footer = new StackPanel();
        DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
        shell.Children.Add(footer);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var primary = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        var details = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(details, 1);
        columns.Children.Add(primary);
        columns.Children.Add(details);
        shell.Children.Add(new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = shell;
        var panel = primary;
        var shared = new CheckBox { Content = "Use global light", IsChecked = effect.UseGlobalLight };
        void Field(string label, Control item)
        {
            panel.Children.Add(new Label { Content = label, Target = item });
            panel.Children.Add(item);
        }
        TextBox NumberField(string label, double value, double min, double max)
        {
            var box = new TextBox { Text = value.ToString("0.###") };
            NumericWheel.Enable(box, min, max);
            if (label.StartsWith("Angle", StringComparison.Ordinal))
            {
                panel.Children.Add(new Label { Content = label, Target = box });
                panel.Children.Add(AngleRow(box, signed: true, shared: bevel || shadow ? shared : null));
            }
            else Field(label, box);
            return box;
        }
        var blend = new ComboBox { ItemsSource = Enum.GetValues<Blend>(), SelectedItem = effect.Mode };
        if (!bevel) Field("Blend mode", blend);
        TextBox OpacityField(string name, double value)
        {
            var control = StyleFields.Range(value * 100, 0, 100);
            panel.Children.Add(new Label { Content = name, Target = control.Input });
            panel.Children.Add(control.Row);
            return control.Input;
        }
        var opacity = bevel ? null : OpacityField("Opacity (%)", effect.Opacity);
        var contour = StyleFields.Contour(effect.Contour);
        if (bevel || shadow || glow) Field("Contour", contour);
        var placement = new ComboBox { ItemsSource = Enum.GetValues<StrokeAlignment>(), SelectedItem = effect.Alignment };
        if (glow || bevel) Field("Position", placement);
        string[] techniques = ["Smooth", "Chisel Hard", "Chisel Soft"];
        ComboBox? technique = null;
        TextBox? soften = null, reliefDepth = null, highlightContrast = null, shadowContrast = null;
        if (bevel)
        {
            technique = new ComboBox { ItemsSource = techniques, SelectedIndex = (int)(effect.Lighting?.Technique ?? BevelTechnique.Smooth) };
            Field("Technique", technique);
            var depthControl = StyleFields.Range((effect.Lighting?.Depth ?? 1) * 100, 0, 1000);
            panel.Children.Add(new Label { Content = "Bevel depth (%)", Target = depthControl.Input });
            panel.Children.Add(depthControl.Row);
            reliefDepth = depthControl.Input;
            soften = NumberField("Soften (px)", effect.Lighting?.Soften ?? 0, 0, 16);
        }
        var size = glow || shadow || bevel ? NumberField("Size (px)", effect.Size, bevel ? 1 : .1, 100) : null;
        var angle = !glow && !pattern ? NumberField("Angle (degrees)", effect.A, -360, 360) : null;
        shared.Checked += (_, _) =>
        {
            if (angle is not null) angle.Text = (active?.Engine.Current?.GlobalLightAngle ?? 225).ToString("0.###");
        };
        var distance = shadow ? NumberField("Distance (px)", effect.B, 0, 512) :
            bevel ? NumberField("Light altitude (degrees)", effect.B, 0, 90) : null;
        var spread = glow ? NumberField("Spread (%)", effect.Spread, 0, 100) : null;
        var range = glow ? NumberField("Range (%)", effect.GlowRange, 1, 100) : null;
        var glowTechnique = new ComboBox { ItemsSource = new[] { "Softer", "Precise" }, SelectedIndex = effect.PreciseGlow ? 1 : 0 };
        if (glow) Field("Technique", glowTechnique);
        Action refreshPreview = () => { };
        var lighting = effect.Lighting ?? new();
        panel = details;
        TextBox ContrastField(string name, bool shadowSide)
        {
            var field = StyleFields.Range(lighting.ContrastFor(shadowSide) * 100, -100, 100);
            panel.Children.Add(new Label { Content = name, Target = field.Input });
            panel.Children.Add(field.Row);
            return field.Input;
        }
        ComboBox? highlightMode = null, shadowMode = null;
        TextBox? highlightOpacity = null, shadowOpacity = null;
        string highlight = lighting.Highlight, shade = lighting.Shadow;
        if (bevel)
        {
            ComboBox ModeField(string title, Blend current)
            {
                var control = new ComboBox { ItemsSource = Enum.GetValues<Blend>(), SelectedItem = current };
                Field(title, control);
                return control;
            }
            Button LightColor(string name, string current, Action<string> set) => StyleFields.Swatch(dialog, name, current, picked =>
            { set(picked); refreshPreview(); });
            panel.Children.Add(new Separator());
            panel.Children.Add(new TextBlock { Text = "Highlight", FontWeight = FontWeights.SemiBold });
            highlightMode = ModeField("Highlight blend mode", lighting.HighlightMode);
            panel.Children.Add(LightColor("Highlight color…", highlight, color => highlight = color));
            highlightOpacity = OpacityField("Highlight opacity (%)", lighting.HighlightOpacity * effect.Opacity);
            highlightContrast = ContrastField("Highlight contrast (%)", false);
            panel.Children.Add(new Separator());
            panel.Children.Add(new TextBlock { Text = "Shadow", FontWeight = FontWeights.SemiBold });
            shadowMode = ModeField("Shadow blend mode", lighting.ShadowMode);
            panel.Children.Add(LightColor("Shadow color…", shade, color => shade = color));
            shadowOpacity = OpacityField("Shadow opacity (%)", lighting.ShadowOpacity * effect.Opacity);
            shadowContrast = ContrastField("Shadow contrast (%)", true);
        }
        string ink = effect.Ink;
        if (glow || shadow)
        {
            var color = StyleFields.Swatch(dialog, "Effect color", ink, picked => { ink = picked; refreshPreview(); });
            Field("Color", color);
        }
        var stops = effect.Stops ?? DefaultRamp("#FF000000", "#FFFFFFFF");
        if (gradient) panel.Children.Add(Button("Edit Gradient Stops…", () => { stops = EditGradientStops(dialog, stops) ?? stops; refreshPreview(); }));
        LayerTexture? texture = effect.Texture;
        TextBox? scale = null, depth = null;
        ComboBox? textureType = null;
        if (pattern || bevel)
        {
            if (bevel) panel = primary;
            string[] choices = pattern ? ["Checker", "Grain", "Weave", "Image"] : ["None", "Checker", "Grain", "Weave", "Image"];
            textureType = new ComboBox { ItemsSource = choices,
                SelectedItem = texture?.Png is not null ? "Image" : texture?.Preset.ToString() ?? (pattern ? "Checker" : "None") };
            Field("Texture", textureType);
            var sample = new System.Windows.Controls.Image { Width = 56, Height = 44, Stretch = Stretch.Uniform };
            void PreviewTile() => sample.Source = texture?.Png is { } bytes ? EditorCanvas.Bitmap(renderer.DecodePng(bytes)) : null;
            panel.Children.Add(Button("Browse Image…", () =>
            {
                var picker = new OpenFileDialog { Filter = "Bitmap images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp" };
                if (picker.ShowDialog(dialog) != true) return;
                try
                {
                    var pixels = renderer.DecodeImage(File.ReadAllBytes(picker.FileName));
                    if (pixels.Width > 1024 || pixels.Height > 1024) throw new InvalidDataException("Use a texture no larger than 1024 × 1024.");
                    var png = renderer.EncodePng(pixels);
                    if (png.Length > 2_000_000) throw new InvalidDataException("The embedded texture exceeds 2 MB.");
                    texture = new(Png: png);
                    textureType.SelectedItem = "Image";
                    PreviewTile();
                    refreshPreview();
                }
                catch (Exception error) { MessageBox.Show(dialog, error.Message, "Texture"); }
            }));
            panel.Children.Add(sample);
            PreviewTile();
            scale = NumberField("Texture scale (%)", (texture?.Scale ?? 1) * 100, 10, 1000);
            if (bevel) depth = NumberField("Texture depth (%)", (texture?.Depth ?? .5) * 100, 0, 100);
        }
        Effect? result = null;
        var live = new CheckBox { Content = "Live preview", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };
        footer.Children.Add(live);
        footer.Children.Add(errorText);
        Effect ReadEffect()
        {
            double Read(TextBox? box, double fallback) => box is null ? fallback : Number(box.Text);
            string? selection = textureType?.SelectedItem as string;
            LayerTexture? chosen = selection switch
            {
                null or "None" => null,
                "Image" => new(Png: texture?.Png ?? throw new InvalidDataException("Choose a texture image.")),
                _ => new(Enum.Parse<TexturePreset>(selection))
            };
            if (chosen is not null) chosen = chosen with { Scale = Read(scale, 100) / 100, Depth = Read(depth, 50) / 100 };
            var edited = effect with { Ink = ink, Stops = gradient ? stops : null, Texture = chosen,
                Mode = (Blend)blend.SelectedItem, Opacity = Read(opacity, 100) / 100,
                Size = Read(size, effect.Size), A = Read(angle, effect.A), B = Read(distance, effect.B),
                Spread = Read(spread, effect.Spread), GlowRange = Read(range, effect.GlowRange), PreciseGlow = glowTechnique.SelectedIndex == 1, Alignment = (StrokeAlignment)placement.SelectedItem,
                UseGlobalLight = Chop.Core.Effect.UsesLight(effect.Kind) && shared.IsChecked == true,
                Contour = (StyleContour)((ComboBoxItem)contour.SelectedItem).Tag };
            if (technique is not null)
                edited = edited with { Lighting = lighting with
                    { Technique = (BevelTechnique)technique.SelectedIndex, Soften = Read(soften, 0), Depth = Read(reliefDepth, 100) / 100, Contrast = null, HighlightContrast = Read(highlightContrast, 100) / 100, ShadowContrast = Read(shadowContrast, 100) / 100,
                        Highlight = highlight, Shadow = shade, HighlightMode = (Blend)highlightMode!.SelectedItem,
                        ShadowMode = (Blend)shadowMode!.SelectedItem, HighlightOpacity = Read(highlightOpacity, 100) / 100,
                        ShadowOpacity = Read(shadowOpacity, 100) / 100 } };
            return edited;
        }
        var engine = active?.Engine;
        var layer = ActiveLayer();
        Command Make(Effect edited) => Command.Of(layer!.Effects.Any(existing => existing.Id == effect.Id)
            ? "effect.update" : "effect.add", edited, layer.Id);
        refreshPreview = () =>
        {
            preview = null;
            try
            {
                errorText.Text = "";
                if (live.IsChecked == true && engine is not null && layer is not null)
                    preview = engine.Preview(Make(ReadEffect()));
            }
            catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException)
            { errorText.Text = error.Message; }
            UpdateCanvas();
        };
        columns.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => refreshPreview()));
        columns.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => refreshPreview()));
        shared.Click += (_, _) => refreshPreview();
        live.Click += (_, _) => refreshPreview();
        dialog.Loaded += (_, _) => refreshPreview();
        dialog.Closed += (_, _) => { preview = null; UpdateCanvas(); };
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var apply = Button("Apply", () =>
        {
            try
            {
                var edited = ReadEffect();
                if (engine is not null && layer is not null) engine.Preview(Make(edited));
                result = edited;
                dialog.DialogResult = true;
            }
            catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException)
            { errorText.Text = error.Message; }
        });
        apply.IsDefault = true;
        actions.Children.Add(apply);
        actions.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(3) });
        footer.Children.Add(actions);
        return dialog.ShowDialog() == true ? result : null;
    }
}
