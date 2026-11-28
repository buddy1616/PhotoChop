using Chop.Core;
using SkiaSharp;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private static bool EditablePsdBranch(Layer[] parents) => parents.All(parent =>
        (!parent.MaskEnabled || parent.Mask is null) && !ActiveVectorMask(parent.VectorMask) &&
        (!parent.TransformsEnabled || !parent.Effects.Any(ProcessesPixels)));

    private static bool TryPsdTranslation(Layer layer, Layer[] parents, out int dx, out int dy)
    {
        dx = dy = 0;
        foreach (var item in parents.Append(layer))
        {
            var matrix = SKMatrix.Concat(SpatialMatrix(item), LayerMatrix(item, !item.TransformsEnabled));
            if (item.RasterGrid is { } grid) matrix = SKMatrix.Concat(matrix, ToSkia(grid));
            if (matrix != SKMatrix.CreateTranslation(matrix.TransX, matrix.TransY) ||
                Math.Abs(matrix.TransX) > 1_000_000 || Math.Abs(matrix.TransY) > 1_000_000 ||
                matrix.TransX != MathF.Truncate(matrix.TransX) || matrix.TransY != MathF.Truncate(matrix.TransY))
                return false;
            dx += (int)matrix.TransX;
            dy += (int)matrix.TransY;
        }
        return true;
    }

    private static VectorMask PsdShapeMask(Layer layer)
    {
        double left = layer.RasterX, top = layer.RasterY;
        double right = left + layer.Width, bottom = top + layer.Height;
        double x = (left + right) / 2, y = (top + bottom) / 2;
        const double circle = 0.5522847498307936;
        double hx = layer.Width * circle / 2, hy = layer.Height * circle / 2;
        PathNode[] nodes = layer.Kind == LayerKind.Rectangle
            ? [new(new(left, top)), new(new(right, top)), new(new(right, bottom)), new(new(left, bottom))]
            : [new(new(x, top), new(x - hx, top), new(x + hx, top)),
               new(new(right, y), new(right, y - hy), new(right, y + hy)),
               new(new(x, bottom), new(x + hx, bottom), new(x - hx, bottom)),
               new(new(left, y), new(left, y + hy), new(left, y - hy))];
        return new([new(nodes, true)], Operations: [VectorMaskOperation.Union]);
    }

    private static VectorMask? TryPsdVectorMask(VectorMask mask, Area canvas, int dx, int dy)
    {
        if (mask.Operations is { } operations && operations.Length != mask.Contours.Length) return null;
        if (mask.Contours.Length > 2000 || mask.Contours.Sum(contour => (long)contour.Nodes.Length) > 100000 ||
            mask.Contours.Any(contour => contour.Nodes.Length > ushort.MaxValue)) return null;
        PointD Move(PointD p) => new(p.X + dx, p.Y + dy);
        bool Fits(PointD p) => Math.Abs((p.X + dx) / canvas.Width) < 128 &&
            Math.Abs((p.Y + dy) / canvas.Height) < 128;
        if (mask.Contours.SelectMany(contour => contour.Nodes).Any(node =>
            !Fits(node.Point) || !Fits(node.Incoming ?? node.Point) || !Fits(node.Outgoing ?? node.Point))) return null;
        return mask with { Contours = mask.Contours.Select(contour => contour with
        {
            Nodes = contour.Nodes.Select(node => new PathNode(Move(node.Point),
                node.Incoming is { } incoming ? Move(incoming) : null,
                node.Outgoing is { } outgoing ? Move(outgoing) : null)).ToArray()
        }).ToArray() };
    }

    private static void WritePsdVectorMask(PsdOutput writer, VectorMask mask, Area canvas)
    {
        writer.Int(3);
        writer.Int((mask.Inverted ? 1 : 0) | (mask.Enabled ? 0 : 4));
        writer.Short(6); // Path fill rule.
        Padding(12);
        writer.Short(8); // Initial fill rule.
        writer.Short(mask.InitialFill ? (short)1 : (short)0);
        Padding(11);
        for (int index = 0; index < mask.Contours.Length; index++)
        {
            var contour = mask.Contours[index];
            writer.Short(contour.Closed ? (short)0 : (short)3);
            writer.Short(unchecked((short)contour.Nodes.Length));
            writer.Short((short)(mask.Operations is { } operations && index < operations.Length
                ? operations[index] : VectorMaskOperation.Exclude));
            Padding(10);
            foreach (var node in contour.Nodes)
            {
                writer.Short(contour.Closed ? (short)2 : (short)5);
                Point(node.Incoming ?? node.Point);
                Point(node.Point);
                Point(node.Outgoing ?? node.Point);
            }
        }

        void Padding(int words)
        {
            for (int i = 0; i < words; i++) writer.Short(0);
        }
        void Point(PointD point)
        {
            const double scale = 16777216;
            writer.Int(checked((int)Math.Round(point.Y / canvas.Height * scale)));
            writer.Int(checked((int)Math.Round(point.X / canvas.Width * scale)));
        }
    }

    private static string? PsdStyleKey(Effect effect) => effect.Kind switch
    {
        EffectKind.Stroke => "FrFX",
        EffectKind.ColorOverlay => "SoFi",
        EffectKind.DropShadow => "DrSh",
        EffectKind.InnerShadow => "IrSh",
        EffectKind.Glow when effect.Alignment == StrokeAlignment.Inside => "IrGl",
        EffectKind.Glow when effect.Alignment == StrokeAlignment.Outside => "OrGl",
        _ => null
    };

    private static bool PsdStylesCanRemainEditable(Effect[] effects) =>
        effects.All(effect => PsdStyleKey(effect) is not null && effect.Stops is null &&
            effect.Contour == StyleContour.Linear && (Format.Color(effect.Ink) >> 24) == 255 &&
            (effect.Kind != EffectKind.Stroke || effect.A == Math.Round(effect.A)) &&
            (effect.Kind != EffectKind.ColorOverlay || effect.CmykInk is null)) &&
        effects.Select(PsdStyleKey).Distinct().Count() == effects.Length &&
        (!effects.Any(effect => effect.Kind == EffectKind.Stroke) ||
            !effects.Any(effect => effect.Kind == EffectKind.ColorOverlay) ||
            effects.All(effect => effect.PhotoshopStyle)) &&
        effects.Select(effect => effect.PhotoshopOrder()).SequenceEqual(effects.Select(effect => effect.PhotoshopOrder()).Order());

    private static double PsdLightAngle(double angle) => Math.IEEERemainder(-angle, 360);

    private static void WritePsdSolidFill(PsdOutput output, string color)
    {
        output.Int(16);
        var writer = new PsdDescriptorOutput(output);
        writer.Descriptor("null", [writer.Object("Clr ", "RGBC", writer.Color(color))]);
    }

    private static void WritePsdStyles(PsdOutput output, Effect[] effects)
    {
        output.Int(0);
        output.Int(16);
        var writer = new PsdDescriptorOutput(output);
        var fields = new List<Action> { writer.Flag("masterFXSwitch", true), writer.Unit("Scl ", "#Prc", 100) };
        foreach (var effect in effects)
        {
            var key = PsdStyleKey(effect)!;
            var style = new List<Action>
            {
                writer.Flag("enab", true), writer.Flag("present", true), writer.Flag("showInDialog", true),
                writer.Enum("Md  ", "BlnM", PsdBlendKey(effect.Mode)),
                writer.Unit("Opct", "#Prc", effect.Opacity * 100),
                writer.Object("Clr ", "RGBC", writer.Color(effect.Ink))
            };
            if (effect.Kind == EffectKind.Stroke)
            {
                style.Add(writer.Enum("Styl", "FStl", effect.Alignment switch
                    { StrokeAlignment.Inside => "InsF", StrokeAlignment.Center => "CtrF", _ => "OutF" }));
                style.Add(writer.Enum("PntT", "FrFl", "SClr"));
                style.Add(writer.Unit("Sz  ", "#Pxl", effect.A));
            }
            else if (effect.Kind != EffectKind.ColorOverlay)
            {
                style.Add(writer.Unit("blur", "#Pxl", effect.Size));
                style.Add(writer.Unit("Ckmt", "#Pxl", effect.Spread));
                if (effect.Kind == EffectKind.Glow)
                {
                    style.Add(writer.Enum("GlwT", "BETE", effect.PreciseGlow ? "PrBL" : "SfBL"));
                    style.Add(writer.Unit("Inpr", "#Prc", effect.GlowRange));
                    if (effect.Alignment == StrokeAlignment.Inside)
                        style.Add(writer.Enum("glwS", "IGSr", "SrcE"));
                }
                else
                {
                    style.Add(writer.Flag("uglg", effect.UseGlobalLight));
                    style.Add(writer.Unit("lagl", "#Ang", PsdLightAngle(Effect.LightAngle(effect.Kind, effect.A))));
                    style.Add(writer.Unit("Dstn", "#Pxl", effect.B));
                }
            }
            fields.Add(writer.Object(key, key, style));
        }
        writer.Descriptor("Lefx", fields);
    }

    private sealed class PsdDescriptorOutput(PsdOutput output)
    {
        public void Descriptor(string type, IReadOnlyList<Action> fields)
        {
            output.Unicode("");
            Id(type);
            output.Int(fields.Count);
            foreach (var field in fields) field();
        }

        public Action Object(string key, string type, IReadOnlyList<Action> fields) =>
            Field(key, "Objc", () => Descriptor(type, fields));

        public Action Flag(string key, bool value) => Field(key, "bool", () => output.Byte(value ? (byte)1 : (byte)0));

        public Action Unit(string key, string unit, double value) =>
            Field(key, "UntF", () => { output.Key(unit); output.Double(value); });

        public Action Enum(string key, string type, string value) =>
            Field(key, "enum", () => { Id(type); Id(value); });

        public Action[] Color(string value)
        {
            uint argb = Format.Color(value);
            return
            [
                Field("Rd  ", "doub", () => output.Double((argb >> 16) & 255)),
                Field("Grn ", "doub", () => output.Double((argb >> 8) & 255)),
                Field("Bl  ", "doub", () => output.Double(argb & 255))
            ];
        }

        private Action Field(string key, string type, Action write) =>
            () => { Id(key); output.Key(type); write(); };

        private void Id(string id)
        {
            output.Int(id.Length == 4 ? 0 : id.Length);
            output.Key(id);
        }
    }
}
