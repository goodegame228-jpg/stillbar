using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace Stillbar.App.Tests;

/// <summary>S-1: the built exe renders every snapshot view offscreen and exits cleanly.</summary>
public class SmokeTests
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "Stillbar.exe");

    // Expected height: 0 = an open panel (taller than the pills), otherwise the exact PNG height.
    // Pills: 38 DIP + 24 of backdrop = 62; the finished-timer alert: 64 + 24 = 88.
    public static TheoryData<string, int> Views => new()
    {
        { "pill", 62 }, { "pill-night", 62 }, { "pill-rain", 62 }, { "music", 62 },
        { "download", 62 }, { "toast", 62 }, { "timer", 62 }, { "alert", 88 },
        { "panel", 0 }, { "panel-pink", 0 }, { "panel-timer", 0 }, { "empty", 0 }, { "devices", 0 }, { "player", 0 },
        { "mixer", 0 }, { "clipboard", 0 }, { "notes", 0 }, { "system", 0 }, { "weather", 0 }, { "rain", 88 }, { "listening", 62 },
    };

    // The bar lives in a 640 DIP see-through window; the panel may use 628 of it (the rest is the wave).
    // Snapshot PNGs add 24 px of backdrop under the shape.
    private const int MaxPanel = 628, Backdrop = 24;

    [Theory]
    [MemberData(nameof(Views))]
    public void S1_view_renders(string view, int height)
    {
        Assert.True(File.Exists(Exe), $"not built: {Exe}");
        var png = Path.Combine(Path.GetTempPath(), $"stillbar-smoke-{view}-{Guid.NewGuid():N}.png");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Exe, $"--snapshot \"{png}\" --view {view}") { UseShellExecute = false })!;
            Assert.True(p.WaitForExit(30_000), "timed out");
            Assert.Equal(0, p.ExitCode);

            var frame = BitmapDecoder.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(800, frame.PixelWidth);
            if (height == 0)
            {
                Assert.True(frame.PixelHeight > 140, $"panel did not open: {frame.PixelHeight}px");
                Assert.True(frame.PixelHeight - Backdrop <= MaxPanel, $"{view} is {frame.PixelHeight - Backdrop} DIP: taller than the window, it would be cut off");
            }
            else Assert.Equal(height, frame.PixelHeight);
        }
        finally
        {
            if (File.Exists(png)) File.Delete(png);
        }
    }
}
