namespace Chop.Core;

internal static class PsdDescriptorStyles
{
    private sealed record Descriptor(string Type, Dictionary<string, object> Fields)
    {
        public object? Get(string key) => Fields.GetValueOrDefault(key);
        public double Number(string key, double fallback) => Get(key) is double value && double.IsFinite(value) ? value : fallback;
        public bool Flag(string key, bool fallback = false) => Get(key) is bool value ? value : fallback;
        public string? Enum(string key) => Get(key) as string;
        public IEnumerable<Descriptor> Objects(string key) => Get(key) is object[] list
            ? list.OfType<Descriptor>() : Get(key) is Descriptor single ? [single] : [];
    }

    public static Effect[] Read(ReadOnlyMemory<byte> data, Func<string, Blend> blend, out bool partial)
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
                        "FrFX" => EffectKind.Stroke, _ => null
                    };
                    if (type is null) { partial = true; continue; }
                    if (type == EffectKind.Stroke && style.Enum("PntT") is { } paint && paint != "SClr")
                    { partial = true; continue; }
                    var color = ReadColor(style.Get("Clr ") as Descriptor);
                    if (color is null && type is not (EffectKind.BevelEmboss or EffectKind.GradientOverlay))
                    { partial = true; continue; }
                    GradientStop[]? stops = null;
                    if (type == EffectKind.GradientOverlay)
                    {
                        stops = ReadGradient(style.Get("Grad") as Descriptor);
                        if (stops is null) { partial = true; continue; }
                        if (style.Flag("Rvrs")) stops = stops.Reverse().Select(stop => stop with { Position = 1 - stop.Position }).ToArray();
                        partial |= style.Enum("Type") is { } geometry && geometry != "Lnr " ||
                            style.Number("Scl ", 100) != 100 || style.Get("Ofst") is Descriptor offset &&
                            (offset.Number("Hrzn", 0) != 0 || offset.Number("Vrtc", 0) != 0);
                    }
                    double angle = style.Flag("uglg") ? root.Number("gagl", 120) : style.Number("lagl", 120);
                    angle = ((key is "DrSh" or "IrSh" or "ebbl" ? -angle : angle) % 360 + 360) % 360;
                    var align = key switch
                    {
                        "IrGl" or "IrSh" => StrokeAlignment.Inside,
                        "ebbl" => style.Enum("bvlS") switch { "OtrB" => StrokeAlignment.Outside, "Embs" => StrokeAlignment.Center, _ => StrokeAlignment.Inside },
                        "FrFX" => style.Enum("Styl") switch { "InsF" => StrokeAlignment.Inside, "CtrF" => StrokeAlignment.Center, _ => StrokeAlignment.Outside },
                        _ => StrokeAlignment.Outside
                    };
                    if (type == EffectKind.BevelEmboss) partial = true; // Separate highlight and shadow settings are approximated.
                    effects.Add(new($"psd-style-{effects.Count + 1}", type.Value, Enabled: masterEnabled && style.Flag("enab"),
                        A: type == EffectKind.Stroke ? Math.Clamp(style.Number("Sz  ", 1), 1, 512) : angle,
                        B: type == EffectKind.BevelEmboss ? Math.Clamp(style.Number("Lald", 30), 0, 90) : Math.Clamp(style.Number("Dstn", 0), 0, 512),
                        Mode: style.Enum("Md  ") is { } mode ? blend(mode) : Blend.Normal, Ink: color ?? "#FFFFFFFF", Alignment: align,
                        Size: Math.Clamp(style.Number("blur", style.Number("Sz  ", 4)), type == EffectKind.BevelEmboss ? 1 : .1, 100),
                        Spread: Math.Clamp(style.Number("Ckmt", 0), 0, 100),
                        Opacity: Math.Clamp(style.Number("Opct", 100) / 100, 0, 1), Stops: stops));
                }
            }
        }
        catch (InvalidDataException) { partial = true; }
        return effects.ToArray();
    }

    private static GradientStop[]? ReadGradient(Descriptor? descriptor)
    {
        if (descriptor is null || descriptor.Enum("GrdF") is { } form && form != "CstS" ||
            descriptor.Objects("Trns").Any(stop => stop.Number("Opct", 100) != 100)) return null;
        var stops = new List<GradientStop>();
        foreach (var stop in descriptor.Objects("Clrs"))
        {
            var color = ReadColor(stop.Get("Clr ") as Descriptor);
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

    private static string? ReadColor(Descriptor? color)
    {
        if (color?.Type != "RGBC") return null;
        int Read(string key) => (int)Math.Round(Math.Clamp(color.Number(key, 0), 0, 255));
        return $"#FF{Read("Rd  "):X2}{Read("Grn "):X2}{Read("Bl  "):X2}";
    }

    private static Descriptor ReadDescriptor(PsdReader reader, int depth)
    {
        reader.Skip(reader.ReadUInt32() * 2L);
        string type = ReadId(reader);
        uint count = reader.ReadUInt32();
        if (count > 2048 || depth > 24) throw new InvalidDataException("Photoshop style descriptor is too complex.");
        var fields = new Dictionary<string, object>();
        for (int i = 0; i < count; i++) fields[ReadId(reader)] = ReadValue(reader, depth + 1);
        return new(type, fields);
    }

    private static object ReadValue(PsdReader reader, int depth)
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
            case "TEXT": reader.Skip(reader.ReadUInt32() * 2L); return string.Empty;
            case "tdta": reader.Skip(reader.ReadUInt32()); return string.Empty;
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
