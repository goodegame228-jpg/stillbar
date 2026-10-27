namespace Stillbar.Core.Timer;

/// <summary>
/// One countdown, driven by the caller's clock (so it can be tested and survives a sleeping PC: time is computed
/// from the end moment, not counted in ticks). States: off → running ⇄ paused; running → finished when the end passes.
/// </summary>
public sealed class CountdownTimer
{
    private DateTimeOffset? _endsAt;
    private TimeSpan _pausedRemaining;

    /// <summary>What it was started with (shown as «Таймер на 5 мин»).</summary>
    public TimeSpan Duration { get; private set; }

    public bool IsRunning => _endsAt is not null;
    public bool IsPaused => _endsAt is null && _pausedRemaining > TimeSpan.Zero;
    public bool IsSet => IsRunning || IsPaused;

    public void Start(TimeSpan duration, DateTimeOffset now)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = duration;
        _pausedRemaining = TimeSpan.Zero;
        _endsAt = now + duration;
    }

    public void Pause(DateTimeOffset now)
    {
        if (!IsRunning || IsFinished(now)) return;
        _pausedRemaining = Remaining(now);
        _endsAt = null;
    }

    public void Resume(DateTimeOffset now)
    {
        if (!IsPaused) return;
        _endsAt = now + _pausedRemaining;
        _pausedRemaining = TimeSpan.Zero;
    }

    public void Reset()
    {
        _endsAt = null;
        _pausedRemaining = TimeSpan.Zero;
    }

    public TimeSpan Remaining(DateTimeOffset now) =>
        _endsAt is { } end ? (end > now ? end - now : TimeSpan.Zero) : _pausedRemaining;

    public bool IsFinished(DateTimeOffset now) => _endsAt is { } end && now >= end;

    /// <summary>«+1 мин»: after the end it starts a fresh countdown; while running or paused it extends.</summary>
    public void Add(TimeSpan extra, DateTimeOffset now)
    {
        if (IsFinished(now)) Start(extra, now);
        else if (_endsAt is { } end) _endsAt = end + extra;
        else if (IsPaused) _pausedRemaining += extra;
        else Start(extra, now);
    }

    /// <summary>Remaining time for the pill: 4:59 counts down to 0:00; whole seconds rounded up so 0:00 means done.</summary>
    public static string Format(TimeSpan remaining)
    {
        var t = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, remaining.TotalSeconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
