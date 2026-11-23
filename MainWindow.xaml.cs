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
        public string? InitialTitle { get; set; }
        public string? MaskOnly { get; set; }
        public string? VectorMaskTarget { get; set; }
        public bool VectorMaskOnly { get; set; }
        public string? ChannelView { get; set; }
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
        public string? RenderBackend { get; set; }
        public System.Windows.Media.Imaging.BitmapSource? Frame { get; set; }
        public Document? Rendered { get; set; }
        public bool NeedsFit { get; set; } = true;
        public bool KeepPaintFrame { get; set; }
        public System.Windows.Media.Imaging.BitmapSource? CommittedTonalFrame { get; set; }
        public Document? TonalDocument { get; set; }
        public string Title => (Engine.Path is { } path ? Path.GetFileName(path) : InitialTitle ?? "Untitled") + (Engine.Dirty ? " *" : "");
    }

    private sealed class Preferences
    {
        public WindowPosition? WindowPosition { get; set; }
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public List<string> Recent { get; set; } = [];
        public string ArtworkForeground { get; set; } = "#FF50A7FF";
        public string ArtworkBackground { get; set; } = "#FFFFFFFF";
        public string MaskForeground { get; set; } = "#FF000000";
        public string MaskBackground { get; set; } = "#FFFFFFFF";
        public bool CenterHorizontal { get; set; }
        public bool CenterVertical { get; set; }
        public double UiScale { get; set; } = 1;
        public bool ExperimentalGpu { get; set; }
        public bool ShowPixelGrid { get; set; } = true;
        public int CheckerSize { get; set; } = 8;
        public bool ShowRulers { get; set; } = true;
        public bool ShowGuides { get; set; } = true;
        public string GuideColor { get; set; } = "#FF48CDE9";
        public RulerUnit RulerUnit { get; set; }
        public int ToolCatalogVersion { get; set; }
        public string[]? ToolOrder { get; set; }
        public string[]? Tools { get; set; }
    }

    private readonly SkiaRenderer renderer = new();
    private WindowsGpuCompositor? gpu;
    private readonly List<Session> sessions = [];
    private readonly EditorCanvas emptyCanvas = new();
    private EditorCanvas canvas => active?.View ?? emptyCanvas;
    private LayoutDocumentPane? documentPane;
    private readonly StackPanel toolColors = new() { Margin = new Thickness(6) };
    private readonly StackPanel toolStack = new();
    private const string PalettePane = "Color Palette";
    private static IEnumerable<string> ToolDockTitles => new[] { "Tools", PalettePane };
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
    private bool UsesMaskPalette => tool == "Masker" || active?.MaskTarget is { } id && ActiveLayer()?.Id == id;
    private string foreground
    {
        get => UsesMaskPalette ? preferences.MaskForeground : preferences.ArtworkForeground;
        set { if (UsesMaskPalette) preferences.MaskForeground = value; else preferences.ArtworkForeground = value; }
    }
    private string background
    {
        get => UsesMaskPalette ? preferences.MaskBackground : preferences.ArtworkBackground;
        set { if (UsesMaskPalette) preferences.MaskBackground = value; else preferences.ArtworkBackground = value; }
    }
    private double brushSize = 24, hardness = .8, brushOpacity = 1;
    private enum ShapeToolKind { Rectangle, Ellipse, Line, Triangle, Polygon }
    private ShapeToolKind shapeKind;
    private int polygonSides = 5;
    private Point? lastCursor;
    private static readonly string[] ToolNames = ["Selection", "Lasso", "Magic Wand", "Pen", "Path Select", "Text", "Shape", "Eyedropper", "Transform", "Masker", "Move", "Brush", "Eraser", "Burn", "Dodge", "Bucket", "Chop"];

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, _) => RefreshMarqueeCursor();
        PreviewKeyUp += (_, _) => RefreshMarqueeCursor();
        InputManager.Current.PreProcessInput += WorkspaceShortcuts;
        Closed += (_, _) => InputManager.Current.PreProcessInput -= WorkspaceShortcuts;
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
        Closed += (_, _) => gpu?.Dispose();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (WindowPlacement.TryConstrain(preferences.WindowPosition, out var placement))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (_, _) => WindowPlacement.Restore(this, placement);
        }
        if (preferences is { ToolCatalogVersion: < 4, Tools: { } configured })
        {
            var added = preferences.ToolCatalogVersion switch
            {
                0 => new[] { "Masker", "Bucket", "Lasso", "Magic Wand", "Pen", "Path Select" },
                1 => new[] { "Magic Wand", "Pen", "Path Select" },
                2 => new[] { "Pen", "Path Select" },
                _ => Array.Empty<string>()
            };
            preferences.Tools = configured.Concat(added).Concat(new[] { "Burn", "Dodge" }).Distinct().ToArray();
        }
        preferences.ToolCatalogVersion = 4;
        BuildMenus();
        BuildPanels();
        ApplyControlScale();
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
        var path = Path.Combine(settingsFolder, "layout-v4.xml");
        if (!File.Exists(path)) path = Path.Combine(settingsFolder, "layout-v3.xml");
        if (!File.Exists(path)) return;
        try
        {
            var serializer = new XmlLayoutSerializer(Dock);
            bool toolsRestored = false;
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                if (args.Model.ContentId == "Tools" || args.Model.ContentId == "More Tools" ||
                    ToolSections.Any(section => section.Title == args.Model.ContentId))
                {
                    if (toolsRestored) { args.Cancel = true; return; }
                    toolsRestored = true;
                    args.Model.ContentId = "Tools";
                    args.Model.Title = "Tools";
                }
                args.Content = args.Model.ContentId == "workspace" ? emptyCanvas
                    : panelContent.GetValueOrDefault(args.Model.ContentId ?? "");
                args.Cancel = args.Content is null;
            };
            serializer.Deserialize(path);
            workspace = Dock.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(item => item.ContentId == "workspace");
            documentPane = workspace?.Parent as LayoutDocumentPane ?? Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
            if (documentPane is null) ResetLayout();
            else
            {
                var existing = Dock.Layout.Descendents().OfType<LayoutAnchorable>().Select(panel => panel.ContentId).ToHashSet();
                var missing = ToolDockTitles.Where(title => !existing.Contains(title)).ToArray();
                if (missing.Length > 0)
                {
                    var newPanes = CreateToolDocks(missing);
                    var pantry = Dock.Layout.Descendents().OfType<LayoutAnchorable>()
                        .FirstOrDefault(panel => panel.ContentId == "Tools")?.FindParent<LayoutAnchorablePaneGroup>();
                    if (pantry is null) Dock.Layout.RootPanel.Children.Insert(0, newPanes);
                    else foreach (var pane in newPanes.Children.ToArray())
                    {
                        newPanes.Children.Remove(pane);
                        pantry.Children.Add(pane);
                    }
                }
            }
            if (!Dock.Layout.Descendents().OfType<LayoutAnchorable>().Any(panel => panel.ContentId == "Info") &&
                Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(panel => panel.ContentId == "Navigation")?.Parent is LayoutAnchorablePane navigationPane)
                navigationPane.Children.Add(NewPanel("Info"));
            if (Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(panel => panel.ContentId == "Navigation")?.Parent is LayoutAnchorablePane typographyPane)
                foreach (string title in new[] { "Character", "Paragraph" })
                    if (!Dock.Layout.Descendents().OfType<LayoutAnchorable>().Any(panel => panel.ContentId == title))
                        typographyPane.Children.Add(NewPanel(title));
            if (Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(panel => panel.ContentId == "Tools")?.FindParent<LayoutAnchorablePaneGroup>() is { } toolbox)
            {
                toolbox.DockMinWidth = 54;
                if (toolbox.DockWidth.IsAbsolute && toolbox.DockWidth.Value > 86) toolbox.DockWidth = new GridLength(86);
            }
            if (!Dock.Layout.Descendents().OfType<LayoutAnchorable>().Any(panel => panel.ContentId == "Channels") &&
                Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(panel => panel.ContentId == "Layers")?.Parent is LayoutAnchorablePane rightPane)
                rightPane.Children.Insert(rightPane.Children.IndexOf(rightPane.Children.First(panel => panel.ContentId == "Layers")) + 1, NewPanel("Channels"));
        }
        catch (Exception error) { ResetLayout(); Say($"Workspace reset. {error.Message}"); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (committingTonalStroke) { e.Cancel = true; return; }
        FinishTextEdit();
        foreach (var item in sessions.ToArray())
        {
            if (CanClose(item)) continue;
            e.Cancel = true;
            return;
        }
        try
        {
            Directory.CreateDirectory(settingsFolder);
            preferences.WindowPosition = WindowPlacement.Capture(this) ?? preferences.WindowPosition;
            SavePreferences();
            new XmlLayoutSerializer(Dock).Serialize(Path.Combine(settingsFolder, "layout-v4.xml"));
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "CHOP settings"); }
    }

    private void SavePreferences()
    {
        Directory.CreateDirectory(settingsFolder);
        var settings = Path.Combine(settingsFolder, "settings.json");
        var temporary = Path.Combine(settingsFolder, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, Format.Json));
            File.Move(temporary, settings, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
        foreach (string title in new[] { "Layers", "Channels", "History", "Command Log" }) right.Children.Add(NewPanel(title));
        var upper = new LayoutAnchorablePane { DockHeight = new GridLength(1, GridUnitType.Star) };
        upper.Children.Add(NewPanel("Navigation"));
        upper.Children.Add(NewPanel("Info"));
        upper.Children.Add(NewPanel("Character"));
        upper.Children.Add(NewPanel("Paragraph"));
        upper.SelectedContentIndex = 0;
        var sidebar = new LayoutAnchorablePaneGroup { Orientation = Orientation.Vertical, DockWidth = new GridLength(400),
            Children = { upper, right } };
        Dock.Layout = new LayoutRoot { RootPanel = new LayoutPanel { Orientation = Orientation.Horizontal, Children = { CreateToolDocks(), center, sidebar } } };
        right.SelectedContentIndex = 0;
        if (active?.Tab is { } selected) selected.IsActive = true;
    }

    private LayoutAnchorablePaneGroup CreateToolDocks(string[]? titles = null)
    {
        var group = new LayoutAnchorablePaneGroup { Orientation = Orientation.Vertical, DockWidth = new GridLength(86), DockMinWidth = 54 };
        foreach (string title in titles ?? ToolDockTitles)
        {
            var pane = new LayoutAnchorablePane();
            pane.Children.Add(NewPanel(title));
            group.Children.Add(pane);
        }
        return group;
    }

    private LayoutAnchorable NewPanel(string title) => new()
    {
        Title = title, ContentId = title, Content = panelContent[title], CanClose = false, CanHide = true
    };

    private void TogglePanel(string title)
    {
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

    private MenuItem? fileMenu;
    private void FileAccessKey(object sender, PreProcessInputEventArgs args)
    {
        if (blockRenderInput || committingTonalStroke || fileMenu is not { } menu || args.StagingItem.Input is not KeyEventArgs e ||
            e.RoutedEvent != Keyboard.PreviewKeyDownEvent || e.IsUp) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Alt &&
            (IsActive || Application.Current.Windows.OfType<AvalonDock.Controls.LayoutFloatingWindowControl>().Any(host => host.IsActive)))
        {
            e.Handled = true;
            if (!IsActive) Activate();
            menu.Focus();
            menu.IsSubmenuOpen = true;
            return;
        }

        if (!menu.IsSubmenuOpen || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Alt)) return;
        Action? action = key switch
        {
            Key.A when active is not null => () => Save(active, true),
            Key.N => NewDocument,
            Key.O => OpenDocument,
            Key.S when active is not null => () => Save(active),
            Key.C when active is not null => () => CloseSession(active),
            _ => null
        };
        if (action is null) return;
        e.Handled = true;
        menu.IsSubmenuOpen = false;
        Dispatcher.BeginInvoke(action);
    }

    private void BuildMenus()
    {
        var file = fileMenu = Menu("_File");
        InputManager.Current.PreProcessInput += FileAccessKey;
        Closed += (_, _) => InputManager.Current.PreProcessInput -= FileAccessKey;
        Item(file, "_New…", NewDocument, "Ctrl+N");
        Item(file, "_Open…", OpenDocument, "Ctrl+O");
        recentMenu = new MenuItem { Header = "Recent files" };
        file.Items.Add(recentMenu);
        UpdateRecent();
        var save = Item(file, "_Save", () => { FinishTextEdit(); Save(active); }, "Ctrl+S");
        var saveAs = Item(file, "Save _As…", () => { FinishTextEdit(); Save(active, true); }, "Ctrl+Shift+S");
        var import = new MenuItem { Header = "_Import" };
        file.Items.Add(import);
        Item(import, "Image…", ImportImage);
        Item(import, "Photoshop PSD…", () => ImportPsd(false));
        Item(import, "Photoshop PSD (Flattened)…", () => ImportPsd(true));
        var export = new MenuItem { Header = "_Export" };
        file.Items.Add(export);
        Item(export, "PNG", ExportPng, "Ctrl+Shift+E");
        Item(export, "JPEG", () => ExportImage("JPEG", ".jpg", "*.jpg;*.jpeg", ImageExportFormat.Jpeg));
        Item(export, "WebP", () => ExportImage("WebP", ".webp", "*.webp", ImageExportFormat.WebP));
        Item(export, "SVG", () => ExportImage("SVG", ".svg", "*.svg", ImageExportFormat.Svg));
        var close = Item(file, "_Close document", () => CloseSession(active), "Ctrl+W");
        save.IsEnabled = saveAs.IsEnabled = close.IsEnabled = false;
        file.SubmenuOpened += (_, _) => save.IsEnabled = saveAs.IsEnabled = close.IsEnabled = active?.Engine.Current is not null;

        var edit = Menu("Edit");
        Item(edit, "Document Settings…", EditDocumentSettings);
        Item(edit, "Undo", () => Run("history.undo", 0), "Ctrl+Z");
        Item(edit, "Redo", () => Run("history.redo", 0), "Ctrl+Y");
        Item(edit, "Copy Pixels", CopyPixels, "Ctrl+C");
        Item(edit, "Paste Pixels", PastePixels, "Ctrl+V");
        Item(edit, "Configure tools…", ConfigureTools);
        var transformItem = new MenuItem { Header = "Transform" };
        edit.Items.Add(transformItem);
        Item(transformItem, "Free Transform", () => BeginTransform(SpatialKind.FreeTransform), "Ctrl+T");
        Item(transformItem, "Rotate", () => BeginTransform(SpatialKind.Rotate));
        Item(transformItem, "Skew", () => BeginTransform(SpatialKind.Skew));
        Item(transformItem, "Scale…", ScaleLayer);
        Item(transformItem, "Flip Horizontal", () => RunLayer("layer.flip", new FlipArgs(true)));
        Item(transformItem, "Flip Vertical", () => RunLayer("layer.flip", new FlipArgs(false)));
        var fill = Item(edit, "Fill…", FillPixels);
        var stroke = Item(edit, "Stroke…", StrokeSelection);
        var textItem = Item(edit, "Edit text…", EditText);
        var shapeItem = Item(edit, "Edit shape…", EditShape);
        edit.Items.Add(new Separator());
        Item(edit, "Preferences…", EditPreferences);
        edit.SubmenuOpened += (_, _) =>
        {
            var selected = ActiveLayer();
            transformItem.IsEnabled = selected is not null && selected.Kind != LayerKind.Effect;
            fill.IsEnabled = selected?.Kind == LayerKind.Raster;
            stroke.IsEnabled = fill.IsEnabled;
            textItem.IsEnabled = selected?.Kind == LayerKind.Text;
            shapeItem.IsEnabled = selected?.Kind is LayerKind.Rectangle or LayerKind.Ellipse;
        };
        var image = Menu("Image");
        var adjustments = new MenuItem { Header = "Adjustments" };
        image.Items.Add(adjustments);
        foreach (var kind in new[] { EffectKind.BrightnessContrast, EffectKind.HueSaturation, EffectKind.Opacity, EffectKind.Burn, EffectKind.Dodge })
            Item(adjustments, EffectName(kind), () => AddEffect(kind));

        AddColorModeMenu(image);
        Item(image, "Canvas Size…", () => ResizeDocument(canvasOnly: true));
        Item(image, "Image Size…", () => ResizeDocument(canvasOnly: false));
        var orientation = new MenuItem { Header = "Image Rotation" };
        image.Items.Add(orientation);
        foreach (var (label, action) in new[]
        {
            ("Flip Horizontal", OrientationAction.FlipHorizontal), ("Flip Vertical", OrientationAction.FlipVertical),
            ("90° Clockwise", OrientationAction.Clockwise), ("90° Counterclockwise", OrientationAction.Counterclockwise),
            ("180°", OrientationAction.HalfTurn), ("Reset Rotation", OrientationAction.ResetRotation), ("Reset Flip", OrientationAction.ResetFlip), ("Bake Rotation / Flip…", OrientationAction.Bake)
        })
            Item(orientation, label, () =>
            {
                if (action == OrientationAction.Bake && !Confirm("Bake the image orientation into raster pixels?")) return;
                if (Run("document.orient", action)) Fit();
            });
        Item(image, "Chop tool", () => SetTool("Chop"));
        Item(image, "Chop to selection", () => { if (active?.Engine.Current?.Selection is { } area) Run("document.chop", area); });
        Item(image, "Flatten image…", Flatten);
        Item(image, "Default document size…", SetDefaults);

        var layer = Menu("Layer");
        var addLayerItem = new MenuItem { Header = "Add" };
        layer.Items.Add(addLayerItem);
        Item(addLayerItem, "Vector Mask", AddVectorMask);
        var newLayer = new MenuItem { Header = "New" };
        layer.Items.Add(newLayer);
        Item(newLayer, "Raster Layer", () => CreateLayer(LayerKind.Raster));
        Item(newLayer, "Fill Layer…", () => CreateLayer(LayerKind.Fill));
        Item(newLayer, "Effect Layer", () => CreateLayer(LayerKind.Effect));
        Item(newLayer, "Text Layer", () =>
        {
            if (active?.Engine.Current is not { } doc) return;
            var point = canvas.FromView(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
            CreateLayer(LayerKind.Text, new PointD(Math.Clamp(point.X, 0, doc.Width - 1), Math.Clamp(point.Y, 0, doc.Height - 1)));
        });
        Item(newLayer, "Chop Layer…", CreateChopLayer);
        Item(newLayer, "Path", () => CreateNewPath());
        Item(layer, "Duplicate selected layer", Duplicate);
        Item(layer, "Rename selected layer…", Rename);
        Item(layer, "Delete selected layer", DeleteLayer);
        Item(layer, "Group selected layers", Group, "Ctrl+G");
        Item(layer, "Ungroup", () => RunLayer("layer.ungroup", 0));
        Item(layer, "Move into group…", MoveToGroup);
        Item(layer, "Merge Down", MergeDown, "Ctrl+E");
        Item(layer, "Merge selected layers…", Merge);
        Item(layer, "Position and Rotation…", EditTransform);
        var select = Menu("Select");
        Item(select, "Rectangular selection", () => SetTool("Selection"), "M");
        Item(select, "Convert Selection to Paths", ConvertSelectionToPaths);
        var modify = new MenuItem { Header = "Modify" };
        select.Items.Add(modify);
        Item(modify, "Expand…", () => ModifySelection(expand: true));
        Item(modify, "Contract…", () => ModifySelection(expand: false));
        Item(select, "Color Range…", SelectColorRange);
        Item(select, "Deselect", () => Run<Area?>("document.selection", null), "Ctrl+D");

        var filter = Menu("Filter");
        Item(filter, "Bit Crush…", BitCrush);
        Item(filter, EffectName(EffectKind.Grayscale), () => AddEffect(EffectKind.Grayscale));
        var blur = new MenuItem { Header = "Blur" };
        filter.Items.Add(blur);
        foreach (var kind in new[] { EffectKind.GaussianBlur, EffectKind.MotionBlur })
            Item(blur, EffectName(kind), () => AddEffect(kind));
        var view = Menu("View");
        UpdateGpuPreference();
        BuildGuideMenus(view);
        Item(view, "Zoom in", () => ZoomBy(1.25), "Ctrl++");
        Item(view, "Zoom out", () => ZoomBy(.8), "Ctrl+-");
        Item(view, "Fit canvas", Fit, "Ctrl+0");
        Item(view, "Actual pixels", ActualPixels, "Ctrl+1");
        var window = Menu("Window");
        foreach (string title in new[] { "Tools", PalettePane, "Navigation", "Info", "Character", "Paragraph", "Layers", "Channels", "History", "Command Log" })
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
        var (width, height) = ClipboardDimensions() ?? (preferences.Width, preferences.Height);
        var settings = NewDocumentDialog.Show(this, width, height, background);
        if (settings is null) return;
        Guard(() =>
        {
            var session = NewSession();
            session.Engine.Execute(Command.Of("document.create", settings));
            session.ActiveId = settings.Id + "-layer";
            AddSession(session);
        });
    }

    private static (int Width, int Height)? ClipboardDimensions()
    {
        try
        {
            var bitmap = ClipboardBitmap(Clipboard.GetDataObject());
            return bitmap is null ? null : (bitmap.PixelWidth, bitmap.PixelHeight);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or IOException or FormatException or NotSupportedException)
        { return null; }
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

    private async void OpenPath(string path)
    {
        var existing = sessions.FirstOrDefault(item => string.Equals(item.Engine.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { SelectTab(existing); return; }
        var session = NewSession();
        try
        {
            await WhileLoading(() => session.Engine.Execute(Command.Of("document.open", new OpenArgs(path, File.ReadAllBytes(path)))));
            existing = sessions.FirstOrDefault(item => string.Equals(item.Engine.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { SelectTab(existing); return; }
            Remember(path);
            AddSession(session);
        }
        catch (Exception error)
        {
            MessageBox.Show(EditorOwner, error.Message, "Could not open document", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private int waitingLoads;
    private Cursor? cursorBeforeLoading;

    private void StartWaiting()
    {
        if (waitingLoads++ != 0) return;
        cursorBeforeLoading = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
    }

    private void StopWaiting()
    {
        if (--waitingLoads == 0) Mouse.OverrideCursor = cursorBeforeLoading;
    }

    private async Task WhileLoading(Action load)
    {
        StartWaiting();
        try { await Task.Run(load); }
        finally { StopWaiting(); }
    }

    private Session NewSession()
    {
        var session = new Session(new CommandEngine(renderer));
        session.Engine.Executed += entry => { if (session.Recording) session.Log.Add(entry); };
        return session;
    }

    private void AddSession(Session session)
    {
        if (session.Engine.Current is { } document)
            session.Collapsed.UnionWith(document.AllLayers().Where(layer => layer.Kind == LayerKind.Group && layer.InitiallyCollapsed).Select(layer => layer.Id));
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
        FinishTextEdit();
        CancelTransform();
        CancelPathEditing();
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
        if (ReferenceEquals(session, editingText?.Session)) FinishTextEdit();
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

    private void ImportImage()
    {
        var picker = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp" };
        if (picker.ShowDialog(this) != true) return;
        Guard(() =>
        {
            var input = new ImageImportArgs(Format.Id(), Path.GetFileNameWithoutExtension(picker.FileName),
                Data: File.ReadAllBytes(picker.FileName));
            if (active is null)
            {
                var session = NewSession();
                session.Engine.Execute(Command.Of("document.importImage", input with { DocumentId = Format.Id() }));
                session.ActiveId = input.Id;
                AddSession(session);
            }
            else
            {
                string? parent = DestinationGroup();
                if (parent is not null) active.Collapsed.Remove(parent);
                if (!Run("layer.importImage", input with { Parent = parent }, selectId: input.Id)) return;
            }
            Say($"Imported {Path.GetFileName(picker.FileName)}");
        });
    }

    private void ExportPng() => ExportImage("PNG", ".png", "*.png", ImageExportFormat.Png);

    private void ExportImage(string label, string extension, string pattern, ImageExportFormat format)
    {
        if (active is null) return;
        var picker = new SaveFileDialog { Filter = $"{label} images ({pattern})|{pattern}", DefaultExt = extension, AddExtension = true };
        if (picker.ShowDialog(EditorOwner) == true) Run("document.export", new ExportArgs(picker.FileName, format));
    }

    private bool CanClose(Session session)
    {
        if (ReferenceEquals(session, editingText?.Session)) FinishTextEdit();
        if (!session.Engine.Dirty) return true;
        var result = MessageBox.Show(this, $"Save changes to {session.Title.TrimEnd(' ', '*')}?", "CHOP",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || (result == MessageBoxResult.Yes && Save(session));
    }

    private void CloseSession(Session? session)
    {
        if (ReferenceEquals(session, editingText?.Session)) FinishTextEdit();
        session?.Tab?.Close();
    }

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

    private bool Run<T>(string action, T args, string? target = null, string? selectId = null, Action<Session>? beforeRefresh = null)
    {
        if (active is not { } session) return false;
        if (interactiveTransform is not null) CancelTransform();
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
            if (session.VectorMaskTarget != session.ActiveId || session.Engine.Current?.AllLayers()
                .Any(layer => layer.Id == session.VectorMaskTarget && layer.VectorMask is not null) != true)
                session.VectorMaskTarget = null;
        });
        if (ok) beforeRefresh?.Invoke(session);
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

    private void Refresh(bool updateCanvas = true)
    {
        syncing = true;
        try
        {
            foreach (var item in sessions)
                if (item.Tab is { } tab) tab.Title = item.Title;
            RefreshLayers();
            RefreshChannels();
            history.ItemsSource = active?.Engine.History.Select(item => item.Name +
                (!item.Enabled ? " (disabled)" : item.Error is { } error ? $" (skipped: {error})" : "")).ToArray();
            history.SelectedIndex = active?.Engine.Cursor ?? -1;
            if (history.SelectedItem is { } step) history.ScrollIntoView(step);
            RefreshProperties();
            RefreshLog();
        }
        finally { syncing = false; }
        RefreshTools();
        RefreshPathOverlay();
        UpdateGuideMenu();
        if (updateCanvas) UpdateCanvas();
        RefreshNavigation();
        Say();
    }

    private static bool SameComposite(Document target, Document prior) =>
        ReferenceEquals(target.Layers, prior.Layers) && target.Floating == prior.Floating &&
        target.Width == prior.Width && target.Height == prior.Height && target.Color == prior.Color &&
        ReferenceEquals(target.IccProfile, prior.IccProfile);

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
        if (active.TonalDocument is { } toned && !SameComposite(doc, toned))
        {
            active.TonalDocument = null;
            active.CommittedTonalFrame = null;
            if (tonalDrag is null) canvas.TonalFrame = null;
        }
        canvas.ImageWidth = doc.Width;
        canvas.ImageHeight = doc.Height;
        if (preview is null && editingText?.Layer is null && paintGesture is null && !active.KeepPaintFrame) canvas.Frame = active.Frame is { } priorFrame &&
            priorFrame.PixelWidth == doc.Width && priorFrame.PixelHeight == doc.Height ? priorFrame : null;
        canvas.Guides = preferences.ShowGuides ? doc.Guides : [];
        var visibleSelection = interactiveTransform is not null ? preview ?? doc : doc;
        canvas.Selection = visibleSelection.Selection;
        canvas.SelectionMask = visibleSelection.SelectionMask;
        if (doc.Selection is { } selected && selectionDragDelta is { } delta)
            canvas.Selection = selected with { X = selected.X + (int)delta.X, Y = selected.Y + (int)delta.Y };
        canvas.Zoom = active.Zoom;
        canvas.Offset = active.Offset;
        UpdateScrollBars(active);
        canvas.InvalidateVisual();
        if (rendering) return;
        rendering = true;
        bool modalRender = active.CommittedTonalFrame is null && canvas.MovingText is null && gesture is null && preview is null && editingText is null && paintGesture is null && effectDialog is not { IsVisible: true };
        blockRenderInput = modalRender;
        if (modalRender)
        {
            StartWaiting();
            RenderStatus.Visibility = Visibility.Visible;
        }
        try
        {
            while (active is { } session && session.Engine.Current is { } snapshot)
            {
                int version = renderVersion;
                var textEdit = editingText;
                var textBackground = ReferenceEquals(textEdit?.Session, session) ? textEdit?.BackgroundFor(snapshot) : null;
                var target = preview ?? textBackground ?? snapshot;
                var move = gesture == "Move" ? moveOriginal : null;
                var shift = selectionDragDelta;
                string? targetId = moveId;
                var selectedLayer = ActiveLayer();
                var channel = (channelList.SelectedItem as ChannelRow) is { Key: not "composite" } component ? component : null;
                var paintCommand = paintPreview;
                var paintId = paintGesture;
                bool isPreview = preview is not null || textBackground is not null || shift is not null || paintCommand is not null;
                var maskLayer = session.MaskOnly is { } maskId
                    ? target.AllLayers().FirstOrDefault(layer => layer.Id == maskId && layer.Mask is not null) : null;
                var vectorLayer = session.VectorMaskOnly && session.VectorMaskTarget is { } vectorId
                    ? target.AllLayers().FirstOrDefault(layer => layer.Id == vectorId && layer.VectorMask is not null) : null;
                bool cached = !isPreview && maskLayer is null && vectorLayer is null && channel is null && session.Rendered is { } prior &&
                    SameComposite(target, prior);
                bool gpuRequested = preferences.ExperimentalGpu;
                var accelerator = !cached && paintCommand is null && gpuRequested && channel is null && maskLayer is null && vectorLayer is null &&
                    SkiaRenderer.CanCompositeOnGpu(target) ? gpu ??= new WindowsGpuCompositor() : null;
                var frame = cached ? (Pixels: session.Composite!, Frame: session.Frame!, Backend: session.RenderBackend) : await Task.Run(() =>
                {
                    if (shift is { } offset && snapshot.Selection is { } area && targetId is not null &&
                        snapshot.AllLayers().FirstOrDefault(layer => layer.Id == targetId) is { Pixels: not null } layer)
                    {
                        target = session.Engine.Preview(snapshot, Command.Of("layer.moveSelection", offset, targetId));
                    }
                    if (paintCommand is not null)
                    {
                        target = session.Engine.Preview(snapshot, paintCommand);
                        if (session.MaskOnly is { } editMask)
                            maskLayer = target.AllLayers().FirstOrDefault(layer => layer.Id == editMask && layer.Mask is not null);
                    }
                    var (accelerated, backend) = accelerator?.TryComposite(renderer, target) ??
                        (null, gpuRequested ? "CPU · preview not eligible for GPU" : "CPU");
                    var pixels = accelerated ?? (vectorLayer is not null ? renderer.VectorMaskView(target, vectorLayer) :
                        maskLayer is not null ? renderer.MaskView(target, maskLayer) : RenderChannel(target, channel, selectedLayer));
                    return (Pixels: pixels, Frame: EditorCanvas.Bitmap(pixels), Backend: (string?)backend);
                });
                bool currentStroke = paintId is not null && ReferenceEquals(paintId, paintGesture);
                bool currentMove = move is not null && gesture == "Move" &&
                    ReferenceEquals(move, moveOriginal) && targetId == moveId;
                if (version != renderVersion && !currentStroke && !currentMove ||
                    gpuRequested != preferences.ExperimentalGpu || !ReferenceEquals(active, session) ||
                    !ReferenceEquals(snapshot, session.Engine.Current) || !ReferenceEquals(textEdit, editingText)) continue;
                if (!isPreview && maskLayer is null && vectorLayer is null && channel is null)
                {
                    session.Rendered = snapshot;
                    session.RenderBackend = frame.Backend;
                    session.Composite = frame.Pixels;
                    session.Frame = frame.Frame;
                }
                if (frame.Backend is { } backend) GpuStatus.Content = backend;
                session.View.Frame = frame.Frame;
                if (!isPreview)
                {
                    if (!committingTonalStroke && paintGesture is null)
                    {
                        session.KeepPaintFrame = false;
                        session.View.TonalFrame = null;
                        session.CommittedTonalFrame = null;
                        session.TonalDocument = null;
                        session.View.PendingPaint.Clear();
                    }
                    if (gesture != "Move" && session.View.MovingText is not null) ClearTextMovePreview(session);
                }
                session.View.InvalidateVisual();
                if (paintCommand is null && !currentMove) RefreshNavigation();
                if (version == renderVersion) break;
            }
        }
        catch (Exception error)
        {
            canvas.PendingPaint.Clear();
            canvas.InvalidateVisual();
            Say(error.Message);
        }
        finally
        {
            if (modalRender) StopWaiting();
            blockRenderInput = false;
            rendering = false;
            RenderStatus.Visibility = Visibility.Collapsed;
        }
    }

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
            if (ranges is not null) NumericWheel.Enable(box, ranges[boxes.Count].Min, ranges[boxes.Count].Max);
            else if (double.TryParse(value, out _)) NumericWheel.Enable(box);
            var caption = new Label { Content = label, Target = box, Padding = new Thickness(0) };
            panel.Children.Add(caption);
            bool rotary = label.Contains("Angle", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("Rotation", StringComparison.OrdinalIgnoreCase);
            if (rotary) panel.Children.Add(AngleRow(box, signed: true));
            else if (ranges is not null)
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
                    var dial = LinkAngleDial(box, signed: true);
                    DockPanel.SetDock(dial, System.Windows.Controls.Dock.Left);
                    row.Children.Add(dial);
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
