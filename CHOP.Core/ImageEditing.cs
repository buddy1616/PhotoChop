namespace Chop.Core;

public sealed record FloatingPixels(string LayerId, Area Source, PixelSelection? Mask, int X, int Y);
public sealed record PastePixelsArgs(string Id, string Name, byte[] Png, string? Parent = null, int X = 0, int Y = 0);
public sealed record WandArgs(PointD Point, int Tolerance = 0, SelectionMode Mode = SelectionMode.Replace);
public enum OrientationAction { FlipHorizontal, FlipVertical, Clockwise, Counterclockwise, HalfTurn, Reset, Bake, ResetRotation, ResetFlip }
