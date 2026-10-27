using Stillbar.Core.Display;
using Stillbar.Core.Health;

namespace Stillbar.Tests;

public class PlacementTests
{
    // 1920×1080 at 150 % with a 48 px taskbar at the bottom; a second 2560×1440 at 100 % to its right.
    private static readonly MonitorArea Laptop = new(@"\\.\DISPLAY1", new(0, 0, 1920, 1080), new(0, 0, 1920, 1032), 1.5, true);
    private static readonly MonitorArea External = new(@"\\.\DISPLAY2", new(1920, 0, 4480, 1440), new(1920, 0, 4480, 1392), 1.0, false);
    private static readonly MonitorArea[] Both = [Laptop, External];

    [Fact]
    public void Picks_the_saved_monitor() => Assert.Same(External, Placement.Pick(Both, @"\\.\DISPLAY2"));

    [Fact]
    public void Falls_back_to_primary_when_the_saved_monitor_is_gone() => Assert.Same(Laptop, Placement.Pick(Both, @"\\.\DISPLAY7"));

    [Fact]
    public void No_saved_monitor_means_primary() => Assert.Same(Laptop, Placement.Pick([External, Laptop], null));

    [Fact]
    public void Top_window_is_centred_and_glued_to_the_top_in_pixels()
    {
        // 760 DIP at 150 % = 1140 px; (1920 - 1140) / 2 = 390.
        Assert.Equal((390, 0), Placement.Window(Laptop, BarEdge.Top, 760, 480, reserved: null));
    }

    [Fact]
    public void Window_on_a_second_monitor_uses_its_own_origin_and_scale() =>
        Assert.Equal((1920 + (2560 - 760) / 2, 0), Placement.Window(External, BarEdge.Top, 760, 480, null));

    [Fact]
    public void Bottom_window_rests_on_the_taskbar() =>
        Assert.Equal(1032 - 720, Placement.Window(Laptop, BarEdge.Bottom, 760, 480, null).Y);

    [Fact]
    public void Bottom_window_rests_on_the_reserved_strip_when_there_is_one() =>
        Assert.Equal(1032 - 720, Placement.Window(Laptop, BarEdge.Bottom, 760, 480, new PixelRect(0, 975, 1920, 1032)).Y);

    [Fact]
    public void Strip_is_the_pill_height_in_pixels_across_the_whole_monitor()
    {
        Assert.Equal(new PixelRect(0, 0, 1920, 57), Placement.Strip(Laptop, BarEdge.Top, 38));        // 38 × 1.5
        Assert.Equal(new PixelRect(1920, 1402, 4480, 1440), Placement.Strip(External, BarEdge.Bottom, 38));
    }
}

public class HealthTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    [Fact]
    public void Answering_ui_is_not_hung()
    {
        var d = new HangDetector(TimeSpan.FromSeconds(15), Every);
        for (int s = 0; s <= 60; s += 2)
        {
            Assert.False(d.Check(T0.AddSeconds(s)));
            d.Pong(T0.AddSeconds(s) + TimeSpan.FromMilliseconds(5));
        }
    }

    [Fact]
    public void Silent_ui_is_hung_after_the_limit()
    {
        var d = new HangDetector(TimeSpan.FromSeconds(15), Every);
        d.Check(T0);
        d.Pong(T0);
        bool hung = false;
        int s = 0;
        while (!hung && s < 60) hung = d.Check(T0.AddSeconds(s += 2));
        Assert.True(hung);
        Assert.Equal(16, s);
    }

    [Fact]
    public void Waking_from_sleep_is_not_a_hang()
    {
        var d = new HangDetector(TimeSpan.FromSeconds(15), Every);
        d.Check(T0);
        d.Pong(T0);
        // The process was suspended for an hour: the next check comes long after the last one.
        Assert.False(d.Check(T0.AddHours(1)));
        Assert.False(d.Check(T0.AddHours(1).AddSeconds(2)));
    }

    [Fact]
    public void Three_restarts_in_ten_minutes_then_no_more()
    {
        var w = TimeSpan.FromMinutes(10);
        var past = new List<DateTimeOffset>();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(RestartBudget.CanRestart(past, T0.AddMinutes(i), 3, w));
            past = RestartBudget.Record(past, T0.AddMinutes(i), w);
        }
        Assert.False(RestartBudget.CanRestart(past, T0.AddMinutes(3), 3, w));
        // Once the oldest falls out of the window, one more is allowed.
        Assert.True(RestartBudget.CanRestart(past, T0.AddMinutes(10).AddSeconds(1), 3, w));
    }

    [Fact]
    public void Old_entries_are_dropped_when_recording() =>
        Assert.Single(RestartBudget.Record([T0, T0.AddMinutes(1)], T0.AddMinutes(30), TimeSpan.FromMinutes(10)));
}
