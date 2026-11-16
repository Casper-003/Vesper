namespace Vesper.Core;

public static class CoverPalette
{
    // Small decoded BGRA sample; ignore transparent pixels and near-white/black margins.
    public static uint DominantArgb(ReadOnlySpan<byte> pixels)
    {
        var counts = new int[512];
        var red = new long[512]; var green = new long[512]; var blue = new long[512];
        long fallbackR = 0, fallbackG = 0, fallbackB = 0; int total = 0;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 128) continue;
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            fallbackR += r; fallbackG += g; fallbackB += b; total++;
            if (Math.Max(r, Math.Max(g, b)) < 20 || Math.Min(r, Math.Min(g, b)) > 240) continue;
            int bin = (r >> 5) * 64 + (g >> 5) * 8 + (b >> 5);
            counts[bin]++; red[bin] += r; green[bin] += g; blue[bin] += b;
        }
        int best = 0;
        for (int i = 1; i < counts.Length; i++) if (counts[i] > counts[best]) best = i;
        if (counts[best] > 0)
        {
            int centerR = (int)(red[best] / counts[best]), centerG = (int)(green[best] / counts[best]), centerB = (int)(blue[best] / counts[best]);
            long rSum = 0, gSum = 0, bSum = 0; int neighbors = 0;
            // Include neighboring bins so nearly identical colors on a quantization boundary stay together.
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (pixels[i + 3] < 128 || Math.Max(r, Math.Max(g, b)) < 20 || Math.Min(r, Math.Min(g, b)) > 240) continue;
                if (Math.Abs(r - centerR) > 32 || Math.Abs(g - centerG) > 32 || Math.Abs(b - centerB) > 32) continue;
                rSum += r; gSum += g; bSum += b; neighbors++;
            }
            return Pack(rSum, gSum, bSum, neighbors);
        }
        return total > 0 ? Pack(fallbackR, fallbackG, fallbackB, total) : 0xFF48545C;
    }
    private static uint Pack(long r, long g, long b, int count) =>
        0xFF000000u | (uint)(r / count) << 16 | (uint)(g / count) << 8 | (uint)(b / count);

    public static FluidStagePalette CreateFluidPalette(uint? dominantArgb)
    {
        if (dominantArgb is not { } argb || (argb >> 24) < 64)
            return new(0xFF0A0D12, 0xFF27445E, 0xFF1C5A58, 0xFF456D8A);

        ToHsl((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, out double h, out double s, out double l);
        if (s < 0.08)
        {
            double baseL = Math.Clamp(l * 0.25 + 0.06, 0.06, 0.14);
            return new(
                FromHsl(h, s, baseL),
                FromHsl(h, Math.Min(0.22, s + 0.10), Math.Clamp(l * 0.45 + 0.18, 0.20, 0.42)),
                FromHsl((h + 26) % 360, 0.24, 0.28),
                FromHsl((h + 330) % 360, 0.28, 0.44));
        }

        double richS = Math.Clamp(s * 0.92, 0.32, 0.78);
        double midL = Math.Clamp(l * 0.65 + 0.14, 0.28, 0.52);
        return new(
            DeepBase: FromHsl(h, Math.Clamp(richS * 0.55, 0.18, 0.45), 0.075),
            PrimaryOrb: FromHsl(h, richS, midL),
            SecondaryOrb: FromHsl((h + 32) % 360, Math.Clamp(richS * 0.88, 0.28, 0.75), Math.Clamp(midL * 0.92, 0.24, 0.48)),
            AccentOrb: FromHsl((h + 322) % 360, Math.Clamp(richS * 0.95, 0.34, 0.82), Math.Clamp(midL + 0.10, 0.34, 0.60)));
    }

    public static FluidStagePalette ExtractFluidPalette(ReadOnlySpan<byte> pixels) =>
        CreateFluidPalette(DominantArgb(pixels));

    private static void ToHsl(byte rByte, byte gByte, byte bByte, out double h, out double s, out double l)
    {
        double r = rByte / 255d, g = gByte / 255d, b = bByte / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2d;
        double d = max - min;
        if (d < 1e-6) { h = 0; s = 0; return; }
        s = l > 0.5 ? d / (2d - max - min) : d / (max + min);
        if (max == r) h = ((g - b) / d + (g < b ? 6d : 0d)) * 60d;
        else if (max == g) h = ((b - r) / d + 2d) * 60d;
        else h = ((r - g) / d + 4d) * 60d;
    }

    private static uint FromHsl(double h, double s, double l)
    {
        h = ((h % 360d) + 360d) % 360d;
        double c = (1d - Math.Abs(2d * l - 1d)) * s;
        double x = c * (1d - Math.Abs((h / 60d) % 2d - 1d));
        double m = l - c / 2d;
        (double r, double g, double b) = h switch
        {
            < 60d => (c, x, 0d),
            < 120d => (x, c, 0d),
            < 180d => (0d, c, x),
            < 240d => (0d, x, c),
            < 300d => (x, 0d, c),
            _ => (c, 0d, x)
        };
        static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255d), 0, 255);
        return 0xFF000000u | (uint)Byte(r + m) << 16 | (uint)Byte(g + m) << 8 | Byte(b + m);
    }
}
