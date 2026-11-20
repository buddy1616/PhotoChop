using System.Windows;
using System.Windows.Controls;
using Chop.Core;
using Chop.Rendering;

namespace PhotoChop;

public partial class MainWindow
{
    private sealed record ChannelRow(string Key, string Name, ColorComponent? Component = null,
        ChannelPlane? Plane = null, int? SourceIndex = null)
    {
        public override string ToString() => Name;
    }

    private readonly ListBox channelList = new();
    private bool refreshingChannels;

    private FrameworkElement BuildChannels()
    {
        var root = new DockPanel { Margin = new Thickness(4) };
        var controls = new WrapPanel();
        controls.Children.Add(Button("Save Selection as Channel", SaveSelectionChannel));
        controls.Children.Add(Button("Load Channel as Selection", () =>
        {
            if (channelList.SelectedItem is ChannelRow { Plane: { } plane })
                Run("channel.select", plane.Info.Id);
        }));
        controls.Children.Add(Button("Delete Channel", () =>
        {
            if (channelList.SelectedItem is ChannelRow { Plane: { } plane })
                Run("channel.remove", plane.Info.Id);
        }));
        DockPanel.SetDock(controls, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(controls);
        root.Children.Add(channelList);
        channelList.SelectionChanged += (_, _) =>
        {
            if (refreshingChannels || active is null) return;
            active.ChannelView = (channelList.SelectedItem as ChannelRow)?.Key;
            UpdateCanvas();
        };
        return root;
    }

    private static ChannelRow[] ChannelRows(Document document, Layer? layer)
    {
        string model = document.Color.Mode switch
        {
            ChannelColorMode.Rgb => "RGB", ChannelColorMode.Cmyk => "CMYK", _ => "Grayscale"
        };
        var rows = new List<ChannelRow> { new("composite", $"{model} Composite · {(int)document.Color.Depth} bit") };
        foreach (var component in ChannelSamples.Components(document.Color.Mode).Append(ColorComponent.Alpha))
            rows.Add(new("component:" + component, component.ToString(), component));
        foreach (var plane in document.Channels)
            rows.Add(new("plane:" + plane.Info.Id, plane.Info.Name + (plane.Info.Spot ? " · Spot" : ""), Plane: plane));
        if (layer?.SourceChannels is { } source)
        {
            var components = ChannelSamples.Components(source.Settings.Mode).Append(ColorComponent.Alpha).ToArray();
            for (int i = 0; i < source.Planes.Length; i++)
                rows.Add(new($"source:{layer.Id}:{i}", $"{layer.Name} · Source {components[i]} · {(int)source.Settings.Depth} bit", SourceIndex: i));
        }
        return rows.ToArray();
    }

    private void RefreshChannels()
    {
        refreshingChannels = true;
        try
        {
            var rows = active?.Engine.Current is { } document ? ChannelRows(document, ActiveLayer()) : [];
            channelList.ItemsSource = rows;
            channelList.SelectedItem = rows.FirstOrDefault(row => row.Key == active?.ChannelView) ?? rows.FirstOrDefault();
            if (active is not null) active.ChannelView = (channelList.SelectedItem as ChannelRow)?.Key;
        }
        finally { refreshingChannels = false; }
    }

    private void SaveSelectionChannel()
    {
        if (active?.Engine.Current is not { Selection: { } area } document)
        {
            Say("Select pixels to save an alpha channel.");
            return;
        }

        int sampleSize = ChannelSamples.Bytes(document.Color.Depth);
        var samples = new byte[checked(area.Width * area.Height * sampleSize)];
        for (int y = 0; y < area.Height; y++)
        for (int x = 0; x < area.Width; x++)
            if (document.Selected(area.X + x, area.Y + y))
                ChannelSamples.Write(samples, document.Color.Depth, y * area.Width + x, 1);
        int number = 1;
        var names = document.Channels.Select(channel => channel.Info.Name).ToHashSet();
        while (names.Contains($"Alpha {number}")) number++;
        var info = new ChannelInfo(Format.Id(), $"Alpha {number}", area.X, area.Y);
        if (Run("channel.set", new ChannelPlane(info, area.Width, area.Height, document.Color.Depth, samples)))
            channelList.SelectedItem = channelList.Items.OfType<ChannelRow>().FirstOrDefault(row => row.Key == "plane:" + info.Id);
    }

    private Pixels RenderChannel(Document document, ChannelRow? row, Layer? selectedLayer)
    {
        if (row?.Plane is { } plane) return SkiaRenderer.ViewChannel(document, plane);
        if (row?.SourceIndex is { } index && selectedLayer?.SourceChannels is { } source)
            return renderer.ViewLayerChannel(document, selectedLayer, source, index);
        var composite = renderer.Composite(document);
        return row?.Component is { } component ? SkiaRenderer.ViewComponent(composite, component) : composite;
    }

    private void AddColorModeMenu(MenuItem image)
    {
        var menu = new MenuItem { Header = "Mode" };
        image.Items.Add(menu);
        foreach (var (title, mode) in new[] { ("RGB", ChannelColorMode.Rgb), ("Grayscale", ChannelColorMode.Grayscale), ("CMYK", ChannelColorMode.Cmyk) })
        {
            var item = Item(menu, title, () =>
            {
                if (active?.Engine.Current is { } doc) Run("document.color", doc.Color with { Mode = mode });
            });
            item.IsCheckable = true;
            item.Tag = mode;
        }
        menu.Items.Add(new Separator());
        foreach (var depth in Enum.GetValues<ChannelDepth>())
        {
            var item = Item(menu, $"{(int)depth} Bits/Channel", () =>
            {
                if (active?.Engine.Current is { } doc) Run("document.color", doc.Color with { Depth = depth });
            });
            item.IsCheckable = true;
            item.Tag = depth;
        }
        menu.SubmenuOpened += (_, _) =>
        {
            var settings = active?.Engine.Current?.Color;
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                item.IsEnabled = settings is not null;
                item.IsChecked = item.Tag is ChannelColorMode mode ? settings?.Mode == mode :
                    item.Tag is ChannelDepth depth && settings?.Depth == depth;
            }
        };
    }
}
