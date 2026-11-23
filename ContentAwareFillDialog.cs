using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private async void OpenContentAwareFill()
    {
        if (active is not { } session || session.Engine.Current is not { Selection: { } selected } document ||
            session.ActiveId is not { } layerId) return;
        if ((long)selected.Width * selected.Height > ContentAwareFill.MaxTargetPixels)
        {
            MessageBox.Show(EditorOwner, $"Select an area with bounds of at most {ContentAwareFill.MaxTargetPixels:N0} pixels to fill.",
                "Content-Aware Fill", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            StartWaiting();
            FillSamplingCanvas surface;
            try
            {
                var source = session.Rendered is { } rendered && SameComposite(document, rendered) ? session.Composite : null;
                var thumbnail = await Task.Run(() => FillSamplingCanvas.Thumbnail(source ?? renderer.Composite(document)));
                if (!ReferenceEquals(active, session) || !ReferenceEquals(document, session.Engine.Current)) return;
                surface = new FillSamplingCanvas(document, thumbnail);
            }
            finally { StopWaiting(); }
            ShowContentAwareFill(session, document, layerId, surface);
        }
        catch (Exception error)
        {
            MessageBox.Show(EditorOwner, error.Message, "Content-Aware Fill", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowContentAwareFill(Session session, Document document, string layerId, FillSamplingCanvas surface)
    {
        var dialog = new Window { Owner = EditorOwner, Title = "Content-Aware Fill", Width = 900, Height = 680,
            MinWidth = 460, MinHeight = 380, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(12) };
        dialog.Content = root;
        var controls = new StackPanel();
        DockPanel.SetDock(controls, System.Windows.Controls.Dock.Top);
        root.Children.Add(controls);
        controls.Children.Add(new TextBlock { Text = "Green pixels can be sampled. The red selection will be filled on a new layer.", TextWrapping = TextWrapping.Wrap });
        var tools = new WrapPanel { Margin = new Thickness(0, 5, 0, 6) };
        controls.Children.Add(tools);
        var mode = new ComboBox { ItemsSource = new[] { "Add sampling area", "Remove sampling area" }, SelectedIndex = 0, MinWidth = 172 };
        mode.SelectionChanged += (_, _) =>
        {
            surface.Include = mode.SelectedIndex == 0;
            surface.RefreshCursor();
        };
        void UpdateAltCursor(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.System ? e.SystemKey : e.Key) is Key.LeftAlt or Key.RightAlt)
                surface.RefreshCursor();
        }
        dialog.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(UpdateAltCursor), handledEventsToo: true);
        dialog.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(UpdateAltCursor), handledEventsToo: true);
        tools.Children.Add(mode);
        tools.Children.Add(new TextBlock { Text = "Brush diameter (px)", Margin = new Thickness(8, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
        double diameter = double.IsFinite(preferences.FillSamplingDiameter) ? Math.Clamp(preferences.FillSamplingDiameter, 1, 4096) : 48;
        surface.Radius = diameter / 2;
        var size = new TextBox { Text = diameter.ToString("0.##"), Width = 62, VerticalContentAlignment = VerticalAlignment.Center };
        NumericWheel.Enable(size, 1, 4096, value =>
        {
            preferences.FillSamplingDiameter = value;
            surface.Radius = value / 2;
        });
        tools.Children.Add(size);
        tools.Children.Add(Button("Sample all", () => surface.Reset(true)));
        tools.Children.Add(Button("Clear sampling", () => surface.Reset(false)));
        tools.Children.Add(Button("Invert sampling", surface.Invert, "Ctrl+I"));
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.I || Keyboard.Modifiers != ModifierKeys.Control || !surface.IsEnabled) return;
            e.Handled = true;
            surface.Invert();
        };

        var footer = new DockPanel { Margin = new Thickness(0, 7, 0, 0), LastChildFill = true };
        DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(footer);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4) };
        DockPanel.SetDock(cancel, System.Windows.Controls.Dock.Right);
        footer.Children.Add(cancel);
        var apply = new Button { Content = "Generate Fill Layer", Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4) };
        DockPanel.SetDock(apply, System.Windows.Controls.Dock.Right);
        footer.Children.Add(apply);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(status);
        var viewport = new ScrollViewer { Content = surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(viewport);
        var zoom = new ComboBox { ItemsSource = new[] { "Fit", "2× fit", "4× fit", "8× fit" }, SelectedIndex = 0,
            Margin = new Thickness(4, 0, 0, 0), ToolTip = "Sampling canvas zoom" };
        tools.Children.Add(zoom);
        void ResizeSurface()
        {
            if (viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return;
            double fit = Math.Min(Math.Max(1, viewport.ActualWidth - 20) / document.Width, Math.Max(1, viewport.ActualHeight - 20) / document.Height);
            double factor = fit * Math.Pow(2, zoom.SelectedIndex);
            surface.Width = document.Width * factor;
            surface.Height = document.Height * factor;
        }
        zoom.SelectionChanged += (_, _) => ResizeSurface();
        viewport.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) ResizeSurface();
        };

        bool generating = false;
        dialog.Closing += (_, args) => args.Cancel = generating;
        apply.Click += async (_, _) =>
        {
            if (generating) return;
            if (!ReferenceEquals(document, session.Engine.Current))
            {
                status.Text = "The document changed. Reopen Content-Aware Fill to use the current selection.";
                return;
            }
            generating = true;
            controls.IsEnabled = surface.IsEnabled = apply.IsEnabled = cancel.IsEnabled = false;
            status.Text = "Generating fill…";
            string id = Format.Id();
            StartWaiting();
            try
            {
                await session.Engine.ExecuteContentAwareFillAsync(surface.Settings(id), layerId);
                session.ActiveId = id;
                generating = false;
                dialog.DialogResult = true;
            }
            catch (Exception error)
            {
                status.Text = error.Message;
            }
            finally
            {
                StopWaiting();
                generating = false;
                controls.IsEnabled = surface.IsEnabled = apply.IsEnabled = cancel.IsEnabled = true;
            }
        };
        if (dialog.ShowDialog() == true) Refresh();
    }

    private sealed class FillSamplingCanvas : FrameworkElement
    {
        private readonly Document document;
        private readonly BitmapSource image;
        private readonly WriteableBitmap tint;
        private readonly byte[] mask, overlay, thumbnailRgba;
        private readonly List<SamplingStroke> strokes = [];
        private readonly double scale;
        private List<PointD>? points;
        private bool sampleAll, strokeInclude;
        private double strokeRadius;
        private Point? brush;
        public bool Include { get; set; } = true;
        public double Radius { get; set; } = 24;
        private bool IncludesSamples => Include && (Keyboard.Modifiers & ModifierKeys.Alt) == 0;

        public void RefreshCursor() =>
            Cursor = IncludesSamples ? ToolCursors.SelectionAdd : ToolCursors.SelectionSubtract;

        public FillSamplingCanvas(Document document, Pixels thumbnail)
        {
            this.document = document;
            image = EditorCanvas.Bitmap(thumbnail);
            thumbnailRgba = thumbnail.Rgba;
            scale = Math.Min(1, 1200d / Math.Max(document.Width, document.Height));
            tint = new WriteableBitmap(thumbnail.Width, thumbnail.Height, 96, 96, PixelFormats.Bgra32, null);
            mask = new byte[thumbnail.Width * thumbnail.Height];
            overlay = new byte[mask.Length * 4];
            RefreshCursor();
            Reset(false);
        }

        public static Pixels Thumbnail(Pixels source)
        {
            double scale = Math.Min(1, 1200d / Math.Max(source.Width, source.Height));
            int w = Math.Max(1, (int)Math.Ceiling(source.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(source.Height * scale));
            var pixels = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(source.Width - 1, (int)((x + .5) / scale)), sy = Math.Min(source.Height - 1, (int)((y + .5) / scale));
                source.Rgba.AsSpan((sy * source.Width + sx) * 4, 4).CopyTo(pixels.AsSpan((y * w + x) * 4));
            }
            return new(w, h, pixels);
        }

        public void Reset(bool all)
        {
            sampleAll = all;
            strokes.Clear();
            Array.Fill(mask, all ? (byte)1 : (byte)0);
            PaintOverlay(new Area(0, 0, tint.PixelWidth, tint.PixelHeight));
        }

        public void Invert()
        {
            if (IsMouseCaptured) ReleaseMouseCapture();
            sampleAll = !sampleAll;
            // Complement every brush assignment so the inverted mask replays at full resolution.
            for (int i = 0; i < strokes.Count; i++) strokes[i] = strokes[i] with { Include = !strokes[i].Include };
            for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(1 - mask[i]);
            PaintOverlay(new Area(0, 0, tint.PixelWidth, tint.PixelHeight));
        }

        public ContentAwareFillArgs Settings(string id) => new(id, sampleAll, strokes.ToArray());

        private Rect ImageBounds
        {
            get
            {
                double zoom = Math.Min(ActualWidth / document.Width, ActualHeight / document.Height);
                return new((ActualWidth - document.Width * zoom) / 2, (ActualHeight - document.Height * zoom) / 2,
                    document.Width * zoom, document.Height * zoom);
            }
        }

        protected override void OnRender(DrawingContext drawing)
        {
            drawing.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
            var area = ImageBounds;
            drawing.DrawImage(image, area);
            drawing.DrawImage(tint, area);
            if (brush is { } point && area.Contains(point))
            {
                double radius = Radius * area.Width / document.Width;
                drawing.DrawEllipse(null, new Pen(Brushes.Black, 3), point, radius, radius);
                drawing.DrawEllipse(null, new Pen(Brushes.White, 1), point, radius, radius);
            }
        }

        private PointD DocumentPoint(Point point)
        {
            var area = ImageBounds;
            return new(Math.Clamp((point.X - area.Left) * document.Width / area.Width, 0, document.Width),
                Math.Clamp((point.Y - area.Top) * document.Height / area.Height, 0, document.Height));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (!ImageBounds.Contains(e.GetPosition(this))) return;
            points = [];
            strokeRadius = Radius;
            strokeInclude = IncludesSamples;
            CaptureMouse();
            Sample(DocumentPoint(e.GetPosition(this)));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            brush = e.GetPosition(this);
            RefreshCursor();
            if (points is not null) Sample(DocumentPoint(brush.Value));
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e) { brush = null; InvalidateVisual(); }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (points is not null) Sample(DocumentPoint(e.GetPosition(this)));
            ReleaseMouseCapture();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            RecordStroke();
            points = null;
            base.OnLostMouseCapture(e);
        }

        private void RecordStroke()
        {
            if (points is { Count: > 0 } recorded)
                strokes.Add(new(recorded.ToArray(), strokeRadius, strokeInclude));
        }

        private void Sample(PointD point)
        {
            var current = points!;
            if (current.Count > 0 && current[^1] == point) return;
            var start = current.Count == 0 ? point : current[^1];
            bool include = IncludesSamples;
            if (strokeInclude != include)
            {
                RecordStroke();
                points = current = [start];
                strokeInclude = include;
            }
            current.Add(point);
            PaintOverlay(ContentAwareFill.PaintSampling(mask, tint.PixelWidth, tint.PixelHeight, start, point, strokeRadius, strokeInclude, scale));
        }

        private void PaintOverlay(Area area)
        {
            if (area.Width <= 0 || area.Height <= 0) return;
            int width = tint.PixelWidth;
            for (int y = area.Y; y < area.Y + area.Height; y++)
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                int pixel = y * width + x, i = pixel * 4;
                bool target = document.Selected(Math.Min(document.Width - 1, (int)((x + .5) / scale)),
                    Math.Min(document.Height - 1, (int)((y + .5) / scale)));
                overlay[i] = 35;
                overlay[i + 1] = target ? (byte)50 : (byte)235;
                overlay[i + 2] = target ? (byte)255 : (byte)20;
                overlay[i + 3] = target ? (byte)120 : mask[pixel] != 0 && thumbnailRgba[i + 3] != 0 ? (byte)93 : (byte)0;
            }
            tint.WritePixels(new Int32Rect(area.X, area.Y, area.Width, area.Height), overlay, width * 4, area.X, area.Y);
            InvalidateVisual();
        }
    }
}
