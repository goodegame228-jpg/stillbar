using System.Globalization;
using Stillbar.Core.Display;

namespace Stillbar.Core.Capture;

public static class ColorText
{
    /// <summary>"#6FD3B8": what designers paste into CSS, Figma and XAML.</summary>
    public static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>"rgb(111, 211, 184)", shown next to the hex in the picker.</summary>
    public static string Rgb(byte r, byte g, byte b) => $"rgb({r}, {g}, {b})";
}

/// <summary>Screen recording through ffmpeg's gdigrab: the arguments and the file name, kept here so they are tested.</summary>
public static class Recording
{
    /// <summary>«Запись 2026-10-27 18-30-05.mp4»: sorts by time, no characters Windows forbids.</summary>
    public static string FileName(DateTimeOffset at) => $"Запись {at:yyyy-MM-dd HH-mm-ss}.mp4";

    /// <summary>
    /// One monitor, 60 fps, cursor drawn, H.264 that every player opens (yuv420p, even sizes), fast enough to
    /// keep up on a laptop. Stopped by sending "q" to ffmpeg's input, which finishes the file properly.
    /// </summary>
    public static string FfmpegArgs(PixelRect area, int fps, string output)
    {
        int w = (area.Right - area.Left) & ~1, h = (area.Bottom - area.Top) & ~1;
        var inv = CultureInfo.InvariantCulture;
        return string.Join(' ',
            "-hide_banner -loglevel error -y",
            $"-f gdigrab -framerate {fps.ToString(inv)} -draw_mouse 1",
            $"-offset_x {area.Left.ToString(inv)} -offset_y {area.Top.ToString(inv)} -video_size {w.ToString(inv)}x{h.ToString(inv)}",
            "-i desktop",
            "-c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p -movflags +faststart",
            $"\"{output}\"");
    }

    /// <summary>«0:07», «12:45», «1:02:03» for the pill.</summary>
    public static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
