using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chop.Core;

public enum LayerKind { Raster, Text, Rectangle, Ellipse, Group, Fill, Effect, Chop, Path }
public sealed record LayerBatch(string[] Ids, ReorderArgs? Order = null, string[]? CopyIds = null);

public enum Blend
{
    Normal, Multiply, Screen, Overlay, Add, Darken, Lighten, ColorBurn, ColorDodge, HardLight, SoftLight,
    Difference, Exclusion, Hue, Saturation, Color, Luminosity, LinearBurn, LinearLight, VividLight, PinLight, HardMix, Subtract, Divide
}
public enum EffectKind { BrightnessContrast, HueSaturation, Grayscale, GaussianBlur, Transform, Opacity, BlendMode, MotionBlur, Stroke, DropShadow, BitCrush, PathFill, ColorOverlay, Glow, InnerShadow, BevelEmboss, GradientOverlay, PatternOverlay, Burn, Dodge }

public readonly record struct PointD(double X, double Y);
public readonly record struct Area(int X, int Y, int Width, int Height);
public sealed record Transform(double X = 0, double Y = 0, double ScaleX = 1, double ScaleY = 1, double Rotation = 0, Projective? Warp = null)
{
    public Projective NativeMatrix()
    {
        double radians = Rotation * Math.PI / 180;
        return new Projective(Math.Cos(radians) * ScaleX, -Math.Sin(radians) * ScaleY, X,
            Math.Sin(radians) * ScaleX, Math.Cos(radians) * ScaleY, Y);
    }
}
public sealed record Placement(double X = 0, double Y = 0, double ScaleX = 1, double ScaleY = 1);
/// <summary>Original ink fractions in the document's CMYK profile.</summary>
public sealed record CmykColor(double Cyan, double Magenta, double Yellow, double Black);

public sealed record Effect(string Id, EffectKind Kind, bool Enabled = true, double A = 0, double B = 0, Transform? Spatial = null, Blend Mode = Blend.Normal, string Ink = "#FF000000", StrokeAlignment Alignment = StrokeAlignment.Outside,
    double Size = 0, double Spread = 0, double Opacity = 1, GradientStop[]? Stops = null, LayerTexture? Texture = null, BevelLighting? Lighting = null, bool UseGlobalLight = false, StyleContour Contour = StyleContour.Linear, CmykColor? CmykInk = null, bool PhotoshopStyle = false, double GlowRange = 50, bool PreciseGlow = false)
{
    [JsonIgnore] public bool ProfiledCmykStyle { get; init; }

    public int PhotoshopOrder() => Kind switch
    {
        EffectKind.BevelEmboss => 0,
        EffectKind.Stroke => 1,
        EffectKind.InnerShadow => 2,
        EffectKind.Glow when Alignment == StrokeAlignment.Inside => 3,
        EffectKind.ColorOverlay => 4,
        EffectKind.GradientOverlay => 5,
        EffectKind.PatternOverlay => 6,
        EffectKind.Glow => 7,
        EffectKind.DropShadow => 8,
        _ => 9
    };

    public static bool UsesLight(EffectKind kind) => kind is EffectKind.BevelEmboss or EffectKind.DropShadow or EffectKind.InnerShadow;
    public static double LightAngle(EffectKind kind, double angle) => ((angle + (kind == EffectKind.DropShadow ? 180 : 0)) % 360 + 360) % 360;
}

/// <summary>Pixels are straight-alpha, row-major RGBA8. Treat buffers as immutable.</summary>
public sealed record Pixels(int Width, int Height, byte[] Rgba, ColorPlanes? Native = null)
{
    public long StorageBytes => Rgba.LongLength + (Native?.Planes.Sum(plane => plane.LongLength) ?? 0);
}
public sealed record AlphaMask(int Width, int Height, byte[] Alpha, int X = 0, int Y = 0, byte Outside = 255)
{
    public byte At(int x, int y)
    {
        long sx = (long)x - X, sy = (long)y - Y;
        return sx >= 0 && sx < Width && sy >= 0 && sy < Height ? Alpha[(int)(sy * Width + sx)] : Outside;
    }
}
public sealed record RasterEdit(Pixels Pixels, int X, int Y, PixelShift? Shift = null);
public sealed record PixelShift(Area Source, byte[] Bits, int Dx, int Dy, bool Clear = false, Pixels? Patch = null);

public sealed record Layer(string Id, string Name, LayerKind Kind)
{
    public Transform Transform { get; init; } = new();
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }
    public double Opacity { get; init; } = 1;
    public double FillOpacity { get; init; } = 1;
    public Blend Blend { get; init; }
    public bool PassThrough { get; init; }
    public bool InitiallyCollapsed { get; init; }
    [JsonIgnore] public Pixels? Pixels { get; init; }
    [JsonIgnore] public AlphaMask? Mask { get; init; }
    [JsonIgnore] public PixelShift[] PixelShifts { get; init; } = [];
    // Version 6 projects stored transforms as a list. It is merged on read.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SpatialEdit[]? SpatialEdits { get; init; }
    public Projective? ImagePlacement { get; init; }
    public Placement Placement { get; init; } = new();
    public bool TransformsEnabled { get; init; } = true;
    [JsonIgnore] public Transform ActiveTransform => TransformsEnabled ? Transform : new();
    public bool MaskEnabled { get; init; } = true;
    public bool Clipped { get; init; }
    public VectorMask? VectorMask { get; init; }
    [JsonIgnore] public ColorPlanes? SourceChannels { get; init; }
    public Projective? RasterGrid { get; init; }
    public int RasterX { get; init; }
    public int RasterY { get; init; }
    public string? ChopSource { get; init; }
    public PathContour[] Contours { get; init; } = [];
    public string Text { get; init; } = "Text";
    public string Font { get; init; } = "Liberation Sans";
    public RichText? Typography { get; init; }
    public double TextBend { get; init; }
    public double Size { get; init; } = 48;
    public string Color { get; init; } = "#FFFFFFFF";
    public int Width { get; init; } = 100;
    public int Height { get; init; } = 100;
    public Effect[] Effects { get; init; } = [];
    public Layer[] Children { get; init; } = [];

    public Layer TranslatePlacement(PointD delta)
    {
        double x = delta.X * Placement.ScaleX, y = delta.Y * Placement.ScaleY;
        var translation = new Projective(1, 0, x, 0, 1, y);
        var inverse = new Projective(1, 0, -x, 0, 1, -y);
        return this with
        {
            Placement = Placement with { X = Placement.X + x, Y = Placement.Y + y },
            Transform = Transform.Warp is { } warp
                ? Transform with { Warp = inverse.Then(warp).Then(translation) } : Transform
        };
    }

}

public sealed record Document(string Id, int Width, int Height)
{
    public double GlobalLightAngle { get; init; } = 225;
    public double PixelsPerInch { get; init; } = 72;
    public string ColorSpace { get; init; } = "sRGB";
    [JsonIgnore] public byte[]? IccProfile { get; init; }
    public ColorSettings Color { get; init; } = new();
    [JsonIgnore] public ChannelPlane[] Channels { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
    public Layer[] Layers { get; init; } = [];
    public Projective? ImageOrientation { get; init; }
    public Projective? ImageFlip { get; init; }
    [JsonIgnore] public FloatingPixels? Floating { get; init; }
    public bool GuidesLocked { get; init; }
    public Guide[] Guides { get; init; } = [];
    [JsonIgnore] public Area? Selection { get; init; }

    [JsonIgnore] public PixelSelection? SelectionMask { get; init; }
    public bool Selected(int x, int y) => SelectionAmount(x, y) != 0;
    public byte SelectionAmount(int x, int y) => SelectionMask?.Amount(x, y) ??
        (Selection is { } area && x >= area.X && y >= area.Y && x < area.X + area.Width && y < area.Y + area.Height
            ? byte.MaxValue : (byte)0);

    public Document ReplaceLayer(string id, Layer replacement)
    {
        Layer[] Replace(Layer[] items) => items.Select(layer => layer.Id == id ? replacement :
            layer.Children.Length == 0 ? layer : layer with { Children = Replace(layer.Children) }).ToArray();
        return this with { Layers = Replace(Layers) };
    }

    public IEnumerable<Layer> AllLayers() => Descendants(Layers);
    private static IEnumerable<Layer> Descendants(IEnumerable<Layer> layers) =>
        layers.SelectMany(layer => new[] { layer }.Concat(Descendants(layer.Children)));
}

public static class Format
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public const int MaxDimension = 32768;
    public const int MaxPixels = 200_000_000;

    public static void Dimensions(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
            throw new InvalidDataException($"Dimensions must be positive, with at most {MaxDimension:N0} pixels per side and {MaxPixels:N0} pixels total.");
    }

    public static string Id() => Guid.NewGuid().ToString("N");
    public static bool ValidId(string id) => id is { Length: > 0 and <= 100 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static uint Color(string value) =>
        value is { Length: 9 } && value[0] == '#' &&
        uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint color)
            ? color : throw new InvalidDataException("Color must use #AARRGGBB.");
}

/// <summary>Backend contract used by the GUI and the headless command engine.</summary>
public interface IRasterBackend
{
    Pixels DecodePng(byte[] data);
    Pixels DecodeImage(byte[] data);
    byte[] ExportImage(Document document, ImageExportFormat format);
    void ExportPsd(Document document, Stream output, bool flattenLayers = true, bool keepLayersSeparate = true);
    byte[] EncodePng(Pixels pixels, double? pixelsPerInch = null);
    Pixels Composite(Document document);
    Pixels CompositeForStorage(Document document);
    Document ResolveFloating(Document document);
    RasterEdit? CopyPixels(Document document, string layerId);
    AlphaMask ReprojectMask(Document document, Layer layer);
    Layer BakeStroke(Layer layer, OutlineArgs outline);
    Layer CrushSelection(Document document, Layer layer, int size);
    RasterEdit PaintArea(Document document, Layer layer, Area bounds, Func<int, int, bool> contains, uint color);
    RasterEdit PaintGradient(Document document, Layer layer, Area bounds, GradientArgs gradient);
    RasterEdit RasterizeLayer(Document document, Layer layer);
    RasterEdit? ResolveRaster(Layer layer);
    Projective ParentCoordinates(Document document, Layer layer);
    Projective ToParentSpace(Document document, Layer layer, Projective transform);
    RasterEdit? TransformPixels(Document document, Layer layer, Area selection, Projective transform);
    Layer PrepareRasterEditing(Document document, Layer layer);
    PixelSelection? BucketRegion(Document document, Layer layer, BucketArgs options, bool clipToSelection = true);
    Layer? MoveSelection(Document document, Layer layer, Area selection, int dx, int dy);
    RasterEdit ClearSelection(Document document, Layer layer);
    Layer InvertPixels(Document document, Layer layer);
    AlphaMask InvertMask(Document document, Layer layer);
    AlphaMask PaintMask(Document document, Layer layer, StrokeArgs stroke);
    Pixels Stroke(Document document, Layer layer, StrokeArgs stroke);
}
