using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;
using Chop.Rendering;

namespace PhotoChop;

public partial class MainWindow
{
    private sealed class TextEditSession(Session session, RichTextBox editor, Window window, LayerArgs? create, Layer? layer, RichText text)
    {
        public Session Session { get; } = session;
        public RichTextBox Editor { get; } = editor;
        public Window Window { get; } = window;
        public LayerArgs? Create { get; } = create;
        public Layer? Layer { get; } = layer;
        public TextWarp? Warp { get; set; } = text.Warp;
        public double Width { get; set; } = text.Width;
        public bool Modified { get; set; }
        public bool LivePreview { get; set; } = true;
        public DispatcherTimer Timer { get; } = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(85) };
        public Document? Draft { get; set; }
        public Document? DraftSource { get; set; }
        public Command? DraftCommand { get; set; }
        public Command? DisplayedCommand { get; set; }
    }

    private TextEditSession? editingText;
    private CharacterStyle textDefaults = new();

    private void BeginTextEdit(LayerArgs? create, Layer? layer)
    {
        FinishTextEdit();
        if (tool != "Text") SetTool("Text");
        if (active?.Engine.Current is not { } doc) return;
        if (layer is not null && !Editable(doc, layer.Id))
        {
            Say("Unlock the text layer to edit it.");
            return;
        }

        CharacterStyle style = layer is null ? textDefaults :
            new(layer.Font, layer.Size, layer.Color);
        var rich = layer?.Typography ?? RichText.FromPlain(layer?.Text ?? "", style,
            layer is { TextBend: not 0 } ? new(TextWarpStyle.Arc, layer.TextBend) : null);
        var flow = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily(ResolveEditorFamily(style.Font)),
            FontSize = style.Size, Foreground = Ink(style.Color), TextAlignment = TextAlignment.Left };
        foreach (var item in rich.Paragraphs)
        {
            var p = new Paragraph { Margin = new Thickness(item.Style.LeftIndent, item.Style.SpaceBefore,
                item.Style.RightIndent, item.Style.SpaceAfter), TextIndent = item.Style.FirstLineIndent,
                TextAlignment = Enum.Parse<TextAlignment>(item.Style.Alignment.ToString()),
                LineHeight = item.Style.Leading == 0 ? double.NaN : item.Style.Leading };
            foreach (var run in item.Runs) p.Inlines.Add(WriteRun(run.Text, run.Style));
            flow.Blocks.Add(p);
        }
        var editor = new RichTextBox(flow)
        {
            Padding = new Thickness(8), Background = Brushes.Transparent, Foreground = Ink(style.Color), CaretBrush = Brushes.DeepSkyBlue,
            AcceptsReturn = true, IsInactiveSelectionHighlightEnabled = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FocusVisualStyle = null, IsUndoEnabled = true
        };
        var window = new Window
        {
            Owner = EditorOwner, Title = layer is null ? "New Text Layer" : $"Edit Text · {layer.Name}",
            Width = 450, Height = 250, MinWidth = 300, MinHeight = 175,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (active.View.MovingText is not null) ClearTextMovePreview(active);
        var edit = new TextEditSession(active, editor, window, create, layer, rich);
        editingText = edit;
        var layout = new DockPanel { Margin = new Thickness(10), Background = new SolidColorBrush(Color.FromRgb(37, 40, 48)) };
        var actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        DockPanel.SetDock(actions, System.Windows.Controls.Dock.Bottom);
        layout.Children.Add(actions);
        var live = new CheckBox { Content = "Live preview", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        live.Click += (_, _) =>
        {
            edit.LivePreview = live.IsChecked == true;
            RefreshTextPreview(edit);
        };
        actions.Children.Add(live);
        foreach (var button in new[] { Button("Cancel", () => FinishTextEdit(cancel: true)), Button("Apply", () => FinishTextEdit()) })
        {
            DockPanel.SetDock(button, System.Windows.Controls.Dock.Right);
            actions.Children.Add(button);
        }
        layout.Children.Add(editor);
        window.Content = layout;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(editingText, edit)) FinishTextEdit(cancel: true, windowClosed: true);
        };
        edit.Timer.Tick += (_, _) => RefreshTextPreview(edit);
        editor.TextChanged += (_, _) => TextEditChanged(edit);
        editor.SelectionChanged += (_, _) => syncTypographyToolbar?.Invoke();
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; FinishTextEdit(cancel: true); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            { e.Handled = true; FinishTextEdit(); }
        };
        RefreshTools();
        window.Show();
        editor.Focus();
        editor.CaretPosition = flow.ContentEnd;
        Say("Edit text in the separate window. Numpad Enter or Ctrl+Enter applies. Esc cancels.");
    }

    private void TextEditorKeyMessage(ref MSG message, ref bool handled)
    {
        const int KeyDown = 0x0100;
        const int ReturnKey = 0x0D;
        const long ExtendedKey = 1L << 24;
        if (handled || message.message != KeyDown || message.wParam.ToInt64() != ReturnKey ||
            (message.lParam.ToInt64() & ExtendedKey) == 0 ||
            editingText?.Editor.IsKeyboardFocusWithin != true) return;
        handled = true;
        FinishTextEdit();
    }

    private static Command? TextEditCommand(TextEditSession edit, RichText rich, CharacterStyle fallback)
    {
        var style = rich.Paragraphs.SelectMany(p => p.Runs).FirstOrDefault()?.Style ?? fallback;
        if (edit.Create is { } create && rich.PlainText.Length > 0)
            return Command.Of("layer.create", create with { Text = rich.PlainText, Font = style.Font, Size = style.Size,
                Color = style.Color, Typography = rich });
        return edit.Layer is { } layer
            ? Command.Of("layer.text", new TextArgs(rich.PlainText, style.Font, style.Size, style.Color, Typography: rich), layer.Id)
            : null;
    }

    private static void TextEditChanged(TextEditSession edit)
    {
        edit.Modified = true;
        if (edit.LivePreview && !edit.Timer.IsEnabled) edit.Timer.Start();
    }

    private void RefreshTextPreview(TextEditSession edit, TextWarp? temporaryWarp = null)
    {
        edit.Timer.Stop();
        if (!ReferenceEquals(editingText, edit) || !ReferenceEquals(active, edit.Session) ||
            edit.Session.Engine.Current is not { } source) return;
        var text = ReadRichText(edit);
        if (temporaryWarp is not null) text = text with { Warp = temporaryWarp };
        var command = edit.LivePreview && (edit.Modified || temporaryWarp is not null)
            ? TextEditCommand(edit, text, textDefaults) : null;
        edit.Draft = null;
        edit.DraftCommand = null;
        edit.DraftSource = source;
        if (command is not null)
        {
            try
            {
                edit.Draft = edit.Session.Engine.Preview(source, command);
                edit.DraftCommand = command;
            }
            catch (InvalidDataException error) { Say(error.Message); }
        }
        UpdateCanvas();
    }

    private void FinishTextEdit(bool cancel = false, bool windowClosed = false)
    {
        if (editingText is not { } edit) return;
        var command = !cancel && edit.Modified && ReferenceEquals(active, edit.Session)
            ? TextEditCommand(edit, ReadRichText(edit), textDefaults) : null;
        edit.Timer.Stop();
        editingText = null;
        syncTypographyToolbar = null;
        if (!windowClosed) edit.Window.Close();
        if (command is not null)
        {
            if (ReferenceEquals(edit.DraftSource, edit.Session.Engine.Current) &&
                edit.DisplayedCommand is { } displayed && displayed.Args.GetRawText() == command.Args.GetRawText())
                edit.Session.KeepTextFrame = true;
            Run(command.Name, command.Args, command.Target, edit.Create?.Id ?? edit.Layer?.Id);
        }
        else
        {
            if (edit.Draft is not null || edit.DisplayedCommand is not null) UpdateCanvas();
            RefreshTools();
        }
    }

    private static Brush Ink(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
    private static Run WriteRun(string content, CharacterStyle s)
    {
        var ink = (SolidColorBrush)Ink(s.Color);
        var color = ink.Color;
        var backdrop = .2126 * color.R + .7152 * color.G + .0722 * color.B < 105 ? Brushes.Gainsboro : null;
        var run = new Run(content) { Tag = s, FontFamily = new FontFamily(ResolveEditorFamily(s.Font)), FontSize = s.Size,
            FontWeight = FontWeight.FromOpenTypeWeight(s.FauxBold ? Math.Max(700, s.Weight) : s.Weight),
            FontStyle = s.Italic || s.FauxItalic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = ink, Background = backdrop, BaselineAlignment = s.Script switch
            {
                TextScript.Superscript => BaselineAlignment.Superscript, TextScript.Subscript => BaselineAlignment.Subscript, _ => BaselineAlignment.Baseline
            } };
        var lines = new TextDecorationCollection();
        if (s.Underline) lines.Add(TextDecorations.Underline[0]);
        if (s.Strike) lines.Add(TextDecorations.Strikethrough[0]);
        run.TextDecorations = lines;
        return run;
    }

    private static CharacterStyle ReadRun(Run run)
    {
        var baseline = run.Tag as CharacterStyle ?? new();
        return baseline with { Font = run.FontFamily.Source, Size = Math.Clamp(run.FontSize, 1, 512),
            Color = run.Foreground is SolidColorBrush ink ? ink.Color.ToString() : baseline.Color,
            Weight = baseline.FauxBold && run.FontWeight.ToOpenTypeWeight() == Math.Max(700, baseline.Weight)
                ? baseline.Weight : Math.Clamp(run.FontWeight.ToOpenTypeWeight(), 100, 950),
            Italic = baseline.FauxItalic ? baseline.Italic : run.FontStyle != FontStyles.Normal,
            Underline = run.TextDecorations?.Any(line => line.Location == TextDecorationLocation.Underline) == true,
            Strike = run.TextDecorations?.Any(line => line.Location == TextDecorationLocation.Strikethrough) == true,
            Script = run.BaselineAlignment switch
            {
                BaselineAlignment.Subscript => TextScript.Subscript, BaselineAlignment.Superscript => TextScript.Superscript, _ => TextScript.Normal
            } };
    }

    private static IEnumerable<Inline> TextInlines(InlineCollection items)
    {
        foreach (var inline in items)
            if (inline is Span span)
                foreach (var item in TextInlines(span.Inlines)) yield return item;
            else yield return inline;
    }

    private static RichText ReadRichText(TextEditSession edit)
    {
        var paragraphs = new List<TextParagraph>();
        foreach (var p in edit.Editor.Document.Blocks.OfType<Paragraph>())
        {
            var settings = new ParagraphStyle(Enum.Parse<TextAlignmentMode>(p.TextAlignment.ToString()), double.IsNaN(p.LineHeight) ? 0 : p.LineHeight,
                p.Margin.Left, p.Margin.Right, p.TextIndent, p.Margin.Top, p.Margin.Bottom);
            var runs = new List<TextRun>();
            void AddParagraph() { paragraphs.Add(new(runs.ToArray(), settings)); runs.Clear(); }
            foreach (var inline in TextInlines(p.Inlines))
            {
                if (inline is LineBreak) { AddParagraph(); continue; }
                if (inline is not Run run) continue;
                CharacterStyle style = ReadRun(run);
                if (runs.LastOrDefault() is { } previous && previous.Style == style)
                    runs[^1] = previous with { Text = previous.Text + run.Text };
                else runs.Add(new(run.Text, style));
            }
            AddParagraph();
        }
        return new(paragraphs.Count == 0 ? [new([], new())] : paragraphs.ToArray(), edit.Warp, edit.Width);
    }

    private CharacterStyle CurrentTextStyle()
    {
        if (editingText?.Editor.Selection.Start is { } point)
        {
            if (point.Parent is Run run) return ReadRun(run);
            if (point.GetAdjacentElement(LogicalDirection.Forward) is Run next) return ReadRun(next);
            if (point.GetAdjacentElement(LogicalDirection.Backward) is Run previous) return ReadRun(previous);
        }
        return textDefaults;
    }

    private void SetTextProperty(DependencyProperty property, object value, Func<CharacterStyle, CharacterStyle> update)
    {
        textDefaults = update(textDefaults);
        if (editingText is not { } edit) return;
        if (!edit.Editor.Selection.IsEmpty) SetCustomTextStyle(update);
        else edit.Editor.Selection.ApplyPropertyValue(property, value);
        TextEditChanged(edit);
    }

    private void SetCustomTextStyle(Func<CharacterStyle, CharacterStyle> update)
    {
        textDefaults = update(textDefaults);
        if (editingText is not { } edit) return;
        var selection = edit.Editor.Selection;
        if (selection.IsEmpty)
        {
            if (selection.Start.Parent is Run run) run.Tag = update(ReadRun(run));
            TextEditChanged(edit);
            return;
        }
        var affected = edit.Editor.Document.Blocks.OfType<Paragraph>().SelectMany(p => TextInlines(p.Inlines)).OfType<Run>()
            .Where(run => run.ContentEnd.CompareTo(selection.Start) > 0 && run.ContentStart.CompareTo(selection.End) < 0)
            .Select(run => (Run: run, Start: Math.Max(0, run.ContentStart.GetOffsetToPosition(selection.Start)),
                End: Math.Min(run.Text.Length, run.ContentStart.GetOffsetToPosition(selection.End))))
            .ToArray();
        Run? first = null, last = null;
        foreach (var item in affected)
        {
            if (item.End <= item.Start) continue;
            var run = item.Run;
            var container = run.Parent is Span span ? span.Inlines : ((Paragraph)run.Parent).Inlines;
            var style = ReadRun(run);
            if (item.Start > 0) container.InsertBefore(run, WriteRun(run.Text[..item.Start], style));
            var changed = WriteRun(run.Text[item.Start..item.End], update(style));
            container.InsertBefore(run, changed);
            if (item.End < run.Text.Length) container.InsertBefore(run, WriteRun(run.Text[item.End..], style));
            container.Remove(run);
            first ??= changed;
            last = changed;
        }
        if (first is not null && last is not null) selection.Select(first.ContentStart, last.ContentEnd);
        TextEditChanged(edit);
    }
}
