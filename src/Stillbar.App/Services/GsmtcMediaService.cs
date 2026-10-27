using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stillbar.Core.Media;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Stillbar.App.Services;

/// <summary>
/// Reads whatever Windows shows in its own media overlay (Spotify, browsers, players that use
/// System Media Transport Controls) through the official GSMTC API.
/// </summary>
public sealed class GsmtcMediaService : IMediaService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionManager? _manager;
    private Session? _session;
    private string _artworkKey = "";
    private TimeSpan _timelineStart;
    private bool _artworkRetryPending;

    public MediaSnapshot Current { get; private set; } = MediaSnapshot.None;
    public ImageSource? Artwork { get; private set; }
    public event EventHandler? Changed;

    public async Task StartAsync()
    {
        _manager = await SessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => _ = AttachAsync(_manager.GetCurrentSession());
        await AttachAsync(_manager.GetCurrentSession());
    }

    public Task PlayPauseAsync() => _session?.TryTogglePlayPauseAsync().AsTask() ?? Task.CompletedTask;
    public Task NextAsync() => _session?.TrySkipNextAsync().AsTask() ?? Task.CompletedTask;
    public Task PreviousAsync() => _session?.TrySkipPreviousAsync().AsTask() ?? Task.CompletedTask;

    public Task SeekAsync(TimeSpan position) =>
        _session?.TryChangePlaybackPositionAsync((_timelineStart + position).Ticks).AsTask() ?? Task.CompletedTask;

    private async Task AttachAsync(Session? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnPlaybackChanged;
        }

        _session = session;
        _artworkKey = "";

        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackChanged;
            session.TimelinePropertiesChanged += OnPlaybackChanged;
        }

        await RefreshAsync(withProperties: true);
    }

    private void OnMediaPropertiesChanged(Session s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync(withProperties: true);
    private void OnPlaybackChanged(Session s, object e) => _ = RefreshAsync(withProperties: false);

    private async Task RefreshAsync(bool withProperties)
    {
        await _gate.WaitAsync();
        try
        {
            var session = _session;
            if (session is null)
            {
                Current = MediaSnapshot.None;
                Artwork = null;
            }
            else
            {
                string title = Current.Title, artist = Current.Artist, album = Current.Album;
                GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                if (withProperties)
                {
                    props = await session.TryGetMediaPropertiesAsync();
                    title = props?.Title ?? "";
                    artist = props?.Artist ?? "";
                    album = props?.AlbumTitle ?? "";
                }

                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                _timelineStart = timeline.StartTime;
                Current = new MediaSnapshot(
                    title, artist, album,
                    playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    timeline.Position - timeline.StartTime,
                    timeline.EndTime - timeline.StartTime,
                    timeline.LastUpdatedTime,
                    session.SourceAppUserModelId ?? "")
                {
                    // What the player allows right now (BUG-002). Missing info means "allowed", as before.
                    CanSeek = playback?.Controls?.IsPlaybackPositionEnabled ?? true,
                    CanNext = playback?.Controls?.IsNextEnabled ?? true,
                    CanPrevious = playback?.Controls?.IsPreviousEnabled ?? true,
                };

                // Covers are re-read when the track changes, and also while we still have none: players such as
                // Yandex Music send the title first and the cover a moment later, as a second change of the same track.
                var key = $"{title}\u0001{artist}\u0001{album}";
                if (props is not null && (key != _artworkKey || Artwork is null))
                {
                    if (key != _artworkKey) Artwork = null;
                    _artworkKey = key;
                    Artwork = await LoadArtworkAsync(props);
                    if (Artwork is null && props.Thumbnail is not null && !_artworkRetryPending)
                    {
                        // The cover is announced but not readable yet: one more try shortly, in case no further event comes.
                        _artworkRetryPending = true;
                        _ = Task.Delay(1500).ContinueWith(_ => { _artworkRetryPending = false; return RefreshAsync(withProperties: true); });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Players vanish mid-call all the time (tab closed, app quit). Keep the last good state.
            Log.Warn("media", $"refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<ImageSource?> LoadArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail is null)
        {
            Log.Info("media", $"no cover announced for '{props.Title}'");
            return null;
        }
        try
        {
            using var ras = await props.Thumbnail.OpenReadAsync();
            await using var source = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await source.CopyToAsync(ms);
            if (ms.Length == 0)
            {
                Log.Info("media", $"cover for '{props.Title}' is empty for now ({ras.ContentType})");
                return null;
            }
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 192; // 2x the 96 DIP art slot
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            Log.Info("media", $"cover for '{props.Title}': {ms.Length} bytes {ras.ContentType}");
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn("media", $"cover for '{props.Title}' failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
