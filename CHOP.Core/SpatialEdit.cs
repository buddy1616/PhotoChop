namespace Chop.Core;

public enum SpatialKind { Scale, FlipHorizontal, FlipVertical, FreeTransform, Rotate, Skew }
public sealed record SpatialEdit(string Id, SpatialKind Kind, Projective Matrix, bool Enabled = true);
public sealed record WarpArgs(SpatialKind Kind, Area Source, PointD[] Corners);
public sealed record LassoArgs(PointD[] Points, SelectionMode Mode);
public sealed record BucketArgs(PointD Point, string Color, int Tolerance = 0);

/// <summary>A homogeneous 2D transform shared by selection editing and compositing.</summary>
public readonly record struct Projective(double A, double B, double C, double D, double E, double F, double G = 0, double H = 0, double I = 1)
{
    public static Projective Identity => new(1, 0, 0, 0, 1, 0);
    public PointD Map(double x, double y)
    {
        double w = G * x + H * y + I;
        return new((A * x + B * y + C) / w, (D * x + E * y + F) / w);
    }

    public Projective Then(Projective next) => new(
        next.A*A+next.B*D+next.C*G, next.A*B+next.B*E+next.C*H, next.A*C+next.B*F+next.C*I,
        next.D*A+next.E*D+next.F*G, next.D*B+next.E*E+next.F*H, next.D*C+next.E*F+next.F*I,
        next.G*A+next.H*D+next.I*G, next.G*B+next.H*E+next.I*H, next.G*C+next.H*F+next.I*I);

    public Projective Inverse()
    {
        var adj = new Projective(E*I-F*H, C*H-B*I, B*F-C*E, F*G-D*I, A*I-C*G, C*D-A*F, D*H-E*G, B*G-A*H, A*E-B*D);
        double det = A*adj.A+B*adj.D+C*adj.G;
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-10) throw new InvalidDataException("The transform is singular.");
        return new(adj.A/det, adj.B/det, adj.C/det, adj.D/det, adj.E/det, adj.F/det, adj.G/det, adj.H/det, adj.I/det);
    }

    public static Projective Anchored(double x, double y, double ax, double ay) => new(x, 0, ax * (1 - x), 0, y, ay * (1 - y));

    public static Projective FromQuad(Area source, PointD[] points)
    {
        Format.Dimensions(source.Width, source.Height);
        if (points is not { Length: 4 } || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
            Math.Abs(p.X) > 100000 || Math.Abs(p.Y) > 100000))
            throw new InvalidDataException("A transform requires four finite corners.");
        var p0 = points[0]; var p1 = points[1]; var p2 = points[2]; var p3 = points[3];
        double orientation = 0;
        for (int n = 0; n < 4; n++)
        {
            var a = points[n]; var b = points[(n + 1) % 4]; var c = points[(n + 2) % 4];
            double cross = (b.X-a.X)*(c.Y-b.Y)-(b.Y-a.Y)*(c.X-b.X);
            if (Math.Abs(cross) < .001 || (n > 0 && Math.Sign(cross) != orientation))
                throw new InvalidDataException("The transform box must stay convex.");
            orientation = Math.Sign(cross);
        }
        double dx1=p1.X-p2.X, dx2=p3.X-p2.X, dx3=p0.X-p1.X+p2.X-p3.X;
        double dy1=p1.Y-p2.Y, dy2=p3.Y-p2.Y, dy3=p0.Y-p1.Y+p2.Y-p3.Y;
        double denominator=dx1*dy2-dx2*dy1;
        double g=(dx3*dy2-dx2*dy3)/denominator, h=(dx1*dy3-dx3*dy1)/denominator;
        var quad = new Projective(p1.X-p0.X+g*p1.X,p3.X-p0.X+h*p3.X,p0.X,
            p1.Y-p0.Y+g*p1.Y,p3.Y-p0.Y+h*p3.Y,p0.Y,g,h);
        return new Projective(1d/source.Width,0,-source.X/(double)source.Width,0,1d/source.Height,-source.Y/(double)source.Height).Then(quad);
    }
}
