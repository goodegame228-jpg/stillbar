using System.Runtime.InteropServices;

namespace Stillbar.App.Shell;

/// <summary>The few user32 calls the bar needs.</summary>
internal static partial class NativeWindow
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
    private static readonly nint HWND_TOPMOST = -1;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// NOACTIVATE: clicks and hover reach us, but focus stays in the window the user is typing in.
    /// TOOLWINDOW: no taskbar button, not in Alt+Tab.
    /// </summary>
    public static void MakeNoActivateToolWindow(nint hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    /// <summary>Other topmost windows (taskbar, overlays) can push us down; re-assert without stealing focus.</summary>
    public static void KeepTopmost(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    /// <summary>
    /// Typing mode: a text field in the panel needs keyboard focus, which a NOACTIVATE window never gets. Allow
    /// activation for as long as the field is in use; the caller gives focus back afterwards.
    /// </summary>
    public static void AllowActivation(nint hwnd, bool allow)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = allow ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)ex);
    }

    public static nint Foreground() => GetForegroundWindow();

    private const long WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>Click-through: every click goes to the window below, even on the black shape.</summary>
    public static void SetTransparent(nint hwnd, bool on)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT));
    }

    /// <summary>Ctrl held right now (the async state: we do not have the keyboard).</summary>
    public static bool CtrlDown() => (GetAsyncKeyState(0x11) & 0x8000) != 0;

    public static int Left(nint hwnd) => GetWindowRect(hwnd, out var r) ? r.Left : 0;
    public static int Top(nint hwnd) => GetWindowRect(hwnd, out var r) ? r.Top : 0;

    public static bool CursorIn(nint hwnd) =>
        GetCursorPos(out var p) && GetWindowRect(hwnd, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint hwnd, out RECT r);

    /// <summary>Dark title bar for our ordinary windows (settings, markup), as Windows 10 2004+ and 11 allow.</summary>
    public static void DarkTitleBar(nint hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")] private static partial int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>A system-wide shortcut with Space: Alt (+Ctrl). False if another app already holds it.</summary>
    public static bool RegisterHotkey(nint hwnd, int id, bool alt, bool ctrl) =>
        RegisterHotKey(hwnd, id, (alt ? 0x1u : 0) | (ctrl ? 0x2u : 0) | 0x4000u /* MOD_NOREPEAT */, 0x20 /* VK_SPACE */);

    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    /// <summary>Place and size in physical pixels (full-screen overlays on any monitor).</summary>
    public static void Cover(nint hwnd, int x, int y, int w, int h) =>
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOOWNERZORDER);

    /// <summary>Allowed for us because the user's last input (the click) went to our window.</summary>
    public static bool Focus(nint hwnd) => IsWindow(hwnd) && SetForegroundWindow(hwnd);

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint hwnd);

    /// <summary>Move in physical pixels: WPF's Left/Top are in the primary monitor's DIPs, which is wrong on a monitor with another scale.</summary>
    public static void MoveTo(nint hwnd, int x, int y) =>
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
}
