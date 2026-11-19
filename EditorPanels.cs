using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;
using Microsoft.Win32;

namespace PhotoChop;

public partial class MainWindow
{
    private sealed record LayerRow(Layer Layer, string? Parent, int Depth)
    {
        public override string ToString() =>
            new string(' ', Depth * 4) + (Layer.Visible ? "● " : "○ ") + (Layer.Locked ? "▣ " : "") +
            (Layer.Kind == LayerKind.Group ? "▾ " : "") + Layer.Name;
    }
    private sealed record LogRow(LogEntry Entry)
    {
        public override string ToString()
        {
            var command = Entry.Command;
            string args = command.Args.ValueKind == JsonValueKind.Object
                ? string.Join("  ", command.Args.EnumerateObject().Select(property =>
                {
                    string value = property.Value.ValueKind switch
                    {
                        JsonValueKind.Array => $"[{property.Value.GetArrayLength()} entries]",
                        JsonValueKind.String when property.Name is "data" or "png" => "[embedded resource]",
                        _ => property.Value.GetRawText()
                    };
                    return $"{property.Name}={value}";
                }))
                : command.Args.GetRawText();
            if (args.Length > 400) args = args[..400] + "…";
            return $"{Entry.Timestamp:HH:mm:ss}  {(Entry.Success ? "✓" : "✗")}  {command.Name}\n" +
                $"Document {Entry.DocumentId ?? "—"}   Target {command.Target ?? "—"}\n{args}\n{Entry.Error}";
        }
    }

    private Point layerDragStart;
    private LayerRow? layerDrag;

    private void BuildPanels()
    {
        var layerButtons = new WrapPanel { Margin = new Thickness(5) };
        layerButtons.Children.Add(Button("+ Raster", () => CreateLayer(LayerKind.Raster)));
        layerButtons.Children.Add(Button("Duplicate", Duplicate));
        layerButtons.Children.Add(Button("Rename", Rename));
        layerButtons.Children.Add(Button("Delete", DeleteLayer));
        DockPanel.SetDock(layerButtons, System.Windows.Controls.Dock.Top);
        layersPanel.Children.Add(layerButtons);
        layersPanel.Children.Add(layers);
        layers.SelectionChanged += (_, _) =>
        {
            if (syncing || active is null) return;
            active.ActiveId = (layers.SelectedItem as LayerRow)?.Layer.Id;
            RefreshProperties();
            Say();
        };
        layers.MouseDoubleClick += (_, _) => Rename();
        layers.PreviewMouseLeftButtonDown += (_, e) =>
        {
            layerDragStart = e.GetPosition(layers);
            layerDrag = RowAt(e.OriginalSource);
        };
        layers.PreviewMouseMove += (_, e) =>
        {
            if (layerDrag is not { } row || e.LeftButton != MouseButtonState.Pressed ||
                (e.GetPosition(layers) - layerDragStart).Length < 6) return;
            layerDrag = null;
            DragDrop.DoDragDrop(layers, new DataObject(typeof(LayerRow), row), DragDropEffects.Move);
        };
        layers.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(LayerRow)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        layers.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(LayerRow)) is not LayerRow source) return;
            var target = RowAt(e.OriginalSource);
            if (target?.Layer.Id == source.Layer.Id) return;
            if (target?.Layer.Kind == LayerKind.Group && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                Run("layer.reorder", new ReorderArgs(target.Layer.Id, null), source.Layer.Id, source.Layer.Id);
            else
            {
                // The panel displays topmost first, the model stores bottommost first.
                // Insert after the target in model order to display above it.
                var all = layers.Items.Cast<LayerRow>().Where(item => item.Parent == target?.Parent).ToArray();
                string? before = target is null ? all.LastOrDefault()?.Layer.Id
                    : all.TakeWhile(item => item.Layer.Id != target.Layer.Id).LastOrDefault()?.Layer.Id;
                Run("layer.reorder", new ReorderArgs(target?.Parent, before), source.Layer.Id, source.Layer.Id);
            }
            e.Handled = true;
        };

        history.Margin = new Thickness(4);
        history.SelectionChanged += (_, _) =>
        {
            if (!syncing && history.SelectedIndex >= 0 && active is { } session)
                Run("history.go", session.Engine.HistoryBase + history.SelectedIndex);
        };

        var logActions = new WrapPanel { Margin = new Thickness(5) };
        logActions.Children.Add(Button("Copy selected", () => ExportLog(true, true)));
        logActions.Children.Add(Button("Export script", () => ExportLog(false, false)));
        logActions.Children.Add(Button("Clear display", () =>
        {
            if (active is null) return;
            active.LogStart = active.Log.Count;
            RefreshLog();
        }));
        var recording = new CheckBox { Content = "Record", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5) };
        recording.Click += (_, _) => { if (active is not null) active.Recording = recording.IsChecked == true; };
        recording.Tag = "Recording";
        logActions.Children.Add(recording);
        DockPanel.SetDock(logActions, System.Windows.Controls.Dock.Top);
        logPanel.Children.Add(logActions);
        commandLog.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        logPanel.Children.Add(commandLog);
        panelContent.Add("Tools", new ScrollViewer { Content = toolsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panelContent.Add("Layers", layersPanel);
        panelContent.Add("History", history);
        panelContent.Add("Properties", new ScrollViewer { Content = properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panelContent.Add("Command Log", logPanel);
    }

    private LayerRow? RowAt(object original) => original is DependencyObject element &&
        ItemsControl.ContainerFromElement(layers, element) is ListBoxItem item ? item.Content as LayerRow : null;

    private void RefreshLayers()
    {
        if (active?.Engine.Current is not { } doc) { layers.ItemsSource = null; return; }
        var selection = layers.SelectedItems.OfType<LayerRow>().Select(row => row.Layer.Id).ToHashSet();
        var previous = active.ActiveId;
        var rows = Rows(doc.Layers, null, 0).ToArray();
        layers.ItemsSource = rows;
        var selected = rows.FirstOrDefault(row => row.Layer.Id == active.ActiveId) ?? rows.FirstOrDefault();
        active.ActiveId = selected?.Layer.Id;
        if (selected is null) return;
        layers.SelectedItem = selected;
        if (previous is not null && selection.Contains(previous))
            foreach (var row in rows.Where(row => row != selected && selection.Contains(row.Layer.Id))) layers.SelectedItems.Add(row);
    }

    private static IEnumerable<LayerRow> Rows(IEnumerable<Layer> items, string? parent, int depth)
    {
        foreach (var layer in items.Reverse())
        {
            yield return new(layer, parent, depth);
            foreach (var child in Rows(layer.Children, layer.Id, depth + 1)) yield return child;
        }
    }

    private Layer? ActiveLayer() => active?.Engine.Current?.AllLayers().FirstOrDefault(layer => layer.Id == active.ActiveId);

    private void CreateLayer(LayerKind kind, PointD? point = null, int width = 100, int height = 100)
    {
        var origin = point ?? new PointD(0, 0);
        string id = Format.Id();
        string text = "Text", font = "Liberation Sans", color = foreground;
        double size = 48;
        if (kind == LayerKind.Text)
        {
            var input = Form("Create text layer", ("Text", text), ("Font family", font), ("Size (pixels)", size.ToString()),
                ("Color #AARRGGBB", color));
            if (input is null) return;
            if (!Guard(() => size = Number(input[2]))) return;
            (text, font, color) = (input[0], input[1], input[3]);
        }
        Run("layer.create", new LayerArgs(id, kind.ToString(), kind, Transform: new(origin.X, origin.Y),
            Width: width, Height: height, Text: text, Font: font, Size: size, Color: color), selectId: id);
    }

    private void Duplicate()
    {
        if (active?.ActiveId is not { } source) return;
        string id = Format.Id();
        Run("layer.duplicate", id, source, id);
    }

    private void Rename()
    {
        if (ActiveLayer() is not { } layer) return;
        var values = Form("Rename layer", ("Name", layer.Name));
        if (values is not null) RunLayer("layer.rename", values[0]);
    }

    private void DeleteLayer() => RunLayer("layer.delete", 0);

    private string[] SelectedLayers() => layers.SelectedItems.OfType<LayerRow>().Select(row => row.Layer.Id).ToArray();

    private void Group()
    {
        var ids = SelectedLayers();
        if (ids.Length == 0) return;
        var values = Form("Group layers", ("Name", "Group"));
        if (values is null) return;
        string id = Format.Id();
        Run("layer.group", new GroupArgs(id, values[0], ids), selectId: id);
    }

    private void MoveToGroup()
    {
        if (ActiveLayer() is not { } layer || active?.Engine.Current is not { } doc) return;
        var dialog = new Window { Owner = this, Title = "Move layer", Width = 360, Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new DockPanel { Margin = new Thickness(12) };
        var options = new ListBox { SelectedIndex = 0, DisplayMemberPath = "Name" };
        options.Items.Add(new Layer("", "Canvas root", LayerKind.Group));
        foreach (var group in doc.AllLayers().Where(item => item.Kind == LayerKind.Group && item.Id != layer.Id)) options.Items.Add(group);
        options.SelectedIndex = 0;
        var ok = Button("Move", () => dialog.DialogResult = true);
        ok.IsDefault = true;
        DockPanel.SetDock(ok, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(ok);
        panel.Children.Add(options);
        dialog.Content = panel;
        if (dialog.ShowDialog() == true && options.SelectedItem is Layer selected)
            RunLayer("layer.reorder", new ReorderArgs(selected.Id == "" ? null : selected.Id, null));
    }

    private bool Confirm(string action) => MessageBox.Show(this, action, "CHOP", MessageBoxButton.OKCancel,
        MessageBoxImage.Warning) == MessageBoxResult.OK;

    private void Merge()
    {
        var ids = SelectedLayers();
        if (ids.Length < 2) { Say("Select at least two adjacent sibling layers."); return; }
        if (!Confirm("Merge selected layers into one raster layer?")) return;
        string id = Format.Id();
        Run("layer.merge", new MergeArgs(id, "Merged", ids), selectId: id);
    }

    private void Flatten()
    {
        if (active is null || !Confirm("Flatten all layers into a single raster layer?")) return;
        string id = Format.Id();
        Run("document.flatten", id, selectId: id);
    }

    private void RefreshProperties()
    {
        properties.Children.Clear();
        if (ActiveLayer() is not { } layer)
        {
            Caption("Select a layer to edit its properties.");
            return;
        }

        Caption(layer.Name);
        var toggles = new WrapPanel();
        var visible = new CheckBox { Content = "Visible", IsChecked = layer.Visible, Margin = new Thickness(2, 4, 10, 4) };
        var locked = new CheckBox { Content = "Locked", IsChecked = layer.Locked, Margin = new Thickness(2, 4, 10, 4) };
        visible.Click += (_, _) => RunLayer("layer.visibility", visible.IsChecked == true);
        locked.Click += (_, _) => RunLayer("layer.lock", locked.IsChecked == true);
        toggles.Children.Add(visible);
        toggles.Children.Add(locked);
        properties.Children.Add(toggles);

        var modes = new ComboBox { ItemsSource = Enum.GetValues<Blend>(), SelectedItem = layer.Blend, Margin = new Thickness(2) };
        modes.SelectionChanged += (_, _) => { if (modes.SelectedItem is Blend blend) RunLayer("layer.blend", blend); };
        Caption("Blend mode");
        properties.Children.Add(modes);

        Caption($"Opacity  {layer.Opacity:P0}");
        var opacity = new Slider { Minimum = 0, Maximum = 1, Value = layer.Opacity, TickFrequency = .01, IsSnapToTickEnabled = true };
        opacity.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => RunLayer("layer.opacity", opacity.Value)));
        properties.Children.Add(opacity);
        properties.Children.Add(Button("Set opacity…", () =>
        {
            var values = Form("Layer opacity", ("Opacity (0–100)", (layer.Opacity * 100).ToString()));
            if (values is not null) Guard(() => RunLayer("layer.opacity", Number(values[0]) / 100));
        }));
        properties.Children.Add(Button("Transform…", EditTransform));
        if (layer.Kind == LayerKind.Text) properties.Children.Add(Button("Edit text…", EditText));
        if (layer.Kind is LayerKind.Rectangle or LayerKind.Ellipse) properties.Children.Add(Button("Edit shape…", EditShape));

        Caption("Effects");
        var effectKinds = new ComboBox { ItemsSource = Enum.GetValues<EffectKind>(), SelectedIndex = 0, Margin = new Thickness(2) };
        var add = Button("Add effect", () => { if (effectKinds.SelectedItem is EffectKind kind) AddEffect(kind); });
        var row = new DockPanel();
        DockPanel.SetDock(add, System.Windows.Controls.Dock.Right);
        row.Children.Add(add);
        row.Children.Add(effectKinds);
        properties.Children.Add(row);

        for (int i = 0; i < layer.Effects.Length; i++)
        {
            var effect = layer.Effects[i];
            int index = i;
            var rowActions = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) };
            var enabled = new CheckBox { Content = EffectName(effect.Kind), IsChecked = effect.Enabled,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
            enabled.Click += (_, _) => RunLayer("effect.update", effect with { Enabled = enabled.IsChecked == true });
            rowActions.Children.Add(enabled);
            if (effect.Kind != EffectKind.Grayscale)
                rowActions.Children.Add(Button("Edit", () => { if (EffectForm(effect) is { } edited) RunLayer("effect.update", edited); }));
            if (i > 0) rowActions.Children.Add(Button("↑", () => RunLayer("effect.reorder", new EffectOrder(effect.Id, index - 1)), "Move effect up"));
            if (i + 1 < layer.Effects.Length) rowActions.Children.Add(Button("↓", () => RunLayer("effect.reorder", new EffectOrder(effect.Id, index + 1)), "Move effect down"));
            rowActions.Children.Add(Button("×", () => RunLayer("effect.remove", effect.Id), "Remove effect"));
            properties.Children.Add(rowActions);
        }
    }

    private void Caption(string text) => properties.Children.Add(new TextBlock
        { Text = text, Margin = new Thickness(2, 7, 2, 3), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

    private void EditTransform()
    {
        if (ActiveLayer() is not { } layer) return;
        var transform = layer.Transform;
        var v = Form("Layer transform", ("X (pixels)", transform.X.ToString()), ("Y (pixels)", transform.Y.ToString()),
            ("Scale X (1 = 100%)", transform.ScaleX.ToString()), ("Scale Y (1 = 100%)", transform.ScaleY.ToString()),
            ("Rotation (degrees)", transform.Rotation.ToString()));
        if (v is not null) Guard(() => RunLayer("layer.transform", new Chop.Core.Transform(Number(v[0]), Number(v[1]),
            Number(v[2]), Number(v[3]), Number(v[4]))));
    }

    private void EditText()
    {
        if (ActiveLayer() is not { } layer) return;
        var v = Form("Edit text", ("Text", layer.Text), ("Font family", layer.Font), ("Size (pixels)", layer.Size.ToString()),
            ("Color #AARRGGBB", layer.Color));
        if (v is not null) Guard(() => RunLayer("layer.text", new TextArgs(v[0], v[1], Number(v[2]), v[3])));
    }

    private void EditShape()
    {
        if (ActiveLayer() is not { } layer) return;
        var v = Form("Edit shape", ("Width", layer.Width.ToString()), ("Height", layer.Height.ToString()), ("Fill #AARRGGBB", layer.Color));
        if (v is not null) Guard(() => RunLayer("layer.shape", new ShapeArgs(Int(v[0]), Int(v[1]), v[2])));
    }

    private void AddEffect(EffectKind kind)
    {
        if (ActiveLayer() is null) return;
        var effect = new Effect(Format.Id(), kind,
            A: kind == EffectKind.GaussianBlur ? 2 : kind == EffectKind.Opacity ? 1 : 0,
            Spatial: kind == EffectKind.Transform ? new() : null);
        if (EffectForm(effect) is { } configured) RunLayer("effect.add", configured);
    }

    private Effect? EffectForm(Effect effect)
    {
        if (effect.Kind == EffectKind.Grayscale) return effect;
        if (effect.Kind == EffectKind.Transform)
        {
            var t = effect.Spatial!;
            var input = Form("Effect transform", ("X", t.X.ToString()), ("Y", t.Y.ToString()),
                ("Scale X", t.ScaleX.ToString()), ("Scale Y", t.ScaleY.ToString()), ("Rotation", t.Rotation.ToString()));
            Effect? spatial = null;
            if (input is not null) Guard(() => spatial = effect with { Spatial = new(Number(input[0]), Number(input[1]),
                Number(input[2]), Number(input[3]), Number(input[4])) });
            return spatial;
        }
        if (effect.Kind == EffectKind.BlendMode)
        {
            var input = Form("Effect blend mode", ("Normal, Multiply, Screen, Overlay, Add", effect.Mode.ToString()));
            Effect? blend = null;
            if (input is not null) Guard(() => blend = effect with { Mode = Enum.Parse<Blend>(input[0], ignoreCase: true) });
            return blend;
        }

        (string Label, string Initial)[] fields = effect.Kind switch
        {
            EffectKind.BrightnessContrast => [("Brightness (-1 to 1)", effect.A.ToString()), ("Contrast (-1 to 1)", effect.B.ToString())],
            EffectKind.HueSaturation => [("Hue (-180° to 180°)", effect.A.ToString()), ("Saturation adjustment (-1 to 1)", effect.B.ToString())],
            EffectKind.Opacity => [("Opacity multiplier (0 to 1)", effect.A.ToString())],
            _ => [("Blur sigma (0–100 px)", effect.A.ToString())]
        };
        var v = Form(EffectName(effect.Kind), fields);
        Effect? result = null;
        if (v is not null) Guard(() => result = effect with { A = Number(v[0]), B = v.Length == 2 ? Number(v[1]) : 0 });
        return result;
    }

    private static string EffectName(EffectKind kind) => kind switch
    {
        EffectKind.BrightnessContrast => "Brightness / Contrast",
        EffectKind.HueSaturation => "Hue / Saturation",
        EffectKind.GaussianBlur => "Gaussian Blur",
        EffectKind.Transform => "Transform",
        EffectKind.Opacity => "Opacity",
        EffectKind.BlendMode => "Blend mode",
        _ => "Grayscale"
    };

    private void RefreshLog()
    {
        commandLog.ItemsSource = active?.Log.Skip(active.LogStart).Select(entry => new LogRow(entry)).ToArray();
        var recording = ((WrapPanel)logPanel.Children[0]).Children.OfType<CheckBox>().Single();
        recording.IsEnabled = active is not null;
        recording.IsChecked = active?.Recording == true;
    }

    private void ExportLog(bool selection, bool clipboard)
    {
        if (active is null) return;
        var entries = selection ? commandLog.SelectedItems.OfType<LogRow>().Select(row => row.Entry) : active.Log;
        var commands = entries.Where(entry => entry.Success && entry.Command.Name is not ("document.save" or "document.export"))
            .OrderBy(entry => active.Log.IndexOf(entry)).Select(entry => entry.Command).ToArray();
        if (commands.Length == 0) { Say("There are no editing commands to copy or export."); return; }
        Guard(() =>
        {
            if (clipboard) Clipboard.SetText(JsonSerializer.Serialize(commands, Format.Json));
            else
            {
                var picker = new SaveFileDialog { Filter = "JSON script (*.json)|*.json", DefaultExt = ".json", AddExtension = true };
                if (picker.ShowDialog(this) != true) return;
                using var file = File.Create(picker.FileName);
                JsonSerializer.Serialize(file, commands, Format.Json);
            }
        });
    }
}
