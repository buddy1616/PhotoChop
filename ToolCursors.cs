using System.Windows;
using System.Windows.Input;

namespace PhotoChop;

internal static class ToolCursors
{
    public static Cursor Eraser { get; } = Load("eraser");
    public static Cursor Eyedropper { get; } = Load("eyedropper");

    private static Cursor Load(string name)
    {
        var resource = Application.GetResourceStream(new Uri($"Cursors/{name}.cur", UriKind.Relative));
        using var stream = resource!.Stream;
        return new Cursor(stream);
    }
}
