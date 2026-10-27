using Stillbar.Core.Timer;

namespace Stillbar.Tests;

public class TimerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

    [Fact]
    public void Counts_down_from_the_end_moment()
    {
        var t = new CountdownTimer();
        t.Start(Five, T0);
        Assert.True(t.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(252), t.Remaining(T0.AddSeconds(48)));
        Assert.False(t.IsFinished(T0.AddSeconds(299)));
        Assert.True(t.IsFinished(T0.AddSeconds(300)));
        Assert.Equal(TimeSpan.Zero, t.Remaining(T0.AddMinutes(9)));
    }

    [Fact]
    public void Pause_keeps_the_rest_and_resume_continues()
    {
        var t = new CountdownTimer();
        t.Start(Five, T0);
        t.Pause(T0.AddSeconds(60));
        Assert.True(t.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(240), t.Remaining(T0.AddHours(3))); // no time passes while paused
        t.Resume(T0.AddHours(3));
        Assert.True(t.IsFinished(T0.AddHours(3).AddSeconds(240)));
    }

    [Fact]
    public void Plus_one_minute_extends_or_restarts()
    {
        var t = new CountdownTimer();
        t.Start(Five, T0);
        t.Add(TimeSpan.FromMinutes(1), T0.AddSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(350), t.Remaining(T0.AddSeconds(10)));

        var done = new CountdownTimer();
        done.Start(Five, T0);
        done.Add(TimeSpan.FromMinutes(1), T0.AddMinutes(7)); // after the alert: a fresh minute from now
        Assert.Equal(TimeSpan.FromMinutes(1), done.Remaining(T0.AddMinutes(7)));
        Assert.False(done.IsFinished(T0.AddMinutes(7)));
    }

    [Fact]
    public void Reset_turns_it_off()
    {
        var t = new CountdownTimer();
        t.Start(Five, T0);
        t.Reset();
        Assert.False(t.IsSet);
        Assert.False(t.IsFinished(T0.AddHours(1)));
    }

    [Theory]
    [InlineData(300, "5:00")]
    [InlineData(299.2, "5:00")]
    [InlineData(59.1, "1:00")]
    [InlineData(0.4, "0:01")]
    [InlineData(0, "0:00")]
    [InlineData(3725, "1:02:05")]
    public void Format_rounds_up_to_whole_seconds(double seconds, string expected) =>
        Assert.Equal(expected, CountdownTimer.Format(TimeSpan.FromSeconds(seconds)));
}
