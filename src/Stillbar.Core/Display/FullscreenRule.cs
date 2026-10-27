namespace Stillbar.Core.Display;

/// <summary>Screen rectangle in physical pixels, edges exclusive on the right and bottom (Win32 RECT).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);

/// <summary>What the detector saw in one poll.</summary>
public readonly record struct ForegroundInfo(
    int NotificationState,
    PixelRect Window,
    PixelRect Monitor,
    bool OnBarMonitor,
    bool IsShellOrDesktop);

public static class FullscreenRule
{
    // SHQueryUserNotificationState values
    public const int QunsRunningD3DFullScreen = 3;
    public const int QunsPresentationMode = 4;

    /// <summary>
    /// Hide the bar when its monitor is taken by a full-screen app: exclusive D3D games and presentations
    /// say so through the notification state; borderless games and full-screen browser video only show up as a
    /// foreground window that covers the whole monitor. The desktop itself also covers it, so it is excluded.
    /// </summary>
    public static bool ShouldHide(ForegroundInfo f)
    {
        if (f.NotificationState is QunsRunningD3DFullScreen or QunsPresentationMode) return true;
        if (f.IsShellOrDesktop || !f.OnBarMonitor) return false;
        return f.Window.Left <= f.Monitor.Left && f.Window.Top <= f.Monitor.Top
            && f.Window.Right >= f.Monitor.Right && f.Window.Bottom >= f.Monitor.Bottom;
    }
}
