namespace Stillbar.Core.Clipboard;

public enum ClipKind { Text, Image }

/// <summary>
/// One thing that was copied. Text items keep the text; image items keep a PNG on disk (ImageFile) and a content
/// hash, so copying the same picture twice is one entry. Source is the app it was copied from, when known.
/// </summary>
public sealed record ClipEntry(
    string Id,
    ClipKind Kind,
    string Text,
    DateTimeOffset At,
    string? Source = null,
    string? ImageFile = null,
    string? Hash = null,
    bool Pinned = false);

/// <summary>
/// The history behind the Clipboard tab: newest first, the same content copied again moves up instead of
/// repeating, pinned items survive Clear and the size limit. Every change returns the entries that fell out,
/// so the caller can delete their image files.
/// </summary>
public sealed class ClipHistory
{
    public const int MaxUnpinned = 200;
    /// <summary>Longer texts are kept whole but are not worth a history entry past this (a whole log file, a dump).</summary>
    public const int MaxTextLength = 100_000;

    private readonly List<ClipEntry> _items;

    public ClipHistory(IEnumerable<ClipEntry>? items = null) =>
        _items = (items ?? []).OrderByDescending(e => e.At).ToList();

    public IReadOnlyList<ClipEntry> Items => _items;

    public (ClipEntry? Added, List<ClipEntry> Dropped) AddText(string text, string? source, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return (null, []);
        int i = _items.FindIndex(e => e.Kind == ClipKind.Text && e.Text == text);
        return Put(i, i >= 0 ? _items[i] with { At = at, Source = source ?? _items[i].Source }
                             : new ClipEntry(NewId(), ClipKind.Text, text, at, source));
    }

    public (ClipEntry? Added, List<ClipEntry> Dropped) AddImage(string file, string hash, int width, int height, string? source, DateTimeOffset at)
    {
        int i = _items.FindIndex(e => e.Kind == ClipKind.Image && e.Hash == hash);
        var dropped = new List<ClipEntry>();
        if (i >= 0 && _items[i].ImageFile != file) dropped.Add(_items[i] with { Pinned = false }); // the new file replaces the old one
        var (added, more) = Put(i, i >= 0 ? _items[i] with { At = at, ImageFile = file, Source = source ?? _items[i].Source }
                                          : new ClipEntry(NewId(), ClipKind.Image, $"Изображение {width}×{height}", at, source, file, hash));
        dropped.AddRange(more);
        return (added, dropped);
    }

    public void SetPinned(string id, bool pinned)
    {
        int i = _items.FindIndex(e => e.Id == id);
        if (i >= 0) _items[i] = _items[i] with { Pinned = pinned };
    }

    public ClipEntry? Remove(string id)
    {
        int i = _items.FindIndex(e => e.Id == id);
        if (i < 0) return null;
        var e = _items[i];
        _items.RemoveAt(i);
        return e;
    }

    /// <summary>Removes everything that is not pinned.</summary>
    public List<ClipEntry> Clear()
    {
        var dropped = _items.Where(e => !e.Pinned).ToList();
        _items.RemoveAll(e => !e.Pinned);
        return dropped;
    }

    /// <summary>What the tab shows: pinned first, then newest first; a query matches text anywhere, any case.</summary>
    public IReadOnlyList<ClipEntry> View(string? query)
    {
        IEnumerable<ClipEntry> items = _items;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            items = items.Where(e => e.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                     || (e.Source?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }
        return items.OrderByDescending(e => e.Pinned).ThenByDescending(e => e.At).ToList();
    }

    private (ClipEntry Added, List<ClipEntry> Dropped) Put(int existing, ClipEntry entry)
    {
        if (existing >= 0) _items.RemoveAt(existing);
        _items.Insert(0, entry);
        var dropped = _items.Where(e => !e.Pinned).Skip(MaxUnpinned).ToList();
        foreach (var d in dropped) _items.Remove(d);
        return (entry, dropped);
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public static class RelativeTime
{
    /// <summary>«только что», «5 мин назад», «2 ч назад», «вчера, 14:05», «12 окт, 09:30».</summary>
    public static string Format(DateTimeOffset at, DateTimeOffset now)
    {
        var d = now - at;
        if (d < TimeSpan.FromMinutes(1)) return "только что";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} мин назад";
        if (at.Date == now.Date) return $"{(int)d.TotalHours} ч назад";
        if (at.Date == now.Date.AddDays(-1)) return $"вчера, {at:HH:mm}";
        return $"{at.Day} {Months[at.Month - 1]}, {at:HH:mm}";
    }

    private static readonly string[] Months = ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];
}
