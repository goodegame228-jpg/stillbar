using Stillbar.Core.Media;

namespace Stillbar.Tests;

public class LightThemeTests
{
    private static readonly Rgb LightTrack = Rgb.FromHex("#C9C9CF");
    private static readonly Rgb DarkTrack = Rgb.FromHex("#363636");

    [Theory]
    [InlineData("#6FD3B8")] // pale teal cover
    [InlineData("#F7E26B")] // yellow
    [InlineData("#202060")] // dark navy
    [InlineData("#808080")] // grey
    public void Progress_reaches_three_to_one_on_both_tracks(string art)
    {
        Assert.True(ArtColors.Contrast(ArtColors.Progress(Rgb.FromHex(art), LightTrack), LightTrack) >= 3.0);
        Assert.True(ArtColors.Contrast(ArtColors.Progress(Rgb.FromHex(art), DarkTrack), DarkTrack) >= 3.0);
    }

    [Fact]
    public void On_a_light_track_the_fill_is_darker_than_on_a_dark_one()
    {
        var art = Rgb.FromHex("#6FD3B8");
        var onLight = ArtColors.Progress(art, LightTrack);
        var onDark = ArtColors.Progress(art, DarkTrack);
        Assert.True(onLight.R + onLight.G + onLight.B < onDark.R + onDark.G + onDark.B);
    }
}
