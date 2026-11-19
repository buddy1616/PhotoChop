using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Chop.Core;
using Chop.Rendering;
using Microsoft.Win32;

namespace PhotoChop;

public partial class MainWindow : Window
{
    private sealed class Session(CommandEngine engine)
    {
        public DocumentViewport Viewport { get; } = new();
        public EditorCanvas View => Viewport.Canvas;
        public LayoutDocument? Tab { get; set; }
        public string? MaskOnly { get; set; }
        public CommandEngine Engine { get; } = engine;
        public List<LogEntry> Log { get; } = [];
        public int LogStart { get; set; }
        public bool Recording { get; set; } = true;
        public bool HistoryEdited { get; set; }
        public string? ActiveId { get; set; }
        public HashSet<string> Collapsed { get; } = [];
        public string? MaskTarget { get; set; }
        public double Zoom { get; set; } = 1;
        public Point Offset { get; set; }
        public Pixels? Composite { get; set; }
        public System.Windows.Media.Imaging.BitmapSource? Frame { get; set; }
        public Document? Rendered { get; set; }
        public bool NeedsFit { get; set; } = true;
        public string Title => (Engine.Path is { } path ? Path.GetFileName(path) : "Untitled") + (Engine.Dirty ? " *" : "");
    }

    private sealed class Preferences
    {
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public List<string> Recent { get; set; } = [];
        public bool ShowRulers { get; set; } = true;
        public RulerUnit RulerUnit { get; set; }
        public string[]? Tools { get; set; }
    }

    private readonly SkiaRenderer renderer = new();
    private readonly List<Session> sessions = [];
    private readonly EditorCanvas emptyCanvas = new();
    private EditorCanvas canvas => active?.View ?? emptyCanvas;
    private LayoutDocumentPane? documentPane;
    private readonly StackPanel toolColors = new() { Margin = new Thickness(6) };
    private readonly StackPanel toolsPanel = new() { Margin = new Thickness(6) };
    private readonly DockPanel layersPanel = new();
    private readonly ListBox layers = new() { SelectionMode = System.Windows.Controls.SelectionMode.Extended, AllowDrop = true };
    private readonly ListBox history = new();
    private readonly StackPanel properties = new() { Margin = new Thickness(8) };
    private readonly DockPanel logPanel = new();
    private readonly ListBox commandLog = new() { SelectionMode = System.Windows.Controls.SelectionMode.Extended };
    private readonly Dictionary<string, FrameworkElement> panelContent = [];
    private readonly string settingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHOP");
    private readonly Preferences preferences;
    private LayoutDocument? workspace;
    private MenuItem? recentMenu;
    private bool syncing;
    private bool rendering;
    private int renderVersion;
    private Session? active;
    private string tool = "Move";
    private string foreground = "#FF50A7FF";
    private string background = "#FFFFFFFF";
    private double brushSize = 24, hardness = .8, brushOpacity = 1;
    private LayerKind shapeKind = LayerKind.Rectangle;
    private Point? lastCursor;
    private static readonly string[] ToolNames = ["Move", "Brush", "Eraser", "Selection", "Chop", "Text", "Shape", "Eyedropper", "Transform"];

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, _) => RefreshMarqueeCursor();
        PreviewKeyUp += (_, _) => RefreshMarqueeCursor();
        Dock.Theme = new AvalonDock.Themes.Vs2013DarkTheme();
        Dock.AllowMixedOrientation = true;
        Dock.LayoutFloatingWindowControlCreated += (_, e) =>
        {
            if (e.LayoutFloatingWindowControl is AvalonDock.Controls.LayoutDocumentFloatingWindowControl host)
            {
                host.AllowMinimize = true;
                host.ResizeMode = ResizeMode.CanResize;
            }
        };
        preferences = LoadPreferences();
        BuildMenus();
        BuildPanels();
        ResetLayout();
        Dock.ActiveContentChanged += (_, _) =>
        {
            if (sessions.FirstOrDefault(session => ReferenceEquals(Dock.ActiveContent, session.Viewport)) is { } session)
                Activate(session);
        };
        SetTool("Move");
        Refresh();
    }

    private Preferences LoadPreferences()
    {
        try
        {
            return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(settingsFolder, "settings.json")), Format.Json) ?? new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(settingsFolder, "layout-v3.xml");
        if (!File.Exists(path)) return;
        try
        {
            var serializer = new XmlLayoutSerializer(Dock);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                args.Content = args.Model.ContentId == "workspace" ? emptyCanvas
                    : panelContent.GetValueOrDefault(args.Model.ContentId ?? "");
                args.Cancel = args.Content is null;
            };
            serializer.Deserialize(path);
            workspace = Dock.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(item => item.ContentId == "workspace");
            documentPane = workspace?.Parent as LayoutDocumentPane ?? Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
            if (documentPane is null) ResetLayout();
        }
        catch (Exception error) { ResetLayout(); Say($"Workspace reset. {error.Message}"); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        foreach (var item in sessions.ToArray())
        {
            if (CanClose(item)) continue;
            e.Cancel = true;
            return;
        }
        try
        {
            Directory.CreateDirectory(settingsFolder);
            new XmlLayoutSerializer(Dock).Serialize(Path.Combine(settingsFolder, "layout-v3.xml"));
            File.WriteAllText(Path.Combine(settingsFolder, "settings.json"), JsonSerializer.Serialize(preferences, Format.Json));
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "CHOP settings"); }
    }

    private void ResetLayout()
    {
        if (Dock.Layout is { } old)
            foreach (var content in old.Descendents().OfType<LayoutContent>().ToArray()) content.Content = null;
        workspace = new LayoutDocument { Title = "Documents", ContentId = "workspace", Content = emptyCanvas, CanClose = false, CanFloat = false };
        var center = new LayoutDocumentPane();
        documentPane = center;
        if (sessions.Count == 0) center.Children.Add(workspace);
        foreach (var session in sessions)
        {
            session.Tab!.Parent?.RemoveChild(session.Tab);
            session.Tab.Content = session.Viewport;
            center.Children.Add(session.Tab);
        }
        var right = new LayoutAnchorablePane { DockHeight = new GridLength(3, GridUnitType.Star) };
        foreach (string title in new[] { "Layers", "History", "Command Log" }) right.Children.Add(NewPanel(title));
        var upper = new LayoutAnchorablePane { DockHeight = new GridLength(1, GridUnitType.Star) };
        upper.Children.Add(NewPanel("Navigation"));
        var sidebar = new LayoutAnchorablePaneGroup { Orientation = Orientation.Vertical, DockWidth = new GridLength(400),
            Children = { upper, right } };
        Dock.Layout = new LayoutRoot { RootPanel = new LayoutPanel { Orientation = Orientation.Horizontal, Children = { center, sidebar } } };
        right.SelectedContentIndex = 0;
        if (active?.Tab is { } selected) selected.IsActive = true;
    }

    private LayoutAnchorable NewPanel(string title) => new()
    {
        Title = title, ContentId = title, Content = panelContent[title], CanClose = false, CanHide = true
    };

    private void TogglePanel(string title)
    {
        if (title == "Tools")
        {
            ToolPantry.Visibility = ToolPantry.IsVisible ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        var panel = Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(item => item.ContentId == title);
        if (panel is null)
        {
            panel = NewPanel(title);
            panel.AddToLayout(Dock, AnchorableShowStrategy.Right);
        }
        else if (!panel.IsHidden && panel.IsActive)
        {
            panel.Hide();
            return;
        }
        if (panel.IsHidden) panel.Show();
        panel.IsActive = true;
    }

    private void BuildMenus()
    {
        var file = Menu("File");
        Item(file, "New…", NewDocument, "Ctrl+N");
        Item(file, "Open…", OpenDocument, "Ctrl+O");
        recentMenu = new MenuItem { Header = "Recent files" };
        file.Items.Add(recentMenu);
        UpdateRecent();
        Item(file, "Import PNG…", ImportPng);
        Item(file, "Save", () => Save(active), "Ctrl+S");
        Item(file, "Save As…", () => Save(active, true), "Ctrl+Shift+S");
        Item(file, "Export PNG…", ExportPng, "Ctrl+Shift+E");
        Item(file, "Close document", () => CloseSession(active), "Ctrl+W");

        var edit = Menu("Edit");
        Item(edit, "Undo", () => Run("history.undo", 0), "Ctrl+Z");
        Item(edit, "Redo", () => Run("history.redo", 0), "Ctrl+Y");
        Item(edit, "Configure tools…", ConfigureTools);
        var transformItem = new MenuItem { Header = "Transform" };
        edit.Items.Add(transformItem);
        Item(transformItem, "Scale…", ScaleLayer);
        Item(transformItem, "Flip Horizontal", () => RunLayer("layer.flip", new FlipArgs(true)));
        Item(transformItem, "Flip Vertical", () => RunLayer("layer.flip", new FlipArgs(false)));
        var fill = Item(transformItem, "Fill…", FillPixels);
        var stroke = Item(transformItem, "Stroke…", StrokeSelection);
        var textItem = Item(edit, "Edit text…", EditText);
        var shapeItem = Item(edit, "Edit shape…", EditShape);
        edit.SubmenuOpened += (_, _) =>
        {
            var selected = ActiveLayer();
            transformItem.IsEnabled = selected is not null;
            fill.IsEnabled = selected?.Kind == LayerKind.Raster;
            stroke.IsEnabled = fill.IsEnabled && active?.Engine.Current?.Selection is not null;
            textItem.IsEnabled = selected?.Kind == LayerKind.Text;
            shapeItem.IsEnabled = selected?.Kind is LayerKind.Rectangle or LayerKind.Ellipse;
        };
        var image = Menu("Image");
        Item(image, "Canvas Size…", () => ResizeDocument(canvasOnly: true));
        Item(image, "Image Size…", () => ResizeDocument(canvasOnly: false));
        Item(image, "Chop tool", () => SetTool("Chop"));
        Item(image, "Chop to selection", () => { if (active?.Engine.Current?.Selection is { } area) Run("document.chop", area); });
        Item(image, "Flatten image…", Flatten);
        Item(image, "Default document size…", SetDefaults);

        var adjustments = new MenuItem { Header = "Adjustments" };
        image.Items.Add(adjustments);
        foreach (var kind in new[] { EffectKind.BrightnessContrast, EffectKind.HueSaturation, EffectKind.Opacity })
            Item(adjustments, EffectName(kind), () => AddEffect(kind));

        var layer = Menu("Layer");
        Item(layer, "New raster layer", () => CreateLayer(LayerKind.Raster));
        Item(layer, "New fill layer…", () => CreateLayer(LayerKind.Fill));
        Item(layer, "Duplicate selected layer", Duplicate);
        Item(layer, "Rename selected layer…", Rename);
        Item(layer, "Delete selected layer", DeleteLayer);
        Item(layer, "Group selected layers", Group, "Ctrl+G");
        Item(layer, "Ungroup", () => RunLayer("layer.ungroup", 0));
        Item(layer, "Move into group…", MoveToGroup);
        Item(layer, "Merge selected layers…", Merge);
        Item(layer, "Position and Rotation…", EditTransform);
        var select = Menu("Select");
        Item(select, "Rectangular selection", () => SetTool("Selection"), "M");
        var modify = new MenuItem { Header = "Modify" };
        select.Items.Add(modify);
        Item(modify, "Expand…", () => ModifySelection(expand: true));
        Item(modify, "Contract…", () => ModifySelection(expand: false));
        Item(select, "Color Range…", SelectColorRange);
        Item(select, "Deselect", () => Run<Area?>("document.selection", null), "Ctrl+D");

        var filter = Menu("Filter");
        Item(filter, EffectName(EffectKind.Grayscale), () => AddEffect(EffectKind.Grayscale));
        var blur = new MenuItem { Header = "Blur" };
        filter.Items.Add(blur);
        foreach (var kind in new[] { EffectKind.GaussianBlur, EffectKind.MotionBlur })
            Item(blur, EffectName(kind), () => AddEffect(kind));
        var view = Menu("View");
        rulersMenu = Item(view, "Hide Rulers", ToggleRulers);
        UpdateRulers();
        Item(view, "Zoom in", () => ZoomBy(1.25), "Ctrl++");
        Item(view, "Zoom out", () => ZoomBy(.8), "Ctrl+-");
        Item(view, "Fit canvas", Fit, "Ctrl+0");
        Item(view, "Actual pixels", ActualPixels, "Ctrl+1");
        var window = Menu("Window");
        foreach (string title in new[] { "Tools", "Navigation", "Layers", "History", "Command Log" })
            Item(window, title, () => TogglePanel(title));
        Item(window, "Split document to the right", SplitDocument);
        Item(window, "Float document", () => FloatDocument(WindowState.Normal));
        Item(window, "Maximize document window", () => FloatDocument(WindowState.Maximized));
        Item(window, "Minimize document window", () => FloatDocument(WindowState.Minimized));
        var openDocuments = new MenuItem { Header = "Open documents" };
        window.Items.Add(openDocuments);
        window.SubmenuOpened += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, window)) return;
            openDocuments.Items.Clear();
            openDocuments.IsEnabled = sessions.Count > 0;
            foreach (var session in sessions) Item(openDocuments, session.Title, () => SelectTab(session));
        };
        Item(window, "Reset workspace", ResetLayout);
        var help = Menu("Help");
        Item(help, "Editing controls", () => MessageBox.Show(this,
            "Use the wheel to zoom. Pan with the middle mouse button or Space + drag.\nDrag layers to reorder. Drop onto the center of a group to move into it.\nDrag inside a selection to move its outline with the Selection tool. Use Ctrl or Shift to select multiple layers or log entries.", "CHOP"));
    }

    private MenuItem Menu(string title)
    {
        var item = new MenuItem { Header = title };
        Menus.Items.Add(item);
        return item;
    }

    private static MenuItem Item(ItemsControl menu, string title, Action action, string? shortcut = null)
    {
        var item = new MenuItem { Header = title, InputGestureText = shortcut ?? "" };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    private void NewDocument()
    {
        var settings = NewDocumentDialog.Show(this, preferences.Width, preferences.Height, background);
        if (settings is null) return;
        Guard(() =>
        {
            var session = NewSession();
            session.Engine.Execute(Command.Of("document.create", settings));
            session.ActiveId = settings.Id + "-layer";
            AddSession(session);
        });
    }

    private void SetDefaults()
    {
        var values = Form("Default document size", ("Width", preferences.Width.ToString()), ("Height", preferences.Height.ToString()));
        if (values is null) return;
        Guard(() =>
        {
            int width = Int(values[0]), height = Int(values[1]);
            Format.Dimensions(width, height);
            (preferences.Width, preferences.Height) = (width, height);
        });
    }

    private void OpenDocument()
    {
        var picker = new OpenFileDialog { Filter = "CHOP documents (*.chop)|*.chop" };
        if (picker.ShowDialog(this) == true) OpenPath(picker.FileName);
    }

    private void OpenPath(string path) => Guard(() =>
    {
        var existing = sessions.FirstOrDefault(item => string.Equals(item.Engine.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { SelectTab(existing); return; }
        var session = NewSession();
        session.Engine.Execute(Command.Of("document.open", new OpenArgs(path, File.ReadAllBytes(path))));
        Remember(path);
        AddSession(session);
    });

    private Session NewSession()
    {
        var session = new Session(new CommandEngine(renderer));
        session.Engine.Executed += entry => { if (session.Recording) session.Log.Add(entry); };
        return session;
    }

    private void AddSession(Session session)
    {
        var pane = active?.Tab?.Parent as LayoutDocumentPane;
        if (pane is null || pane.FindParent<LayoutDocumentFloatingWindow>() is not null)
            pane = Dock.Layout.Descendents().OfType<LayoutDocumentPane>()
                .FirstOrDefault(item => item.FindParent<LayoutDocumentFloatingWindow>() is null) ?? documentPane;
        if (pane is null) throw new InvalidOperationException("No document pane is available.");

        sessions.Add(session);
        session.Tab = new LayoutDocument { Title = session.Title, Content = session.Viewport,
            ContentId = "document-" + Format.Id(), CanClose = true, CanFloat = true };
        session.Tab.Closing += (_, e) => e.Cancel = !CanClose(session);
        session.Tab.Closed += (_, _) => RemoveSession(session);
        session.View.SizeChanged += (_, _) =>
        {
            if (ReferenceEquals(active, session) && session.NeedsFit && canvas.ActualWidth > 0) Fit();
            else UpdateScrollBars(session);
        };
        session.Viewport.Scrolled += offset =>
        {
            Activate(session);
            session.Offset = offset;
            UpdateView();
        };
        session.View.PreviewMouseDown += (_, _) => Activate(session);
        session.View.GotKeyboardFocus += (_, _) => Activate(session);
        session.View.PreviewKeyDown += (_, e) =>
        {
            if (Window.GetWindow(session.View) != this) OnKey(session.View, e);
        };
        HookCanvas(session.View);
        HookRulers(session);
        session.View.Cursor = ToolCursor(tool);
        pane.Children.Add(session.Tab);
        if (workspace?.Parent is { } parent) parent.RemoveChild(workspace);
        SelectTab(session);
    }

    private void SelectTab(Session session)
    {
        if (session.Tab is null) return;
        session.Tab.IsSelected = true;
        session.Tab.IsActive = true;
        if (Window.GetWindow(session.View) is { } host && host != this)
        {
            if (host.WindowState == WindowState.Minimized) host.WindowState = WindowState.Normal;
            host.Activate();
        }
        Activate(session);
    }

    private void Activate(Session? session)
    {
        if (ReferenceEquals(active, session)) return;
        CancelGesture();
        active = session;
        lastCursor = null;
        Refresh();
        if (active is { NeedsFit: true } && canvas.ActualWidth > 0) Fit();
    }

    private void SplitDocument()
    {
        if (active?.Tab is not { Parent: LayoutDocumentPane pane } tab ||
            pane.FindParent<LayoutDocumentFloatingWindow>() is not null) return;
        var neighbor = new LayoutDocumentPane();
        if (pane.Parent is LayoutDocumentPaneGroup group && group.Orientation == Orientation.Horizontal)
            group.Children.Insert(group.Children.IndexOf(pane) + 1, neighbor);
        else if (pane.Parent is ILayoutContainer parent)
        {
            var split = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };
            parent.ReplaceChild(pane, split);
            split.Children.Add(pane);
            split.Children.Add(neighbor);
        }
        else return;
        neighbor.Children.Add(tab);
        tab.IsActive = true;
    }

    private void FloatDocument(WindowState state)
    {
        if (active is not { Tab: { } tab } session) return;
        if (!tab.IsFloating) tab.Float();
        if (state != WindowState.Normal)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (Window.GetWindow(session.View) is { } host && host != this) host.WindowState = state;
            }));
    }

    private bool Save(Session? session, bool saveAs = false)
    {
        if (session is null) return false;
        string? path = saveAs ? null : session.Engine.Path;
        if (path is null)
        {
            var picker = new SaveFileDialog { Filter = "CHOP documents (*.chop)|*.chop", DefaultExt = ".chop", AddExtension = true };
            if (picker.ShowDialog(this) != true) return false;
            path = picker.FileName;
        }
        return Guard(() =>
        {
            if (sessions.Any(item => !ReferenceEquals(item, session) &&
                string.Equals(item.Engine.Path, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("That document is already open in another tab.");
            session.Engine.Execute(Command.Of("document.save", new PathArgs(path)));
            Remember(path);
            Refresh();
        });
    }

    private void ImportPng()
    {
        var picker = new OpenFileDialog { Filter = "PNG images (*.png)|*.png" };
        if (picker.ShowDialog(this) != true) return;
        Guard(() =>
        {
            byte[] source = File.ReadAllBytes(picker.FileName);
            var name = Path.GetFileNameWithoutExtension(picker.FileName);
            string id = Format.Id();
            if (active is null)
            {
                var image = renderer.DecodePng(source);
                var session = NewSession();
                session.Engine.Execute(Command.Of("document.create", new NewArgs(Format.Id(), image.Width, image.Height)));
                session.Engine.Execute(Command.Of("layer.importPng", new ImportArgs(id, name, source)));
                session.ActiveId = id;
                AddSession(session);
            }
            else
            {
                string? parent = DestinationGroup();
                if (parent is not null) active.Collapsed.Remove(parent);
                if (!Run("layer.importPng", new ImportArgs(id, name, source, parent), selectId: id)) return;
            }
            Say($"Imported {name}.png");
        });
    }

    private void ExportPng()
    {
        if (active is null) return;
        var picker = new SaveFileDialog { Filter = "PNG images (*.png)|*.png", DefaultExt = ".png", AddExtension = true };
        if (picker.ShowDialog(this) == true) Run("document.export", new PathArgs(picker.FileName));
    }

    private bool CanClose(Session session)
    {
        if (!session.Engine.Dirty) return true;
        var result = MessageBox.Show(this, $"Save changes to {session.Title.TrimEnd(' ', '*')}?", "CHOP",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || (result == MessageBoxResult.Yes && Save(session));
    }

    private void CloseSession(Session? session) => session?.Tab?.Close();

    private void RemoveSession(Session session)
    {
        if (!sessions.Remove(session)) return;
        if (ReferenceEquals(colorRangeSession, session)) colorRangeWindow?.Close();
        if (sessions.Count == 0 && workspace?.Parent is null)
        {
            var pane = Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
            if (pane is not null && workspace is not null) pane.Children.Add(workspace);
        }
        if (ReferenceEquals(active, session))
            Activate(sessions.FirstOrDefault(item => item.Tab?.IsActive == true) ?? sessions.FirstOrDefault());
    }

    private void Remember(string path)
    {
        preferences.Recent.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        preferences.Recent.Insert(0, path);
        if (preferences.Recent.Count > 10) preferences.Recent.RemoveAt(10);
        UpdateRecent();
    }

    private void UpdateRecent()
    {
        if (recentMenu is null) return;
        recentMenu.Items.Clear();
        recentMenu.IsEnabled = preferences.Recent.Count != 0;
        foreach (var path in preferences.Recent) Item(recentMenu, path, () => OpenPath(path));
    }

    private bool Run<T>(string action, T args, string? target = null, string? selectId = null)
    {
        if (active is not { } session) return false;
        var ok = Guard(() =>
        {
            session.Engine.Execute(Command.Of(action, args, target));
            if (action == "history.change")
            {
                session.HistoryEdited = true;
                session.LogStart = 0;
            }
            if (session.MaskOnly is { } mask && session.Engine.Current?.AllLayers().Any(layer => layer.Id == mask && layer.Mask is not null) != true)
                session.MaskOnly = null;
            if (selectId is not null) session.ActiveId = selectId;
        });
        Refresh();
        return ok;
    }

    private void RunLayer<T>(string action, T args)
    {
        if (active?.ActiveId is { } id) Run(action, args, id);
    }

    private bool Guard(Action action)
    {
        try { action(); return true; }
        catch (Exception error)
        {
            MessageBox.Show(effectDialog ?? this, error.Message, "CHOP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Refresh()
    {
        syncing = true;
        try
        {
            foreach (var item in sessions)
                if (item.Tab is { } tab) tab.Title = item.Title;
            RefreshLayers();
            history.ItemsSource = active?.Engine.History.Select(item => item.Name +
                (!item.Enabled ? " (disabled)" : item.Error is { } error ? $" (skipped: {error})" : "")).ToArray();
            history.SelectedIndex = active?.Engine.Cursor ?? -1;
            if (history.SelectedItem is { } step) history.ScrollIntoView(step);
            RefreshProperties();
            RefreshLog();
        }
        finally { syncing = false; }
        UpdateCanvas();
        RefreshNavigation();
        Say();
    }

    private async void UpdateCanvas()
    {
        renderVersion++;
        if (active?.Engine.Current is not { } doc)
        {
            canvas.Frame = null;
            canvas.ImageWidth = canvas.ImageHeight = 0;
            canvas.InvalidateVisual();
            return;
        }
        canvas.ImageWidth = doc.Width;
        canvas.ImageHeight = doc.Height;
        if (preview is null) canvas.Frame = active.Frame is { } priorFrame &&
            priorFrame.PixelWidth == doc.Width && priorFrame.PixelHeight == doc.Height ? priorFrame : null;
        canvas.Guides = doc.Guides;
        canvas.Selection = doc.Selection;
        canvas.SelectionMask = doc.SelectionMask;
        if (doc.Selection is { } selected && selectionDragDelta is { } delta)
            canvas.Selection = selected with { X = selected.X + (int)delta.X, Y = selected.Y + (int)delta.Y };
        canvas.Zoom = active.Zoom;
        canvas.Offset = active.Offset;
        UpdateScrollBars(active);
        canvas.InvalidateVisual();
        if (rendering) return;
        rendering = true;
        try
        {
            while (active is { } session && session.Engine.Current is { } snapshot)
            {
                int version = renderVersion;
                var target = preview ?? snapshot;
                var shift = selectionDragDelta;
                string? targetId = moveId;
                bool isPreview = preview is not null || shift is not null;
                var maskLayer = session.MaskOnly is { } maskId
                    ? target.AllLayers().FirstOrDefault(layer => layer.Id == maskId && layer.Mask is not null) : null;
                bool cached = !isPreview && maskLayer is null && session.Rendered is { } prior &&
                    ReferenceEquals(target.Layers, prior.Layers) && target.Width == prior.Width && target.Height == prior.Height;
                var frame = cached ? (Pixels: session.Composite!, Frame: session.Frame!) : await Task.Run(() =>
                {
                    if (shift is { } offset && snapshot.Selection is { } area && targetId is not null &&
                        snapshot.AllLayers().FirstOrDefault(layer => layer.Id == targetId) is { Pixels: not null } layer)
                    {
                        var result = renderer.EditSelection(snapshot, layer, area, offset);
                        target = target with { Layers = PreviewPixels(target.Layers, targetId, result) };
                    }
                    var pixels = maskLayer is null ? renderer.Composite(target) : renderer.MaskView(target, maskLayer);
                    return (Pixels: pixels, Frame: EditorCanvas.Bitmap(pixels));
                });
                if (!ReferenceEquals(active, session) || !ReferenceEquals(snapshot, session.Engine.Current) ||
                    (isPreview && preview is null && selectionDragDelta is null)) continue;
                if (!isPreview && maskLayer is null)
                {
                    session.Rendered = snapshot;
                    session.Composite = frame.Pixels;
                    session.Frame = frame.Frame;
                }
                session.View.Frame = frame.Frame;
                if (!isPreview) session.View.PendingPaint.Clear();
                session.View.InvalidateVisual();
                RefreshNavigation();
                if (version == renderVersion) break;
            }
        }
        catch (Exception error)
        {
            canvas.PendingPaint.Clear();
            canvas.InvalidateVisual();
            Say(error.Message);
        }
        finally { rendering = false; }
    }

    private static Layer[] PreviewPixels(Layer[] items, string id, RasterEdit edit) => items.Select(layer =>
        layer.Id == id ? layer with { Pixels = edit.Pixels, RasterX = edit.X, RasterY = edit.Y, PixelShifts = [] }
        : layer.Children.Length == 0 ? layer : layer with { Children = PreviewPixels(layer.Children, id, edit) }).ToArray();

    private void Say(string? message = null)
    {
        var doc = active?.Engine.Current;
        Status.Text = message ?? (doc is null ? "Ctrl+N  New document    ·    Ctrl+O  Open document"
            : $"{doc.Width} × {doc.Height} px    ·    {active!.Zoom:P0}    ·    {ActiveLayer()?.Name ?? "No layer"}    ·    " +
                (lastCursor is { } p ? $"X {Math.Floor(p.X)}  Y {Math.Floor(p.Y)}" : "X —  Y —"));
    }

    private static Button Button(string text, Action action, string? tip = null)
    {
        var item = new Button { Content = text, ToolTip = tip, Margin = new Thickness(2), Padding = new Thickness(6, 3, 6, 3) };
        item.Click += (_, _) => action();
        return item;
    }

    private string[]? Form(string title, params (string Label, string Initial)[] fields) => BuildForm(title, fields);

    private string[]? BuildForm(string title, (string Label, string Initial)[] fields, (double Min, double Max)[]? ranges = null, Action<string[]?>? previewChanged = null, int? hueField = null)
    {
        var dialog = new Window { Owner = effectDialog ?? this, Title = title, Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(16) };
        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            var box = new TextBox { Text = value, Margin = new Thickness(0, 3, 0, 8), Padding = new Thickness(4) };
            var caption = new Label { Content = label, Target = box, Padding = new Thickness(0) };
            panel.Children.Add(caption);
            if (ranges is not null)
            {
                var range = ranges[boxes.Count];
                bool isHue = hueField == boxes.Count;
                var row = new DockPanel();
                box.Width = 88;
                DockPanel.SetDock(box, System.Windows.Controls.Dock.Right);
                row.Children.Add(box);
                bool updating = false;
                void Write(double number)
                {
                    if (updating) return;
                    updating = true;
                    box.Text = number.ToString("0.###");
                    updating = false;
                }
                Action<double> updateControl;
                if (isHue)
                {
                    var hue = new HueSpectrum { Height = 22, Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center, Value = (Number(value) + 360) % 360 };
                    hue.ValueChanged += (_, _) => Write(hue.Value > 180 ? hue.Value - 360 : hue.Value);
                    updateControl = number => hue.Value = (number + 360) % 360;
                    row.Children.Add(hue);
                }
                else
                {
                    var slider = new Slider { Minimum = range.Min, Maximum = range.Max, Value = Number(value),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                    slider.ValueChanged += (_, _) => Write(slider.Value);
                    updateControl = number => slider.Value = number;
                    row.Children.Add(slider);
                }
                box.TextChanged += (_, _) =>
                {
                    if (updating || !double.TryParse(box.Text, out var number) || !double.IsFinite(number)) return;
                    updating = true;
                    updateControl(number);
                    updating = false;
                };
                panel.Children.Add(row);
            }
            else panel.Children.Add(box);
            boxes.Add(box);
        }
        if (previewChanged is not null)
        {
            var check = new CheckBox { Content = "Live preview", IsChecked = true, Margin = new Thickness(0, 8, 0, 6) };
            void PreviewValues() => previewChanged(check.IsChecked == true ? boxes.Select(box => box.Text).ToArray() : null);
            check.Click += (_, _) => PreviewValues();
            foreach (var box in boxes) box.TextChanged += (_, _) => { if (check.IsChecked == true) PreviewValues(); };
            panel.Children.Add(check);
            dialog.Loaded += (_, _) => PreviewValues();
            dialog.Closed += (_, _) => previewChanged(null);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = Button("OK", () => dialog.DialogResult = true);
        ok.IsDefault = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(3) });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { if (boxes.FirstOrDefault() is { } first) { first.Focus(); first.SelectAll(); } };
        return dialog.ShowDialog() == true ? boxes.Select(box => box.Text).ToArray() : null;
    }

    private static int Int(string value) => int.Parse(value, System.Globalization.CultureInfo.CurrentCulture);
    private static double Number(string value) => double.Parse(value, System.Globalization.CultureInfo.CurrentCulture);
}
