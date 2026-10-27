namespace Stillbar.Core.Pill;

public enum PillKind
{
    Idle,
    Music,
    Timer,
    Download,
    Alert,
    Recording,
    Listening,
    Agent,
}

/// <summary>Everything that competes for the collapsed pill.</summary>
public readonly record struct PillInputs(
    bool TimerFinished = false,
    bool DownloadActive = false,
    bool TimerRunning = false,
    bool MediaPlaying = false,
    bool Recording = false,
    bool Listening = false,
    bool Agent = false);

public static class PillStateResolver
{
    /// <summary>
    /// The pill shows one thing at a time: the highest priority that is active. An alert (timer done, a reminder)
    /// needs an answer, so it wins; a running screen recording comes next, since forgetting one fills the disk.
    /// </summary>
    public static PillKind Resolve(PillInputs i) =>
        i.TimerFinished ? PillKind.Alert
        : i.Recording ? PillKind.Recording
        : i.Listening ? PillKind.Listening
        : i.Agent ? PillKind.Agent
        : i.DownloadActive ? PillKind.Download
        : i.TimerRunning ? PillKind.Timer
        : i.MediaPlaying ? PillKind.Music
        : PillKind.Idle;
}
