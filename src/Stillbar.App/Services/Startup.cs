using Microsoft.Win32;

namespace Stillbar.App.Services;

/// <summary>
/// «Запускать вместе с Windows»: one value under the current user's Run key, the same place Task Manager's
/// Startup tab lists. The registry is the only source of truth, so turning it off there is respected.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Stillbar";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string s && s.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool on)
    {
        if (on) { SetPath(Environment.ProcessPath!); return; }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(Name, throwOnMissingValue: false);
        Log.Info("startup", "no longer starts with Windows");
    }

    /// <summary>Start with Windows from this exe (the installer points it at the installed copy).</summary>
    public static void SetPath(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(Name, $"\"{exe}\"");
        Log.Info("startup", $"starts with Windows: {exe}");
    }
}
