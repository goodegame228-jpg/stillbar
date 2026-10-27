using System.Diagnostics;
using System.IO;

namespace Stillbar.App.Services;

/// <summary>
/// Tiny file log at %LocalAppData%\Stillbar\logs\stillbar.log, for what goes wrong with players and services
/// on someone else's machine. Rolls over at 512 KB (one previous file kept). Thread-safe, never throws.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stillbar", "logs", "stillbar.log");

    public static void Info(string area, string message) => Write("info", area, message);
    public static void Warn(string area, string message) => Write("warn", area, message);

    public static void OpenFolder()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private static void Write(string level, string area, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-4} {area,-8} {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
