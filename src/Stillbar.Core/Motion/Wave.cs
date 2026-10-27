namespace Stillbar.Core.Motion;

/// <summary>
/// The liquid bottom edge of the open panel: a sine wave along the straight part of the bottom edge,
/// faded to zero next to the rounded corners so it joins them without a kink.
/// </summary>
public static class Wave
{
    private const double Ramp = 48; // DIP over which the wave grows from the corner

    /// <summary>
    /// Points of the bottom edge from right (x = width − radius) to left (x = radius), every <paramref name="step"/> DIP.
    /// The wave stays inside the shape: y runs between height − <paramref name="depth"/> and height, so nothing
    /// pokes out of the element's layout box (WPF would clip it).
    /// </summary>
    public static List<(double X, double Y)> BottomEdge(
        double width, double height, double radius, double depth, double wavelength, double phase, double step = 4)
    {
        var points = new List<(double, double)>();
        double right = width - radius, left = radius;
        if (right <= left) return points;

        for (double x = right; x > left; x -= step)
        {
            double s = (1 + Math.Sin(2 * Math.PI * x / wavelength + phase)) / 2; // 0..1
            points.Add((x, height - depth * Envelope(x, left, right) * s));
        }
        points.Add((left, height));
        return points;
    }

    private static double Envelope(double x, double left, double right)
    {
        double t = Math.Min(Math.Min(x - left, right - x) / Ramp, 1);
        return t * t * (3 - 2 * t); // smoothstep
    }
}
