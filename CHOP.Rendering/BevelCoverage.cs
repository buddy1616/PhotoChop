using Chop.Core;

namespace Chop.Rendering;

public sealed partial class SkiaRenderer
{
    private sealed class BevelCoverage
    {
        private readonly float[] alpha;
        private readonly int width, height, padding;

        public BevelCoverage(Pixels pixels, double sigma)
        {
            int radius = Math.Max(1, (int)Math.Round((Math.Sqrt(1 + 4 * sigma * sigma) - 1) / 2));
            padding = 3 * radius;
            int paddedWidth = pixels.Width + 2 * padding, paddedHeight = pixels.Height + 2 * padding;
            if (paddedWidth > 16384 || paddedHeight > 16384 || (long)paddedWidth * paddedHeight > 32_000_000)
                padding = 0;
            width = pixels.Width + 2 * padding;
            height = pixels.Height + 2 * padding;
            alpha = new float[width * height];
            var scratch = new float[alpha.Length];
            for (int y = 0; y < pixels.Height; y++)
                for (int x = 0; x < pixels.Width; x++)
                    alpha[(y + padding) * width + x + padding] = (float)PixelBuffer.Alpha(pixels, y * pixels.Width + x);

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
