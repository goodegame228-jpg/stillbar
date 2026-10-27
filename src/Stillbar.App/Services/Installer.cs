using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Stillbar.App.Services;

/// <summary>
/// Per-user install, no administrator: files in %LocalAppData%\Programs\Stillbar, a Start menu shortcut, an entry
/// in Settings → Apps (so it can be removed like any app). Running «--install» from a newer build updates in place.
/// Settings, notes and history live in %AppData%\Stillbar and %LocalAppData%\Stillbar and are not touched by an
/// update; uninstall asks before removing them.
/// </summary>
public static class Installer
{
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Stillbar");
    public static string InstalledExe => Path.Combine(InstallDir, "Stillbar.exe");
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Stillbar.lnk");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Stillbar";

    /// <summary>A running Stillbar listens for this and closes itself (installer, uninstaller).</summary>
    public const string QuitEventName = @"Local\Stillbar.Quit";

    public static int Install()
    {
        var from = AppContext.BaseDirectory.TrimEnd('\\');
        bool wasStartup = Startup.IsEnabled() || StartupPointsSomewhere();
        bool hadHooks = ClaudeCodeSetup.IsConnected();

        CloseRunning();
        if (!string.Equals(Path.GetFullPath(from), Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase))
            CopyFolder(from, InstallDir);

        CreateShortcut();
        WriteUninstallEntry();
        if (wasStartup) Startup.SetPath(InstalledExe);
        if (hadHooks) ClaudeCodeSetup.ConnectFor(InstalledExe);
        Log.Info("install", $"installed to {InstallDir} (startup {wasStartup}, Claude Code {hadHooks})");

        Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir });
        return 0;
    }

    /// <summary>
    /// «--uninstall-cleanup»: what the setup's uninstaller runs before it removes the files. Quiet: closes Stillbar,
    /// removes «start with Windows», the Claude Code hooks and the entries an earlier «--install» made. The
    /// uninstaller itself asks about the user's data.
    /// </summary>
    public static int Cleanup()
    {
        CloseRunning();
        Startup.Set(false);
        if (ClaudeCodeSetup.IsConnected()) ClaudeCodeSetup.Disconnect();
        TryDelete(Shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Log.Info("install", "cleaned up for the uninstaller");
        return 0;
    }

    public static int Uninstall()
    {
        var answer = MessageBox.Show(
            "Удалить Stillbar?\n\nВместе с программой уберутся автозапуск, ярлык и подключение к Claude Code.\n\nУдалить также настройки, заметки, напоминания и историю буфера?\n«Да» — удалить всё, «Нет» — оставить данные.",
            "Удаление Stillbar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Cancel) return 1;

        CloseRunning();
        Startup.Set(false);
        if (ClaudeCodeSetup.IsConnected()) ClaudeCodeSetup.Disconnect();
        TryDelete(Shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        if (answer == MessageBoxResult.Yes)
        {
            TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stillbar"));
            TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stillbar"));
        }
        Log.Info("install", $"uninstalled (data {(answer == MessageBoxResult.Yes ? "removed" : "kept")})");

        // This exe is running from the folder: a short-lived cmd removes it once we have exited.
        if (AppContext.BaseDirectory.TrimEnd('\\').Equals(InstallDir, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        return 0;
    }

    /// <summary>Asks a running Stillbar to close (it finishes a recording, releases the reserved strip), then insists.</summary>
    private static void CloseRunning()
    {
        var others = Process.GetProcessesByName("Stillbar").Where(p => p.Id != Environment.ProcessId).ToList();
        if (others.Count == 0) return;
        try
        {
            using var quit = EventWaitHandle.OpenExisting(QuitEventName);
            quit.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        foreach (var p in others)
        {
            try { if (!p.WaitForExit(8000)) p.Kill(); }
            catch (InvalidOperationException) { }
        }
        Thread.Sleep(300); // file handles go away a moment after the process
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private static void WriteUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var version = typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        long sizeKb = Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
        key.SetValue("DisplayName", "Stillbar");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Stillbar");
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Start menu shortcut through the shell's own IShellLink (no script host).</summary>
    private static void CreateShortcut()
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(InstalledExe);
        link.SetWorkingDirectory(InstallDir);
        link.SetDescription("Полоска сверху, которая не мешает работать");
        Directory.CreateDirectory(Path.GetDirectoryName(Shortcut)!);
        ((IPersistFile)link).Save(Shortcut, true);
    }

    private static bool StartupPointsSomewhere()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("Stillbar") is string;
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch (IOException) { } }
    private static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, nint findData, int flags);
        void GetIDList(out nint idl);
        void SetIDList(nint idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(nint hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
}
