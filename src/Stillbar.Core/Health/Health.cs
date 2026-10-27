namespace Stillbar.Core.Health;

/// <summary>
/// Decides whether the UI thread has stopped answering. A background thread calls <see cref="Check"/> every few
/// seconds and posts a ping to the UI thread, which answers with <see cref="Pong"/>. If the whole process was
/// suspended (sleep, hibernate, a debugger), the gap between checks is large and the clock is restarted instead of
/// calling that a hang.
/// </summary>
public sealed class HangDetector(TimeSpan limit, TimeSpan checkEvery)
{
    private DateTimeOffset _lastPong;
    private DateTimeOffset _lastCheck;
    private bool _started;

    public void Pong(DateTimeOffset at) => _lastPong = at;

    public bool Check(DateTimeOffset now)
    {
        if (!_started || now - _lastCheck > checkEvery * 3)
        {
            _started = true;
            _lastPong = now;
        }
        _lastCheck = now;
        return now - _lastPong > limit;
    }
}

/// <summary>At most <c>max</c> automatic restarts within <c>window</c>; beyond that something is wrong for good and looping would only hide it.</summary>
public static class RestartBudget
{
    public static bool CanRestart(IEnumerable<DateTimeOffset> past, DateTimeOffset now, int max, TimeSpan window) =>
        past.Count(t => now - t < window && t <= now) < max;

    /// <summary>The restarts still inside the window plus this one, to be saved for the next decision.</summary>
    public static List<DateTimeOffset> Record(IEnumerable<DateTimeOffset> past, DateTimeOffset now, TimeSpan window) =>
        [.. past.Where(t => now - t < window && t <= now), now];
}
