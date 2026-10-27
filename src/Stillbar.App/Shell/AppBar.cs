using System.Runtime.InteropServices;
using System.Windows.Interop;
using Stillbar.Core.Display;

namespace Stillbar.App.Shell;

/// <summary>
/// Reserves a strip of the screen the way the taskbar does (SHAppBarMessage), so maximised windows and browser tabs
/// start beside it instead of under it. The registration lives on a small hidden window of its own: the bar's
/// window is see-through and much larger than the strip, and the shell must never move or resize it.
/// </summary>
internal sealed partial class AppBar : IDisposable
{
    private const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    private const int ABN_POSCHANGED = 1;
    private const uint ABE_TOP = 1, ABE_BOTTOM = 3;

    private readonly HwndSource _host;
    private readonly uint _callback;
    private bool _registered;
    private (PixelRect Strip, BarEdge Edge)? _wanted;

    public AppBar()
    {
        _host = new HwndSource(new HwndSourceParameters("Stillbar.AppBar")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x00000080,         // WS_EX_TOOLWINDOW
            Width = 0,
            Height = 0,
        });
        _callback = RegisterWindowMessageW("Stillbar.AppBarMessage");
        _host.AddHook(Hook);
    }

    /// <summary>What the shell actually gave us, in pixels; null when nothing is reserved.</summary>
    public PixelRect? Reserved { get; private set; }

    /// <summary>The shell moved our strip (another app bar came or went, the taskbar moved).</summary>
    public event EventHandler? Moved;

    public PixelRect? Reserve(PixelRect strip, BarEdge edge)
    {
        _wanted = (strip, edge);
        if (!_registered)
        {
            var data = Data();
            data.uCallbackMessage = _callback;
            if (SHAppBarMessage(ABM_NEW, ref data) == 0) return Reserved = null;
            _registered = true;
        }

        int height = strip.Bottom - strip.Top;
        var abd = Data();
        abd.uEdge = edge == BarEdge.Top ? ABE_TOP : ABE_BOTTOM;
        abd.rc = new RECT { Left = strip.Left, Top = strip.Top, Right = strip.Right, Bottom = strip.Bottom };
        // The shell trims the proposal (for example to stay clear of the taskbar); the height is ours to keep.
        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        if (edge == BarEdge.Top) abd.rc.Bottom = abd.rc.Top + height;
        else abd.rc.Top = abd.rc.Bottom - height;
        SHAppBarMessage(ABM_SETPOS, ref abd);
        return Reserved = new PixelRect(abd.rc.Left, abd.rc.Top, abd.rc.Right, abd.rc.Bottom);
    }

    public void Release()
    {
        _wanted = null;
        Reserved = null;
        if (!_registered) return;
        var data = Data();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }

    public void Dispose()
    {
        Release();
        _host.Dispose();
    }

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg == _callback && (int)wParam == ABN_POSCHANGED && _wanted is { } w)
        {
            Reserve(w.Strip, w.Edge);
            Moved?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return 0;
    }

    private unsafe APPBARDATA Data() => new() { cbSize = (uint)sizeof(APPBARDATA), hWnd = _host.Handle };

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [LibraryImport("shell32.dll")] private static partial nuint SHAppBarMessage(int message, ref APPBARDATA data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);
}
