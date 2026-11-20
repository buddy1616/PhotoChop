using System.Windows;
using System.Windows.Controls;

namespace PhotoChop;

public partial class MainWindow
{
    private static AngleDial LinkAngleDial(TextBox box, bool signed = false)
    {
        var dial = new AngleDial { Width = 52, Height = 52, Margin = new Thickness(0, 0, 6, 0) };
        void Update()
        {
            if (double.TryParse(box.Text, out double angle) && double.IsFinite(angle))
                dial.Angle = (angle % 360 + 360) % 360;
        }
        Update();
        box.TextChanged += (_, _) => Update();
        dial.Changed += angle => box.Text = (signed && angle > 180 ? angle - 360 : angle).ToString("0.###");
        return dial;
    }

    private static DockPanel AngleRow(TextBox box, bool signed = false, CheckBox? shared = null)
    {
        var row = new DockPanel();
        var dial = LinkAngleDial(box, signed);
        DockPanel.SetDock(dial, System.Windows.Controls.Dock.Left);
        row.Children.Add(dial);
        if (shared is not null)
        {
            shared.VerticalAlignment = VerticalAlignment.Center;
            shared.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(shared, System.Windows.Controls.Dock.Left);
            row.Children.Add(shared);
        }
        box.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(box);
        return row;
    }
}
