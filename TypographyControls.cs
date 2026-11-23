using System.IO;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private static readonly Lazy<FontFamily[]> InstalledFonts = new(() =>
        Fonts.SystemFontFamilies.OrderBy(f => f.Source, StringComparer.CurrentCultureIgnoreCase).ToArray());

    private Action? syncTypographyToolbar;
    private static readonly ConcurrentDictionary<string, string> EditorFamilies = new();
    private static string ResolveEditorFamily(string font) => EditorFamilies.GetOrAdd(font,
        name => RichText.ResolveFamily(name, InstalledFonts.Value.Select(family => family.Source)));

    private void AddTypographyTools()
    {
        var current = CurrentTextStyle();
        var font = new ComboBox { Width = 162, MaxDropDownHeight = 380, ItemsSource = InstalledFonts.Value,
            IsTextSearchEnabled = true, SelectedValuePath = nameof(FontFamily.Source) };
        TextSearch.SetTextPath(font, nameof(FontFamily.Source));
        var sample = new FrameworkElementFactory(typeof(TextBlock));
        sample.SetBinding(TextBlock.TextProperty, new Binding(nameof(FontFamily.Source)));
        sample.SetBinding(TextBlock.FontFamilyProperty, new Binding());
        font.ItemTemplate = new DataTemplate { VisualTree = sample };
        var face = new ComboBox { Width = 108, DisplayMemberPath = "Label" };
        bool updating = false;
        FontFamily? listedFamily = null;
        FontChoice[] choices = [];
        void Faces(FontFamily family)
        {
            bool previous = updating;
            updating = true;
            if (listedFamily != family)
            {
                listedFamily = family;
                choices = family.GetTypefaces().Select(type => new FontChoice(
                    type.FaceNames.Values.FirstOrDefault() ?? $"{type.Weight} {type.Style}", type.Weight.ToOpenTypeWeight(),
                    type.Style != FontStyles.Normal)).DistinctBy(c => (c.Weight, c.Italic)).ToArray();
                face.ItemsSource = choices;
            }
            var style = CurrentTextStyle();
            face.SelectedItem = choices.FirstOrDefault(c => c.Weight == style.Weight && c.Italic == style.Italic) ?? choices.FirstOrDefault();
            updating = previous;
        }
        font.SelectedValue = ResolveEditorFamily(current.Font);
        Faces(font.SelectedItem as FontFamily ?? new FontFamily(current.Font));
        font.SelectionChanged += (_, _) =>
        {
            if (updating || font.SelectedItem is not FontFamily family) return;
            updating = true;
            SetTextProperty(TextElement.FontFamilyProperty, family, s => s with { Font = family.Source });
            Faces(family);
            updating = false;
        };
        face.SelectionChanged += (_, _) =>
        {
            if (updating || face.SelectedItem is not FontChoice choice) return;
            updating = true;
            SetTextProperty(TextElement.FontWeightProperty, FontWeight.FromOpenTypeWeight(choice.Weight), s => s with { Weight = choice.Weight });
            SetTextProperty(TextElement.FontStyleProperty, choice.Italic ? FontStyles.Italic : FontStyles.Normal, s => s with { Italic = choice.Italic });
            updating = false;
        };
        var size = TypographyNumber(current.Size, 1, 512, value =>
            SetTextProperty(TextElement.FontSizeProperty, value, s => s with { Size = value }));
        var align = AlignmentSelector(() => updating);
        var chip = StyleFields.ColorChip(current.Color);
        var swatch = Button("", () =>
        {
            if (ColorPickerDialog.Show(EditorOwner, "Text color", CurrentTextStyle().Color) is not { } value) return;
            SetTextProperty(TextElement.ForegroundProperty, Ink(value), s => s with { Color = value });
            chip.Background = Ink(value);
        }, "Text color");
        swatch.Content = chip;
        ToolOptions.Children.Add(font);
        ToolOptions.Children.Add(face);
        ToolOptions.Children.Add(size);
        ToolOptions.Children.Add(align);
        ToolOptions.Children.Add(swatch);
        CharacterStyle? lastStyle = null;
        syncTypographyToolbar = () =>
        {
            if (updating) return;
            updating = true;
            var style = CurrentTextStyle();
            if (style != lastStyle)
            {
                lastStyle = style;
                font.SelectedValue = ResolveEditorFamily(style.Font);
                if (font.SelectedItem is FontFamily selected) Faces(selected);
                if (!size.IsKeyboardFocusWithin) size.Text = style.Size.ToString("0.##");
                chip.Background = Ink(style.Color);
            }
            align.SelectedItem = editingText?.Editor.Selection.Start.Paragraph?.TextAlignment ?? TextAlignment.Left;
            updating = false;
        };
        ToolOptions.Children.Add(Button("Warp…", EditTextWarp));
        if (editingText is not null)
        {
            ToolOptions.Children.Add(Button("Apply", () => FinishTextEdit(), "Apply text (Ctrl+Enter)"));
            ToolOptions.Children.Add(Button("Cancel", () => FinishTextEdit(cancel: true), "Cancel text edit (Esc)"));
        }
    }

    private sealed record FontChoice(string Label, int Weight, bool Italic);

    private static TextBox TypographyNumber(double value, double min, double max, Action<double> set)
    {
        var field = new TextBox { Text = value.ToString("0.##"), Width = 66, VerticalContentAlignment = VerticalAlignment.Center };
        NumericWheel.Enable(field, min, max, set);
        return field;
    }

    private ComboBox AlignmentSelector(Func<bool>? syncing = null)
    {
        var initial = editingText?.Editor.Selection.Start.Paragraph?.TextAlignment ?? TextAlignment.Left;
        var field = new ComboBox { ItemsSource = Enum.GetValues<TextAlignment>(), SelectedItem = initial, MinWidth = 76 };
        field.SelectionChanged += (_, _) =>
        {
            if (syncing?.Invoke() != true && field.SelectedItem is TextAlignment selected) SetParagraph(p => p.TextAlignment = selected);
        };
        return field;
    }

    private void SetParagraph(Action<Paragraph> update)
    {
        if (editingText is not { } edit) return;
        var range = edit.Editor.Selection;
        foreach (var p in edit.Editor.Document.Blocks.OfType<Paragraph>())
            if (p.ContentEnd.CompareTo(range.Start) >= 0 && p.ContentStart.CompareTo(range.End) <= 0) update(p);
        edit.Modified = true;
    }

    private static void TypographyRow(Panel body, string label, FrameworkElement control)
    {
        var item = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var description = new TextBlock { Text = label, Width = 132, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(description, System.Windows.Controls.Dock.Left);
        item.Children.Add(description);
        item.Children.Add(control);
        body.Children.Add(item);
    }

    private FrameworkElement BuildCharacterTypography()
    {
        var body = new StackPanel { Margin = new Thickness(8) };
        var view = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void RefreshFields()
        {
            body.Children.Clear();
            var style = CurrentTextStyle();
            void Number(string name, double value, double min, double max, Func<CharacterStyle, double, CharacterStyle> update) =>
                TypographyRow(body, name, TypographyNumber(value, min, max, v => SetCustomTextStyle(s => update(s, v))));
            Number("Tracking", style.Tracking, -500, 2000, (s, v) => s with { Tracking = v });
            Number("Kerning (1/1000 em)", style.KerningOffset, -500, 2000, (s, v) => s with { KerningOffset = v });
            Number("Width (%)", style.HorizontalScale * 100, 10, 500, (s, v) => s with { HorizontalScale = v / 100 });
            Number("Height (%)", style.VerticalScale * 100, 10, 500, (s, v) => s with { VerticalScale = v / 100 });
            Number("Baseline shift (px)", style.BaselineShift, -512, 512, (s, v) => s with { BaselineShift = v });
            TypographyRow(body, "Leading (0 is auto)", TypographyNumber(0, 0, 4096, v => SetParagraph(p => p.LineHeight = v > 0 ? v : double.NaN)));
            void Toggle(string label, bool initial, Action<bool> update)
            {
                var field = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(3) };
                field.Click += (_, _) => update(field.IsChecked == true);
                body.Children.Add(field);
            }
            Toggle("Kerning", style.Kerning, v => SetCustomTextStyle(s => s with { Kerning = v }));
            Toggle("Faux bold", style.FauxBold, v => SetCustomTextStyle(s => s with { FauxBold = v }));
            Toggle("Faux italic", style.FauxItalic, v => SetCustomTextStyle(s => s with { FauxItalic = v }));
            Toggle("Underline", style.Underline, v => SetCustomTextStyle(s => s with { Underline = v }));
            Toggle("Strikethrough", style.Strike, v => SetCustomTextStyle(s => s with { Strike = v }));
            var casing = new ComboBox { ItemsSource = Enum.GetValues<TextCase>(), SelectedItem = style.Case };
            casing.SelectionChanged += (_, _) =>
            { if (casing.SelectedItem is TextCase format) SetCustomTextStyle(s => s with { Case = format }); };
            TypographyRow(body, "Caps", casing);
            var script = new ComboBox { ItemsSource = Enum.GetValues<TextScript>(), SelectedItem = style.Script };
            script.SelectionChanged += (_, _) =>
            {
                if (script.SelectedItem is not TextScript value) return;
                SetTextProperty(Inline.BaselineAlignmentProperty, value switch
                {
                    TextScript.Superscript => BaselineAlignment.Superscript,
                    TextScript.Subscript => BaselineAlignment.Subscript, _ => BaselineAlignment.Baseline
                }, s => s with { Script = value });
            };
            TypographyRow(body, "Script", script);
        }
        view.IsVisibleChanged += (_, _) => { if (view.IsVisible) RefreshFields(); };
        RefreshFields();
        return view;
    }

    private FrameworkElement BuildParagraphTypography()
    {
        var body = new StackPanel { Margin = new Thickness(8) };
        var view = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void RefreshFields()
        {
            body.Children.Clear();
            var p = editingText?.Editor.Selection.Start.Paragraph;
            TypographyRow(body, "Alignment", AlignmentSelector());
            void Number(string name, double value, double min, Action<Paragraph, double> update) =>
                TypographyRow(body, name, TypographyNumber(value, min, 4096, v => SetParagraph(paragraph => update(paragraph, v))));
            Number("Left indent", p?.Margin.Left ?? 0, 0, (q, v) => q.Margin = new Thickness(v, q.Margin.Top, q.Margin.Right, q.Margin.Bottom));
            Number("Right indent", p?.Margin.Right ?? 0, 0, (q, v) => q.Margin = new Thickness(q.Margin.Left, q.Margin.Top, v, q.Margin.Bottom));
            Number("First line indent", p?.TextIndent ?? 0, -4096, (q, v) => q.TextIndent = v);
            Number("Space before", p?.Margin.Top ?? 0, 0, (q, v) => q.Margin = new Thickness(q.Margin.Left, v, q.Margin.Right, q.Margin.Bottom));
            Number("Space after", p?.Margin.Bottom ?? 0, 0, (q, v) => q.Margin = new Thickness(q.Margin.Left, q.Margin.Top, q.Margin.Right, v));
            TypographyRow(body, "Text box width", TypographyNumber(editingText?.Width ?? 0, 0, 16384, v =>
            {
                if (editingText is not { } edit) return;
                edit.Width = v;
                edit.Modified = true;
                PositionTextEditor();
            }));
        }
        view.IsVisibleChanged += (_, _) => { if (view.IsVisible) RefreshFields(); };
        RefreshFields();
        return view;
    }

    private void EditTextWarp()
    {
        if (editingText is null && ActiveLayer() is { Kind: LayerKind.Text } layer) EditText(layer);
        if (editingText is not { } edit) return;
        var initial = edit.Warp ?? new();
        var dialog = new Window { Owner = EditorOwner, Title = "Warp Text", Width = 405, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Margin = new Thickness(12) };
        var styles = new ComboBox { ItemsSource = Enum.GetValues<TextWarpStyle>(), SelectedItem = initial.Style };
        TypographyRow(body, "Style", styles);
        var vertical = new CheckBox { Content = "Vertical", IsChecked = initial.Vertical, Margin = new Thickness(3) };
        body.Children.Add(vertical);
        var bend = WarpField("Bend", initial.Bend);
        var horizontal = WarpField("Horizontal distortion", initial.HorizontalDistortion);
        var perpendicular = WarpField("Vertical distortion", initial.VerticalDistortion);
        (DockPanel Row, Slider Slider, TextBox Input) WarpField(string name, double value)
        {
            var control = StyleFields.Range(value * 100, -100, 100);
            body.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 8, 0, 0) });
            body.Children.Add(control.Row);
            return control;
        }
        TextWarp Read() => new((TextWarpStyle)styles.SelectedItem, bend.Slider.Value / 100, vertical.IsChecked == true,
            horizontal.Slider.Value / 100, perpendicular.Slider.Value / 100);
        void PreviewWarp()
        {
            if (edit.Session.Engine.Current is not { } doc) return;
            var rich = ReadRichText(edit) with { Warp = Read() };
            var style = CurrentTextStyle();
            var command = edit.Create is { } create ? Command.Of("layer.create", create with { Typography = rich }) :
                Command.Of("layer.text", new TextArgs(rich.PlainText, style.Font, style.Size, style.Color, Typography: rich), edit.Layer!.Id);
            try { preview = edit.Session.Engine.Preview(doc, command); UpdateCanvas(); }
            catch (InvalidDataException) { }
        }
        styles.SelectionChanged += (_, _) => PreviewWarp();
        vertical.Click += (_, _) => PreviewWarp();
        bend.Slider.ValueChanged += (_, _) => PreviewWarp();
        horizontal.Slider.ValueChanged += (_, _) => PreviewWarp();
        perpendicular.Slider.ValueChanged += (_, _) => PreviewWarp();
        var apply = Button("Apply", () => dialog.DialogResult = true);
        apply.IsDefault = true;
        body.Children.Add(apply);
        body.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(2) });
        dialog.Content = body;
        edit.Editor.Visibility = Visibility.Hidden;
        dialog.Loaded += (_, _) => PreviewWarp();
        if (dialog.ShowDialog() == true) { edit.Warp = Read(); edit.Modified = true; }
        edit.Editor.Visibility = Visibility.Visible;
        preview = null;
        UpdateCanvas();
        edit.Editor.Focus();
    }
}
