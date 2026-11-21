namespace Chop.Core;

internal static class PsdDescriptorStyles
{
    internal sealed record Descriptor(string Type, Dictionary<string, object> Fields)
    {
        public object? Get(string key) => Fields.GetValueOrDefault(key);
        public double Number(string key, double fallback) => Get(key) is double value && double.IsFinite(value) ? value : fallback;
        public bool Flag(string key, bool fallback = false) => Get(key) is bool value ? value : fallback;
        public string? Enum(string key) => Get(key) as string;
        public IEnumerable<Descriptor> Objects(string key) => Get(key) is object[] list
            ? list.OfType<Descriptor>() : Get(key) is Descriptor single ? [single] : [];
    }

    public static Effect[] Read(ReadOnlyMemory<byte> data, Func<string, Blend> blend,
        IReadOnlyDictionary<string, byte[]> patterns, double? globalAngle, double? globalAltitude, CmykProfile? colorTransform, out bool partial)
    {
        partial = false;
        var effects = new List<Effect>();
        try
        {
            var reader = new PsdReader(data);
            reader.Skip(4);
            if (reader.ReadUInt32() != 16) throw new InvalidDataException("Invalid style descriptor version.");
            var root = ReadDescriptor(reader, 0);
            bool masterEnabled = root.Flag("masterFXSwitch", true);
            foreach (var entry in root.Fields)
            {
                foreach (var style in root.Objects(entry.Key))
                {
                    if (!style.Flag("present", style.Flag("enab"))) continue;
                    string key = style.Type;
                    EffectKind? type = key switch
                    {
                        "DrSh" => EffectKind.DropShadow, "IrSh" => EffectKind.InnerShadow, "OrGl" or "IrGl" => EffectKind.Glow,
                        "ebbl" => EffectKind.BevelEmboss, "SoFi" => EffectKind.ColorOverlay, "GrFl" => EffectKind.GradientOverlay,
                        "FrFX" => EffectKind.Stroke, "patternFill" => EffectKind.PatternOverlay, _ => null
                    };
                    if (type is null) { partial = true; continue; }
                    string? strokePaint = type == EffectKind.Stroke ? style.Enum("PntT") : null;
                    if (strokePaint is not (null or "SClr" or "GrFl")) { partial = true; continue; }
                    bool gradient = type == EffectKind.GradientOverlay || strokePaint == "GrFl";
                    var colorDescriptor = style.Get("Clr ") as Descriptor;
                    var color = ReadColor(colorDescriptor, colorTransform);
                    if (color is null && !gradient && type is not (EffectKind.BevelEmboss or EffectKind.PatternOverlay))
                    { partial = true; continue; }
                    GradientStop[]? stops = null;
                    if (gradient)
                    {
                        stops = ReadGradient(style.Get("Grad") as Descriptor, colorTransform);
                        if (stops is null) { partial = true; continue; }
                        if (style.Flag("Rvrs")) stops = stops.Reverse().Select(stop => stop with { Position = 1 - stop.Position }).ToArray();
                        partial |= style.Enum("Type") is { } geometry && geometry != "Lnr " ||
                            style.Number("Scl ", 100) != 100 || style.Get("Ofst") is Descriptor offset &&
                            (offset.Number("Hrzn", 0) != 0 || offset.Number("Vrtc", 0) != 0);
                    }
                    bool useGlobal = style.Flag("uglg");
                    double localAngle = style.Number("lagl", style.Number("Angl", 120));
                    double angle = useGlobal ? globalAngle ?? root.Number("gagl", localAngle) : localAngle;
                    angle = ((key is "DrSh" or "IrSh" or "ebbl" ? -angle : angle) % 360 + 360) % 360;
                    if (type == EffectKind.DropShadow) angle = Effect.LightAngle(type.Value, angle);
                    var align = key switch
                    {
                        "IrGl" or "IrSh" => StrokeAlignment.Inside,
                        "ebbl" => style.Enum("bvlS") switch { "OtrB" => StrokeAlignment.Outside, "Embs" => StrokeAlignment.Center, _ => StrokeAlignment.Inside },
                        "FrFX" => style.Enum("Styl") switch { "InsF" => StrokeAlignment.Inside, "CtrF" => StrokeAlignment.Center, _ => StrokeAlignment.Outside },
                        _ => StrokeAlignment.Outside
                    };
                    LayerTexture? texture = null;
                    if (type == EffectKind.PatternOverlay || (type == EffectKind.BevelEmboss && style.Flag("useTexture")))
                    {
                        if (style.Get("Ptrn") is Descriptor pattern && pattern.Enum("Idnt")?.TrimEnd('\0') is { } id &&
                            patterns.TryGetValue(id, out var png))
                            texture = new(Png: png, Scale: Math.Clamp(style.Number("Scl ", 100) / 100, .1, 10),
                                Depth: Math.Clamp(style.Number("textureDepth", 100) / 100, 0, 1));
                        else if (type == EffectKind.PatternOverlay) { partial = true; continue; }
                    }
                    BevelLighting? lighting = null;
                    if (type == EffectKind.BevelEmboss)
                    {
                        lighting = new BevelLighting(ReadColor(style.Get("hglC") as Descriptor, colorTransform) ?? "#FFFFFFFF",
                            style.Enum("hglM") is { } highlightBlend ? blend(highlightBlend) : Blend.Screen,
                            Math.Clamp(style.Number("hglO", 100) / 100, 0, 1),
                            ReadColor(style.Get("sdwC") as Descriptor, colorTransform) ?? "#FF000000",
                            style.Enum("sdwM") is { } shadowBlend ? blend(shadowBlend) : Blend.Multiply,
                            Math.Clamp(style.Number("sdwO", 100) / 100, 0, 1),
                            Math.Clamp(style.Number("srgR", 100) / 100, 0, 10), style.Enum("bvlD") == "Out ",
                            style.Enum("bvlT") switch
                            {
                                "PrBL" => BevelTechnique.ChiselHard,
                                "Slmt" => BevelTechnique.ChiselSoft,
                                _ => BevelTechnique.Smooth
                            },
                            Math.Clamp(style.Number("Sftn", 0), 0, 16));
                        partial = true; // Photoshop contour curves are still approximated.
                    }
                    var contourName = (style.Get("TrnS") as Descriptor)?.Enum("Nm  ")?.TrimEnd('\0');
                    var contour = contourName switch
                    {
                        "Cone" => StyleContour.Cone,
                        "Rounded" => StyleContour.Rounded,
                        "Cove" or "Cove - Shallow" => StyleContour.Cove,
                        "Ring" => StyleContour.Ring,
                        _ => StyleContour.Linear
                    };
                    partial |= contour == StyleContour.Linear && contourName is not (null or "Linear");
                    if (type == EffectKind.Glow)
                        partial |= style.Number("Nose", 0) != 0 || style.Number("ShdN", 0) != 0 || style.Flag("AntA");
                    effects.Add(new($"psd-style-{effects.Count + 1}", type.Value, Enabled: masterEnabled && style.Flag("enab"),
                        A: type == EffectKind.Stroke ? Math.Clamp(style.Number("Sz  ", 1), 1, 512) : angle,
                        B: type == EffectKind.BevelEmboss ? Math.Clamp((useGlobal ? globalAltitude : null) ?? style.Number("Lald", 30), 0, 90) : strokePaint == "GrFl" ? angle : Math.Clamp(style.Number("Dstn", 0), 0, 512),
                        Mode: style.Enum("Md  ") is { } mode ? blend(mode) : Blend.Normal, Ink: color ?? "#FFFFFFFF", Alignment: align,
                        Size: Math.Clamp(style.Number("blur", style.Number("Sz  ", 4)), type == EffectKind.BevelEmboss ? 1 : .1, 100),
                        Spread: Math.Clamp(style.Number("Ckmt", 0), 0, 100),
                        Opacity: Math.Clamp(style.Number("Opct", 100) / 100, 0, 1), Stops: stops, Texture: texture, Lighting: lighting, UseGlobalLight: useGlobal && Effect.UsesLight(type.Value), Contour: contour, CmykInk: type == EffectKind.ColorOverlay ? ReadCmyk(colorDescriptor) : null,
                        PhotoshopStyle: true, GlowRange: Math.Clamp(style.Number("Inpr", 50), 1, 100),
                        PreciseGlow: style.Enum("GlwT") == "PrBL"));
                }
            }
        }
        catch (InvalidDataException) { partial = true; }
        return effects.OrderBy(effect => effect.PhotoshopOrder()).ToArray();
    }

    private static GradientStop[]? ReadGradient(Descriptor? descriptor, CmykProfile? colorTransform)
    {
        if (descriptor is null || descriptor.Enum("GrdF") is { } form && form != "CstS" ||
            descriptor.Objects("Trns").Any(stop => stop.Number("Opct", 100) != 100)) return null;
        var stops = new List<GradientStop>();
        foreach (var stop in descriptor.Objects("Clrs"))
        {
            var color = ReadColor(stop.Get("Clr ") as Descriptor, colorTransform);
            if (color is null || stop.Number("Mdpn", 50) != 50) return null;
            stops.Add(new(stop.Number("Lctn", -1) / 4096, color));
        }
        if (stops.Count < 2) return null;
        if (stops[0].Position > 0) stops.Insert(0, stops[0] with { Position = 0 });
        if (stops[^1].Position < 1) stops.Add(stops[^1] with { Position = 1 });
        var result = stops.ToArray();
        GradientRamp.Validate(result);
        return result;
    }

    private static CmykColor? ReadCmyk(Descriptor? color)
    {
        if (color is null || (color.Type != "CMYC" && color.Get("Cyn ") is null)) return null;
        double Ink(string key) => Math.Clamp(color.Number(key, 0) / 100, 0, 1);
        return new(Ink("Cyn "), Ink("Mgnt"), Ink("Ylw "), Ink("Blck"));
    }

    private static string? ReadColor(Descriptor? color, CmykProfile? colorTransform)
    {
        if (color is null) return null;
        if (ReadCmyk(color) is { } inks)
        {
            if (colorTransform is not null)
            {
                static ushort Sample(double ink) => (ushort)Math.Round(ink * 65535);
                ushort[] rgb = new ushort[3];
                if (colorTransform.Convert([Sample(inks.Cyan), Sample(inks.Magenta), Sample(inks.Yellow), Sample(inks.Black)], rgb, 1))
                    return $"#FF{(rgb[0] + 128) / 257:X2}{(rgb[1] + 128) / 257:X2}{(rgb[2] + 128) / 257:X2}";
            }
            double light = 1 - inks.Black;
            int Rgb(double ink) => (int)Math.Round(255 * (1 - ink) * light);
            return $"#FF{Rgb(inks.Cyan):X2}{Rgb(inks.Magenta):X2}{Rgb(inks.Yellow):X2}";
        }
        if (color.Type != "RGBC") return null;
        int Read(string key) => (int)Math.Round(Math.Clamp(color.Number(key, 0), 0, 255));
        return $"#FF{Read("Rd  "):X2}{Read("Grn "):X2}{Read("Bl  "):X2}";
    }

    internal static Descriptor ReadDescriptor(PsdReader reader, int depth)
    {
        reader.Skip(reader.ReadUInt32() * 2L);
        string type = ReadId(reader);
        uint count = reader.ReadUInt32();
        if (count > 2048 || depth > 24) throw new InvalidDataException("Photoshop style descriptor is too complex.");
        var fields = new Dictionary<string, object>();
        for (int i = 0; i < count; i++)
        {
            string key = ReadId(reader);
            fields[key] = ReadValue(reader, depth + 1, key == "EngineData");
        }
        return new(type, fields);
    }

    private static object ReadValue(PsdReader reader, int depth, bool preserveData = false)
    {
        if (depth > 24) throw new InvalidDataException("Photoshop style descriptor is too deeply nested.");
        switch (reader.ReadAscii(4))
        {
            case "Objc": case "GlbO": return ReadDescriptor(reader, depth);
            case "enum": ReadId(reader); return ReadId(reader);
            case "bool": return reader.ReadByte() != 0;
            case "long": return (double)reader.ReadInt32();
            case "doub": return BitConverter.Int64BitsToDouble(reader.ReadInt64());
            case "UntF": reader.Skip(4); return BitConverter.Int64BitsToDouble(reader.ReadInt64());
            case "UnFl": reader.Skip(4); return (double)BitConverter.Int32BitsToSingle(reader.ReadInt32());
            case "TEXT":
                uint characters = reader.ReadUInt32();
                if (characters > 16384) throw new InvalidDataException("Photoshop style text is too long.");
                return System.Text.Encoding.BigEndianUnicode.GetString(reader.ReadMemory((int)characters * 2).Span);
            case "tdta":
                uint lengthOfData = reader.ReadUInt32();
                if (preserveData && lengthOfData <= 1024 * 1024) return reader.ReadBytes((int)lengthOfData);
                reader.Skip(lengthOfData);
                return string.Empty;
            case "VlLs":
                uint length = reader.ReadUInt32();
                if (length > 2048) throw new InvalidDataException("Photoshop style list is too long.");
                var items = new object[length];
                for (int i = 0; i < items.Length; i++) items[i] = ReadValue(reader, depth + 1);
                return items;
            default: throw new InvalidDataException("Unsupported Photoshop style descriptor value.");
        }
    }

    private static string ReadId(PsdReader reader)
    {
        uint length = reader.ReadUInt32();
        if (length > 256) throw new InvalidDataException("Photoshop style descriptor identifier is too long.");
        return reader.ReadAscii(length == 0 ? 4 : (int)length);
    }
}
