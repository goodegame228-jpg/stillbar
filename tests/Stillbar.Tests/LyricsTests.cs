using Stillbar.Core.Lyrics;

namespace Stillbar.Tests;

public class LyricsTests
{
    private const string Sample = """
        [ar:Someone]
        [ti:Song]
        [00:12.50]First line
        [00:17.2]Second line
        [00:21.345][01:05.00]Chorus
        [00:30.00]
        not a lyric line
        """;

    [Fact]
    public void Parses_times_in_all_fraction_lengths_and_skips_metadata()
    {
        var lines = Lrc.Parse(Sample);
        Assert.Equal(
            [
                new LrcLine(TimeSpan.FromMilliseconds(12_500), "First line"),
                new LrcLine(TimeSpan.FromMilliseconds(17_200), "Second line"),
                new LrcLine(TimeSpan.FromMilliseconds(21_345), "Chorus"),
                new LrcLine(TimeSpan.FromSeconds(30), ""),
                new LrcLine(TimeSpan.FromSeconds(65), "Chorus"),
            ],
            lines);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(12.5, 0)]
    [InlineData(20, 1)]
    [InlineData(64.9, 3)]
    [InlineData(300, 4)]
    public void Index_at_position(double seconds, int expected) =>
        Assert.Equal(expected, Lrc.IndexAt(Lrc.Parse(Sample), TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("KARADA, сложно найти", "KARADA")]
    [InlineData("Баста & Моя Мишель", "Баста")]
    [InlineData("FRIENDLY THUG 52 NGG, Markul", "FRIENDLY THUG 52 NGG")]
    [InlineData("Artist feat. Guest", "Artist")]
    [InlineData("Artist ft. Guest", "Artist")]
    [InlineData("Нервы и Слот", "Нервы")]
    [InlineData("Imagine Dragons", "Imagine Dragons")]
    [InlineData("AC/DC", "AC/DC")]
    public void Primary_artist(string all, string first) => Assert.Equal(first, Artists.Primary(all));

    [Fact]
    public void Get_url_escapes_and_rounds_duration()
    {
        var url = LrcLib.GetUrl("Би-2", "Мой рок-н-ролл", "", TimeSpan.FromSeconds(214.6));
        Assert.Equal(
            "https://lrclib.net/api/get?artist_name=%D0%91%D0%B8-2&track_name=%D0%9C%D0%BE%D0%B9%20%D1%80%D0%BE%D0%BA-%D0%BD-%D1%80%D0%BE%D0%BB%D0%BB&duration=215",
            url);
    }

    [Fact]
    public void Record_with_synced_lyrics()
    {
        var r = LrcLib.ParseRecord("""{"id":1,"instrumental":false,"plainLyrics":"a","syncedLyrics":"[00:01.00]a\n[00:02.00]b"}""");
        Assert.Equal(LyricsKind.Synced, r.Kind);
        Assert.Equal(2, r.Lines.Count);
    }

    [Fact]
    public void Record_with_only_plain_lyrics()
    {
        var r = LrcLib.ParseRecord("""{"instrumental":false,"plainLyrics":"one\n\ntwo","syncedLyrics":null}""");
        Assert.Equal(LyricsKind.Plain, r.Kind);
        Assert.Equal(["one", "two"], r.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Instrumental_and_not_found()
    {
        Assert.Equal(LyricsKind.Instrumental, LrcLib.ParseRecord("""{"instrumental":true,"plainLyrics":null,"syncedLyrics":null}""").Kind);
        Assert.Equal(LyricsKind.NotFound, LrcLib.ParseRecord("""{"code":404,"name":"TrackNotFound"}""").Kind);
        Assert.Equal(LyricsKind.NotFound, LrcLib.ParseRecord("<html>"). Kind);
    }

    [Fact]
    public void Search_skips_wrong_duration_and_prefers_synced()
    {
        const string json = """
            [
              {"duration":100,"syncedLyrics":"[00:01.00]wrong length","plainLyrics":"x"},
              {"duration":214,"syncedLyrics":null,"plainLyrics":"plain only"},
              {"duration":215,"syncedLyrics":"[00:01.00]right","plainLyrics":"right"}
            ]
            """;
        var r = LrcLib.ParseSearch(json, TimeSpan.FromSeconds(214.6));
        Assert.Equal(LyricsKind.Synced, r.Kind);
        Assert.Equal("right", r.Lines[0].Text);
    }
}
