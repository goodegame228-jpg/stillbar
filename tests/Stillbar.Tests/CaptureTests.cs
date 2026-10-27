using Stillbar.Core.Capture;
using Stillbar.Core.Display;
using Stillbar.Core.Pill;

namespace Stillbar.Tests;

public class CaptureTests
{
    [Fact]
    public void Hex_is_upper_case_with_leading_zeros()
    {
        Assert.Equal("#6FD3B8", ColorText.Hex(0x6F, 0xD3, 0xB8));
        Assert.Equal("#00000A", ColorText.Hex(0, 0, 10));
        Assert.Equal("rgb(111, 211, 184)", ColorText.Rgb(111, 211, 184));
    }

    [Fact]
    public void Recording_file_name_sorts_by_time_and_has_no_forbidden_characters()
    {
        var name = Recording.FileName(new DateTimeOffset(2026, 10, 27, 18, 30, 5, TimeSpan.FromHours(3)));
        Assert.Equal("Запись 2026-10-27 18-30-05.mp4", name);
        Assert.True(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    [Fact]
    public void Ffmpeg_records_the_given_monitor_with_even_size()
    {
        var args = Recording.FfmpegArgs(new PixelRect(-1080, -836, 0, 1085), 60, @"C:\V\a b.mp4");
        Assert.Contains("-offset_x -1080 -offset_y -836", args);
        Assert.Contains("-video_size 1080x1920", args);      // 1921 high → 1920: H.264 with yuv420p needs even sizes
        Assert.Contains("-framerate 60", args);
        Assert.Contains("-pix_fmt yuv420p", args);
        Assert.EndsWith("\"C:\\V\\a b.mp4\"", args);          // a path with spaces stays one argument
    }

    [Theory]
    [InlineData(7, "0:07")]
    [InlineData(765, "12:45")]
    [InlineData(3723, "1:02:03")]
    public void Elapsed_reads_like_a_clock(int seconds, string expected) =>
        Assert.Equal(expected, Recording.Elapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Recording_outranks_everything_but_an_alert()
    {
        Assert.Equal(PillKind.Recording, PillStateResolver.Resolve(new(DownloadActive: true, TimerRunning: true, MediaPlaying: true, Recording: true)));
        Assert.Equal(PillKind.Alert, PillStateResolver.Resolve(new(TimerFinished: true, Recording: true)));
    }
}
