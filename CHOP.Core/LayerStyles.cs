namespace Chop.Core;

public sealed record GradientStop(double Position, string Color);
public enum TexturePreset { Checker, Grain, Weave }
public sealed record LayerTexture(TexturePreset Preset = TexturePreset.Checker, byte[]? Png = null,
    double Scale = 1, double Depth = .5);

public enum BevelTechnique { Smooth, ChiselHard, ChiselSoft }

public sealed record BevelLighting(string Highlight = "#FFFFFFFF", Blend HighlightMode = Blend.Normal, double HighlightOpacity = 1,
    string Shadow = "#FF000000", Blend ShadowMode = Blend.Normal, double ShadowOpacity = 1, double Depth = 1, bool Down = false, BevelTechnique Technique = BevelTechnique.Smooth, double Soften = 0);
public static class GradientRamp
{
    public static GradientStop[] Stops(string first, string last, GradientStop[]? stops) =>
        stops is { Length: > 0 } ? stops : [new(0, first), new(1, last)];

    public static void Validate(GradientStop[] stops)
    {
        if (stops.Length is < 2 or > 64 || stops[0]?.Position != 0 || stops[^1]?.Position != 1)
            throw new InvalidDataException("A gradient requires 2–64 stops, starting at 0% and ending at 100%.");
        double previous = -1;
        foreach (var stop in stops)
        {
            if (stop is null || !double.IsFinite(stop.Position) || stop.Position <= previous || stop.Position > 1)
                throw new InvalidDataException("Gradient stop positions must increase.");
            Format.Color(stop.Color);
            previous = stop.Position;
        }
    }

    public static Func<double, PixelValue> Sampler(GradientStop[] stops)
    {
        var values = stops.Select(stop => (stop.Position, Color: PixelValue.FromArgb(Format.Color(stop.Color)))).ToArray();
        return position =>
        {
            position = Math.Clamp(position, 0, 1);
            for (int i = 1; i < values.Length; i++)
                if (position <= values[i].Position)
                {
                    var (a, first) = values[i - 1];
                    var (b, last) = values[i];
                    return PixelValue.Lerp(first, last, (position - a) / (b - a));
                }
            return values[^1].Color;
        };
    }
}
