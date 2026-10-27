using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stillbar.Core.Dock;

namespace Stillbar.App.Services;

/// <summary>One shortcut on the Home tab.</summary>
public sealed record DockApp(string Path, string Name, ImageSource? Icon);

public interface IAppDock
{
    IReadOnlyList<DockApp> Apps { get; }
    event EventHandler? Changed;
    void Add(IEnumerable<string> paths);
    void Remove(string path);
    void Launch(string path);
}

/// <summary>dock.json + system icons. First run seeds Explorer and Notepad so the row is not empty.</summary>
public sealed partial class AppDock : IAppDock
{
    private readonly DockStore _store;
    private List<DockEntry> _entries;

    public AppDock(string settingsDir)
    {
        _store = new DockStore(settingsDir);
        if (_store.Exists)
        {
            _entries = _store.Load();
        }
        else
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            _entries = DockStore.Add([], new[] { "explorer.exe", "notepad.exe" }
                .Select(f => System.IO.Path.Combine(win, f)).Where(File.Exists));
            _store.Save(_entries);
        }
        Apps = Build(_entries);
    }

    public IReadOnlyList<DockApp> Apps { get; private set; }
    public event EventHandler? Changed;

    public void Add(IEnumerable<string> paths)
    {
        var next = DockStore.Add(_entries, paths.Where(p => DockStore.IsLaunchable(p) && File.Exists(p)));
        if (next.Count == _entries.Count) return;
        Commit(next);
    }

    public void Remove(string path) => Commit(DockStore.Remove(_entries, path));

    public void Launch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Debug.WriteLine($"launch {path}: {ex.Message}");
        }
    }

    private void Commit(List<DockEntry> next)
    {
        _entries = next;
        _store.Save(next);
        Apps = Build(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<DockApp> Build(IEnumerable<DockEntry> entries) =>
        entries.Select(e => new DockApp(e.Path, DisplayName(e.Path), LoadIcon(e.Path))).ToList();

    private static string DisplayName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription!;
        }
        catch (FileNotFoundException) { }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>The shell's own large icon (32 px), so shortcuts get their target's icon. Frozen.</summary>
    internal static ImageSource? LoadIcon(string path)
    {
        var info = new SHFILEINFOW();
        if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_LARGEICON) == 0 || info.hIcon == 0)
            return null;
        try
        {
            var img = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    // Classic DllImport: the struct carries fixed-size strings, which LibraryImport does not marshal.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string path, uint attributes, ref SHFILEINFOW info, uint size, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}

/// <summary>Fixed shortcuts for --snapshot renders (icons from the real files, if present).</summary>
public sealed class SampleDock : IAppDock
{
    public SampleDock()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Apps = new[] { "explorer.exe", "notepad.exe", @"System32\cmd.exe" }
            .Select(f => System.IO.Path.Combine(win, f))
            .Select(p => new DockApp(p, System.IO.Path.GetFileNameWithoutExtension(p), File.Exists(p) ? AppDock.LoadIcon(p) : null))
            .ToList();
    }

    public IReadOnlyList<DockApp> Apps { get; }
    public event EventHandler? Changed { add { } remove { } }
    public void Add(IEnumerable<string> paths) { }
    public void Remove(string path) { }
    public void Launch(string path) { }
}
