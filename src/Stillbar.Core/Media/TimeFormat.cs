namespace Stillbar.Core.Media;

public static class TimeFormat
{
    /// <summary>1:05, 12:40, 1:02:03. Negative values are shown as zero.</summary>
    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Time left with a real minus sign: −2:08.</summary>
    public static string Remaining(TimeSpan position, TimeSpan duration) =>
        "−" + Clock(duration - position);
}
