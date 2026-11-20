using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Chop.Core;

namespace PhotoChop;

public partial class MainWindow
{
    private const string ClipboardOrigin = "CHOP.PixelOrigin";
    private void CopyPixels()
    {
        if (active?.Engine.Current is not { } doc || ActiveLayer() is not { } layer) return;
        Guard(() =>
        {
            if (renderer.CopyPixels(doc, layer.Id) is not { } copy) return;
            var data = new DataObject();
            data.SetImage(EditorCanvas.Bitmap(copy.Pixels));
            data.SetData("PNG", new MemoryStream(renderer.EncodePng(copy.Pixels), writable: false));
            data.SetData(ClipboardOrigin, $"{doc.Id}|{copy.X}|{copy.Y}");
            Clipboard.SetDataObject(data, true);
            Say("Copied pixels");
        });
    }

    private static BitmapSource? ClipboardBitmap(IDataObject? data)
    {
        foreach (string format in new[] { "PNG", "image/png", DataFormats.Tiff })
        {
            try
            {
                if (data?.GetDataPresent(format, autoConvert: false) != true) continue;
                using Stream? stream = data.GetData(format, autoConvert: false) switch
                {
                    byte[] bytes => new MemoryStream(bytes, writable: false),
                    Stream input => input,
                    _ => null
                };
                if (stream is null) continue;
                if (stream.CanSeek) stream.Position = 0;
                var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad).Frames.FirstOrDefault();
                if (image is not null) return image;
            }
            catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException
                or System.Runtime.InteropServices.COMException) { }
        }
        return ClipboardDib.Read() ?? Clipboard.GetImage();
    }

    private void PastePixels()
    {
        Guard(() =>
        {
            var data = Clipboard.GetDataObject();
            var image = ClipboardBitmap(data);
            if (image is null) { Say("The clipboard has no image."); return; }
            Format.Dimensions(image.PixelWidth, image.PixelHeight);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var bytes = new MemoryStream();
            encoder.Save(bytes);
            string id = Format.Id();
            if (active is null)
            {
                var session = NewSession();
                session.Engine.Execute(Command.Of("document.create", new NewArgs(Format.Id(), image.PixelWidth, image.PixelHeight)));
                session.Engine.Execute(Command.Of("layer.pastePixels", new PastePixelsArgs(id, "Pasted Layer", bytes.ToArray())));
                session.ActiveId = id;
                AddSession(session);
                return;
            }
            var doc = active.Engine.Current!;
            int x = doc.Selection?.X ?? Math.Max(0, (doc.Width - image.PixelWidth) / 2);
            int y = doc.Selection?.Y ?? Math.Max(0, (doc.Height - image.PixelHeight) / 2);
            if (data?.GetDataPresent(ClipboardOrigin) == true && data.GetData(ClipboardOrigin) is string origin)
            {
                var parts = origin.Split('|');
                if (parts.Length == 3 && parts[0] == doc.Id && int.TryParse(parts[1], out var ox) && int.TryParse(parts[2], out var oy))
                    (x, y) = (ox, oy);
            }
            string? parent = DestinationGroup();
            if (parent is not null) active.Collapsed.Remove(parent);
            Run("layer.pastePixels", new PastePixelsArgs(id, "Pasted Layer", bytes.ToArray(), parent, x, y), selectId: id);
        });
    }
}
