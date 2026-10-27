using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Stillbar.Core.Downloads;

namespace Stillbar.App.Services;

public interface IDownloadSource
{
    DownloadInfo? Primary { get; }
    event EventHandler? Changed;
}

/// <summary>
/// Watches the Downloads folder for browser temp files. A FileSystemWatcher wakes it up; while anything is
/// downloading it polls the known temp files once a second for size and speed. Idle, it costs nothing.
/// </summary>
public sealed partial class DownloadWatcher : IDownloadSource, IDisposable
{
    private readonly string _folder;
    private readonly DownloadTracker _tracker = new();
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _poll;
    private FileSystemWatcher? _watcher;

    public DownloadWatcher(Dispatcher ui)
    {
        _folder = DownloadsFolder();
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Poll(), ui);
    }

    public DownloadInfo? Primary => _tracker.Primary;
    public event EventHandler? Changed;

    public void Start()
    {
        if (!Directory.Exists(_folder)) return;
        foreach (var f in Directory.EnumerateFiles(_folder).Where(DownloadTracker.IsPartial)) _known.Add(f);

        _watcher = new FileSystemWatcher(_folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
        _watcher.Created += (_, e) => Wake(e.FullPath);
        _watcher.Renamed += (_, e) => Wake(e.FullPath);
        _watcher.EnableRaisingEvents = true;
        if (_known.Count > 0) _poll.Start();
    }

    public void Dispose() => _watcher?.Dispose();

    private void Wake(string path)
    {
        // Watcher events come on a pool thread; the timer lives on the UI thread.
        _poll.Dispatcher.InvokeAsync(() =>
        {
            if (DownloadTracker.IsPartial(path)) _known.Add(path);
            if (!_poll.IsEnabled) { _poll.Start(); Poll(); }
        });
    }

    private void Poll()
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _known.ToList())
        {
            var fi = new FileInfo(path);
            if (fi.Exists) sizes[path] = fi.Length;
            else _known.Remove(path);
        }

        if (_tracker.Update(DateTimeOffset.Now, sizes, File.Exists) || _tracker.HasActive)
            Changed?.Invoke(this, EventArgs.Empty);
        if (_tracker.IsEmpty && _known.Count == 0) _poll.Stop();
    }

    private static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, 0, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p)!; }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch (EntryPointNotFoundException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}

/// <summary>A running download for --snapshot renders.</summary>
public sealed class SampleDownloadSource : IDownloadSource
{
    public DownloadInfo? Primary { get; } = new(@"C:\D\Фото с отпуска в Сочи летом.zip.crdownload",
        "Фото с отпуска в Сочи летом.zip", 432_013_312, 22_020_096, false);
    public event EventHandler? Changed { add { } remove { } }
}

public sealed class NoDownloads : IDownloadSource
{
    public DownloadInfo? Primary => null;
    public event EventHandler? Changed { add { } remove { } }
}
