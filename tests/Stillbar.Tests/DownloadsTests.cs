using Stillbar.Core.Downloads;

namespace Stillbar.Tests;

public class DownloadsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string Temp = @"C:\D\film.mkv.crdownload";

    private static Dictionary<string, long> Files(params (string Path, long Size)[] f) => f.ToDictionary(x => x.Path, x => x.Size);

    [Theory]
    [InlineData(@"C:\D\a.zip.crdownload", true)]
    [InlineData(@"C:\D\a.zip.part", true)]
    [InlineData(@"C:\D\a.zip", false)]
    [InlineData(@"C:\D\a.tmp", false)]
    public void Recognises_browser_temp_files(string path, bool partial) => Assert.Equal(partial, DownloadTracker.IsPartial(path));

    [Fact]
    public void Reports_size_and_speed_while_running()
    {
        var t = new DownloadTracker();
        t.Update(T0, Files((Temp, 0)), _ => false);
        t.Update(T0.AddSeconds(1), Files((Temp, 20_000_000)), _ => false);
        var d = t.Primary!;
        Assert.Equal("film.mkv", d.Name);
        Assert.Equal(20_000_000, d.Bytes);
        Assert.Equal(20_000_000, d.BytesPerSecond, 1);
        Assert.False(d.Completed);
        Assert.True(t.HasActive);
    }

    [Fact]
    public void Finished_when_temp_goes_and_final_exists_then_clears()
    {
        var t = new DownloadTracker();
        t.Update(T0, Files((Temp, 10)), _ => false);
        t.Update(T0.AddSeconds(1), Files(), p => p == @"C:\D\film.mkv");
        Assert.True(t.Primary!.Completed);
        Assert.False(t.HasActive);
        t.Update(T0.AddSeconds(1) + DownloadTracker.ShowCompletedFor + TimeSpan.FromSeconds(1), Files(), _ => true);
        Assert.Null(t.Primary);
    }

    [Fact]
    public void Cancelled_download_just_disappears()
    {
        var t = new DownloadTracker();
        t.Update(T0, Files((Temp, 10)), _ => false);
        t.Update(T0.AddSeconds(1), Files(), _ => false);
        Assert.Null(t.Primary);
    }

    [Fact]
    public void Newest_running_download_wins()
    {
        var t = new DownloadTracker();
        t.Update(T0, Files((@"C:\D\old.zip.part", 1)), _ => false);
        t.Update(T0.AddSeconds(2), Files((@"C:\D\old.zip.part", 2), (@"C:\D\new.iso.crdownload", 1)), _ => false);
        Assert.Equal("new.iso", t.Primary!.Name);
    }

    [Theory]
    [InlineData(900, "900 Б")]
    [InlineData(870_400, "850 КБ")]
    [InlineData(432_013_312, "412 МБ")]
    [InlineData(1_288_490_189, "1,2 ГБ")]
    public void Sizes_in_russian(long bytes, string expected) => Assert.Equal(expected, SizeFormat.Bytes(bytes));

    [Fact]
    public void Middle_ellipsis_keeps_extension() =>
        Assert.Equal("Фото с отпуска в Сочи….zip",
            SizeFormat.MiddleEllipsis("Фото с отпуска в Сочи летом 2026 года.zip", 26));

    [Fact]
    public void Short_names_are_untouched() => Assert.Equal("a.zip", SizeFormat.MiddleEllipsis("a.zip", 26));
}
