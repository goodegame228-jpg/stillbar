using System.IO;
using Stillbar.Core.Commands;
using Stillbar.Core.Storage;

namespace Stillbar.App.Services;

/// <summary>A saved list the view model owns the logic of (notes, reminders): read it, replace it, hear about changes.</summary>
public interface IListStore<T>
{
    IReadOnlyList<T> Items { get; }
    event EventHandler? Changed;
    void Set(IEnumerable<T> items);
}

/// <summary>Backed by a JSON file in %AppData%\Stillbar.</summary>
public sealed class FileListStore<T>(string directory, string fileName) : IListStore<T>
{
    private readonly JsonListStore<T> _file = new(directory, fileName);
    private List<T>? _items;

    public IReadOnlyList<T> Items => _items ??= _file.Load();
    public event EventHandler? Changed;

    public void Set(IEnumerable<T> items)
    {
        _items = items.ToList();
        try { _file.Save(_items); }
        catch (IOException ex) { Log.Warn("store", $"{fileName}: {ex.Message}"); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Kept in memory only: snapshots and tests.</summary>
public sealed class MemoryListStore<T>(IEnumerable<T>? items = null) : IListStore<T>
{
    private List<T> _items = items?.ToList() ?? [];
    public IReadOnlyList<T> Items => _items;
    public event EventHandler? Changed;

    public void Set(IEnumerable<T> items)
    {
        _items = items.ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// What the command bar can open: Start menu shortcuts (yours and everyone's) and files on the Desktop, in
/// Documents and Downloads (two levels deep). Built in the background, refreshed every few minutes.
/// </summary>
public sealed class LaunchIndex
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private volatile IReadOnlyList<Launchable> _items = [];
    private DateTimeOffset _built = DateTimeOffset.MinValue;
    private Task? _building;

    public IReadOnlyList<Launchable> Items
    {
        get
        {
            if (DateTimeOffset.Now - _built > MaxAge && _building is not { IsCompleted: false }) _building = Task.Run(Build);
            return _items;
        }
    }

    private void Build()
    {
        var list = new List<Launchable>();
        foreach (var root in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
            foreach (var f in Files(Environment.GetFolderPath(root), 6).Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                // Uninstallers and help links are noise in a launcher.
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("удал", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new Launchable(name, f, IsApp: true));
            }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), downloads })
            foreach (var f in Files(root, 2).Take(3000))
                if (!Path.GetFileName(f).StartsWith('.') && !f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                    list.Add(new Launchable(Path.GetFileName(f), f, IsApp: f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)));
        _items = list;
        _built = DateTimeOffset.Now;
    }

    private static IEnumerable<string> Files(string dir, int depth)
    {
        if (depth < 0 || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(dir);
            dirs = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
            foreach (var f in Files(d, depth - 1)) yield return f;
    }
}
