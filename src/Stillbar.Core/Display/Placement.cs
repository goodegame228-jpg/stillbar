namespace Stillbar.Core.Display;

public enum BarEdge { Top, Bottom }

/// <summary>One monitor as Windows reports it: device name (\\.\DISPLAY2), full and work area in pixels, DPI scale.</summary>
public sealed record MonitorArea(string Name, PixelRect Bounds, PixelRect Work, double Scale, bool IsPrimary)
{
    public int Width => Bounds.Right - Bounds.Left;
    public int Height => Bounds.Bottom - Bounds.Top;
}

/// <summary>Where the bar's window goes and which strip of the screen it reserves. All results in physical pixels.</summary>
public static class Placement
{
    /// <summary>The monitor saved in settings if it is still connected, else the primary one.</summary>
    public static MonitorArea Pick(IReadOnlyList<MonitorArea> monitors, string? name)
    {
        if (monitors.Count == 0) throw new ArgumentException("no monitors", nameof(monitors));
        return monitors.FirstOrDefault(m => m.Name == name)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors[0];
    }

    /// <summary>
    /// The strip asked of the shell: the full width of the monitor, as tall as the collapsed pill. For the bottom edge
    /// it is asked at the very bottom; the shell moves it above the taskbar itself.
    /// </summary>
    public static PixelRect Strip(MonitorArea m, BarEdge edge, double heightDip)
    {
        int h = (int)Math.Ceiling(heightDip * m.Scale);
        return edge == BarEdge.Top
            ? new PixelRect(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Top + h)
            : new PixelRect(m.Bounds.Left, m.Bounds.Bottom - h, m.Bounds.Right, m.Bounds.Bottom);
    }

    /// <summary>
    /// Top-left corner of the window. Top edge: glued to the screen edge (or the reserved strip). Bottom edge:
    /// resting on the taskbar (the work area), or on the bottom of the reserved strip.
    /// Horizontally: <paramref name="offset"/> −1 … 1 moves it across the free space (0 = centred, ±1 = as far as
    /// the open panel still fits on the monitor). A fraction, so it survives a change of monitor, size or scale.
    /// </summary>
    public static (int X, int Y) Window(MonitorArea m, BarEdge edge, double widthDip, double heightDip, PixelRect? reserved, double offset = 0)
    {
        int w = (int)Math.Round(widthDip * m.Scale);
        int h = (int)Math.Round(heightDip * m.Scale);
        int free = Math.Max(0, m.Width - w);
        int x = m.Bounds.Left + (int)Math.Round(free / 2.0 * (1 + Math.Clamp(offset, -1, 1)));
        int y = edge == BarEdge.Top
            ? reserved?.Top ?? m.Bounds.Top
            : (reserved?.Bottom ?? m.Work.Bottom) - h;
        return (x, y);
    }

    /// <summary>The offset (−1 … 1) for a window whose left edge was dragged to <paramref name="x"/> pixels.</summary>
    public static double OffsetFor(MonitorArea m, double widthDip, int x)
    {
        int w = (int)Math.Round(widthDip * m.Scale);
        int free = m.Width - w;
        if (free <= 0) return 0;
        double o = (x - m.Bounds.Left) * 2.0 / free - 1;
        return Math.Abs(o) < 0.03 ? 0 : Math.Clamp(o, -1, 1); // close to the middle snaps to the middle
    }
}
