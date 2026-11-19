using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PhotoChop;

internal sealed class AnchorPicker : UniformGrid
{
    public int X { get; private set; } = 1;
    public int Y { get; private set; } = 1;

    public AnchorPicker()
    {
        Rows = Columns = 3;
        Width = 114;
        Height = 105;
        HorizontalAlignment = HorizontalAlignment.Left;
        string[] symbols = ["↖", "↑", "↗", "←", "•", "→", "↙", "↓", "↘"];
        string[] names = ["Top left", "Top center", "Top right", "Middle left", "Center", "Middle right", "Bottom left", "Bottom center", "Bottom right"];
        for (int i = 0; i < symbols.Length; i++)
        {
            int index = i;
            var anchor = new RadioButton { Content = symbols[i], IsChecked = i == 4,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = names[i], FontSize = 15 };
            AutomationProperties.SetName(anchor, names[i]);
            anchor.Checked += (_, _) => (X, Y) = (index % 3, index / 3);
            Children.Add(anchor);
        }
    }
}
