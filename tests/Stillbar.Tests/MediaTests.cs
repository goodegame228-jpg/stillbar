using Stillbar.Core.Media;

namespace Stillbar.Tests;

public class MediaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static MediaSnapshot Track(bool playing, double pos, double dur = 200) =>
        new("Song", "Artist", "Album", playing, TimeSpan.FromSeconds(pos), TimeSpan.FromSeconds(dur), T0, "app");

    [Fact]
    public void Playing_position_moves_on_from_last_report() =>
        Assert.Equal(TimeSpan.FromSeconds(15), Track(true, 10).PositionAt(T0.AddSeconds(5)));

    [Fact]
    public void Paused_position_stays() =>
        Assert.Equal(TimeSpan.FromSeconds(10), Track(false, 10).PositionAt(T0.AddSeconds(5)));

    [Fact]
    public void Position_never_runs_past_the_end() =>
        Assert.Equal(TimeSpan.FromSeconds(200), Track(true, 195).PositionAt(T0.AddSeconds(60)));

    [Fact]
    public void None_has_no_session() => Assert.False(MediaSnapshot.None.HasSession);

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(760, "12:40")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "0:00")]
    public void Clock_format(double seconds, string expected) =>
        Assert.Equal(expected, TimeFormat.Clock(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Remaining_uses_a_real_minus_sign() =>
        Assert.Equal("−2:08", TimeFormat.Remaining(TimeSpan.FromSeconds(86), TimeSpan.FromSeconds(214)));
}
