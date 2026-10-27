using Stillbar.Core.Media;

namespace Stillbar.Tests;

public class ArtColorsTests
{
    private static readonly Rgb Track = Rgb.FromHex("#363636");

    /// <summary>BGRA buffer: <paramref name="share"/> of pixels in <paramref name="main"/>, the rest in <paramref name="rest"/>.</summary>
    private static byte[] Image(Rgb main, double share, Rgb rest, int n = 1024)
    {
        var px = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            var c = i < n * share ? main : rest;
            px[i * 4] = c.B; px[i * 4 + 1] = c.G; px[i * 4 + 2] = c.R; px[i * 4 + 3] = 255;
        }
        return px;
    }

    [Fact]
    public void Picks_the_colour_that_covers_most_of_the_cover()
    {
        var pink = Rgb.FromHex("#E0457B");
        var blue = Rgb.FromHex("#2B4FD8");
        Assert.Equal(pink, ArtColors.Dominant(Image(pink, 0.7, blue)));
    }

    [Fact]
    public void Black_and_grey_do_not_win_over_a_small_colour_area()
    {
        var teal = Rgb.FromHex("#1FA28A");
        Assert.Equal(teal, ArtColors.Dominant(Image(teal, 0.1, Rgb.FromHex("#202020"))));
    }

    [Fact]
    public void Greyscale_cover_has_no_dominant_colour() =>
        Assert.Null(ArtColors.Dominant(Image(Rgb.FromHex("#808080"), 0.5, Rgb.FromHex("#101010"))));

    [Theory]
    [InlineData("#1B2F6B")] // dark navy
    [InlineData("#E0457B")] // pink
    [InlineData("#F7F3A0")] // pale yellow
    [InlineData("#5B0A0A")] // very dark red
    public void Progress_colour_always_reaches_3_to_1_on_the_track(string art)
    {
        var p = ArtColors.Progress(Rgb.FromHex(art), Track);
        Assert.True(ArtColors.Contrast(p, Track) >= 3.0, $"{art} -> {p}: {ArtColors.Contrast(p, Track):0.00}");
    }

    [Fact]
    public void Mix_at_22_percent_stays_close_to_surface()
    {
        var tint = ArtColors.Mix(Rgb.FromHex("#0F0F0F"), Rgb.FromHex("#E0457B"), ArtColors.TintAmount);
        // 15 + (224-15)*0.22 = 61, 15 + (69-15)*0.22 = 27, 15 + (123-15)*0.22 = 39
        Assert.Equal(Rgb.FromHex("#3D1B27"), tint);
    }
}
