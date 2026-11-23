using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;
using SkiaSharp;
using Chop.Rendering;

namespace PhotoChop;

public partial class MainWindow
{
    private sealed class TextEditSession(Session session, RichTextBox editor, LayerArgs? create, Layer? layer, Canvas host, SKMatrix placement, RichText text)
    {
        public Session Session { get; } = session;
        public RichTextBox Editor { get; } = editor;
        public LayerArgs? Create { get; } = create;
        public Layer? Layer { get; } = layer;
        public Canvas Host { get; } = host;
        public SKMatrix Placement { get; } = placement;
        private Document? source;
        private Document? background;

        public Document? BackgroundFor(Document document)
        {
            if (Layer is null) return null;
            if (!ReferenceEquals(source, document))
            {
                source = document;
                background = document.AllLayers().FirstOrDefault(item => item.Id == Layer.Id) is { } layer
                    ? document.ReplaceLayer(layer.Id, layer with { Visible = false }) : null;
            }
            return background;
        }
        public TextWarp? Warp { get; set; } = text.Warp;
        public double Width { get; set; } = text.Width;
        public bool Modified { get; set; }
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
        var placement = SKMatrix.CreateTranslation((float)(create?.Transform?.X ?? 0), (float)(create?.Transform?.Y ?? 0));
        if (create?.Parent is { } parent && SkiaRenderer.WorldMatrix(doc.Layers, parent, SKMatrix.Identity) is { } parentWorld)
            placement = SKMatrix.Concat(parentWorld, placement);
        if (layer is not null && SkiaRenderer.WorldMatrix(doc.Layers, layer.Id, SKMatrix.Identity) is { } world)
            placement = SKMatrix.Concat(world, SKMatrix.CreateTranslation(layer.RasterX, layer.RasterY));

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
            Padding = new Thickness(2), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Ink(style.Color), CaretBrush = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            AcceptsReturn = true, IsInactiveSelectionHighlightEnabled = true, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = rich.Width == 0 ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
            FocusVisualStyle = null, IsUndoEnabled = true
        };
        var host = new Canvas { ClipToBounds = true };
        host.Children.Add(editor);
        editingText = new(active, editor, create, layer, host, placement, rich);
        Grid.SetColumn(host, 1);
        Grid.SetRow(host, 1);
        Panel.SetZIndex(host, 10);
        active.Viewport.Children.Add(host);
        editor.TextChanged += (_, _) => { if (editingText is { } edit) edit.Modified = true; };
        editor.SelectionChanged += (_, _) => syncTypographyToolbar?.Invoke();
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; FinishTextEdit(cancel: true); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            { e.Handled = true; FinishTextEdit(); }
        };
        PositionTextEditor();
        if (layer is not null) UpdateCanvas();
        RefreshTools();
        editor.Focus();
        editor.CaretPosition = flow.ContentEnd;
        Say("Edit text on the canvas. Numpad Enter or Ctrl+Enter applies. Esc cancels.");
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

    private void PositionTextEditor()
    {
        if (editingText is not { } edit) return;
        var editor = edit.Editor;
        double zoom = edit.Session.Zoom;
        var offset = edit.Session.Offset;
        var placement = edit.Placement;
        Canvas.SetLeft(editor, placement.TransX * zoom + offset.X);
        Canvas.SetTop(editor, placement.TransY * zoom + offset.Y);
        editor.RenderTransform = new MatrixTransform(new Matrix(
            placement.ScaleX * zoom, placement.SkewY * zoom,
            placement.SkewX * zoom, placement.ScaleY * zoom, 0, 0));
        editor.HorizontalScrollBarVisibility = edit.Width > 0 ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        editor.Width = edit.Width > 0 ? edit.Width : double.NaN;
        editor.MinWidth = 16;
        editor.MinHeight = 16;
        editor.MaxWidth = edit.Width > 0 ? double.PositiveInfinity : 16384;

    }

    private void FinishTextEdit(bool cancel = false)
    {
        if (editingText is not { } edit) return;
        RichText? text = cancel ? null : ReadRichText(edit);
        editingText = null;
        syncTypographyToolbar = null;
        edit.Session.Viewport.Children.Remove(edit.Host);
        bool previewWasVisible = preview is not null;
        preview = null;
        if (text is not null && ReferenceEquals(active, edit.Session) &&
            (edit.Layer is not null && edit.Modified || edit.Create is not null && text.PlainText.Length > 0))
        {
            CharacterStyle style = text.Paragraphs.SelectMany(p => p.Runs).FirstOrDefault()?.Style ?? textDefaults;
            if (edit.Create is { } create)
            {
                var previous = edit.Session.Engine.Current;
                Run("layer.create", create with { Text = text.PlainText, Font = style.Font, Size = style.Size, Color = style.Color,
                    Typography = text }, selectId: create.Id, beforeRefresh: session =>
                    {
                        if (session.Engine.Current is not { } document || ActiveLayer() is not { } added) return;
                        var underlay = previous is not null && session.Rendered is { } rendered &&
                            SameComposite(previous, rendered) && SkiaRenderer.IsTopText(document.Layers, added.Id)
                            ? session.Frame : null;
                        ShowTextMovePreview(session, document, added, underlay, newlyCreated: true);
                    });
            }
            else if (edit.Layer is { } layer)
                Run("layer.text", new TextArgs(text.PlainText, style.Font, style.Size, style.Color, Typography: text), layer.Id, layer.Id);
        }
        else
        {
            if (edit.Layer is not null || previewWasVisible) UpdateCanvas();
            RefreshTools();
        }
    }

    private static Brush Ink(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
    private static Run WriteRun(string content, CharacterStyle s)
    {
        var run = new Run(content) { Tag = s, FontFamily = new FontFamily(ResolveEditorFamily(s.Font)), FontSize = s.Size,
            FontWeight = FontWeight.FromOpenTypeWeight(s.FauxBold ? Math.Max(700, s.Weight) : s.Weight),
            FontStyle = s.Italic || s.FauxItalic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = Ink(s.Color), BaselineAlignment = s.Script switch
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
        edit.Modified = true;
    }

    private void SetCustomTextStyle(Func<CharacterStyle, CharacterStyle> update)
    {
        textDefaults = update(textDefaults);
        if (editingText is not { } edit) return;
        var selection = edit.Editor.Selection;
        if (selection.IsEmpty)
        {
            if (selection.Start.Parent is Run run) run.Tag = update(ReadRun(run));
            edit.Modified = true;
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
        edit.Modified = true;
    }
}
