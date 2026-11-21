using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private MenuItem? rulersMenu, showGuidesMenu, lockGuidesMenu, clearGuidesMenu;
    private sealed record GuideDrag(Session Session, UIElement Source, Guide Guide, bool Existing, int[] SelectionEdges);
    private GuideDrag? guideDrag;
    private const double SnapRadius = 7;

    private void ToggleRulers()
    {
        preferences.ShowRulers = !preferences.ShowRulers;
        UpdateRulers();
        if (active?.NeedsFit == true) Fit();
    }

    private void BuildGuideMenus(MenuItem view)
    {
        var show = new MenuItem { Header = "Show…" };
        view.Items.Add(show);
        rulersMenu = Item(show, "Show Rulers", ToggleRulers, "Ctrl+R");
        rulersMenu.IsCheckable = true;
        showGuidesMenu = Item(show, "Show Guides", ToggleGuides, "Ctrl+;");
        showGuidesMenu.IsCheckable = true;
        var rulers = new MenuItem { Header = "Rulers" };
        view.Items.Add(rulers);
        var horizontal = Item(rulers, "Center Horizontal", () =>
        { preferences.CenterHorizontal = !preferences.CenterHorizontal; UpdateRulers(); });
        var vertical = Item(rulers, "Center Vertical", () =>
        { preferences.CenterVertical = !preferences.CenterVertical; UpdateRulers(); });
        horizontal.IsCheckable = vertical.IsCheckable = true;
        rulers.SubmenuOpened += (_, _) =>
        {
            horizontal.IsChecked = preferences.CenterHorizontal;
            vertical.IsChecked = preferences.CenterVertical;
        };
        var guides = new MenuItem { Header = "Guides" };
        view.Items.Add(guides);
        clearGuidesMenu = Item(guides, "Clear Guides", () => Run("guide.clear", 0));
        lockGuidesMenu = Item(guides, "Lock Guides", () =>
        {
            if (active?.Engine.Current is { } doc) Run("guide.lock", !doc.GuidesLocked);
        });
        lockGuidesMenu.IsCheckable = true;
        MenuItem guideColor = null!;
        guideColor = Item(guides, "Guides Color…", () =>
        {
            if (ColorPickerDialog.Show(EditorOwner, "Guides Color", preferences.GuideColor) is { } color)
            {
                preferences.GuideColor = color;
                guideColor.Icon = StyleFields.ColorChip(color);
                UpdateRulers();
            }
        });
        guideColor.Icon = StyleFields.ColorChip(preferences.GuideColor);
        UpdateRulers();
    }

    private void ToggleGuides()
    {
        preferences.ShowGuides = !preferences.ShowGuides;
        if (!preferences.ShowGuides) CancelGuide();
        UpdateRulers();
    }

    private void UpdateGuideMenu()
    {
        if (rulersMenu is not null) rulersMenu.IsChecked = preferences.ShowRulers;
        if (showGuidesMenu is not null) showGuidesMenu.IsChecked = preferences.ShowGuides;
        if (lockGuidesMenu is not null)
        {
            lockGuidesMenu.IsEnabled = active is not null;
            lockGuidesMenu.IsChecked = active?.Engine.Current?.GuidesLocked == true;
        }
        if (clearGuidesMenu is not null) clearGuidesMenu.IsEnabled = active?.Engine.Current?.Guides.Length > 0;
    }

    private void UpdateRulers()
    {
        UpdateGuideMenu();
        foreach (var session in sessions) UpdateRulers(session);
    }

    private void UpdateRulers(Session session)
    {
        if (session.Engine.Current is not { } doc) return;
        session.Viewport.UpdateRulers(preferences.ShowRulers, preferences.RulerUnit, session.Offset, session.Zoom, doc.PixelsPerInch,
            preferences.CenterHorizontal ? doc.Width / 2d : 0, preferences.CenterVertical ? doc.Height / 2d : 0);
        uint argb = Format.Color(preferences.GuideColor);
        session.View.SetGuideColor(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        session.View.Guides = preferences.ShowGuides ? doc.Guides : [];
        session.View.ShowPixelGrid = preferences.ShowPixelGrid;
        session.View.SetCheckerSize(preferences.CheckerSize);
        session.View.InvalidateVisual();
        session.View.InvalidateVisual();
    }

    private void HookRulers(Session session)
    {
        foreach (var ruler in session.Viewport.Rulers)
        {
            var menu = new ContextMenu();
            ruler.ContextMenu = menu;
            ruler.ContextMenuOpening += (_, _) =>
            {
                menu.Items.Clear();
                foreach (var (unit, title) in new[] { (RulerUnit.Pixels, "Pixels"), (RulerUnit.Inches, "Inches"),
                    (RulerUnit.Millimeters, "Millimeters") })
                {
                    var item = Item(menu, title, () => { preferences.RulerUnit = unit; UpdateRulers(); });
                    item.IsCheckable = true;
                    item.IsChecked = preferences.RulerUnit == unit;
                }
                menu.Items.Add(new Separator());
                Item(menu, "Hide Rulers", () => { if (preferences.ShowRulers) ToggleRulers(); });
            };
            ruler.MouseLeftButtonDown += (_, e) =>
            {
                Activate(session);
                session.View.Focus();
                BeginGuide(session, ruler, new Guide(Format.Id(), ruler.Axis, 0), existing: false);
                TrackGuide(e.GetPosition(session.View));
                e.Handled = true;
            };
            ruler.MouseMove += (_, e) =>
            {
                if (guideDrag?.Source != ruler) return;
                TrackGuide(e.GetPosition(session.View));
                e.Handled = true;
            };
            ruler.MouseLeftButtonUp += (_, e) =>
            {
                if (guideDrag?.Source != ruler) return;
                FinishGuide(e.GetPosition(session.View));
                e.Handled = true;
            };
            ruler.LostMouseCapture += (_, _) => { if (guideDrag?.Source == ruler) CancelGuide(); };
        }
        UpdateRulers(session);
    }

    private void BeginGuide(Session session, UIElement source, Guide guide, bool existing)
    {
        if (!preferences.ShowGuides || session.Engine.Current is not { GuidesLocked: false } doc) return;
        CancelGuide();
        var boundaries = GuideSnap.SelectionEdges(doc);
        var edges = guide.Axis == GuideAxis.Vertical ? boundaries.Vertical : boundaries.Horizontal;
        guideDrag = new GuideDrag(session, source, guide, existing, edges);
        session.View.DraggedGuide = existing ? guide.Id : null;
        source.CaptureMouse();
    }

    private static Cursor GuideCursor(GuideAxis axis) => axis == GuideAxis.Horizontal ? Cursors.SizeNS : Cursors.SizeWE;

    private Guide? HitGuide(Session session, Point position)
    {
        if (!preferences.ShowGuides || session.Engine.Current is not { GuidesLocked: false } doc) return null;
        var point = session.View.FromView(position);
        if (point.X < 0 || point.Y < 0 || point.X > doc.Width || point.Y > doc.Height) return null;
        double Distance(Guide guide) => Math.Abs((guide.Axis == GuideAxis.Vertical ? point.X : point.Y) - guide.Position) * session.Zoom;
        return doc.Guides.Where(guide => Distance(guide) <= SnapRadius).OrderBy(Distance).FirstOrDefault();
    }

    private void TrackGuide(Point view)
    {
        if (guideDrag is not { } drag || drag.Session.Engine.Current is not { } doc) return;
        var target = drag.Session.View;
        var point = target.FromView(view);
        bool vertical = drag.Guide.Axis == GuideAxis.Vertical;
        double value = GuideSnap.Nearest(vertical ? point.X : point.Y, drag.SelectionEdges, SnapRadius / target.Zoom);
        int pos = (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, vertical ? doc.Width : doc.Height);
        target.GhostGuide = drag.Guide with { Position = pos };
        target.Cursor = GuideCursor(drag.Guide.Axis);
        target.InvalidateVisual();
    }

    private void FinishGuide(Point view)
    {
        if (guideDrag is not { } drag) return;
        TrackGuide(view);
        var target = drag.Session.View;
        var ghost = target.GhostGuide;
        var doc = drag.Session.Engine.Current;
        var point = target.FromView(view);
        bool insidePane = view.X >= 0 && view.Y >= 0 && view.X <= target.ActualWidth && view.Y <= target.ActualHeight;
        bool insideAxis = doc is not null && (drag.Guide.Axis == GuideAxis.Vertical
            ? point.X >= 0 && point.X <= doc.Width : point.Y >= 0 && point.Y <= doc.Height);
        CancelGuide();
        if (!ReferenceEquals(active, drag.Session)) return;
        if (insidePane && insideAxis && ghost is not null) Run("guide.set", ghost);
        else if (drag.Existing) Run("guide.remove", drag.Guide.Id);
    }

    private void CancelGuide()
    {
        if (guideDrag is not { } drag) return;
        guideDrag = null;
        drag.Session.View.GhostGuide = null;
        drag.Session.View.DraggedGuide = null;
        drag.Session.View.Cursor = ToolCursor(tool);
        drag.Session.View.InvalidateVisual();
        if (drag.Source.IsMouseCaptured) drag.Source.ReleaseMouseCapture();
    }

    private int? SnapBoundary(double value, GuideAxis axis)
    {
        if (!preferences.ShowGuides || active?.Engine.Current is not { } doc) return null;
        double radius = SnapRadius / canvas.Zoom;
        return doc.Guides.Where(guide => guide.Axis == axis && Math.Abs(guide.Position - value) <= radius)
            .OrderBy(guide => Math.Abs(guide.Position - value)).ThenBy(guide => guide.Position)
            .Select(guide => (int?)guide.Position).FirstOrDefault();
    }

    private Point SnapMarqueePoint(Point point) =>
        new(SnapBoundary(point.X, GuideAxis.Vertical) ?? point.X, SnapBoundary(point.Y, GuideAxis.Horizontal) ?? point.Y);

    private Rect SnapMarqueeEdges(Rect rect)
    {
        double Offset(double near, double far, GuideAxis axis)
        {
            double? first = SnapBoundary(near, axis) - near, second = SnapBoundary(far, axis) - far;
            if (first is null) return second ?? 0;
            return second is null || Math.Abs(first.Value) <= Math.Abs(second.Value) ? first.Value : second.Value;
        }
        rect.Offset(Offset(rect.Left, rect.Right, GuideAxis.Vertical), Offset(rect.Top, rect.Bottom, GuideAxis.Horizontal));
        return rect;
    }

    private double SnapRatioScale(Point start, Vector direction, double width, double height, double scale)
    {
        var end = start + new Vector(direction.X * width * scale, direction.Y * height * scale);
        int? x = SnapBoundary(end.X, GuideAxis.Vertical);
        int? y = SnapBoundary(end.Y, GuideAxis.Horizontal);
        bool useX = x is not null && (y is null || Math.Abs(end.X - x.Value) <= Math.Abs(end.Y - y.Value));
        if (useX) return Math.Max(0, (x!.Value - start.X) / (width * direction.X));
        return y is null ? scale : Math.Max(0, (y.Value - start.Y) / (height * direction.Y));
    }
}
