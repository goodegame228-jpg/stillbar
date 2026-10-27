using Stillbar.Core.Display;

namespace Stillbar.Tests;

public class FullscreenRuleTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    private static ForegroundInfo Fg(PixelRect window, int state = 1, bool primary = true, bool shell = false) =>
        new(state, window, Monitor, primary, shell);

    [Fact]
    public void Window_covering_the_monitor_hides() =>
        Assert.True(FullscreenRule.ShouldHide(Fg(new PixelRect(0, 0, 1920, 1080))));

    [Fact]
    public void Maximised_window_leaves_the_taskbar_and_does_not_hide() =>
        Assert.False(FullscreenRule.ShouldHide(Fg(new PixelRect(-8, -8, 1928, 1040))));

    [Fact]
    public void Desktop_covers_the_monitor_but_does_not_hide() =>
        Assert.False(FullscreenRule.ShouldHide(Fg(new PixelRect(0, 0, 1920, 1080), shell: true)));

    [Fact]
    public void Fullscreen_on_another_monitor_does_not_hide() =>
        Assert.False(FullscreenRule.ShouldHide(Fg(new PixelRect(0, 0, 1920, 1080), primary: false)));

    [Theory]
    [InlineData(FullscreenRule.QunsRunningD3DFullScreen)]
    [InlineData(FullscreenRule.QunsPresentationMode)]
    public void Exclusive_games_and_presentations_hide_whatever_the_window(int state) =>
        Assert.True(FullscreenRule.ShouldHide(Fg(new PixelRect(100, 100, 400, 400), state, shell: true)));
}
