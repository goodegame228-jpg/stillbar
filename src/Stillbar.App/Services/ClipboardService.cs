using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stillbar.Core.Clipboard;
using Stillbar.Core.Shelf;
using Stillbar.Core.Storage;

namespace Stillbar.App.Services;

public interface IClipboardService
{
    event EventHandler? Changed;
    IReadOnlyList<ClipEntry> View(string? query);
    void SetPinned(string id, bool pinned);
    void Remove(string id);
    void Clear();

    /// <summary>Puts the entry back on the clipboard and presses Ctrl+V in the window the user is typing in. False if the clipboard was busy.</summary>
    bool Paste(ClipEntry entry);
}

public interface IShelfService
{
    event EventHandler? Changed;
    IReadOnlyList<ShelfItem> Items { get; }
    void Add(IEnumerable<string> paths);
    void Remove(string path);
}

/// <summary>
/// Watches the clipboard (AddClipboardFormatListener on a hidden window) and keeps the history in
/// %AppData%\Stillbar\clipboard.json, pictures as PNG in %LocalAppData%\Stillbar\clips. Content that apps mark as
/// private (password managers set ExcludeClipboardContentFromMonitorProcessing or CanIncludeInClipboardHistory = 0,
/// the same signals Windows' own clipboard history honours) is never recorded.
/// </summary>
public sealed partial class ClipboardService : IClipboardService, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly HwndSource _listener;
    private readonly JsonListStore<ClipEntry> _store;
    private readonly string _imageDir;
    private readonly ClipHistory _history;

    public ClipboardService(string settingsDir, string cacheDir)
    {
        _store = new JsonListStore<ClipEntry>(settingsDir, "clipboard.json");
        _imageDir = Path.Combine(cacheDir, "clips");
        _history = new ClipHistory(_store.Load().Where(e => e.Kind == ClipKind.Text || File.Exists(e.ImageFile)));

        _listener = new HwndSource(new HwndSourceParameters("Stillbar.Clipboard") { WindowStyle = 0, Width = 0, Height = 0, ParentWindow = new nint(-3) }); // HWND_MESSAGE
        _listener.AddHook(Hook);
        AddClipboardFormatListener(_listener.Handle);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ClipEntry> View(string? query) => _history.View(query);

    public void SetPinned(string id, bool pinned)
    {
        _history.SetPinned(id, pinned);
        Commit([]);
    }

    public void Remove(string id)
    {
        if (_history.Remove(id) is { } e) Commit([e]);
    }

    public void Clear() => Commit(_history.Clear());

    public bool Paste(ClipEntry entry)
    {
        bool ok = entry.Kind == ClipKind.Text
            ? Retry(() => Clipboard.SetText(entry.Text))
            : entry.ImageFile is { } f && File.Exists(f) && Retry(() => Clipboard.SetImage(LoadPng(f)));
        if (ok) Keys.CtrlV();
        return ok;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_listener.Handle);
        _listener.Dispose();
    }

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            try { Record(); }
            catch (Exception ex) when (ex is COMException or ExternalException or IOException or NotSupportedException)
            {
                Log.Warn("clipboard", ex.Message);
            }
        }
        return 0;
    }

    private void Record()
    {
        var data = Retry(Clipboard.GetDataObject);
        if (data is null || IsPrivate(data)) return;

        string? source = SourceApp();
        var now = DateTimeOffset.Now;
        // Office and browsers put a picture of copied text next to the text: the text is what was meant.
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
        {
            var (added, dropped) = _history.AddText(text, source, now);
            if (added is not null) Commit(dropped);
        }
        else if (data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is { } image)
        {
            var png = EncodePng(image);
            var hash = Convert.ToHexString(SHA256.HashData(png))[..16].ToLowerInvariant();
            Directory.CreateDirectory(_imageDir);
            var file = Path.Combine(_imageDir, hash + ".png");
            if (!File.Exists(file)) File.WriteAllBytes(file, png);
            var (_, dropped) = _history.AddImage(file, hash, image.PixelWidth, image.PixelHeight, source, now);
            Commit(dropped);
        }
    }

    private static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return true;
        return data.GetDataPresent("CanIncludeInClipboardHistory")
            && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms
            && ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0;
    }

    private void Commit(List<ClipEntry> dropped)
    {
        foreach (var d in dropped)
        {
            // An image file can be shared by an entry that is still there (same picture copied again).
            if (d.ImageFile is { } f && _history.Items.All(e => e.ImageFile != f))
                try { File.Delete(f); } catch (IOException) { }
        }
        _store.Save(_history.Items);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The app that owns the clipboard now, by its description ("Google Chrome"); null for our own writes or when unknown.</summary>
    private static string? SourceApp()
    {
        nint owner = GetClipboardOwner();
        if (owner == 0) return null;
        GetWindowThreadProcessId(owner, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            try
            {
                var d = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return p.ProcessName;
        }
        catch (ArgumentException) { return null; }
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    internal static BitmapSource LoadPng(string file)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(file);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>The clipboard is shared: another app may hold it for a moment.</summary>
    private static T? Retry<T>(Func<T> read)
    {
        for (int i = 0; ; i++)
        {
            try { return read(); }
            catch (COMException) when (i < 4) { Thread.Sleep(40); }
            catch (COMException) { return default; }
        }
    }

    private static bool Retry(Action write) => Retry(() => { write(); return true; });

    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool AddClipboardFormatListener(nint hwnd);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool RemoveClipboardFormatListener(nint hwnd);
    [LibraryImport("user32.dll")] private static partial nint GetClipboardOwner();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}

/// <summary>Files parked on the shelf, in %AppData%\Stillbar\shelf.json.</summary>
public sealed class ShelfService(string settingsDir) : IShelfService
{
    private readonly JsonListStore<ShelfItem> _store = new(settingsDir, "shelf.json");
    private List<ShelfItem>? _items;

    public event EventHandler? Changed;

    public IReadOnlyList<ShelfItem> Items => _items ??= ShelfList.Existing(_store.Load(), p => File.Exists(p) || Directory.Exists(p));

    public void Add(IEnumerable<string> paths) => Commit(ShelfList.Add(Items, paths, DateTimeOffset.Now));

    public void Remove(string path) => Commit(ShelfList.Remove(Items, path));

    private void Commit(List<ShelfItem> next)
    {
        _items = next;
        _store.Save(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Keyboard input sent to whatever window has focus (never ours: the bar does not take focus).</summary>
internal static partial class Keys
{
    private const ushort VK_CONTROL = 0x11, VK_V = 0x56, VK_LWIN = 0x5B, VK_H = 0x48;
    private const uint KEYEVENTF_KEYUP = 0x2;

    public static void CtrlV() => Send([(VK_CONTROL, false), (VK_V, false), (VK_V, true), (VK_CONTROL, true)]);

    /// <summary>Win+H: Windows' own voice typing, into the window that has the focus.</summary>
    public static void WinH() => Send([(VK_LWIN, false), (VK_H, false), (VK_H, true), (VK_LWIN, true)]);

    /// <summary>
    /// Types text into the window that has the focus, as Unicode key presses (any language, any layout; the
    /// clipboard is not touched). A line break is Enter.
    /// </summary>
    public static unsafe void Type(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = 0x0D } });
                inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = 0x0D, dwFlags = KEYEVENTF_KEYUP } });
                continue;
            }
            inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } });
            inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } });
        }
        if (inputs.Count > 0) SendInput((uint)inputs.Count, [.. inputs], sizeof(INPUT));
    }

    private const uint KEYEVENTF_UNICODE = 0x4;

    private static unsafe void Send((ushort Key, bool Up)[] keys)
    {
        var inputs = keys.Select(k => new INPUT { type = 1, ki = new KEYBDINPUT { wVk = k.Key, dwFlags = k.Up ? KEYEVENTF_KEYUP : 0 } }).ToArray();
        SendInput((uint)inputs.Length, inputs, sizeof(INPUT));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }

    // INPUT is a union; the mouse variant is the largest, so pad to its size.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [LibraryImport("user32.dll")] private static partial uint SendInput(uint count, [In] INPUT[] inputs, int size);
}

/// <summary>For --snapshot and tests that do not care.</summary>
public sealed class SampleClipboard : IClipboardService
{
    private readonly ClipHistory _h;

    public SampleClipboard()
    {
        var now = DateTimeOffset.Now;
        _h = new ClipHistory([
            new ClipEntry("1", ClipKind.Text, "https://open-meteo.com/en/docs", now.AddMinutes(-2), "Google Chrome"),
            new ClipEntry("2", ClipKind.Text, "Встреча перенесена на четверг, 15:30. Ссылка та же.", now.AddMinutes(-18), "Telegram"),
            new ClipEntry("3", ClipKind.Text, "git commit -m \"bar: reserved strip\"", now.AddHours(-2), "Терминал Windows", Pinned: true),
            new ClipEntry("4", ClipKind.Text, "Тихая гавань — Северный ветер", now.AddDays(-1).AddHours(-3), "Яндекс Музыка"),
        ]);
    }

    public event EventHandler? Changed { add { } remove { } }
    public IReadOnlyList<ClipEntry> View(string? query) => _h.View(query);
    public void SetPinned(string id, bool pinned) { }
    public void Remove(string id) { }
    public void Clear() { }
    public bool Paste(ClipEntry entry) => true;
}

public sealed class SampleShelf : IShelfService
{
    public event EventHandler? Changed { add { } remove { } }
    public IReadOnlyList<ShelfItem> Items { get; } =
    [
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), DateTimeOffset.Now),
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "win.ini"), DateTimeOffset.Now),
    ];
    public void Add(IEnumerable<string> paths) { }
    public void Remove(string path) { }
}
