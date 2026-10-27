using System.Runtime.InteropServices;
using System.Windows.Threading;
using Stillbar.Core.Display;

namespace Stillbar.App.Services;

/// <summary>Polls once a second whether the bar's monitor is taken by a full-screen app. Cheap: a handful of user32 calls.</summary>
public sealed partial class FullscreenDetector
{
    private readonly DispatcherTimer _timer;
    private readonly Func<string?> _barMonitor;
    private readonly Func<IReadOnlyList<string>> _excluded;
    private readonly Func<bool> _hideInFullscreen;
    private (uint Pid, string Name) _lastProcess;

    /// <param name="barMonitor">Device name of the monitor the bar is on; null means the primary one.</param>
    /// <param name="excluded">Process names that hide the bar while they are in front (settings).</param>
    /// <param name="hideInFullscreen">Read on every poll, so the setting applies at once.</param>
    public FullscreenDetector(Dispatcher ui, Func<string?> barMonitor, Func<IReadOnlyList<string>>? excluded = null, Func<bool>? hideInFullscreen = null)
    {
        _barMonitor = barMonitor;
        _excluded = excluded ?? (() => []);
        _hideInFullscreen = hideInFullscreen ?? (() => true);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Poll(), ui);
    }

    public bool IsFullscreen { get; private set; }
    public event EventHandler? Changed;

    public void Start()
    {
        Poll();
        _timer.Start();
    }

    private void Poll()
    {
        bool now = (_hideInFullscreen() && FullscreenRule.ShouldHide(Read()))
                   || Stillbar.Core.Controls.Exclusions.Matches(_excluded(), ForegroundProcess());
        if (now == IsFullscreen) return;
        IsFullscreen = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private unsafe ForegroundInfo Read()
    {
        SHQueryUserNotificationState(out int state);
        nint fg = GetForegroundWindow();
        if (fg == 0) return new ForegroundInfo(state, default, default, true, IsShellOrDesktop: true);

        GetWindowRect(fg, out var wr);
        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        GetMonitorInfoW(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), ref mi);

        char* cls = stackalloc char[64];
        int n = GetClassNameW(fg, cls, 64);
        var name = new string(cls, 0, n);
        bool shell = fg == GetShellWindow() || name is "Progman" or "WorkerW" or "Shell_TrayWnd";

        return new ForegroundInfo(
            state,
            new PixelRect(wr.Left, wr.Top, wr.Right, wr.Bottom),
            new PixelRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
            _barMonitor() is { } bar ? Shell.Monitors.NameOf(fg) == bar : (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
            shell);
    }

    /// <summary>Process name of the window in front; cached by process id (this runs every second).</summary>
    private string? ForegroundProcess()
    {
        if (_excluded().Count == 0) return null;
        nint fg = GetForegroundWindow();
        if (fg == 0) return null;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        if (_lastProcess.Pid == pid) return _lastProcess.Name;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            _lastProcess = (pid, p.ProcessName);
            return p.ProcessName;
        }
        catch (ArgumentException) { return null; }
    }

    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [LibraryImport("shell32.dll")] private static partial int SHQueryUserNotificationState(out int state);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial nint GetShellWindow();
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [LibraryImport("user32.dll")] private static unsafe partial int GetClassNameW(nint hwnd, char* name, int max);
}
