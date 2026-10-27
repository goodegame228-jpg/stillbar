using System.Runtime.InteropServices;
using Stillbar.Core.Display;

namespace Stillbar.App.Shell;

/// <summary>Connected monitors with their pixel rectangles and DPI (user32 + shcore).</summary>
internal static partial class Monitors
{
    public static IReadOnlyList<MonitorArea> All()
    {
        var list = new List<MonitorArea>();
        EnumDisplayMonitors(0, 0, (h, _, _, _) =>
        {
            if (Read(h) is { } m) list.Add(m);
            return true;
        }, 0);
        return list;
    }

    /// <summary>Device name of the monitor a window is mostly on (\\.\DISPLAY1).</summary>
    public static string? NameOf(nint hwnd) => Read(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST))?.Name;

    private static unsafe MonitorArea? Read(nint monitor)
    {
        var mi = new MONITORINFOEXW { cbSize = sizeof(MONITORINFOEXW) };
        if (!GetMonitorInfoW(monitor, ref mi)) return null;
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return new MonitorArea(
            new string(mi.szDevice).TrimEnd('\0'),
            new PixelRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
            new PixelRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
            scale,
            (mi.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFOEXW info);

    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
