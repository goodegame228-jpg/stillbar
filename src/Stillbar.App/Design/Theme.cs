using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Stillbar.App.Design;

/// <summary>
/// The light theme replaces the colour tokens before anything uses them. Styles resolve their StaticResource
/// brushes lazily, the first time a control needs them, so swapping the values at startup (before the first
/// window) recolours everything. That is also why a theme change needs a restart.
/// Values: replica/design/tokens.json "light"; contrast checked on the same pairs as the dark theme, 0 failures.
/// </summary>
public static class Theme
{
    private static readonly Dictionary<string, string> Light = new()
    {
        ["Bg"] = "#F4F4F5",
        ["Surface"] = "#FFFFFF",
        ["SurfaceHover"] = "#EAEAEC",
        ["SurfaceActive"] = "#DFDFE3",
        ["Track"] = "#C9C9CF",
        ["Text"] = "#111113",
        ["TextMuted"] = "#56565E",
        ["TextFaint"] = "#66666E",
        ["Accent"] = "#167A62",
        ["OnAccent"] = "#FFFFFF",
        ["Accent2"] = "#4E7419",
        ["OnAccent2"] = "#FFFFFF",
        ["Control"] = "#111113",
        ["OnControl"] = "#FFFFFF",
        ["Danger"] = "#C42B2B",
        ["Success"] = "#1A7A4C",
        ["Warning"] = "#8A6400",
    };

    /// <summary>"dark", "light" or "system" (follows Windows' app theme).</summary>
    public static bool IsLight(string setting) => setting switch
    {
        "light" => true,
        "system" => WindowsUsesLightApps(),
        _ => false,
    };

    public static void Apply(string setting)
    {
        if (!IsLight(setting)) return;
        var app = Application.Current.Resources;
        var tokens = Find(app, "Color.Bg") ?? app;
        foreach (var (name, hex) in Light)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            tokens["Color." + name] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            tokens["Brush." + name] = brush;
        }
    }

    private static ResourceDictionary? Find(ResourceDictionary d, string key)
    {
        foreach (var m in d.MergedDictionaries)
            if (Find(m, key) is { } found) return found;
        return d.Contains(key) ? d : null;
    }

    private static bool WindowsUsesLightApps()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
    }
}
