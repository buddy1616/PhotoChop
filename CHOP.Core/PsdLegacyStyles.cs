namespace Chop.Core;

// Adobe Effects Layer (lrFX). Descriptor-only and unsupported records remain covered by import notes.
internal static class PsdLegacyStyles
{
    public static Effect[] Read(ReadOnlyMemory<byte> data, Func<string, Blend> blend, out bool partial)
    {
        partial = false;
        var effects = new List<Effect>();
        bool masterEnabled = true;
        try
        {
            var reader = new PsdReader(data);
            if (reader.ReadUInt16() != 0) throw new InvalidDataException("Unsupported effects layer version.");
            int count = reader.ReadUInt16();
            if (count > 64) throw new InvalidDataException("Too many Photoshop layer effects.");
            for (int i = 0; i < count; i++)
            {
                Signature(reader);
                string key = reader.ReadAscii(4);
                var block = reader.ReadSection(reader.ReadUInt32());
                string id = $"psd-style-{i + 1}";
                block.ReadUInt32(); // Record version.
                switch (key)
                {
                    case "cmnS":
                        masterEnabled = block.ReadByte() != 0;
                        break;
                    case "oglw":
                    case "iglw":
                        double radius = Math.Clamp((double)block.ReadUInt32(), .1, 100);
                        block.Skip(4); // Intensity uses Photoshop-specific falloff.
                        string ink = Color(block);
                        Signature(block);
                        var mode = blend(block.ReadAscii(4));
                        bool enabled = block.ReadByte() != 0;
                        effects.Add(new(id, EffectKind.Glow, enabled, Mode: mode, Ink: ink,
                            Alignment: key == "oglw" ? StrokeAlignment.Outside : StrokeAlignment.Inside,
                            Size: radius, Opacity: block.ReadByte() / 255d));
                        break;
                    case "dsdw":
                    case "isdw":
                        double blur = Math.Clamp((double)block.ReadUInt32(), .1, 100);
                        block.Skip(4);
                        double angle = -block.ReadInt32() % 360;
                        double distance = Math.Min(512, (double)block.ReadUInt32());
                        string shadow = Color(block);
                        Signature(block);
                        var shadowMode = blend(block.ReadAscii(4));
                        bool on = block.ReadByte() != 0;
                        block.Skip(1);
                        effects.Add(new(id, key == "dsdw" ? EffectKind.DropShadow : EffectKind.InnerShadow,
                            on, A: (angle + 360) % 360, B: distance, Mode: shadowMode, Ink: shadow,
                            Size: blur, Opacity: block.ReadByte() / 255d));
                        break;
                    case "sofi":
                        Signature(block);
                        var overlayMode = blend(block.ReadAscii(4));
                        string overlay = Color(block);
                        double opacity = block.ReadByte() / 255d;
                        effects.Add(new(id, EffectKind.ColorOverlay, block.ReadByte() != 0,
                            Mode: overlayMode, Ink: overlay, Opacity: opacity));
                        break;
                    default:
                        partial = true;
                        break;
                }
            }
        }
        catch (InvalidDataException) { partial = true; }
        return effects.Select(effect => masterEnabled ? effect : effect with { Enabled = false }).ToArray();
    }

    private static void Signature(PsdReader reader)
    {
        if (reader.ReadAscii(4) != "8BIM") throw new InvalidDataException("Invalid Photoshop layer effect signature.");
    }

    private static string Color(PsdReader reader)
    {
        if (reader.ReadUInt16() != 0) throw new InvalidDataException("Unsupported layer effect color mode.");
        int r = reader.ReadUInt16(), g = reader.ReadUInt16(), b = reader.ReadUInt16();
        reader.Skip(2);
        return $"#FF{(r + 128) / 257:X2}{(g + 128) / 257:X2}{(b + 128) / 257:X2}";
    }
}
