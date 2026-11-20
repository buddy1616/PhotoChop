using System.IO;
using System.Windows;
using Chop.Core;
using Microsoft.Win32;

namespace PhotoChop;

public partial class MainWindow
{
    private async void ImportPsd(bool flattened)
    {
        var picker = new OpenFileDialog { Filter = "Photoshop Document (*.psd)|*.psd", CheckFileExists = true };
        if (picker.ShowDialog(EditorOwner) != true) return;

        string path = picker.FileName;
        var session = NewSession();
        session.InitialTitle = Path.GetFileNameWithoutExtension(path);
        string id = Format.Id();
        Say($"Importing {Path.GetFileName(path)}…");
        try
        {
            await Task.Run(() => session.Engine.Execute(Command.Of("document.importPsd",
                new PsdImportArgs(id, Path: path, Flattened: flattened))));
            session.ActiveId = session.Engine.Current?.Layers.LastOrDefault()?.Id;
            AddSession(session);
            var warnings = session.Engine.LastImportWarnings;
            if (warnings.Count > 0)
                MessageBox.Show(EditorOwner, string.Join(Environment.NewLine + Environment.NewLine, warnings),
                    "PSD import notes", MessageBoxButton.OK, MessageBoxImage.Information);
            Say($"Imported {Path.GetFileName(path)}");
        }
        catch (Exception error)
        {
            MessageBox.Show(EditorOwner, error.Message, "Could not import PSD", MessageBoxButton.OK, MessageBoxImage.Warning);
            Say();
        }
    }
}
