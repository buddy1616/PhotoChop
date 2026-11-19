using System.Windows;
using System.Windows.Input;

namespace PhotoChop;

internal static class ToolCursors
{
    public static Cursor Eraser { get; } = Load("eraser");
    public static Cursor Masker { get; } = Load("masker");
    public static Cursor Bucket { get; } = Load("bucket");
    public static Cursor Eyedropper { get; } = Load("eyedropper");
    public static Cursor Rotate { get; } = Load("rotate");
    public static Cursor SelectionAdd { get; } = Load("marquee-add");
    public static Cursor SelectionSubtract { get; } = Load("marquee-subtract");

    private static Cursor Load(string name)
    {
        var resource = Application.GetResourceStream(new Uri($"Cursors/{name}.cur", UriKind.Relative));
        using var stream = resource!.Stream;
        return new Cursor(stream);
    }
}
