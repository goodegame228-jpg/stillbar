using Stillbar.Core.Clipboard;
using Stillbar.Core.Shelf;
using Stillbar.Core.Storage;

namespace Stillbar.Tests;

public class ClipHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Newest_first_and_the_same_text_moves_up_instead_of_repeating()
    {
        var h = new ClipHistory();
        h.AddText("a", "Chrome", T0);
        h.AddText("b", null, T0.AddMinutes(1));
        h.AddText("a", null, T0.AddMinutes(2));
        Assert.Equal(["a", "b"], h.Items.Select(e => e.Text));
        Assert.Equal("Chrome", h.Items[0].Source); // an unknown source does not erase a known one
        Assert.Equal(T0.AddMinutes(2), h.Items[0].At);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    public void Blank_text_is_not_recorded(string text) => Assert.Null(new ClipHistory().AddText(text, null, T0).Added);

    [Fact]
    public void Huge_text_is_not_recorded() =>
        Assert.Null(new ClipHistory().AddText(new string('x', ClipHistory.MaxTextLength + 1), null, T0).Added);

    [Fact]
    public void Limit_drops_the_oldest_unpinned_and_keeps_pinned()
    {
        var h = new ClipHistory();
        var first = h.AddText("first", null, T0).Added!;
        h.SetPinned(first.Id, true);
        var second = h.AddText("second", null, T0.AddSeconds(1)).Added!;
        List<ClipEntry> dropped = [];
        for (int i = 0; i < ClipHistory.MaxUnpinned; i++) dropped.AddRange(h.AddText($"n{i}", null, T0.AddSeconds(2 + i)).Dropped);
        Assert.Equal([second.Id], dropped.Select(d => d.Id));
        Assert.Contains(h.Items, e => e.Id == first.Id);
        Assert.Equal(ClipHistory.MaxUnpinned + 1, h.Items.Count);
    }

    [Fact]
    public void Same_picture_twice_is_one_entry_and_the_old_file_is_reported()
    {
        var h = new ClipHistory();
        h.AddImage(@"c:\clips\a.png", "hash1", 800, 600, "Paint", T0);
        var (added, dropped) = h.AddImage(@"c:\clips\b.png", "hash1", 800, 600, null, T0.AddMinutes(1));
        Assert.Single(h.Items);
        Assert.Equal(@"c:\clips\b.png", added!.ImageFile);
        Assert.Equal("Изображение 800×600", added.Text);
        Assert.Equal([@"c:\clips\a.png"], dropped.Select(d => d.ImageFile));
    }

    [Fact]
    public void Clear_keeps_pinned_and_returns_the_rest()
    {
        var h = new ClipHistory();
        var keep = h.AddText("keep", null, T0).Added!;
        h.AddText("go", null, T0.AddSeconds(1));
        h.SetPinned(keep.Id, true);
        Assert.Equal(["go"], h.Clear().Select(e => e.Text));
        Assert.Equal(["keep"], h.Items.Select(e => e.Text));
    }

    [Fact]
    public void View_puts_pinned_first_and_searches_text_and_source_in_any_case()
    {
        var h = new ClipHistory();
        var old = h.AddText("Адрес: Тверская, 7", null, T0).Added!;
        h.AddText("https://example.com", "Google Chrome", T0.AddMinutes(1));
        h.AddText("пароль от wi-fi", null, T0.AddMinutes(2));
        h.SetPinned(old.Id, true);

        Assert.Equal("Адрес: Тверская, 7", h.View(null)[0].Text);
        Assert.Equal(["Адрес: Тверская, 7"], h.View("тверская").Select(e => e.Text));
        Assert.Equal(["https://example.com"], h.View("chrome").Select(e => e.Text));
        Assert.Empty(h.View("нет такого"));
    }

    [Fact]
    public void Remove_returns_the_entry_once()
    {
        var h = new ClipHistory();
        var e = h.AddText("x", null, T0).Added!;
        Assert.Equal(e, h.Remove(e.Id));
        Assert.Null(h.Remove(e.Id));
    }

    [Theory]
    [InlineData(0, "только что")]
    [InlineData(5, "5 мин назад")]
    [InlineData(150, "2 ч назад")]
    public void Relative_time_today(int minutesAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Format(T0.AddMinutes(-minutesAgo), T0));

    [Fact]
    public void Relative_time_yesterday_and_earlier()
    {
        Assert.Equal("вчера, 09:15", RelativeTime.Format(new DateTimeOffset(2026, 10, 26, 9, 15, 0, T0.Offset), T0));
        Assert.Equal("3 окт, 18:40", RelativeTime.Format(new DateTimeOffset(2026, 10, 3, 18, 40, 0, T0.Offset), T0));
    }

    [Fact]
    public void History_round_trips_through_its_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stillbar-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonListStore<ClipEntry>(dir, "clipboard.json");
            var h = new ClipHistory();
            var e = h.AddText("Москва → «кавычки»", "Блокнот", T0).Added!;
            h.SetPinned(e.Id, true);
            store.Save(h.Items);
            var back = new ClipHistory(store.Load());
            Assert.Equal(h.Items, back.Items);
            Assert.Contains("«кавычки»", File.ReadAllText(store.FilePath)); // readable, not \u-escaped
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Broken_file_is_kept_aside_and_reads_as_empty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stillbar-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "clipboard.json"), "{ not json");
            Assert.Empty(new JsonListStore<ClipEntry>(dir, "clipboard.json").Load());
            Assert.True(File.Exists(Path.Combine(dir, "clipboard.json.bad")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class ShelfTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Newest_first_and_no_duplicates_in_any_case()
    {
        var s = ShelfList.Add([], [@"C:\a.txt", @"C:\b.txt"], T0);
        s = ShelfList.Add(s, [@"c:\A.TXT"], T0.AddMinutes(1));
        Assert.Equal([@"c:\A.TXT", @"C:\b.txt"], s.Select(i => i.Path));
    }

    [Fact]
    public void Limit_keeps_the_newest()
    {
        var s = ShelfList.Add([], Enumerable.Range(0, ShelfList.MaxItems + 5).Select(i => $@"C:\f{i}"), T0);
        Assert.Equal(ShelfList.MaxItems, s.Count);
        Assert.Equal($@"C:\f{ShelfList.MaxItems + 4}", s[0].Path);
    }

    [Fact]
    public void Remove_and_missing_files()
    {
        var s = ShelfList.Add([], [@"C:\a", @"C:\b"], T0);
        Assert.Equal([@"C:\a"], ShelfList.Remove(s, @"c:\B").Select(i => i.Path));
        Assert.Equal([@"C:\b"], ShelfList.Existing(s, p => p.EndsWith('b')).Select(i => i.Path));
    }
}
