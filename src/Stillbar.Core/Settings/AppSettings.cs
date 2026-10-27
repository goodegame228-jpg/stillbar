namespace Stillbar.Core.Settings;

/// <summary>%AppData%\Stillbar\settings.json. Schema in replica/architecture.md; bump Version and add a migration on breaking changes.</summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string Theme { get; init; } = "dark";
    public int Scale { get; init; } = 100;
    public bool HideInFullscreen { get; init; } = true;
    public CitySetting City { get; init; } = new("Москва", 55.752, 37.617);
    public bool LyricsEnabled { get; init; } = true;
    public int HoverExpandDelayMs { get; init; } = 120;
    public int CollapseDelayMs { get; init; } = 350;

    /// <summary>Register the pill's strip with the shell, so maximised windows and browser tabs start below it. Off by default.</summary>
    public bool ReserveStrip { get; init; }
    /// <summary>"top" or "bottom".</summary>
    public string Edge { get; init; } = "top";
    /// <summary>Device name (\\.\DISPLAY2); null or disconnected means the primary monitor.</summary>
    public string? Monitor { get; init; }
    /// <summary>Restart by itself when the window stops answering or crashes (at most 3 times in 10 minutes).</summary>
    public bool RestartOnHang { get; init; } = true;
    /// <summary>Keep a history of what was copied (the Clipboard tab). Content marked private by its app is never kept.</summary>
    public bool ClipboardHistory { get; init; } = true;
    /// <summary>The pill shrinks to a thin line while there is nothing to show; it comes back under the cursor.</summary>
    public bool AutoHide { get; init; }
    /// <summary>Clicks go through the pill to the window below; hold Ctrl over it to use it.</summary>
    public bool ClickThrough { get; init; }
    /// <summary>Process names, comma-separated («obs64, zoom»): the bar hides while one of them is in front. A string, so the record compares by value and the file stays easy to edit.</summary>
    public string ExcludedApps { get; init; } = "";
    /// <summary>An alert about an hour before rain or snow starts (from the forecast).</summary>
    public bool RainWarnings { get; init; } = true;
    /// <summary>The pill says «RU» / «EN» and «Caps Lock включён» when they change.</summary>
    public bool KeyboardIndicator { get; init; } = true;
    /// <summary>Where along the edge the pill sits: −1 (left) … 0 (centre) … 1 (right), dragged with the mouse.</summary>
    public double Offset { get; init; }
}

public sealed record CitySetting(string Name, double Lat, double Lon);
