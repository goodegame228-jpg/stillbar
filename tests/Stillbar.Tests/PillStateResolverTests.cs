using Stillbar.Core.Pill;

namespace Stillbar.Tests;

public class PillStateResolverTests
{
    [Fact]
    public void Nothing_active_is_idle() =>
        Assert.Equal(PillKind.Idle, PillStateResolver.Resolve(new PillInputs()));

    [Fact]
    public void Playing_music_shows_music() =>
        Assert.Equal(PillKind.Music, PillStateResolver.Resolve(new PillInputs(MediaPlaying: true)));

    [Fact]
    public void Running_timer_beats_music() =>
        Assert.Equal(PillKind.Timer, PillStateResolver.Resolve(new PillInputs(TimerRunning: true, MediaPlaying: true)));

    [Fact]
    public void Download_beats_timer_and_music() =>
        Assert.Equal(PillKind.Download,
            PillStateResolver.Resolve(new PillInputs(DownloadActive: true, TimerRunning: true, MediaPlaying: true)));

    [Fact]
    public void Finished_timer_beats_everything() =>
        Assert.Equal(PillKind.Alert,
            PillStateResolver.Resolve(new PillInputs(true, true, true, true)));
}
