using Stillbar.Core.Display;
using Stillbar.Core.Keyboard;

namespace Stillbar.Tests;

public class KeyboardWatchTests
{
    private const int Ru = 0x0419, En = 0x0409;

    [Fact]
    public void First_reading_is_only_the_baseline() => Assert.Null(new KeyboardWatch().Next(new(Ru, true)));

    [Fact]
    public void Layout_change_says_the_language()
    {
        var w = new KeyboardWatch();
        w.Next(new(Ru, false));
        Assert.Equal("EN", w.Next(new(En, false)));
        Assert.Null(w.Next(new(En, false)));   // nothing changed: nothing to say
        Assert.Equal("RU", w.Next(new(Ru, false)));
    }

    [Fact]
    public void Caps_lock_wins_over_a_layout_change_in_the_same_reading()
    {
        var w = new KeyboardWatch();
        w.Next(new(Ru, false));
        Assert.Equal("Caps Lock включён", w.Next(new(En, true)));
        Assert.Equal("Caps Lock выключен", w.Next(new(En, false)));
    }

    [Fact]
    public void Unknown_window_layout_is_ignored()
    {
        var w = new KeyboardWatch();
        w.Next(new(Ru, false));
        Assert.Null(w.Next(new(0, false))); // no foreground window (desktop switching)
    }

    [Theory]
    [InlineData(0x0422, "UK")]
    [InlineData(0x0407, "DE")]
    [InlineData(0x0419, "RU")]
    public void Language_names(int id, string expected) => Assert.Equal(expected, KeyboardWatch.LayoutName(id));
}

public class OffsetTests
{
    // 1920 px monitor at 100 %, 760 DIP window: 1160 px of free space.
    private static readonly MonitorArea M = new(@"\\.\DISPLAY1", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1.0, true);

    [Theory]
    [InlineData(0, 580)]
    [InlineData(-1, 0)]
    [InlineData(1, 1160)]
    [InlineData(0.5, 870)]
    [InlineData(5, 1160)] // clamped: the open panel must stay on the monitor
    public void Offset_moves_across_the_free_space(double offset, int x) =>
        Assert.Equal(x, Placement.Window(M, BarEdge.Top, 760, 640, null, offset).X);

    [Theory]
    [InlineData(870, 0.5)]
    [InlineData(0, -1)]
    [InlineData(1160, 1)]
    [InlineData(590, 0)] // within 3 % of the middle snaps to the middle
    public void Dragged_position_becomes_an_offset(int x, double expected) =>
        Assert.Equal(expected, Placement.OffsetFor(M, 760, x), 3);

    [Fact]
    public void Offset_survives_a_change_of_scale()
    {
        var hiDpi = M with { Scale = 1.5 };
        // Half-way right stays half-way right: 1920 − 1140 = 780 free → 585 px.
        Assert.Equal(585, Placement.Window(hiDpi, BarEdge.Top, 760, 640, null, 0.5).X);
    }
}
