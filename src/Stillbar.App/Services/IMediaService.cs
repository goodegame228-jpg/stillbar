using System.Windows.Media;
using Stillbar.Core.Media;

namespace Stillbar.App.Services;

/// <summary>The system's current media session. Changed may fire on any thread.</summary>
public interface IMediaService
{
    MediaSnapshot Current { get; }

    /// <summary>Frozen, so it can cross threads. Null when the player gives no cover.</summary>
    ImageSource? Artwork { get; }

    event EventHandler? Changed;

    Task StartAsync();
    Task PlayPauseAsync();
    Task NextAsync();
    Task PreviousAsync();

    /// <summary>Position from the start of the track. Players that do not allow seeking ignore it.</summary>
    Task SeekAsync(TimeSpan position);
}
