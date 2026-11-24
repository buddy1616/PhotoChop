using System.IO;
using System.Windows;
using System.Windows.Media;

namespace PhotoChop;

public partial class MainWindow
{
    private bool importingDroppedFiles;

    private static bool IsRasterFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".ico";

    private static bool IsSupportedDrop(string path) =>
        IsRasterFile(path) || Path.GetExtension(path).ToLowerInvariant() is ".chop" or ".psd";

    private void HookFileDrops(UIElement surface)
    {
        surface.AllowDrop = true;
        surface.PreviewDragEnter -= UpdateFileDrop;
        surface.PreviewDragOver -= UpdateFileDrop;
        surface.PreviewDrop -= DropFiles;
        surface.PreviewDragEnter += UpdateFileDrop;
        surface.PreviewDragOver += UpdateFileDrop;
        surface.PreviewDrop += DropFiles;
    }

    private void UpdateFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Effects = !importingDroppedFiles && (e.AllowedEffects & DragDropEffects.Copy) != 0 && paths.Any(IsSupportedDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        if (importingDroppedFiles || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
        var destination = sessions.FirstOrDefault(session =>
            e.OriginalSource is Visual visual && (ReferenceEquals(visual, session.View) || session.View.IsAncestorOf(visual)));
        importingDroppedFiles = true;
        try
        {
            foreach (string path in paths.Where(IsSupportedDrop))
            {
                if (IsRasterFile(path)) await ImportImagePath(path, destination is not null && sessions.Contains(destination) ? destination : null);
                else if (Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase)) await ImportPsdPath(path);
                else await OpenPath(path);
            }
        }
        finally { importingDroppedFiles = false; }
    }
}
