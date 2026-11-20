using System.IO;
using System.Windows;
using System.Windows.Controls;
using Chop.Core;
using Microsoft.Win32;

namespace PhotoChop;

public partial class MainWindow
{
    private string? ChooseChop(string? source = null)
    {
        var picker = new OpenFileDialog { Filter = "CHOP documents (*.chop)|*.chop", CheckFileExists = true };
        if (!string.IsNullOrWhiteSpace(source)) picker.FileName = source;
        return picker.ShowDialog(effectDialog ?? EditorOwner) == true ? picker.FileName : null;
    }

    private void CreateChopLayer()
    {
        if (active is null || ChooseChop() is not { } path || BakeLinkedFile(path) is not { } snapshot) return;
        string id = Format.Id();
        string? parent = DestinationGroup();
        if (parent is not null) active.Collapsed.Remove(parent);
        Run("layer.createChop", new CreateChopArgs(id, Path.GetFileNameWithoutExtension(path), snapshot, parent), selectId: id);
    }

    private void RebakeChopLayer(Layer layer, bool changeSource)
    {
        string? path = changeSource ? ChooseChop(layer.ChopSource) : layer.ChopSource;
        if (path is not null && BakeLinkedFile(path) is { } snapshot)
            Run("layer.rebakeChop", snapshot, layer.Id);
    }

    private ChopSnapshotArgs? BakeLinkedFile(string file)
    {
        string path = Path.GetFullPath(file);
        if (string.Equals(active?.Engine.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(effectDialog ?? EditorOwner, "Choose another document. A document cannot link to itself.", "Chop Layer");
            return null;
        }

        var dialog = new Window { Owner = effectDialog ?? EditorOwner, Title = "Baking Chop Layer", Width = 365,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new ProgressBar { Height = 14, Margin = new Thickness(0, 12, 0, 0), IsIndeterminate = true });
        dialog.Content = content;
        bool running = true;
        dialog.Closing += (_, e) => e.Cancel = running;
        ChopSnapshotArgs? snapshot = null;
        Exception? failure = null;
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                snapshot = await Task.Run(() =>
                {
                    var document = ProjectFile.Open(path);
                    return new ChopSnapshotArgs(path, renderer.EncodePng(renderer.Composite(document), document.PixelsPerInch));
                });
            }
            catch (Exception error) { failure = error; }
            finally
            {
                running = false;
                dialog.Close();
            }
        };
        dialog.ShowDialog();
        if (failure is not null) MessageBox.Show(effectDialog ?? EditorOwner, failure.Message, "Could not bake Chop Layer");
        return snapshot;
    }

    private void AddChopProperties(Layer layer, Panel panel)
    {
        if (layer.Kind != LayerKind.Chop) return;
        panel.Children.Add(new TextBlock { Text = layer.ChopSource is null ? "Source unavailable" : Path.GetFileName(layer.ChopSource),
            ToolTip = layer.ChopSource, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 2, 2) });
        var actions = new WrapPanel();
        actions.Children.Add(Button("Re-Bake", () => RebakeChopLayer(layer, changeSource: false)));
        actions.Children.Add(Button("Change Source…", () => RebakeChopLayer(layer, changeSource: true)));
        actions.Children.Add(Button("Open Source", () => { if (layer.ChopSource is { } path) { effectDialog?.Close(); OpenPath(path); } }));
        panel.Children.Add(actions);
    }
}
