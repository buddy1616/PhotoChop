using System.Windows;
using System.Windows.Input;

namespace PhotoChop;

internal static class ToolCursors
{
    public static Cursor Clipping { get; } = Load("clipping");
    public static Cursor Eraser { get; } = Load("eraser");
    public static Cursor Masker { get; } = Load("masker");
    public static Cursor Bucket { get; } = Load("bucket");
    public static Cursor Eyedropper { get; } = Load("eyedropper");
    public static Cursor Wand { get; } = Load("wand");
    public static Cursor WandAdd { get; } = Load("wand-add");
    public static Cursor WandSubtract { get; } = Load("wand-subtract");
    public static Cursor Rotate { get; } = Load("rotate");
    public static Cursor SelectionAdd { get; } = Load("marquee-add");
    public static Cursor SelectionSubtract { get; } = Load("marquee-subtract");

    private static readonly Dictionary<string, System.Windows.Media.ImageSource> Icons = new();

    public static System.Windows.Media.ImageSource Icon(string name)
    {
        if (!Icons.TryGetValue(name, out var icon))
        {
            icon = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Cursors/{name}.png"));
            Icons.Add(name, icon);
        }
        return icon;
    }

    private static Cursor Load(string name)
    {
        var resource = Application.GetResourceStream(new Uri($"Cursors/{name}.cur", UriKind.Relative));
        using var stream = resource!.Stream;
        return new Cursor(stream);
    }
}
