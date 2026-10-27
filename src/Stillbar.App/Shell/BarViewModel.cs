using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Stillbar.App.Design;
using Stillbar.App.Services;
using Stillbar.Core.Agents;
using Stillbar.Core.Clipboard;
using Stillbar.Core.Commands;
using Stillbar.Core.Notes;
using Stillbar.Core.Reminders;
using Stillbar.Core.Downloads;
using Stillbar.Core.Lyrics;
using Stillbar.Core.Media;
using Stillbar.Core.Pill;
using Stillbar.Core.Timer;
using Stillbar.Core.Voice;
using Stillbar.Core.Weather;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Stillbar.App.Shell;

/// <summary>Everything the bar talks to. Real services in the app, samples in --snapshot.</summary>
public sealed record BarServices(
    IMediaService Media,
    IVolumeService Volume,
    IWeatherService Weather,
    ILyricsService Lyrics,
    IDownloadSource Downloads,
    IAppDock Dock,
    IScreenActions Screen,
    bool LyricsEnabled = true,
    Func<DateTimeOffset>? Clock = null,
    Action? Chime = null,
    IClipboardService? Clipboard = null,
    IShelfService? Shelf = null,
    IScreenRecorder? Recorder = null,
    IListStore<Note>? Notes = null,
    IListStore<Reminder>? Reminders = null,
    ISystemControls? Controls = null,
    Func<string>? CityName = null,
    IDictation? Dictation = null,
    IAgentHub? Agents = null);

/// <summary>One hour on the weather page.</summary>
public sealed record HourRow(string Label, Geometry Icon, Brush IconBrush, string Temperature, string Rain)
{
    public bool HasRain => Rain.Length > 0;
}

/// <summary>One day on the weather page: the bar shows its min–max within the week's range.</summary>
public sealed record DayRow(string Name, Geometry Icon, Brush IconBrush, string Min, string Max, string Rain, Thickness BarOffset, double BarWidth)
{
    public bool HasRain => Rain.Length > 0;
}

/// <summary>A paired Bluetooth device on the Система tab.</summary>
public sealed record BluetoothRow(string Name, bool Connected, string Kind)
{
    public string State => Connected ? "подключено" : "не подключено";
    public string Glyph => Kind switch { "audio" => "", "input" => "", "phone" => "", _ => "" };
}

/// <summary>One VPN connection on the Система tab.</summary>
public sealed record VpnRow(string Name, bool Connected)
{
    public string Action => Connected ? "Отключить" : "Подключить";
    public string State => Connected ? "подключено" : "не подключено";
}

/// <summary>One note in the list on the Notes tab; IsCurrent is the one in the editor.</summary>
public sealed record NoteRow(Note Note, bool IsCurrent)
{
    public string Title => Note.Title;
    public string Detail => Note.Progress.Length > 0 ? $"{Note.Progress} · {Note.Preview}" : Note.Preview;
}

/// <summary>One upcoming reminder.</summary>
public sealed record ReminderRow(Reminder Reminder, string When)
{
    public string Text => Reminder.Text;
}

/// <summary>One row of the Clipboard tab.</summary>
public sealed record ClipRow(ClipEntry Entry, string Text, string Meta, ImageSource? Image)
{
    public bool IsImage => Image is not null;
    public bool IsText => Image is null;
    public bool Pinned => Entry.Pinned;
    public string PinGlyph => Entry.Pinned ? "" : "";
    public string PinLabel => Entry.Pinned ? "Открепить" : "Закрепить";
}

/// <summary>One file on the shelf.</summary>
public sealed record ShelfRow(string Path, string Name, ImageSource? Icon);

/// <summary>One row of the output device list.</summary>
public sealed record DeviceItem(string Id, string Name, bool IsCurrent);

/// <summary>One app in the mixer: its slider and mute button talk straight to the service.</summary>
public sealed partial class MixerItem : ObservableObject
{
    private readonly IVolumeService _volume;
    private bool _syncing;

    public MixerItem(AppVolume app, IVolumeService volume)
    {
        _volume = volume;
        Key = app.Key;
        Name = app.Name;
        Sync(app);
    }

    public string Key { get; }
    public string Name { get; }

    [ObservableProperty] private double _volumePercent;
    [ObservableProperty] private string _glyph = "";
    [ObservableProperty] private string _muteLabel = "";

    internal void Sync(AppVolume app)
    {
        _syncing = true;
        VolumePercent = app.Volume;
        _syncing = false;
        Glyph = app.Muted || app.Volume == 0 ? "" : "";
        MuteLabel = app.Muted ? $"Включить звук: {Name}" : $"Выключить звук: {Name}";
    }

    partial void OnVolumePercentChanged(double value)
    {
        if (!_syncing) _volume.SetAppVolume(Key, (int)Math.Round(value));
    }

    [RelayCommand] private void ToggleMute() => _volume.ToggleAppMute(Key);
}

public partial class BarViewModel : ObservableObject
{
    private const string GlyphPlay = "";
    private const string GlyphPause = "";
    private const string GlyphVolume = "";
    private const string GlyphMuted = "";

    // After a seek the player needs a moment to report the new position; until then trust our own.
    private static readonly TimeSpan SeekGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SlowTick = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FastTick = TimeSpan.FromMilliseconds(200); // synced lyrics on screen
    private static readonly TimeSpan LyricsDebounce = TimeSpan.FromMilliseconds(500);

    private readonly BarServices _s;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _statusTimer;
    private readonly Brush _defaultCard;
    private readonly Brush _defaultProgress;

    private ImageSource? _tintedFor;
    private bool _syncingVolume;
    private (TimeSpan Position, DateTimeOffset At)? _seek;
    private bool _expanded;
    private string _lyricsKey = "";
    private CancellationTokenSource? _lyricsCts;
    private LyricsResult _lyrics = LyricsResult.NotFound;
    private int _lyricIndex = int.MinValue;
    private readonly Func<DateTimeOffset> _now;
    private readonly CountdownTimer _timer = new();
    private bool _alerted;

    // ---- pill
    [ObservableProperty] private string _clock = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMusicPill), nameof(IsDownloadPill), nameof(ShowPillMusic), nameof(ShowPillDownload),
        nameof(ShowPillWeatherIcon), nameof(ShowPillTemperature), nameof(ShowPillClock), nameof(ShowPillTimer),
        nameof(IsAlertPill), nameof(ShowNormalPill), nameof(IsRecordingPill), nameof(IsListeningPill), nameof(IsAgentPill))]
    private PillKind _pillKind;

    // ---- timer
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimerOff))]
    private bool _isTimerSet;
    [ObservableProperty] private bool _isTimerRunning;
    [ObservableProperty] private string _timerText = "";
    [ObservableProperty] private string _timerPauseGlyph = "";
    [ObservableProperty] private string _timerPauseLabel = "Пауза";
    [ObservableProperty] private string _alertDetail = "";

    // ---- weather
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPillWeatherIcon), nameof(ShowPillTemperature))]
    private bool _hasWeather;
    [ObservableProperty] private string _temperatureText = "";
    [ObservableProperty] private Geometry? _weatherIcon;
    [ObservableProperty] private Brush? _weatherBrush;

    // ---- media
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMedia))]
    private bool _hasMedia;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private ImageSource? _artwork;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _remainingText = "";
    [ObservableProperty] private string _playGlyph = GlyphPlay;
    [ObservableProperty] private string _playLabel = "Играть";
    [ObservableProperty] private Brush _cardBackground;
    [ObservableProperty] private Brush _progressBrush;
    [ObservableProperty] private bool _isScrubbing;

    // What the player allows (BUG-002): live streams and some web players refuse seeking or skipping.
    [ObservableProperty] private bool _canSeek;
    [ObservableProperty] private bool _canNext;
    [ObservableProperty] private bool _canPrevious;

    // ---- pages: Home, or the mini player with lyrics (opened from the music pill)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomePage), nameof(IsPlayerPage), nameof(IsClipboardPage), nameof(IsNotesPage), nameof(IsSystemPage), nameof(IsWeatherPage), nameof(ShowTabBar))]
    private string _page = "home";

    // ---- lyrics
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLyricsColumn))]
    private bool _hasLyrics;

    /// <summary>Why there are no lines: searching, not found, instrumental, service down. Empty when lines are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLyricsColumn))]
    private string _lyricsStatus = "";

    /// <summary>The lyrics toggle under the cover in the mini player.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLyricsColumn))]
    private bool _lyricsHidden;

    [ObservableProperty] private bool _lyricsSynced;
    [ObservableProperty] private string _previousLine = "";
    [ObservableProperty] private string _currentLine = "";
    [ObservableProperty] private string _nextLine = "";

    // ---- volume
    [ObservableProperty] private bool _hasAudioDevice;
    [ObservableProperty] private double _volumePercent;
    [ObservableProperty] private string _volumeText = "";
    [ObservableProperty] private string _volumeGlyph = GlyphVolume;
    [ObservableProperty] private string _muteLabel = "Выключить звук";
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private bool _isDeviceListOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceError))]
    private string _deviceError = "";

    // ---- downloads
    [ObservableProperty] private string _downloadName = "";
    [ObservableProperty] private string _downloadDetail = "";
    [ObservableProperty] private string _downloadGlyph = "";
    [ObservableProperty] private Brush? _downloadBrush;

    // ---- status toast in the pill (result of an action, a couple of seconds)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(ShowPillMusic), nameof(ShowPillDownload),
        nameof(ShowPillWeatherIcon), nameof(ShowPillTemperature), nameof(ShowPillClock), nameof(ShowPillTimer))]
    private string _statusText = "";

    public BarViewModel(BarServices services, Dispatcher dispatcher)
    {
        _s = services;
        _dispatcher = dispatcher;
        _now = services.Clock ?? (() => DateTimeOffset.Now);
        _defaultCard = Tokens.Brush("Brush.Surface");
        _defaultProgress = Tokens.Brush("Brush.Accent");
        _cardBackground = _defaultCard;
        _progressBrush = _defaultProgress;

        _s.Media.Changed += (_, _) => _dispatcher.InvokeAsync(ApplyMedia);
        _s.Volume.Changed += (_, _) => _dispatcher.InvokeAsync(ApplyVolume);
        _s.Weather.Changed += (_, _) => _dispatcher.InvokeAsync(ApplyWeather);
        _s.Downloads.Changed += (_, _) => _dispatcher.InvokeAsync(ApplyDownload);
        _s.Dock.Changed += (_, _) => _dispatcher.InvokeAsync(ApplyDock);
        if (_s.Clipboard is not null) _s.Clipboard.Changed += (_, _) => _dispatcher.InvokeAsync(RefreshClips);
        if (_s.Shelf is not null) _s.Shelf.Changed += (_, _) => _dispatcher.InvokeAsync(RefreshShelf);
        if (_s.Recorder is not null) _s.Recorder.Changed += (_, _) => _dispatcher.InvokeAsync(ApplyRecording);
        if (_s.Agents is not null) _s.Agents.Received += (_, m) => _dispatcher.InvokeAsync(() => OnAgent(m));
        if (_s.Dictation is not null)
        {
            _s.Dictation.Changed += (_, _) => _dispatcher.InvokeAsync(ApplyDictation);
            _s.Dictation.Heard += (_, words) => _dispatcher.InvokeAsync(() => TypeHeard(words));
            ApplyDictation();
        }
        NoteStore.Changed += (_, _) => _dispatcher.InvokeAsync(RefreshNotes);
        ReminderStore.Changed += (_, _) => _dispatcher.InvokeAsync(RefreshReminders);
        RefreshNotes();
        RefreshReminders();
        RefreshClips();
        RefreshShelf();

        _tick = new DispatcherTimer(SlowTick, DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Background,
            (_, _) => { StatusText = ""; _statusTimer!.Stop(); }, dispatcher);
        _statusTimer.Stop();

        ApplyMedia();
        ApplyVolume();
        ApplyWeather();
        ApplyDownload();
        ApplyDock();
        Tick();
    }

    // Plain bools for XAML: referencing our own types there needs the second markup pass, which drops the BAML in this setup.
    public bool IsMusicPill => PillKind == PillKind.Music;
    public bool IsDownloadPill => PillKind == PillKind.Download;
    // A status toast replaces whatever the pill shows for its 2.5 s.
    public bool ShowPillMusic => IsMusicPill && !HasStatus;
    public bool ShowPillDownload => IsDownloadPill && !HasStatus;
    public bool ShowPillTimer => PillKind == PillKind.Timer && !HasStatus;
    private bool IsClockPill => PillKind is PillKind.Idle or PillKind.Music;
    public bool ShowPillClock => IsClockPill && !HasStatus;
    public bool ShowPillWeatherIcon => HasWeather && PillKind == PillKind.Idle && !HasStatus; // music puts the cover there
    public bool ShowPillTemperature => HasWeather && IsClockPill && !HasStatus;
    /// <summary>The finished-timer pill: taller, with its own buttons, and it does not open the panel on hover.</summary>
    public bool IsAlertPill => PillKind == PillKind.Alert;
    public bool ShowNormalPill => !IsAlertPill && !IsRecordingPill && !IsListeningPill && !IsAgentPill;
    public bool IsAgentPill => PillKind == PillKind.Agent;
    public bool IsListeningPill => PillKind == PillKind.Listening;
    public bool IsRecordingPill => PillKind == PillKind.Recording;
    public bool IsTimerOff => !IsTimerSet;
    public bool HasNoMedia => !HasMedia;
    public bool HasDeviceError => DeviceError.Length > 0;
    public bool HasStatus => StatusText.Length > 0;
    public bool ShowLyricsColumn => (HasLyrics || LyricsStatus.Length > 0) && !LyricsHidden;
    // Settable bools so the tab radio buttons bind TwoWay (no local types or converters in XAML). An unchecked
    // radio button sets false: that only matters for the player, which falls back to Home.
    public bool IsHomePage { get => Page == "home"; set { if (value) Page = "home"; } }
    public bool IsClipboardPage { get => Page == "clipboard"; set { if (value) Page = "clipboard"; } }
    public bool IsNotesPage { get => Page == "notes"; set { if (value) Page = "notes"; } }
    public bool IsSystemPage { get => Page == "system"; set { if (value) Page = "system"; } }
    public bool IsWeatherPage { get => Page == "weather"; set { if (value) Page = "weather"; } }
    public bool IsPlayerPage
    {
        get => Page == "player";
        set
        {
            if (value) Page = "player";
            else if (Page == "player") Page = "home";
        }
    }
    /// <summary>The mini player is one compact block with its own way back; every other page has the tab bar.</summary>
    public bool ShowTabBar => !IsPlayerPage;
    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public ObservableCollection<DockApp> DockApps { get; } = [];

    /// <summary>The view asks for this before a full-screen capture, so the panel is not in the picture.</summary>
    public event EventHandler? CollapseRequested;

    public void Start() => _tick.Start();

    /// <summary>Called by the view when the panel opens or closes; picks the page and the tick rate.</summary>
    public void OnExpandedChanged(bool expanded)
    {
        // The page is chosen when the panel opens (hovering the music pill means "the player"); not on repeat calls.
        if (expanded && !_expanded) IsPlayerPage = IsMusicPill && HasMedia;
        if (!expanded)
        {
            IsDeviceListOpen = false;
            IsMixerOpen = false;
            SaveNote();
        }
        _expanded = expanded;
        UpdateTickRate();
        UpdateControlsTimer();
    }

    private void UpdateTickRate() =>
        _tick.Interval = _expanded && IsPlayerPage && LyricsSynced ? FastTick : SlowTick;

    private void RecomputePill() => PillKind = PillStateResolver.Resolve(new PillInputs(
        TimerFinished: _alerted || _dueReminder is not null || _rainAlert is not null || _agentAsk is not null,
        DownloadActive: _s.Downloads.Primary is not null,
        TimerRunning: _timer.IsSet,
        MediaPlaying: _s.Media.Current.IsPlaying,
        Recording: _s.Recorder?.IsRecording == true,
        Listening: _s.Dictation?.IsListening == true,
        Agent: AgentText.Length > 0));

    // ======================= media

    private void ApplyMedia()
    {
        var m = _s.Media.Current;
        HasMedia = m.HasSession;
        Title = m.Title.Length > 0 ? m.Title : "Без названия";
        Artist = m.Artist;
        Album = m.Album;
        Artwork = _s.Media.Artwork;
        IsPlaying = m.IsPlaying;
        PlayGlyph = m.IsPlaying ? GlyphPause : GlyphPlay;
        PlayLabel = m.IsPlaying ? "Пауза" : "Играть";
        DurationSeconds = Math.Max(1, m.Duration.TotalSeconds);
        CanSeek = m.HasSession && m.CanSeek && m.Duration > TimeSpan.Zero; // no duration = nothing to seek in
        CanNext = m.HasSession && m.CanNext;
        CanPrevious = m.HasSession && m.CanPrevious;
        RecomputePill();
        if (!ReferenceEquals(Artwork, _tintedFor)) ApplyTint();
        RequestLyrics(m);
        UpdatePosition();
    }

    private void ApplyTint()
    {
        _tintedFor = Artwork;
        Rgb? art = Artwork is null ? null : ArtColors.Dominant(ArtSampler.Bgra(Artwork));
        if (art is null)
        {
            CardBackground = _defaultCard;
            ProgressBrush = _defaultProgress;
            return;
        }

        var surface = Tokens.Rgb("Color.Surface");
        var tint = ArtColors.Mix(surface, art.Value, ArtColors.TintAmount);
        // Tint on the left, fading into plain surface by 85% of the width.
        var card = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        card.GradientStops.Add(new GradientStop(Color.FromRgb(tint.R, tint.G, tint.B), 0));
        card.GradientStops.Add(new GradientStop(Color.FromRgb(surface.R, surface.G, surface.B), 0.85));
        card.Freeze();
        CardBackground = card;
        ProgressBrush = Tokens.Solid(ArtColors.Progress(art.Value, Tokens.Rgb("Color.Track")));
    }

    /// <summary>Clock, track position and timer. Internal so tests can drive it with a fake clock.</summary>
    internal void Tick()
    {
        Clock = _now().LocalDateTime.ToString("HH:mm");
        if (IsPlaying) UpdatePosition();
        UpdateTimer();
        if (IsRecording || _s.Recorder?.IsRecording == true) ApplyRecording();
        CheckReminders();
        if (AgentText.Length > 0) ApplyAgents(); // the elapsed time, and sessions that went quiet
        if (_expanded && IsNotesPage) RefreshReminders();
        if (_now().Second < 1 || _rainAlert is null) CheckRain(); // the forecast is fixed for 15 min; the clock is what moves // «через 5 мин» counts down while it is on screen
    }

    private void UpdatePosition()
    {
        if (IsScrubbing) return;
        var now = DateTimeOffset.Now;
        TimeSpan pos;
        if (_seek is { } s && now - s.At < SeekGrace)
            pos = IsPlaying ? s.Position + (now - s.At) : s.Position;
        else
        {
            _seek = null;
            pos = _s.Media.Current.PositionAt(now);
        }
        PositionSeconds = Math.Min(pos.TotalSeconds, DurationSeconds);
    }

    partial void OnPositionSecondsChanged(double value)
    {
        var pos = TimeSpan.FromSeconds(value);
        PositionText = TimeFormat.Clock(pos);
        var dur = _s.Media.Current.Duration;
        RemainingText = dur > TimeSpan.Zero ? TimeFormat.Remaining(pos, dur) : "";
        UpdateLyricLine(pos);
    }

    /// <summary>Mouse down on the progress bar: stop following the player while the user drags.</summary>
    public void BeginScrub() => IsScrubbing = true;

    /// <summary>Mouse up: seek to where the bar was let go.</summary>
    public void CommitScrub()
    {
        if (!IsScrubbing) return;
        IsScrubbing = false;
        if (!CanSeek)
        {
            UpdatePosition(); // snap back to where the player really is
            return;
        }
        var target = TimeSpan.FromSeconds(PositionSeconds);
        _seek = (target, DateTimeOffset.Now);
        _ = _s.Media.SeekAsync(target);
    }

    [RelayCommand] private Task PlayPause() => _s.Media.PlayPauseAsync();
    [RelayCommand] private Task Next() => _s.Media.NextAsync();
    [RelayCommand] private Task Previous() => _s.Media.PreviousAsync();
    [RelayCommand] private void ShowHome() => IsPlayerPage = false;
    [RelayCommand] private void ShowPlayer() => IsPlayerPage = HasMedia;

    partial void OnPageChanged(string value)
    {
        SaveNote();
        UpdateTickRate();
        UpdateControlsTimer();
    }

    /// <summary>The Система tab reads the radios and VPN while it is on screen, and only then.</summary>
    private void UpdateControlsTimer()
    {
        _controlsTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) => await RefreshControls(), _dispatcher);
        if (_expanded && IsSystemPage)
        {
            _ = RefreshControls();
            _controlsTimer.Start();
        }
        else
        {
            _controlsTimer.Stop();
        }
    }

    // BUG-001: no player page without a player.
    partial void OnHasMediaChanged(bool value)
    {
        if (!value) IsPlayerPage = false;
    }

    // ======================= lyrics

    private async void RequestLyrics(MediaSnapshot m)
    {
        var key = $"{m.Artist}\u0001{m.Title}";
        if (key == _lyricsKey) return;
        _lyricsKey = key;
        _lyricsCts?.Cancel();
        SetLyrics(LyricsResult.NotFound);
        LyricsStatus = "";
        if (!_s.LyricsEnabled || !m.HasSession || m.Title.Length == 0) return;

        LyricsStatus = "Ищем слова…";
        var cts = _lyricsCts = new CancellationTokenSource();
        try
        {
            // Skipping through tracks fires many changes: only ask for the one the user stays on.
            await Task.Delay(LyricsDebounce, cts.Token);
            var result = await _s.Lyrics.GetAsync(m, cts.Token);
            if (cts.IsCancellationRequested) return;
            SetLyrics(result);
            LyricsStatus = result.Kind switch
            {
                LyricsKind.NotFound => "Для этой песни слов нет в LRCLIB",
                LyricsKind.Instrumental => "Эта песня без слов",
                LyricsKind.Unavailable => "LRCLIB не отвечает. Попробуем на следующем треке",
                _ => "",
            };
        }
        catch (OperationCanceledException)
        {
            // a newer track took over
        }
        catch (Exception ex)
        {
            Log.Warn("lyrics", ex.Message);
            if (!cts.IsCancellationRequested) LyricsStatus = "";
        }
    }

    private void SetLyrics(LyricsResult r)
    {
        _lyrics = r;
        _lyricIndex = int.MinValue;
        HasLyrics = r.Kind is LyricsKind.Synced or LyricsKind.Plain;
        LyricsSynced = r.Kind == LyricsKind.Synced;
        UpdateTickRate();
        if (r.Kind == LyricsKind.Plain)
        {
            PreviousLine = "";
            CurrentLine = r.Lines.Count > 0 ? r.Lines[0].Text : "";
            NextLine = r.Lines.Count > 1 ? r.Lines[1].Text : "";
        }
        else
        {
            UpdateLyricLine(TimeSpan.FromSeconds(PositionSeconds));
        }
    }

    private void UpdateLyricLine(TimeSpan pos)
    {
        if (!LyricsSynced) return;
        var lines = _lyrics.Lines;
        int i = Lrc.IndexAt(lines, pos + TimeSpan.FromMilliseconds(150)); // a hair early reads as on time
        if (i == _lyricIndex) return;
        _lyricIndex = i;
        PreviousLine = i >= 1 ? lines[i - 1].Text : "";
        CurrentLine = i >= 0 ? (lines[i].Text.Length > 0 ? lines[i].Text : "♪") : "♪";
        NextLine = i + 1 < lines.Count ? lines[i + 1].Text : "";
    }

    // ======================= volume

    private void ApplyVolume()
    {
        var v = _s.Volume;
        _syncingVolume = true;
        HasAudioDevice = v.Device is not null;
        VolumePercent = v.Volume;
        _syncingVolume = false;

        VolumeText = v.Volume.ToString();
        VolumeGlyph = v.Muted || v.Volume == 0 ? GlyphMuted : GlyphVolume;
        MuteLabel = v.Muted ? "Включить звук" : "Выключить звук";
        DeviceName = v.Device?.ShortName ?? "Нет устройства";

        // BUG-003: volume notifications come in streams; only touch the rows when the devices themselves changed,
        // so a row under the mouse is not replaced mid-click.
        var rows = v.Devices.Select(d => new DeviceItem(d.Id, d.Name, d.Id == v.Device?.Id)).ToList();
        if (!rows.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var r in rows) Devices.Add(r);
        }
    }

    partial void OnVolumePercentChanged(double value)
    {
        VolumeText = ((int)Math.Round(value)).ToString();
        if (!_syncingVolume) _s.Volume.SetVolume((int)Math.Round(value));
    }

    [RelayCommand] private void ToggleMute() => _s.Volume.ToggleMute();

    [RelayCommand]
    private void ToggleDeviceList()
    {
        IsDeviceListOpen = !IsDeviceListOpen;
        if (IsDeviceListOpen) IsMixerOpen = false;
        DeviceError = "";
    }

    // ---- mixer: per-app volume, read once a second while it is open (Windows sends no "app started playing" event we use)

    [ObservableProperty] private bool _isMixerOpen;
    [ObservableProperty] private bool _hasNoMixerApps;
    public ObservableCollection<MixerItem> MixerApps { get; } = [];
    private DispatcherTimer? _mixerTimer;

    [RelayCommand]
    private void ToggleMixer()
    {
        IsMixerOpen = !IsMixerOpen;
        if (IsMixerOpen) IsDeviceListOpen = false;
    }

    partial void OnIsMixerOpenChanged(bool value)
    {
        _mixerTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshMixer(), _dispatcher);
        if (value)
        {
            RefreshMixer();
            _mixerTimer.Start();
        }
        else
        {
            _mixerTimer.Stop();
        }
    }

    /// <summary>Update rows in place (a row being dragged keeps its slider), add new apps, drop apps that went quiet for good.</summary>
    internal void RefreshMixer()
    {
        var apps = _s.Volume.Apps();
        for (int i = MixerApps.Count - 1; i >= 0; i--)
            if (apps.All(a => a.Key != MixerApps[i].Key)) MixerApps.RemoveAt(i);
        for (int i = 0; i < apps.Count; i++)
        {
            var row = MixerApps.FirstOrDefault(r => r.Key == apps[i].Key);
            if (row is null) MixerApps.Insert(Math.Min(i, MixerApps.Count), new MixerItem(apps[i], _s.Volume));
            else row.Sync(apps[i]);
        }
        HasNoMixerApps = MixerApps.Count == 0;
    }

    [RelayCommand]
    private void SelectDevice(DeviceItem? item)
    {
        if (item is null) return;
        if (!item.IsCurrent && !_s.Volume.SetDefaultDevice(item.Id))
        {
            DeviceError = "Windows не разрешила сменить устройство.";
            return;
        }
        IsDeviceListOpen = false;
        DeviceError = "";
    }

    [RelayCommand]
    private static void OpenSoundSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });

    // ======================= clipboard tab and shelf

    public ObservableCollection<ClipRow> ClipRows { get; } = [];
    public ObservableCollection<ShelfRow> ShelfRows { get; } = [];
    [ObservableProperty] private string _clipQuery = "";
    [ObservableProperty] private bool _hasNoClips;
    /// <summary>Row counts, for the view: the panel's height follows them.</summary>
    [ObservableProperty] private int _clipCount;
    [ObservableProperty] private int _shelfCount;
    [ObservableProperty] private string _clipEmptyText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoShelf))]
    private bool _hasShelf;
    public bool HasNoShelf => !HasShelf;

    /// <summary>The view hands keyboard focus back to the window the user was typing in (before a paste, for example).</summary>
    public event EventHandler? ReturnFocusRequested;

    partial void OnClipQueryChanged(string value) => RefreshClips();

    internal void RefreshClips()
    {
        if (_s.Clipboard is null) return;
        var now = _now();
        var rows = _s.Clipboard.View(ClipQuery).Select(e => new ClipRow(
            e,
            OneLine(e.Text),
            e.Source is null ? RelativeTime.Format(e.At, now) : $"{e.Source} · {RelativeTime.Format(e.At, now)}",
            e.Kind == ClipKind.Image && e.ImageFile is { } f && System.IO.File.Exists(f) ? Thumbnail(f) : null)).ToList();
        ClipRows.Clear();
        foreach (var r in rows) ClipRows.Add(r);
        HasNoClips = rows.Count == 0;
        ClipCount = rows.Count;
        ClipEmptyText = ClipQuery.Trim().Length > 0 ? "Ничего не нашлось." : "Скопируйте что-нибудь — оно появится здесь.";
    }

    private void RefreshShelf()
    {
        if (_s.Shelf is null) return;
        ShelfRows.Clear();
        foreach (var i in _s.Shelf.Items)
            ShelfRows.Add(new ShelfRow(i.Path, System.IO.Path.GetFileName(i.Path.TrimEnd('\\')), AppDock.LoadIcon(i.Path)));
        HasShelf = ShelfRows.Count > 0;
        ShelfCount = ShelfRows.Count;
    }

    [RelayCommand]
    private void PasteClip(ClipRow? row)
    {
        if (row is null || _s.Clipboard is null) return;
        ReturnFocusRequested?.Invoke(this, EventArgs.Empty);
        ShowStatus(_s.Clipboard.Paste(row.Entry) ? "Вставлено" : "Буфер обмена занят. Попробуйте ещё раз");
    }

    [RelayCommand]
    private void TogglePin(ClipRow? row)
    {
        if (row is not null) _s.Clipboard?.SetPinned(row.Entry.Id, !row.Entry.Pinned);
    }

    [RelayCommand]
    private void RemoveClip(ClipRow? row)
    {
        if (row is not null) _s.Clipboard?.Remove(row.Entry.Id);
    }

    [RelayCommand] private void ClearClips() => _s.Clipboard?.Clear();

    public void AddToShelf(IEnumerable<string> paths) => _s.Shelf?.Add(paths);

    /// <summary>The «+» on the shelf: the same as dropping files, for when dragging is awkward.</summary>
    [RelayCommand]
    private void PickShelfFiles()
    {
        var dlg = new OpenFileDialog { Title = "Положить на полку", Multiselect = true };
        if (dlg.ShowDialog() == true) AddToShelf(dlg.FileNames);
    }

    [RelayCommand]
    private void OpenShelfItem(ShelfRow? row)
    {
        if (row is not null) _s.Dock.Launch(row.Path);
    }

    [RelayCommand]
    private void RemoveFromShelf(ShelfRow? row)
    {
        if (row is not null) _s.Shelf?.Remove(row.Path);
    }

    private static string OneLine(string text)
    {
        var s = string.Join(" ", text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    private static ImageSource? Thumbnail(string file)
    {
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.DecodePixelHeight = 72;
            img.UriSource = new Uri(file);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException) { return null; }
    }

    // ======================= notes tab

    public ObservableCollection<NoteRow> NoteRows { get; } = [];
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private bool _hasNoNotes;
    private string? _noteId;          // the note in the editor; null until something is typed
    private bool _loadingNote;
    private DispatcherTimer? _noteSave;

    private IListStore<Note>? _noteStore;
    private IListStore<Note> NoteStore => _noteStore ??= _s.Notes ?? new MemoryListStore<Note>();

    private void RefreshNotes()
    {
        NoteRows.Clear();
        foreach (var n in NoteStore.Items) NoteRows.Add(new NoteRow(n, n.Id == _noteId));
        HasNoNotes = NoteRows.Count == 0;
    }

    partial void OnNoteTextChanged(string value)
    {
        if (_loadingNote) return;
        _noteId ??= Guid.NewGuid().ToString("N")[..12];
        _noteSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveNote(), _dispatcher);
        _noteSave.Stop();
        _noteSave.Start(); // typing is saved 0.4 s after the last key
    }

    /// <summary>Writes the editor's text now (also when the panel closes or the page changes).</summary>
    internal void SaveNote()
    {
        _noteSave?.Stop();
        if (_noteId is null) return;
        var current = NoteStore.Items.FirstOrDefault(n => n.Id == _noteId);
        if (current?.Text == NoteText) return;
        NoteStore.Set(NoteList.Upsert(NoteStore.Items, new Note(_noteId, NoteText, _now())));
    }

    [RelayCommand]
    private void OpenNote(NoteRow? row)
    {
        if (row is null) return;
        SaveNote();
        _noteId = row.Note.Id;
        _loadingNote = true;
        NoteText = row.Note.Text;
        _loadingNote = false;
        RefreshNotes();
    }

    [RelayCommand]
    private void NewNote()
    {
        SaveNote();
        _noteId = null;
        _loadingNote = true;
        NoteText = "";
        _loadingNote = false;
        RefreshNotes();
    }

    [RelayCommand]
    private void DeleteNote(NoteRow? row)
    {
        if (row is null) return;
        if (row.Note.Id == _noteId) { _noteSave?.Stop(); _noteId = null; _loadingNote = true; NoteText = ""; _loadingNote = false; }
        NoteStore.Set(NoteStore.Items.Where(n => n.Id != row.Note.Id));
    }

    /// <summary>From the command bar: «заметка купить хлеб».</summary>
    public void AddNote(string text) => NoteStore.Set(NoteList.Upsert(NoteStore.Items, new Note(Guid.NewGuid().ToString("N")[..12], text, _now())));

    // ---- reminders (on the Notes tab; they fire as an alert pill)

    public ObservableCollection<ReminderRow> ReminderRows { get; } = [];
    [ObservableProperty] private string _reminderInput = "";
    [ObservableProperty] private bool _hasReminders;
    [ObservableProperty] private int _reminderCount;
    private Reminder? _dueReminder;

    private IListStore<Reminder>? _reminderStore;
    private IListStore<Reminder> ReminderStore => _reminderStore ??= _s.Reminders ?? new MemoryListStore<Reminder>();

    private void RefreshReminders()
    {
        var now = _now();
        ReminderRows.Clear();
        foreach (var r in ReminderStore.Items.OrderBy(r => r.At)) ReminderRows.Add(new ReminderRow(r, ReminderText.When(r, now)));
        HasReminders = ReminderRows.Count > 0;
        ReminderCount = ReminderRows.Count;
    }

    [RelayCommand]
    private void AddReminderFromInput()
    {
        if (ReminderInput.Trim().Length == 0) return;
        if (ReminderParser.Parse(ReminderInput, _now()) is not { } r)
        {
            ShowStatus("Не понял, когда напомнить");
            return;
        }
        AddReminder(r);
        ReminderInput = "";
    }

    public void AddReminder(Reminder r)
    {
        ReminderStore.Set(ReminderStore.Items.Append(r));
        ShowStatus($"Напомню {ReminderText.When(r, _now())}");
    }

    [RelayCommand]
    private void RemoveReminder(ReminderRow? row)
    {
        if (row is not null) ReminderStore.Set(ReminderStore.Items.Where(r => r.Id != row.Reminder.Id));
    }

    /// <summary>Called every tick: the earliest reminder that is due becomes the alert (after a finished timer, if any).</summary>
    private void CheckReminders()
    {
        if (_dueReminder is not null || _alerted) return;
        var now = _now();
        var due = ReminderStore.Items.Where(r => r.At <= now).OrderBy(r => r.At).FirstOrDefault();
        if (due is null) return;
        _dueReminder = due;
        RefreshAlert();
    }

    /// <summary>Done: a one-off is removed, a repeating one moves to its next time.</summary>
    private void FinishReminder(TimeSpan? snooze)
    {
        if (_dueReminder is not { } r) return;
        var now = _now();
        var rest = ReminderStore.Items.Where(x => x.Id != r.Id);
        var next = snooze is { } s ? r with { At = now + s } : r.Next(now) is { } n ? r with { At = n } : null;
        ReminderStore.Set(next is null ? rest : rest.Append(next));
        _dueReminder = null;
        RefreshAlert();
        CheckReminders(); // another one may be waiting
    }

    // ---- alert pill: one at a time, by importance. A finished timer, then Claude asking, then a reminder, then rain.

    [ObservableProperty] private string _alertTitle = "Таймер закончился";
    [ObservableProperty] private string _alertGlyph = "";
    [ObservableProperty] private string _alertSecondaryLabel = "+1 мин";
    [ObservableProperty] private string _alertPrimaryLabel = "Готово";
    private string _alertOwner = "";

    /// <summary>Shows the most important pending alert; sounds once when a new one appears.</summary>
    private void RefreshAlert(bool force = false)
    {
        string owner = _alerted ? "timer" : _agentAsk is not null ? "agent" : _dueReminder is not null ? "reminder" : _rainAlert is not null ? "rain" : "";
        if (owner != _alertOwner || force)
        {
            bool isNew = owner != _alertOwner;
            _alertOwner = owner;
            switch (owner)
            {
                case "timer":
                    (AlertTitle, AlertDetail, AlertGlyph, AlertSecondaryLabel, AlertPrimaryLabel) =
                        ("Таймер закончился", $"Был поставлен на {FormatDuration(_timer.Duration)}", "", "+1 мин", "Готово");
                    break;
                case "agent":
                    var a = _agentAsk!;
                    (AlertTitle, AlertDetail, AlertGlyph, AlertSecondaryLabel, AlertPrimaryLabel) =
                        ("Claude спрашивает", a.Project.Length > 0 ? $"{a.Project} · {a.Detail}" : a.Detail, "", "Запретить", "Разрешить");
                    break;
                case "reminder":
                    var r = _dueReminder!;
                    (AlertTitle, AlertDetail, AlertGlyph, AlertSecondaryLabel, AlertPrimaryLabel) =
                        (r.Text, $"Напоминание · {r.At:HH:mm}", "", "Через 10 мин", "Готово");
                    break;
                case "rain":
                    var w = _rainAlert!.Value;
                    (AlertTitle, AlertDetail, AlertGlyph, AlertSecondaryLabel, AlertPrimaryLabel) =
                        (w.Title, w.Detail, "", "Прогноз", "Готово");
                    break;
            }
            if (owner.Length > 0 && isNew) (_s.Chime ?? System.Media.SystemSounds.Asterisk.Play)();
        }
        RecomputePill();
    }

    [RelayCommand]
    private void AlertSecondary()
    {
        switch (_alertOwner)
        {
            case "timer": TimerAddMinute(); break;
            case "agent": AnswerAgent(false); break;
            case "reminder": FinishReminder(TimeSpan.FromMinutes(10)); break;
            case "rain":
                _rainAlert = null;
                IsWeatherPage = true;
                ExpandRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
        RefreshAlert();
    }

    // ======================= Claude Code (its hooks call «Stillbar.exe --hook», see AgentServer)

    private readonly AgentBoard _agents = new();
    private AgentSession? _agentAsk;       // the question on the alert pill
    private PermissionTicket? _ticket;     // how to answer it

    /// <summary>«stillbar · Bash: npm test»; empty when no session is busy or waiting.</summary>
    [ObservableProperty] private string _agentText = "";
    [ObservableProperty] private string _agentElapsed = "";

    internal void OnAgent(AgentMessage m)
    {
        var (s, finished) = _agents.Apply(m.Event, _now());
        if (m.Ticket is { } t)
        {
            // A newer question replaces an unanswered one; the older one goes back to Claude Code's own window.
            if (_ticket is { IsOpen: true } old && old != t) old.Decide(null);
            _ticket = t;
            t.Expired += () => _dispatcher.InvokeAsync(() =>
            {
                if (_ticket != t) return;
                _ticket = null;
                ShowStatus("Ответьте Claude в его окне");
                ApplyAgents();
            });
        }
        if (finished && s is not null) ShowStatus(s.Project.Length > 0 ? $"Claude закончил · {s.Project}" : "Claude закончил");
        ApplyAgents();
    }

    private void ApplyAgents()
    {
        var now = _now();
        var cur = _agents.Current(now);
        // The question on the alert pill is the one that can still be answered from here (the open ticket's).
        var ask = _ticket is { IsOpen: true } t
            ? _agents.Sessions.FirstOrDefault(x => x.Id == t.SessionId && x.State == AgentState.Asking)
            : null;
        if (ask?.Id != _agentAsk?.Id || ask?.Detail != _agentAsk?.Detail)
        {
            _agentAsk = ask;
            RefreshAlert(force: ask is not null);
        }
        var project = cur is { Project.Length: > 0 } ? cur.Project : "Claude"; // scratch folders have no name worth showing
        AgentText = cur switch
        {
            null => "",
            { State: AgentState.Waiting } => $"{project} · ждёт вашего ответа",
            { State: AgentState.Asking } => $"{project} · ждёт разрешения",
            _ => $"{project} · {cur.Detail}",
        };
        AgentElapsed = cur is { State: AgentState.Working } ? Stillbar.Core.Capture.Recording.Elapsed(now - cur.Since) : "";
        RecomputePill();
    }

    private void AnswerAgent(bool allow)
    {
        if (_ticket is { } t)
        {
            t.Decide(allow);
            _agents.Answered(t.SessionId, _now());
            _ticket = null;
        }
        _agentAsk = null;
        ShowStatus(allow ? "Разрешено" : "Запрещено");
        ApplyAgents();
    }

    // ======================= control centre tab (Система)

    [ObservableProperty] private bool _hasWifi;
    [ObservableProperty] private bool _wifiOn;
    [ObservableProperty] private bool _hasBluetooth;
    [ObservableProperty] private bool _bluetoothOn;
    [ObservableProperty] private bool _darkTheme;
    [ObservableProperty] private bool _hasNoVpn;
    [ObservableProperty] private int _vpnCount;
    [ObservableProperty] private string _restartLabel = "Перезагрузка";
    [ObservableProperty] private string _shutdownLabel = "Выключение";
    public ObservableCollection<VpnRow> VpnRows { get; } = [];
    public ObservableCollection<BluetoothRow> BluetoothRows { get; } = [];
    [ObservableProperty] private bool _hasNoPaired;
    [ObservableProperty] private int _pairedCount;

    private readonly Stillbar.Core.Controls.ConfirmGate _confirm = new(TimeSpan.FromSeconds(4));
    private bool _syncingControls;
    private DispatcherTimer? _controlsTimer;

    /// <summary>Reads Wi-Fi, Bluetooth, theme and VPN now; the tab calls it every few seconds while it is open.</summary>
    internal async Task RefreshControls()
    {
        if (_s.Controls is not { } c) return;
        var st = await c.ReadAsync();
        _syncingControls = true;
        HasWifi = st.WiFi is not null;
        WifiOn = st.WiFi == true;
        HasBluetooth = st.Bluetooth is not null;
        BluetoothOn = st.Bluetooth == true;
        DarkTheme = st.DarkTheme;
        _syncingControls = false;
        var rows = st.Vpns.Select(v => new VpnRow(v.Name, v.Connected)).ToList();
        if (!rows.SequenceEqual(VpnRows))
        {
            VpnRows.Clear();
            foreach (var r in rows) VpnRows.Add(r);
        }
        HasNoVpn = VpnRows.Count == 0;
        VpnCount = VpnRows.Count;
        var paired = (st.Paired ?? []).Select(p => new BluetoothRow(p.Name, p.Connected, p.Kind)).ToList();
        if (!paired.SequenceEqual(BluetoothRows))
        {
            BluetoothRows.Clear();
            foreach (var p in paired) BluetoothRows.Add(p);
        }
        HasNoPaired = BluetoothRows.Count == 0;
        PairedCount = BluetoothRows.Count;
        UpdateConfirmLabels();
    }

    partial void OnWifiOnChanged(bool value) { if (!_syncingControls) _ = SetRadio(RadioKind.WiFi, value, "Wi-Fi"); }
    partial void OnBluetoothOnChanged(bool value) { if (!_syncingControls) _ = SetRadio(RadioKind.Bluetooth, value, "Bluetooth"); }

    partial void OnDarkThemeChanged(bool value)
    {
        if (_syncingControls || _s.Controls is null) return;
        _s.Controls.SetDarkTheme(value);
        ShowStatus(value ? "Тёмная тема Windows" : "Светлая тема Windows");
    }

    private async Task SetRadio(RadioKind kind, bool on, string name)
    {
        if (_s.Controls is null) return;
        if (!await _s.Controls.SetRadioAsync(kind, on))
            ShowStatus($"Windows не разрешила переключить {name}");
        await RefreshControls(); // show what is true, not what was asked
    }

    [RelayCommand]
    private async Task ToggleVpn(VpnRow? row)
    {
        if (row is null || _s.Controls is null) return;
        ShowStatus(row.Connected ? $"Отключаю {row.Name}…" : $"Подключаю {row.Name}…");
        var error = await _s.Controls.SetVpnAsync(row.Name, !row.Connected);
        ShowStatus(error ?? (row.Connected ? $"{row.Name} отключён" : $"{row.Name} подключён"));
        await RefreshControls();
    }

    [RelayCommand]
    private void OpenWindowsSettings(string? uri) => _s.Controls?.OpenSettings(uri is { Length: > 0 } ? uri : "ms-settings:");

    [RelayCommand]
    private void LockComputer()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        _s.Controls?.Lock();
    }

    [RelayCommand]
    private void SleepComputer()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        _s.Controls?.Sleep();
    }

    /// <summary>First click asks («Точно?»), a second one within 4 s restarts. Unsaved work is the user's to save first.</summary>
    [RelayCommand]
    private void RestartComputer()
    {
        if (_confirm.Click("restart", _now())) _s.Controls?.Restart();
        UpdateConfirmLabels();
    }

    [RelayCommand]
    private void ShutDownComputer()
    {
        if (_confirm.Click("shutdown", _now())) _s.Controls?.ShutDown();
        UpdateConfirmLabels();
    }

    private void UpdateConfirmLabels()
    {
        var armed = _confirm.Armed(_now());
        RestartLabel = armed == "restart" ? "Точно перезагрузить?" : "Перезагрузка";
        ShutdownLabel = armed == "shutdown" ? "Точно выключить?" : "Выключение";
    }

    // ======================= settings

    /// <summary>The gear on the panel: the app opens the settings window.</summary>
    public event EventHandler? SettingsRequested;

    [RelayCommand]
    private void OpenSettings()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    // ======================= command bar (Alt+Space)

    /// <summary>The app opens the command bar window.</summary>
    public event EventHandler? CommandBarRequested;

    [RelayCommand]
    private void OpenCommandBar()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        CommandBarRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Does what a command bar line says. Returns false if it could not (the reason is in the pill).</summary>
    public bool ExecuteCommand(CommandResult r)
    {
        switch (r.Kind)
        {
            case CommandKind.Calc or CommandKind.Convert:
                bool copied = _s.Screen.SetClipboardText((string)r.Payload!);
                ShowStatus(copied ? $"{r.Payload} скопировано" : "Буфер обмена занят. Попробуйте ещё раз");
                return copied;
            case CommandKind.Timer:
                StartTimerFor((TimeSpan)r.Payload!);
                return true; // the pill shows the countdown at once: no toast over it
            case CommandKind.Reminder:
                AddReminder((Reminder)r.Payload!);
                return true;
            case CommandKind.Note:
                AddNote((string)r.Payload!);
                ShowStatus("Заметка сохранена");
                return true;
            case CommandKind.TextTool:
                return ApplyTextTool((string)r.Payload!);
            case CommandKind.App or CommandKind.File:
                _s.Dock.Launch((string)r.Payload!);
                return true;
            default:
                return false;
        }
    }

    private bool ApplyTextTool(string id)
    {
        var text = _s.Screen.GetClipboardText();
        if (string.IsNullOrEmpty(text)) { ShowStatus("В буфере нет текста"); return false; }
        if (id == "stats") { ShowStatus(TextTools.Stats(text)); return true; }
        var tool = TextTools.All.First(t => t.Id == id);
        if (tool.Apply(text) is not { } result) { ShowStatus(id == "json" ? "В буфере не JSON" : "Не получилось: текст не подходит"); return false; }
        bool ok = _s.Screen.SetClipboardText(result);
        ShowStatus(ok ? "Готово, текст в буфере" : "Буфер обмена занят. Попробуйте ещё раз");
        return ok;
    }

    // ======================= weather

    private void ApplyWeather()
    {
        var w = _s.Weather.Current;
        HasWeather = w is not null;
        if (w is null)
        {
            ApplyForecast(); // stale weather takes the old forecast with it
            return;
        }

        TemperatureText = w.TemperatureText;
        (WeatherIcon, WeatherBrush) = Glyph(w.Kind, w.IsDay);
        ApplyForecast();
    }

    private static (Geometry Icon, Brush Brush) Glyph(WeatherKind kind, bool isDay) =>
        (Tokens.Geometry(!isDay && kind == WeatherKind.Clear ? "Weather.Clear.Night" : $"Weather.{kind}"),
         Tokens.Brush(kind == WeatherKind.Clear && isDay ? "Brush.Warning" : "Brush.Text"));

    // ---- weather page: 24 hours and 7 days (opened from the temperature in the tab bar)

    public ObservableCollection<HourRow> HourRows { get; } = [];
    public ObservableCollection<DayRow> DayRows { get; } = [];
    [ObservableProperty] private bool _hasForecast;
    [ObservableProperty] private string _weatherSummary = "";
    [ObservableProperty] private string _weatherPlace = "";
    [ObservableProperty] private string _todayRange = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRainNote))]
    private string _rainNote = "";
    public bool HasRainNote => RainNote.Length > 0;

    /// <summary>Settings → «Предупреждать о дожде».</summary>
    public bool RainWarningsEnabled { get; set; } = true;

    [RelayCommand]
    private void ShowWeather()
    {
        if (HasForecast) IsWeatherPage = true;
    }

    private const double RangeWidth = 150;

    private void ApplyForecast()
    {
        var f = _s.Weather.Forecast;
        HasForecast = f is { Hours.Count: > 0 };
        if (f is null || !HasForecast)
        {
            if (IsWeatherPage) IsHomePage = true;
            return;
        }
        WeatherSummary = WeatherText.Describe(f.Now.Kind);
        WeatherPlace = _s.CityName?.Invoke() ?? "";
        TodayRange = f.Days.FirstOrDefault() is { } today ? $"↑ {WeatherText.Degrees(today.MaxC)}   ↓ {WeatherText.Degrees(today.MinC)}" : "";

        HourRows.Clear();
        for (int i = 0; i < f.Hours.Count; i++)
        {
            var h = f.Hours[i];
            var (icon, brush) = Glyph(h.Kind, h.IsDay);
            HourRows.Add(new HourRow(i == 0 ? "Сейчас" : h.Time.ToString("HH"), icon, brush, h.TemperatureText, h.PrecipChance >= 20 ? $"{h.PrecipChance} %" : ""));
        }

        DayRows.Clear();
        double lo = f.Days.Count > 0 ? f.Days.Min(d => d.MinC) : 0, hi = f.Days.Count > 0 ? f.Days.Max(d => d.MaxC) : 1;
        double span = Math.Max(1, hi - lo);
        var today0 = _now().LocalDateTime;
        foreach (var d in f.Days)
        {
            var (icon, brush) = Glyph(d.Kind, true);
            double left = (d.MinC - lo) / span * RangeWidth, width = Math.Max(6, (d.MaxC - d.MinC) / span * RangeWidth);
            DayRows.Add(new DayRow(WeatherText.DayName(d.Date, today0), icon, brush, WeatherText.Degrees(d.MinC), WeatherText.Degrees(d.MaxC),
                d.PrecipChance >= 20 ? $"{d.PrecipChance} %" : "", new Thickness(left, 0, 0, 0), width));
        }

        var start = RainWarning.Upcoming(f, _now().LocalDateTime);
        RainNote = start is { } s ? RainWarning.Text(f, s, _now().LocalDateTime).Sentence : "";
        CheckRain();
    }

    // ---- rain warning: an alert pill about an hour before rain, once per spell

    private DateTime? _rainWarnedFor;
    private (string Title, string Detail)? _rainAlert;

    private void CheckRain()
    {
        if (!RainWarningsEnabled || _rainAlert is not null || _s.Weather.Forecast is not { } f) return;
        var now = _now().LocalDateTime;
        if (RainWarning.Upcoming(f, now) is not { } start) return;
        // One warning per spell: a start within 3 h of the last one we announced is the same rain.
        if (_rainWarnedFor is { } last && Math.Abs((start - last).TotalHours) < 3) return;
        _rainWarnedFor = start;
        var (title, detail, _) = RainWarning.Text(f, start, now);
        _rainAlert = (title, detail);
        RefreshAlert();
    }

    /// <summary>The panel opens on a page by itself (the rain alert's «Прогноз»).</summary>
    public event EventHandler? ExpandRequested;

    // ======================= downloads

    private void ApplyDownload()
    {
        var d = _s.Downloads.Primary;
        if (d is not null)
        {
            DownloadName = SizeFormat.MiddleEllipsis(d.Name, 26);
            DownloadDetail = d.Completed
                ? "Готово"
                : d.BytesPerSecond > 0
                    ? $"{SizeFormat.Bytes(d.Bytes)} · {SizeFormat.Speed(d.BytesPerSecond)}"
                    : SizeFormat.Bytes(d.Bytes);
            DownloadGlyph = d.Completed ? "" : "";
            DownloadBrush = Tokens.Brush(d.Completed ? "Brush.Success" : "Brush.Text");
        }
        RecomputePill();
    }

    // ======================= dock

    private void ApplyDock()
    {
        DockApps.Clear();
        foreach (var a in _s.Dock.Apps) DockApps.Add(a);
    }

    [RelayCommand] private void LaunchApp(DockApp? app) { if (app is not null) _s.Dock.Launch(app.Path); }
    [RelayCommand] private void RemoveApp(DockApp? app) { if (app is not null) _s.Dock.Remove(app.Path); }

    public void AddApps(IEnumerable<string> paths) => _s.Dock.Add(paths);

    [RelayCommand]
    private void AddApp()
    {
        // A file dialog is a normal window and takes focus: fine, the user asked for it.
        var dlg = new OpenFileDialog
        {
            Title = "Добавить программу",
            Filter = "Программы и ярлыки|*.exe;*.lnk;*.url",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        if (dlg.ShowDialog() == true) _s.Dock.Add(dlg.FileNames);
    }

    // ======================= quick actions

    /// <summary>Windows' snip overlay; the picture is in the clipboard at once and opens in the markup editor.</summary>
    [RelayCommand]
    private async Task SnipArea()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        var image = await _s.Screen.SnipImageAsync();
        if (image is null) { ShowStatus("Снимок отменён"); return; }
        _s.Screen.OpenMarkup(image);
        ShowStatus("Снимок скопирован");
    }

    [RelayCommand]
    private async Task PickColor()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        await Task.Delay(250); // the frozen picture must not contain the panel
        var hex = await _s.Screen.PickColorAsync();
        if (hex is null) { ShowStatus("Цвет не выбран"); return; }
        ShowStatus(_s.Screen.SetClipboardText(hex) ? $"{hex} скопирован" : "Буфер обмена занят. Попробуйте ещё раз");
    }

    /// <summary>
    /// Windows' voice typing (Win+H) in the window the user was typing in: the panel closes first, and since the bar
    /// never takes the focus, the key press reaches that window.
    /// </summary>
    [RelayCommand]
    private async Task VoiceTyping()
    {
        if (_s.Dictation is { IsListening: true } listening)
        {
            listening.Stop();
            return;
        }
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        ReturnFocusRequested?.Invoke(this, EventArgs.Empty); // in case a field of ours had the keyboard
        await Task.Delay(150);
        if (_s.Screen.StartVoiceTyping()) return; // Windows' own voice typing is allowed here: it is the better one
        if (_s.Dictation is not { } d)
        {
            ShowStatus("Голосовой ввод Windows выключен администратором");
            return;
        }
        // Switched off by policy: Stillbar's own offline dictation instead.
        _sentenceStart = true;
        if (await d.StartAsync(new Progress<string>(ShowStatus)) is { } error) ShowStatus(error);
    }

    // ---- offline dictation (Vosk): the pill shows what it hears; each finished phrase is typed where the cursor is

    [ObservableProperty] private string _dictationText = "";
    [ObservableProperty] private string _voiceTileLabel = "Голос в текст";
    private bool _sentenceStart = true;

    [RelayCommand]
    private void StopDictation() => _s.Dictation?.Stop();

    private void ApplyDictation()
    {
        bool on = _s.Dictation?.IsListening == true;
        var partial = _s.Dictation?.Partial ?? "";
        DictationText = partial.Length > 0 ? partial : "Слушаю… говорите";
        VoiceTileLabel = on ? "Остановить диктовку" : "Голос в текст";
        RecomputePill();
    }

    private void TypeHeard(string words)
    {
        var (text, ends) = Phrase.Format(words, _sentenceStart);
        if (text.Length == 0) return;
        _sentenceStart = ends;
        _s.Screen.TypeText(text);
    }

    /// <summary>"Без формата": the text in the clipboard loses fonts, colours and links, so it pastes as plain text.</summary>
    [RelayCommand]
    private void PlainText()
    {
        var n = _s.Screen.StripClipboardFormatting();
        ShowStatus(n is { } len ? $"Формат убран · {len} {Plural(len, "символ", "символа", "символов")}" : "В буфере нет текста");
    }

    // ---- screen recording: the pill shows it while it runs; the same tile stops it

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordingText = "";
    [ObservableProperty] private string _recordTileLabel = "Видео экрана";

    [RelayCommand]
    private async Task ToggleRecording()
    {
        if (_s.Recorder is not { } rec) return;
        if (rec.IsRecording)
        {
            var file = await rec.StopAsync();
            ShowStatus(file is null ? "Видео не записалось" : "Видео сохранено в «Видео\\Stillbar»");
        }
        else
        {
            CollapseRequested?.Invoke(this, EventArgs.Empty);
            if (rec.Start() is { } error) ShowStatus(error);
        }
        ApplyRecording();
    }

    private void ApplyRecording()
    {
        bool on = _s.Recorder?.IsRecording == true;
        IsRecording = on;
        RecordingText = on ? Stillbar.Core.Capture.Recording.Elapsed(_s.Recorder!.Elapsed) : "";
        RecordTileLabel = on ? "Остановить запись" : "Видео экрана";
        RecomputePill();
    }

    [RelayCommand]
    private async Task CopyScreen()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        await Task.Delay(300); // let the panel leave the screen before the capture
        ShowStatus(_s.Screen.CopyPrimaryScreen() ? "Снимок экрана скопирован" : "Буфер обмена занят. Попробуйте ещё раз");
    }

    [RelayCommand]
    private async Task TextFromScreen()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        var image = await _s.Screen.SnipImageAsync();
        if (image is null) { ShowStatus("Снимок отменён"); return; }
        var text = await _s.Screen.RecognizeTextAsync(image);
        if (text.Length == 0) { ShowStatus("На снимке нет текста"); return; }
        ShowStatus(_s.Screen.SetClipboardText(text)
            ? $"Текст скопирован · {text.Length} {Plural(text.Length, "символ", "символа", "символов")}"
            : "Буфер обмена занят. Попробуйте ещё раз");
    }

    [RelayCommand]
    private async Task QrFromScreen()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        var image = await _s.Screen.SnipImageAsync();
        if (image is null) { ShowStatus("Снимок отменён"); return; }
        var text = _s.Screen.DecodeQr(image);
        if (text is null) { ShowStatus("На снимке нет QR-кода"); return; }
        bool link = Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        // Never opened automatically: a QR code can point anywhere. It goes to the clipboard; the user decides.
        ShowStatus(_s.Screen.SetClipboardText(text)
            ? (link ? $"Ссылка скопирована · {u!.Host}" : "Содержимое QR скопировано")
            : "Буфер обмена занят. Попробуйте ещё раз");
    }

    private static string Plural(int n, string one, string few, string many) =>
        (n % 10, n % 100) switch
        {
            (1, not 11) => one,
            (>= 2 and <= 4, < 12 or > 14) => few,
            _ => many,
        };

    // ======================= timer

    [RelayCommand]
    private void StartTimer(string? minutes)
    {
        if (int.TryParse(minutes, out int m) && m > 0) StartTimerFor(TimeSpan.FromMinutes(m));
    }

    /// <summary>Any length: presets, and «таймер 7 мин» / «таймер 1:30» from the command bar.</summary>
    public void StartTimerFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        _alerted = false;
        _timer.Start(duration, _now());
        UpdateTimer();
    }

    [RelayCommand]
    private void PauseResumeTimer()
    {
        if (_timer.IsRunning) _timer.Pause(_now());
        else _timer.Resume(_now());
        UpdateTimer();
    }

    [RelayCommand]
    private void ResetTimer()
    {
        _timer.Reset();
        _alerted = false;
        UpdateTimer();
    }

    /// <summary>«+1 мин» on the alert: a fresh minute, the alert goes away.</summary>
    [RelayCommand]
    private void TimerAddMinute()
    {
        _timer.Add(TimeSpan.FromMinutes(1), _now());
        _alerted = false;
        UpdateTimer();
    }

    [RelayCommand]
    private void DismissAlert()
    {
        switch (_alertOwner)
        {
            case "timer": ResetTimer(); break;
            case "agent": AnswerAgent(true); break;
            case "reminder": FinishReminder(snooze: null); break;
            case "rain": _rainAlert = null; break;
        }
        CheckReminders();
        RefreshAlert(); // whatever waited behind this one comes forward
    }

    private void UpdateTimer()
    {
        var now = _now();
        if (_timer.IsFinished(now) && !_alerted) _alerted = true;
        IsTimerSet = _timer.IsSet && !_alerted;
        IsTimerRunning = _timer.IsRunning && !_alerted;
        TimerText = CountdownTimer.Format(_timer.Remaining(now));
        TimerPauseGlyph = _timer.IsRunning ? "" : "";
        TimerPauseLabel = _timer.IsRunning ? "Пауза" : "Продолжить";
        RefreshAlert();
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} ч {d.Minutes} мин" : $"{(int)Math.Round(d.TotalMinutes)} мин";

    [RelayCommand] private void ToggleLyrics() => LyricsHidden = !LyricsHidden;

    public void ShowStatus(string text)
    {
        StatusGlyph = "";
        StatusBrush = Tokens.Brush("Brush.Success");
        StatusText = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // The toast's icon: a tick for results, a keyboard for the layout and Caps Lock indicator.
    [ObservableProperty] private string _statusGlyph = "";
    [ObservableProperty] private Brush? _statusBrush;

    /// <summary>«RU», «Caps Lock включён»: a short toast with a keyboard icon, shorter than a result toast.</summary>
    public void ShowKeyboard(string text)
    {
        ShowStatus(text);
        StatusGlyph = "";
        StatusBrush = Tokens.Brush("Brush.Accent");
    }
}
