using System.Globalization;

namespace Stillbar.Core.Downloads;

public sealed record DownloadInfo(string TempPath, string Name, long Bytes, double BytesPerSecond, bool Completed);

/// <summary>
/// Follows browser downloads by their temp files in the Downloads folder:
/// Chrome/Edge write name.ext.crdownload, Firefox name.ext.part. When the temp file goes away and the final
/// file exists, the download finished; if neither exists it was cancelled.
/// </summary>
public sealed class DownloadTracker
{
    public static readonly TimeSpan ShowCompletedFor = TimeSpan.FromSeconds(4);
    private static readonly string[] PartialExtensions = [".crdownload", ".part", ".download"];

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public required string TempPath;
        public long Bytes;
        public double Speed;
        public DateTimeOffset Seen;
        public DateTimeOffset Started;
        public DateTimeOffset? CompletedAt;
    }

    public static bool IsPartial(string path) =>
        PartialExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>"C:\D\film.mkv.crdownload" → "C:\D\film.mkv".</summary>
    public static string FinalPath(string tempPath) =>
        PartialExtensions.FirstOrDefault(e => tempPath.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } ext
            ? tempPath[..^ext.Length]
            : tempPath;

    /// <summary>
    /// One poll: current partial files with their sizes. <paramref name="exists"/> answers whether a final file is on disk.
    /// Returns true if anything visible changed.
    /// </summary>
    public bool Update(DateTimeOffset now, IReadOnlyDictionary<string, long> partials, Func<string, bool> exists)
    {
        bool changed = false;
        foreach (var (path, size) in partials)
        {
            if (!_entries.TryGetValue(path, out var e))
            {
                _entries[path] = new Entry { TempPath = path, Bytes = size, Seen = now, Started = now };
                changed = true;
                continue;
            }
            double dt = (now - e.Seen).TotalSeconds;
            if (dt > 0)
            {
                double instant = Math.Max(0, size - e.Bytes) / dt;
                e.Speed = e.Speed == 0 ? instant : 0.5 * e.Speed + 0.5 * instant; // smooth the jumpy per-second rate
            }
            changed |= size != e.Bytes;
            e.Bytes = size;
            e.Seen = now;
        }

        foreach (var e in _entries.Values.ToList())
        {
            if (e.CompletedAt is { } done)
            {
                if (now - done > ShowCompletedFor) { _entries.Remove(e.TempPath); changed = true; }
                continue;
            }
            if (partials.ContainsKey(e.TempPath)) continue;
            if (exists(FinalPath(e.TempPath)))
            {
                e.CompletedAt = now;
                e.Speed = 0;
            }
            else
            {
                _entries.Remove(e.TempPath); // cancelled
            }
            changed = true;
        }
        return changed;
    }

    public bool HasActive => _entries.Values.Any(e => e.CompletedAt is null);
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>What the pill shows: the newest running download, else the newest finished one.</summary>
    public DownloadInfo? Primary
    {
        get
        {
            var e = _entries.Values.Where(x => x.CompletedAt is null).MaxBy(x => x.Started)
                    ?? _entries.Values.MaxBy(x => x.CompletedAt);
            if (e is null) return null;
            var name = Path.GetFileName(FinalPath(e.TempPath));
            return new DownloadInfo(e.TempPath, name, e.Bytes, e.Speed, e.CompletedAt is not null);
        }
    }
}

public static class SizeFormat
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>850 КБ, 412 МБ, 1,2 ГБ. Under 10 of a unit gets one decimal.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double v = bytes;
        int u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1024; u++; }
        string num = u > 0 && v < 10 ? v.ToString("0.#", Ru) : Math.Round(v).ToString("0", Ru);
        return num + " " + units[u];
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/с";

    /// <summary>Shortens in the middle and keeps the extension: "Фото с отпуска….zip".</summary>
    public static string MiddleEllipsis(string name, int max)
    {
        if (name.Length <= max) return name;
        var ext = Path.GetExtension(name);
        if (ext.Length > 8) ext = "";
        int keep = Math.Max(1, max - ext.Length - 1);
        return name[..keep].TrimEnd() + "…" + ext;
    }
}
