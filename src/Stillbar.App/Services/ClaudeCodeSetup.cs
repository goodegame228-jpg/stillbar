using System.IO;
using System.Text.Json;
using Stillbar.Core.Agents;

namespace Stillbar.App.Services;

/// <summary>
/// Connects Stillbar to Claude Code by adding its hooks to ~/.claude/settings.json (the file the Claude desktop
/// app's Code tab and the CLI both read). Only on the user's click in Settings; a copy of the previous file is
/// kept next to it as settings.json.stillbar-backup. New Claude Code sessions pick the hooks up.
/// </summary>
public static class ClaudeCodeSetup
{
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static string Exe => Environment.ProcessPath!;

    public static bool IsConnected()
    {
        try { return File.Exists(SettingsPath) && HookSettings.IsInstalled(File.ReadAllText(SettingsPath), Exe); }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }

    /// <summary>Null when it worked; otherwise why not, for the settings window.</summary>
    public static string? Connect() => ConnectFor(Exe);

    /// <summary>Hooks pointing at a given exe (the installer moves them to the installed copy; old entries are replaced).</summary>
    public static string? ConnectFor(string exe) => Change(json => HookSettings.Install(json, exe), "connected");

    public static string? Disconnect() => Change(json => HookSettings.Uninstall(json, Exe), "disconnected");

    private static string? Change(Func<string?, string> edit, string what)
    {
        try
        {
            string? before = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
            var after = edit(before);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            if (before is not null) File.WriteAllText(SettingsPath + ".stillbar-backup", before);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, after);
            File.Move(tmp, SettingsPath, overwrite: true);
            Log.Info("agents", $"Claude Code hooks {what} in {SettingsPath}");
            return null;
        }
        catch (JsonException)
        {
            return "Файл настроек Claude Code повреждён: Stillbar не стал его трогать";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("agents", ex.Message);
            return "Не удалось записать настройки Claude Code";
        }
    }
}
