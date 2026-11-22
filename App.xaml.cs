using System.Windows;
using System.Windows.Media;

namespace PhotoChop;

public partial class App : Application
{
    internal static double ControlScale { get; set; } = 1;

    static App()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ScaleDialog));
    }

    private static void ScaleDialog(object sender, RoutedEventArgs args)
    {
        if (sender.GetType() == typeof(Window) && sender is Window { Content: FrameworkElement content } dialog)
        {
            ScaleControls(content);
            dialog.MaxHeight = Math.Min(dialog.MaxHeight, SystemParameters.WorkArea.Height);
            dialog.MaxWidth = Math.Min(dialog.MaxWidth, SystemParameters.WorkArea.Width);
        }
    }

    internal static void ScaleControls(FrameworkElement element) =>
        element.LayoutTransform = new ScaleTransform(ControlScale, ControlScale);
}
