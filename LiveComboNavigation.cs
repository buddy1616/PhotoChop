using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoChop;

internal static class LiveComboNavigation
{
    public static void Enable(UIElement scope) => scope.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(ApplyArrow));

    public static bool IsNavigating => FindCombo(Keyboard.FocusedElement) is { IsDropDownOpen: true };

    private static ComboBox? FindCombo(object? source) => source switch
    {
        ComboBox combo => combo,
        ComboBoxItem item => ItemsControl.ItemsControlFromItemContainer(item) as ComboBox,
        _ => null
    };

    private static void ApplyArrow(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.Key is not (Key.Up or Key.Down)) return;
        var combo = sender as ComboBox ?? FindCombo(e.OriginalSource);
        if (combo is null && e.OriginalSource is ComboBoxItem item)
            combo = ItemsControl.ItemsControlFromItemContainer(item) as ComboBox;
        combo ??= e.OriginalSource as ComboBox;
        if (combo is not { IsDropDownOpen: true, IsEditable: false } || combo.Items.Count == 0) return;
        int next = Math.Clamp(combo.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, combo.Items.Count - 1);
        combo.SelectedIndex = next;
        if (combo.ItemContainerGenerator.ContainerFromIndex(next) is ComboBoxItem selected) selected.BringIntoView();
        e.Handled = true;
    }
}
