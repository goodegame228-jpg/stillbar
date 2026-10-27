namespace Stillbar.Core.Media;

/// <summary>What the system media session reports at one moment. Immutable; a new one arrives on every change.</summary>
public sealed record MediaSnapshot(
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedAt,
    string SourceApp)
{
    public static readonly MediaSnapshot None =
        new("", "", "", false, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue, "");

    /// <summary>What the player allows. Live streams and some web players refuse seeking or skipping.</summary>
    public bool CanSeek { get; init; } = true;
    public bool CanNext { get; init; } = true;
    public bool CanPrevious { get; init; } = true;

    public bool HasSession => SourceApp.Length > 0 || Title.Length > 0;

    /// <summary>
    /// Players report the position only now and then (browsers every few seconds),
    /// so while playing we extrapolate from the last report.
    /// </summary>
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        var p = Position;
        if (IsPlaying && PositionUpdatedAt != DateTimeOffset.MinValue && now > PositionUpdatedAt)
            p += now - PositionUpdatedAt;
        if (p < TimeSpan.Zero) return TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && p > Duration) return Duration;
        return p;
    }
}
