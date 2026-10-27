namespace Stillbar.Core.Media;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new Rgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Colours for the media card taken from the album art: the card background tint and the progress fill.
/// tokens.json: tint = dominant colour mixed into surface at 22%; progress = same hue, lightness 55-70%, at least 3:1 on the track.
/// </summary>
public static class ArtColors
{
    public const double TintAmount = 0.22;
    private const int HueBuckets = 12;

    /// <summary>
    /// The most prominent saturated colour in a small BGRA image (32x32 is plenty).
    /// Near-black, near-white and grey pixels do not vote. Null for a greyscale cover.
    /// </summary>
    public static Rgb? Dominant(ReadOnlySpan<byte> bgra)
    {
        Span<double> weight = stackalloc double[HueBuckets];
        Span<double> sumR = stackalloc double[HueBuckets];
        Span<double> sumG = stackalloc double[HueBuckets];
        Span<double> sumB = stackalloc double[HueBuckets];

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
            if (a < 128) continue;
            var (h, s, v) = ToHsv(r, g, b);
            if (v < 0.15 || s < 0.2) continue;
            int bucket = (int)(h / 360.0 * HueBuckets) % HueBuckets;
            double w = s * v;
            weight[bucket] += w;
            sumR[bucket] += r * w;
            sumG[bucket] += g * w;
            sumB[bucket] += b * w;
        }

        int best = -1;
        for (int k = 0; k < HueBuckets; k++)
            if (weight[k] > 0 && (best < 0 || weight[k] > weight[best])) best = k;
        if (best < 0) return null;

        double wt = weight[best];
        return new Rgb((byte)Math.Round(sumR[best] / wt), (byte)Math.Round(sumG[best] / wt), (byte)Math.Round(sumB[best] / wt));
    }

    public static Rgb Mix(Rgb a, Rgb b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Progress fill from the art colour: same hue, readable lightness, 3:1 against the track (WCAG 1.4.11).
    /// On a dark track it is lightened; on a light track (the light theme) it is darkened.
    /// </summary>
    public static Rgb Progress(Rgb art, Rgb track)
    {
        var (h, s, l) = ToHsl(art);
        bool lightTrack = ToHsl(track).L > 0.5;
        s = Math.Max(s, 0.35);
        l = lightTrack ? Math.Clamp(l, 0.25, 0.40) : Math.Clamp(l, 0.55, 0.70);
        var c = FromHsl(h, s, l);
        while (Contrast(c, track) < 3.0 && l is > 0.05 and < 0.95)
        {
            l += lightTrack ? -0.05 : 0.05;
            c = FromHsl(h, s, l);
        }
        return c;
    }

    public static double Contrast(Rgb a, Rgb b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Rgb c)
    {
        static double Ch(byte v)
        {
            double x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), d = max - min;
        double h = d == 0 ? 0
            : max == rf ? 60 * (((gf - bf) / d) % 6)
            : max == gf ? 60 * ((bf - rf) / d + 2)
            : 60 * ((rf - gf) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static (double H, double S, double L) ToHsl(Rgb c)
    {
        var (h, _, _) = ToHsv(c.R, c.G, c.B);
        double rf = c.R / 255.0, gf = c.G / 255.0, bf = c.B / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double l = (max + min) / 2, d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return (h, s, l);
    }

    private static Rgb FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new Rgb(B(r + m), B(g + m), B(b + m));
    }
}
