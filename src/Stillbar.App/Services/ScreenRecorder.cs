using System.Diagnostics;
using System.IO;
using Stillbar.Core.Capture;
using Stillbar.Core.Display;

namespace Stillbar.App.Services;

public interface IScreenRecorder
{
    bool IsRecording { get; }
    TimeSpan Elapsed { get; }
    /// <summary>Started, stopped, or failed on its own.</summary>
    event EventHandler? Changed;

    /// <summary>Null when recording started; otherwise why not, in words for the user.</summary>
    string? Start();

    /// <summary>The saved file, or null if nothing usable was written.</summary>
    Task<string?> StopAsync();
}

/// <summary>
/// Records the bar's monitor to Videos\Stillbar as MP4 with ffmpeg (gdigrab), if ffmpeg is installed. No ffmpeg
/// is bundled: it is large and its builds carry their own licences; the user installs it once (winget).
/// </summary>
public sealed class FfmpegRecorder(Func<PixelRect> area) : IScreenRecorder
{
    private Process? _ffmpeg;
    private string? _output;
    private DateTimeOffset _started;
    private bool _stopping;

    public bool IsRecording => _ffmpeg is { HasExited: false };
    public TimeSpan Elapsed => IsRecording ? DateTimeOffset.Now - _started : TimeSpan.Zero;
    public event EventHandler? Changed;

    public string? Start()
    {
        if (IsRecording) return null;
        var exe = FindFfmpeg();
        if (exe is null) return "Для записи нужен ffmpeg: winget install Gyan.FFmpeg";

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Stillbar");
        Directory.CreateDirectory(dir);
        _output = Path.Combine(dir, Recording.FileName(DateTimeOffset.Now));
        var start = new ProcessStartInfo(exe, Recording.FfmpegArgs(area(), 60, _output))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        try
        {
            _ffmpeg = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn("record", ex.Message);
            return "Не удалось запустить ffmpeg";
        }
        _stopping = false;
        _started = DateTimeOffset.Now;
        var errors = new System.Text.StringBuilder();
        _ffmpeg.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
        _ffmpeg.BeginErrorReadLine();
        _ffmpeg.EnableRaisingEvents = true;
        _ffmpeg.Exited += (_, _) =>
        {
            if (!_stopping) Log.Warn("record", $"ffmpeg stopped by itself: {errors}");
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Log.Info("record", $"recording to {_output}");
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public async Task<string?> StopAsync()
    {
        var p = _ffmpeg;
        if (p is null) return null;
        _stopping = true;
        try
        {
            if (!p.HasExited)
            {
                await p.StandardInput.WriteAsync("q");
                await p.StandardInput.FlushAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { p.Kill(); }
            }
        }
        catch (IOException) { } // ffmpeg already closed its input
        finally
        {
            p.Dispose();
            _ffmpeg = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return _output is { } f && File.Exists(f) && new FileInfo(f).Length > 0 ? f : null;
    }

    /// <summary>On PATH, or where winget puts its link or the Gyan package.</summary>
    internal static string? FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(link)) return link;
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages)) return null;
        return Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*")
            .SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories))
            .FirstOrDefault();
    }
}

public sealed class NoRecorder : IScreenRecorder
{
    public bool IsRecording => false;
    public TimeSpan Elapsed => TimeSpan.Zero;
    public event EventHandler? Changed { add { } remove { } }
    public string? Start() => "Запись недоступна";
    public Task<string?> StopAsync() => Task.FromResult<string?>(null);
}
