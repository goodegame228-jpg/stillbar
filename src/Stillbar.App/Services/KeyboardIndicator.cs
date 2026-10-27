using System.Runtime.InteropServices;
using System.Windows.Threading;
using Stillbar.Core.Keyboard;

namespace Stillbar.App.Services;

/// <summary>
/// Watches the input language of the window in front and Caps Lock, 7 times a second, by asking Windows
/// (GetKeyboardLayout, GetKeyState): no keyboard hook, nothing typed is ever seen. Checked live: the toggle
/// state reads correctly from a process that does not have the focus.
/// </summary>
public sealed partial class KeyboardIndicator
{
    private readonly DispatcherTimer _timer;
    private readonly KeyboardWatch _watch = new();
    private readonly Func<bool> _enabled;

    public KeyboardIndicator(Dispatcher ui, Func<bool> enabled)
    {
        _enabled = enabled;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Poll(), ui);
    }

    /// <summary>«RU», «Caps Lock включён»… whenever one of them changes.</summary>
    public event EventHandler<string>? Changed;

    public void Start() => _timer.Start();

    private void Poll()
    {
        var state = Read();
        // Keep the baseline current even while switched off, so turning it on does not announce an old change.
        if (_watch.Next(state) is { } text && _enabled()) Changed?.Invoke(this, text);
    }

    private static KeyboardState Read()
    {
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), 0);
        int langId = (int)(GetKeyboardLayout(thread) & 0xFFFF);
        bool caps = (GetKeyState(0x14 /* VK_CAPITAL */) & 1) != 0;
        return new KeyboardState(langId, caps);
    }

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, nint pid);
    [LibraryImport("user32.dll")] private static partial nint GetKeyboardLayout(uint thread);
    [LibraryImport("user32.dll")] private static partial short GetKeyState(int vk);
}
