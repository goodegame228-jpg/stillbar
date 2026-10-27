using System.Text.Json;

namespace Stillbar.Core.Dock;

public sealed record DockEntry(string Path);

/// <summary>%AppData%\Stillbar\dock.json: the app shortcuts on the Home tab, in order.</summary>
public sealed class DockStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const int MaxItems = 12;

    public string FilePath { get; } = System.IO.Path.Combine(directory, "dock.json");

    public bool Exists => File.Exists(FilePath);

    public List<DockEntry> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DockEntry>>(File.ReadAllText(FilePath), Json)?
                .Where(e => !string.IsNullOrWhiteSpace(e.Path)).ToList() ?? [];
        }
        catch (JsonException)
        {
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            return [];
        }
    }

    public void Save(IReadOnlyList<DockEntry> entries)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Adds at the end, skipping duplicates (paths compare case-insensitively) and anything past the limit.</summary>
    public static List<DockEntry> Add(IReadOnlyList<DockEntry> entries, IEnumerable<string> paths)
    {
        var list = entries.ToList();
        foreach (var p in paths)
        {
            if (list.Count >= MaxItems) break;
            if (list.Any(e => string.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new DockEntry(p));
        }
        return list;
    }

    public static List<DockEntry> Remove(IReadOnlyList<DockEntry> entries, string path) =>
        entries.Where(e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>What can sit in the dock: programs and shortcuts.</summary>
    public static bool IsLaunchable(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
}
