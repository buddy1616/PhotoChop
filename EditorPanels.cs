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
    private sealed record LayerRow(Layer Layer, string? Parent, int Depth, bool Collapsed, bool EditingMask)
    {
        public Visibility HiddenEye => Layer.Visible ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GroupVisibility => Layer.Kind == LayerKind.Group ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MaskVisibility => Layer.Mask is null ? Visibility.Collapsed : Visibility.Visible;
        public string GroupGlyph => Collapsed ? "▸" : "▾";
        public string LockGlyph => Layer.Locked ? "\uE72E" : "\uE785";
        public string TransformTip => Layer.TransformsEnabled ? "Bypass unbaked transforms, adjustments, and filters" : "Show unbaked transforms, adjustments, and filters";
        public double TransformIconOpacity => Layer.TransformsEnabled ? 1 : .4;
        public double MaskIconOpacity => Layer.MaskEnabled ? 1 : .4;
        public Brush? MaskBorder => EditingMask ? Brushes.DeepSkyBlue : null;
        public Thickness Indent => new(Depth * 13, 0, 3, 0);
        public Thickness NameIndent => Layer.Kind == LayerKind.Group ? new(2, 0, 0, 0) : new(Depth * 13 + 16, 0, 0, 0);
    }
    private StackPanel? effectEditor;
    private Window? effectDialog;

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
        var rasterButton = LayerIconButton("+", "New Raster Layer", () => CreateLayer(LayerKind.Raster), glyph: false);
        var groupButton = LayerArtButton("New Group", () => CreateLayer(LayerKind.Group),
            "M 0,2 L 7,2 9,4 19,4 19,6 3,6 0,14 Z M 3.5,7 L 21,7 18,15 0,15 Z");
        var deleteButton = LayerIconButton("\uE74D", "Delete Layer or Group", DeleteLayer);
        layerButtons.Children.Add(rasterButton);
        layerButtons.Children.Add(groupButton);
        BindLayerDrop(rasterButton, "Copy here", Duplicate);
        BindLayerDrop(groupButton, "Copy here", Duplicate);
        BindLayerDrop(deleteButton, "Delete here", id =>
        {
            if (Confirm("Delete the dropped layer or group?")) Run("layer.delete", 0, id);
        }, copy: false);
        var maskButton = LayerMaskButton();
        maskButton.ContextMenu = new ContextMenu();
        maskButton.ContextMenuOpening += OpenMaskMenu;
        BindLayerDrop(maskButton, "Add mask here", id => Run("mask.add", 0, id, id), copy: false);
        layerButtons.Children.Add(maskButton);
        layerButtons.Children.Add(deleteButton);
        DockPanel.SetDock(properties, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(layerButtons, System.Windows.Controls.Dock.Bottom);
        layersPanel.Children.Add(properties);
        layersPanel.Children.Add(layerButtons);
        layers.ItemTemplate = (DataTemplate)FindResource("LayerRowTemplate");
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        layers.ItemContainerStyle = itemStyle;
        layersPanel.Children.Add(layers);
        layers.SelectionChanged += (_, e) =>
        {
            if (syncing || active is null) return;
            CancelTransform();
            active.ActiveId = (e.AddedItems.OfType<LayerRow>().LastOrDefault() ?? layers.SelectedItem as LayerRow)?.Layer.Id;
            if (active.MaskTarget != active.ActiveId) { active.MaskTarget = null; active.MaskOnly = null; UpdateCanvas(); }
            RefreshProperties();
            RefreshTools();
            Say();
        };
        layers.MouseDoubleClick += (_, e) =>
        {
            if (RowAt(e.OriginalSource) is null || IsButton(e.OriginalSource)) return;
            if (ActiveLayer() is { Kind: LayerKind.Fill } fill && ColorPickerDialog.Show(this, "Fill layer color", fill.Color) is { } color)
                Run("layer.fill", color, fill.Id);
            else if (ActiveLayer() is { Kind: LayerKind.Text } text) EditText(text);
            else if (ActiveLayer()?.Kind != LayerKind.Fill) Rename();
            e.Handled = true;
        };
        layers.PreviewMouseLeftButtonDown += (_, e) =>
        {
            layerDragStart = e.GetPosition(layers);
            layerDrag = IsButton(e.OriginalSource) ? null : RowAt(e.OriginalSource);
        };
        layers.PreviewMouseMove += (_, e) =>
        {
            if (layerDrag is not { } row || e.LeftButton != MouseButtonState.Pressed ||
                (e.GetPosition(layers) - layerDragStart).Length < Math.Max(5, SystemParameters.MinimumVerticalDragDistance)) return;
            layerDrag = null;
            DragLayer(row);
        };
        layers.DragOver += (_, e) =>
        {
            var hint = GetDrop(e);
            e.Effects = hint is null ? DragDropEffects.None : DragDropEffects.Move;
            if (layerDragAdorner is { } adorner)
            {
                adorner.Pointer = e.GetPosition(layers);
                adorner.Hint = hint;
                adorner.InvalidateVisual();
            }
            e.Handled = true;
        };
        layers.DragLeave += (_, _) =>
        {
            if (layerDragAdorner is { } adorner) { adorner.Hint = null; adorner.InvalidateVisual(); }
        };
        layers.Drop += (_, e) =>
        {
            if (active is null || GetDrop(e) is not { } hint) return;
            if (hint.IntoGroup && hint.Order.Parent is { } parent) active.Collapsed.Remove(parent);
            Run("layer.reorder", hint.Order, hint.Source.Layer.Id, hint.Source.Layer.Id);
            e.Handled = true;
        };

        BuildLayerMenu();
        history.Margin = new Thickness(4);
        history.SelectionChanged += (_, _) =>
        {
            if (!syncing && history.SelectedIndex >= 0 && active is { } session)
                Run("history.go", session.Engine.HistoryBase + history.SelectedIndex);
        };

        BuildHistoryMenu();

        var logActions = new WrapPanel { Margin = new Thickness(5) };
        logActions.Children.Add(Button("Copy selected", () => ExportLog(true, true)));
        logActions.Children.Add(Button("Export script", () => ExportLog(false, false)));
        logActions.Children.Add(Button("Clear display", () =>
        {
            if (active is null) return;
            active.LogStart = DisplayLog(active).Count();
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
        var toolDock = new DockPanel();
        DockPanel.SetDock(toolColors, System.Windows.Controls.Dock.Bottom);
        toolDock.Children.Add(toolColors);
        toolDock.Children.Add(new ScrollViewer { Content = toolsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ToolPantry.Content = toolDock;
        panelContent.Add("Navigation", BuildNavigation());
        panelContent.Add("Layers", layersPanel);
        panelContent.Add("History", history);
        panelContent.Add("Command Log", logPanel);
    }

    private static bool IsButton(object original)
    {
        for (var element = original as DependencyObject; element is Visual; element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase) return true;
        return false;
    }

    private void ToggleRowVisibility(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayerRow row })
            Run("layer.visibility", !row.Layer.Visible, row.Layer.Id);
    }

    private void ToggleRowLock(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LayerRow row })
            Run("layer.lock", !row.Layer.Locked, row.Layer.Id);
    }

    private void ToggleRowGroup(object sender, RoutedEventArgs e)
    {
        if (active is null || sender is not FrameworkElement { DataContext: LayerRow row }) return;
        if (!active.Collapsed.Add(row.Layer.Id)) active.Collapsed.Remove(row.Layer.Id);
        else if (active.ActiveId is { } selected && !Rows(active.Engine.Current!.Layers, null, 0).Any(item => item.Layer.Id == selected))
            active.ActiveId = row.Layer.Id;
        Refresh();
    }

    private void ToggleRowMask(object sender, RoutedEventArgs e)
    {
        if (active is null || sender is not FrameworkElement { DataContext: LayerRow row }) return;
        active.ActiveId = row.Layer.Id;
        bool isolated = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (isolated)
        {
            active.MaskOnly = active.MaskOnly == row.Layer.Id ? null : row.Layer.Id;
            active.MaskTarget = row.Layer.Id;
        }
        else
        {
            active.MaskOnly = null;
            active.MaskTarget = active.MaskTarget == row.Layer.Id ? null : row.Layer.Id;
        }
        Refresh();
    }

    private void AddOrSelectMask()
    {
        if (active is null || ActiveLayer() is not { } layer) return;
        if (layer.Mask is null && !Run("mask.add", 0, layer.Id)) return;
        active.MaskTarget = layer.Id;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            active.MaskOnly = active.MaskOnly == layer.Id ? null : layer.Id;
        Refresh();
    }

    private string? DestinationGroup()
    {
        if (ActiveLayer() is not { } layer || active?.Engine.Current is not { } doc) return null;
        return layer.Kind == LayerKind.Group ? layer.Id :
            doc.AllLayers().FirstOrDefault(group => group.Children.Any(child => child.Id == layer.Id))?.Id;
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

    private IEnumerable<LayerRow> Rows(IEnumerable<Layer> items, string? parent, int depth)
    {
        foreach (var layer in items.Reverse())
        {
            bool collapsed = active?.Collapsed.Contains(layer.Id) == true;
            yield return new(layer, parent, depth, collapsed, active?.MaskTarget == layer.Id);
            if (!collapsed)
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
        if (kind == LayerKind.Fill)
        {
            if (ColorPickerDialog.Show(this, "Fill layer color", foreground) is not { } fill) return;
            color = fill;
        }
        string? parent = DestinationGroup();
        if (active?.Engine.Current is not { } doc) return;
        if (parent is not null)
        {
            active.Collapsed.Remove(parent);
            if (point is not null && Chop.Rendering.SkiaRenderer.WorldMatrix(doc.Layers, parent, SkiaSharp.SKMatrix.Identity) is { } matrix &&
                matrix.TryInvert(out var inverse))
            {
                var local = inverse.MapPoint((float)origin.X, (float)origin.Y);
                origin = new(local.X, local.Y);
            }
        }
        string name = kind switch { LayerKind.Raster => NextLayerName("Layer"), LayerKind.Fill => NextLayerName("Fill"), LayerKind.Group => NextLayerName("Group"), _ => kind.ToString() };
        var args = new LayerArgs(id, name, kind, Parent: parent, Transform: new(origin.X, origin.Y),
            Width: width, Height: height, Text: text, Font: font, Size: size, Color: color);
        if (kind == LayerKind.Text)
        {
            var edited = TextLayerDialog.Show(EditorOwner, "Create text layer", new TextArgs(text, font, size, color),
                input => PreviewText(input, value => Command.Of("layer.create", ApplyText(args, value))));
            if (edited is null) return;
            args = ApplyText(args, edited);
        }
        Run("layer.create", args, selectId: id);
    }

    private void Duplicate() { if (active?.ActiveId is { } source) Duplicate(source); }

    private void Duplicate(string source)
    {
        string id = Format.Id();
        Run("layer.duplicate", id, source, id);
    }

    private void Rename()
    {
        if (ActiveLayer() is not { } layer) return;
        var dialog = new Window { Owner = this, Title = "Layer name and effects", Width = 440, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var shell = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel();
        var name = new TextBox { Text = layer.Name, Margin = new Thickness(2, 2, 2, 6) };
        header.Children.Add(new Label { Content = "Name", Target = name });
        header.Children.Add(name);
        var done = Button("Rename and close", () =>
        {
            if (name.Text == ActiveLayer()?.Name || Run("layer.rename", name.Text, layer.Id)) dialog.DialogResult = true;
        });
        done.IsDefault = true;
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(done, System.Windows.Controls.Dock.Bottom);
        shell.Children.Add(header);
        shell.Children.Add(done);
        effectEditor = new StackPanel();
        shell.Children.Add(new ScrollViewer { Content = effectEditor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = shell;
        PopulateEffects(layer);
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        effectDialog = dialog;
        try { dialog.ShowDialog(); }
        finally { effectEditor = null; effectDialog = null; }
    }

    private string NextLayerName(string prefix)
    {
        var names = active?.Engine.Current?.AllLayers().Select(layer => layer.Name).ToHashSet() ?? [];
        int number = 1;
        while (names.Contains($"{prefix} {number}")) number++;
        return $"{prefix} {number}";
    }

    private void DeleteLayer() => RunLayer("layer.delete", 0);

    private string[] SelectedLayers() => layers.SelectedItems.OfType<LayerRow>().Select(row => row.Layer.Id).ToArray();

    private void Group()
    {
        var ids = SelectedLayers();
        if (ids.Length == 0) return;
        string id = Format.Id();
        Run("layer.group", new GroupArgs(id, NextLayerName("Group"), ids, active?.ActiveId), selectId: id);
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

        Caption(layer.Name + (active?.MaskTarget == layer.Id ? " · Mask selected" : ""));
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
        if (effectEditor is not null) PopulateEffects(layer);
    }

    private void PopulateEffects(Layer layer)
    {
        if (effectEditor is not { } panel) return;
        panel.Children.Clear();
        if (layer.SpatialEdits.Length != 0)
        {
            panel.Children.Add(new TextBlock { Text = "Transforms", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 2, 4) });
            for (int i = 0; i < layer.SpatialEdits.Length; i++)
            {
                var entry = layer.SpatialEdits[i];
                int index = i;
                var controls = new WrapPanel();
                var name = entry.Kind switch
                {
                    SpatialKind.FreeTransform => "Free Transform",
                    SpatialKind.FlipHorizontal => "Flip Horizontal",
                    SpatialKind.FlipVertical => "Flip Vertical",
                    _ => entry.Kind.ToString()
                };
                var enabled = new CheckBox { Content = name, IsChecked = entry.Enabled, VerticalAlignment = VerticalAlignment.Center };
                enabled.Click += (_, _) => RunLayer("layer.toggleSpatial", index);
                controls.Children.Add(enabled);
                controls.Children.Add(Button("×", () => RunLayer("layer.removeSpatial", index), "Remove transform"));
                panel.Children.Add(controls);
            }
        }
        panel.Children.Add(new TextBlock { Text = "Effects", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 2, 4) });
        var effectKinds = new ComboBox { ItemsSource = Enum.GetValues<EffectKind>(), SelectedIndex = 0, Margin = new Thickness(2) };
        var add = Button("Add effect", () => { if (effectKinds.SelectedItem is EffectKind kind) AddEffect(kind); });
        var row = new DockPanel();
        DockPanel.SetDock(add, System.Windows.Controls.Dock.Right);
        row.Children.Add(add);
        row.Children.Add(effectKinds);
        panel.Children.Add(row);

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
            panel.Children.Add(rowActions);
        }
    }

    private void Caption(string text) => properties.Children.Add(new TextBlock
        { Text = text, Margin = new Thickness(2, 7, 2, 3), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

    private void EditTransform()
    {
        if (ActiveLayer() is not { } layer) return;
        var transform = layer.Transform;
        var v = Form("Layer transform", ("X (pixels)", transform.X.ToString()), ("Y (pixels)", transform.Y.ToString()),
            ("Rotation (degrees)", transform.Rotation.ToString()));
        if (v is not null) Guard(() => RunLayer("layer.transform", new Chop.Core.Transform(Number(v[0]), Number(v[1]),
            transform.ScaleX, transform.ScaleY, Number(v[2]))));
    }

    private Window EditorOwner => Window.GetWindow(canvas) ?? this;

    private void EditText()
    {
        if (ActiveLayer() is { Kind: LayerKind.Text } layer) EditText(layer);
    }

    private void EditText(Layer layer)
    {
        if (active?.Engine.Current is not { } doc || !Editable(doc, layer.Id))
        {
            Say("Unlock the text layer to edit it.");
            return;
        }
        if (TextLayerDialog.Show(EditorOwner, "Edit text layer", new TextArgs(layer.Text, layer.Font, layer.Size, layer.Color),
            input => PreviewText(input, value => Command.Of("layer.text", value, layer.Id))) is { } values)
            Run("layer.text", values, layer.Id, layer.Id);
    }

    private static LayerArgs ApplyText(LayerArgs layer, TextArgs text) =>
        layer with { Text = text.Text, Font = text.Font, Size = text.Size, Color = text.Color };

    private void PreviewText(TextArgs? text, Func<TextArgs, Command> command)
    {
        preview = null;
        try
        {
            if (text is not null) preview = active?.Engine.Preview(command(text));
        }
        catch (InvalidDataException) { }
        UpdateCanvas();
    }

    private void EditTextAt(Document doc, PointD point)
    {
        Layer? Hit(IEnumerable<Layer> layers)
        {
            foreach (var layer in layers.Reverse())
            {
                if (!layer.Visible || layer.Opacity == 0) continue;
                if (layer.Kind == LayerKind.Group && Hit(layer.Children) is { } child) return child;
                if (renderer.HitsText(doc, layer, point)) return layer;
            }
            return null;
        }

        if (Hit(doc.Layers) is { } text)
        {
            SelectLayer(text.Id);
            EditText(text);
        }
        else CreateLayer(LayerKind.Text, new PointD(Math.Floor(point.X), Math.Floor(point.Y)));
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
            A: kind == EffectKind.GaussianBlur ? 2 : kind == EffectKind.MotionBlur ? 12 : kind == EffectKind.Opacity ? 1 : 0,
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
            EffectKind.MotionBlur => [("Distance (px)", effect.A.ToString()), ("Angle (degrees)", effect.B.ToString())],
            _ => [("Blur sigma (0–100 px)", effect.A.ToString())]
        };
        (double Min, double Max)[] ranges = effect.Kind switch
        {
            EffectKind.BrightnessContrast => [(-1, 1), (-1, 1)],
            EffectKind.HueSaturation => [(-180, 180), (-1, 1)],
            EffectKind.Opacity => [(0, 1)],
            EffectKind.MotionBlur => [(0, 200), (-180, 180)],
            _ => [(0, 100)]
        };
        var documentEngine = active?.Engine;
        var target = ActiveLayer();
        void Live(string[]? values)
        {
            preview = null;
            try
            {
                if (values is not null && documentEngine is not null && target is not null &&
                    values.All(value => double.TryParse(value, out double number) && double.IsFinite(number)))
                {
                    var edited = effect with { A = Number(values[0]), B = values.Length == 2 ? Number(values[1]) : 0 };
                    bool exists = target.Effects.Any(item => item.Id == effect.Id);
                    preview = documentEngine.Preview(Command.Of(exists ? "effect.update" : "effect.add", edited, target.Id));
                }
            }
            catch (InvalidDataException) { }
            UpdateCanvas();
        }
        var v = BuildForm(EffectName(effect.Kind), fields, ranges, Live, effect.Kind == EffectKind.HueSaturation ? 0 : null);
        Effect? result = null;
        if (v is not null) Guard(() => result = effect with { A = Number(v[0]), B = v.Length == 2 ? Number(v[1]) : 0 });
        return result;
    }

    private static string EffectName(EffectKind kind) => CommandEngine.EffectLabel(kind);

    private void RefreshLog()
    {
        commandLog.ItemsSource = active is { } session
            ? DisplayLog(session).Skip(session.LogStart).Select(entry => new LogRow(entry)).ToArray() : null;
        var recording = ((WrapPanel)logPanel.Children[0]).Children.OfType<CheckBox>().Single();
        recording.IsEnabled = active is not null;
        recording.IsChecked = active?.Recording == true;
    }

    private void ExportLog(bool selection, bool clipboard)
    {
        if (active is null) return;
        var entries = selection ? commandLog.SelectedItems.OfType<LogRow>().Select(row => row.Entry) : active.Log;
        var commands = entries.Where(entry => entry.Success && entry.Command.Name is not ("document.save" or "document.export"))
            .Select(entry => entry.Command).ToArray();
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
