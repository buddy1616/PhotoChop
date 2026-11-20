using Chop.Core;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private sealed class BevelCoverage
    {
        private readonly float[] alpha;
        private readonly int width, height, padding;

        public BevelCoverage(Pixels pixels, double size, StrokeAlignment alignment, BevelLighting? lighting)
        {
            var technique = lighting?.Technique ?? BevelTechnique.Smooth;
            double soften = lighting?.Soften ?? 0;
            double sigma = technique switch
            {
                BevelTechnique.ChiselHard => soften,
                BevelTechnique.ChiselSoft => Math.Sqrt(soften * soften + Math.Max(.65, size / 10) * Math.Max(.65, size / 10)),
                _ => Math.Sqrt(soften * soften + size * size / 9)
            };
            int radius = sigma == 0 ? 0 : Math.Max(1, (int)Math.Round((Math.Sqrt(1 + 4 * sigma * sigma) - 1) / 2));
            padding = 3 * radius + (technique == BevelTechnique.Smooth ? 0 : (int)Math.Ceiling(size) + 1);
            int paddedWidth = pixels.Width + 2 * padding, paddedHeight = pixels.Height + 2 * padding;
            if (paddedWidth > 16384 || paddedHeight > 16384 || (long)paddedWidth * paddedHeight > 32_000_000)
                padding = 0;
            width = pixels.Width + 2 * padding;
            height = pixels.Height + 2 * padding;
            alpha = new float[width * height];

            for (int y = 0; y < pixels.Height; y++)
                for (int x = 0; x < pixels.Width; x++)
                    alpha[(y + padding) * width + x + padding] = (float)PixelBuffer.Alpha(pixels, y * pixels.Width + x);

            if (technique != BevelTechnique.Smooth) Chisel(size, alignment);
            if (radius == 0) return;
            var scratch = new float[alpha.Length];

            // Three separable box passes approximate a Gaussian without quantizing the relief's alpha.
            double mix = Math.Min(1, sigma * sigma / 2);
            for (int pass = 0; pass < 3; pass++)
            {
                BlurLines(alpha, scratch, height, width, width, 1, radius, mix);
                BlurLines(scratch, alpha, width, height, 1, width, radius, mix);
            }
        }

        public double At(int x, int y)
        {
            x += padding;
            y += padding;
            return x >= 0 && y >= 0 && x < width && y < height ? alpha[y * width + x] : 0;
        }

        private void Chisel(double size, StrokeAlignment alignment)
        {
            var inside = alignment == StrokeAlignment.Outside ? null : DistanceTo(opaque: false);
            var outside = alignment == StrokeAlignment.Inside ? null : DistanceTo(opaque: true);
            double offset = alignment switch
            {
                StrokeAlignment.Inside => 0,
                StrokeAlignment.Outside => size,
                _ => size / 2
            };
            for (int i = 0; i < alpha.Length; i++)
            {
                double distance = alpha[i] switch
                {
                    >= 1 => inside is null ? size : Math.Sqrt(inside[i]) - .5,
                    <= 0 => outside is null ? -size : .5 - Math.Sqrt(outside[i]),
                    _ => alpha[i] - .5
                };
                alpha[i] = (float)Math.Clamp((distance + offset) / size, 0, 1);
            }
        }

        private float[] DistanceTo(bool opaque)
        {
            const float unreachable = 1_000_000_000;
            var data = new float[alpha.Length];
            for (int i = 0; i < data.Length; i++)
                data[i] = (alpha[i] >= .5f) == opaque ? 0 : unreachable;
            int max = Math.Max(width, height);
            var sites = new int[max];
            var boundaries = new double[max + 1];
            var line = new float[max];

            void Pass(int lines, int length, int stride, int step)
            {
                for (int row = 0; row < lines; row++)
                {
                    int start = row * stride;
                    for (int i = 0; i < length; i++) line[i] = data[start + i * step];
                    int count = 0;
                    sites[0] = 0;
                    boundaries[0] = double.NegativeInfinity;
                    for (int i = 1; i < length; i++)
                    {
                        double boundary;
                        while (true)
                        {
                            int prior = sites[count];
                            boundary = ((double)line[i] - line[prior] + (double)i * i - (double)prior * prior) / (2 * (i - prior));
                            if (boundary > boundaries[count]) break;
                            count--;
                        }
                        sites[++count] = i;
                        boundaries[count] = boundary;
                    }
                    boundaries[count + 1] = double.PositiveInfinity;
                    int nearest = 0;
                    for (int i = 0; i < length; i++)
                    {
                        while (boundaries[nearest + 1] < i) nearest++;
                        int j = sites[nearest];
                        double delta = i - j;
                        data[start + i * step] = (float)(delta * delta + line[j]);
                    }
                }
            }

            // The lower envelope of squared distances is separable across rows and columns.
            Pass(height, width, width, 1);
            Pass(width, height, 1, width);
            if (!opaque && padding == 0)
            {
                // When padding would exceed the canvas limit, include the transparent pixels beyond the edges.
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int edge = Math.Min(Math.Min(x + 1, width - x), Math.Min(y + 1, height - y));
                        int i = y * width + x;
                        data[i] = Math.Min(data[i], edge * edge);
                    }
            }
            return data;
        }

        private static void BlurLines(float[] source, float[] output, int lines, int length, int lineStride,
            int step, int radius, double mix)
        {
            double normalization = mix / (2 * radius + 1);
            for (int line = 0; line < lines; line++)
            {
                int offset = line * lineStride;
                double sum = 0;
                for (int i = 0; i < Math.Min(radius, length); i++) sum += source[offset + i * step];
                for (int i = 0; i < length; i++)
                {
                    if (i + radius < length) sum += source[offset + (i + radius) * step];
                    if (i > radius) sum -= source[offset + (i - radius - 1) * step];
                    output[offset + i * step] = (float)(sum * normalization + source[offset + i * step] * (1 - mix));
                }
            }
        }
    }
}
