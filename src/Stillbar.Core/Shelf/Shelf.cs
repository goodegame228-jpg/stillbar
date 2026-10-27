namespace Stillbar.Core.Shelf;

/// <summary>A file parked on the shelf. Only the path is kept: the file itself stays where it is.</summary>
public sealed record ShelfItem(string Path, DateTimeOffset Added);

/// <summary>The file shelf: drop files in, drag them out later. Newest first, no duplicates, limited size.</summary>
public static class ShelfList
{
    public const int MaxItems = 30;

    public static List<ShelfItem> Add(IReadOnlyList<ShelfItem> items, IEnumerable<string> paths, DateTimeOffset at)
    {
        var list = items.ToList();
        foreach (var p in paths)
        {
            list.RemoveAll(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, new ShelfItem(p, at));
        }
        return list.Take(MaxItems).ToList();
    }

    public static List<ShelfItem> Remove(IReadOnlyList<ShelfItem> items, string path) =>
        items.Where(i => !string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Files moved or deleted since they were parked are dropped when the shelf is shown.</summary>
    public static List<ShelfItem> Existing(IReadOnlyList<ShelfItem> items, Func<string, bool> exists) =>
        items.Where(i => exists(i.Path)).ToList();
}
