using System.IO;
using System.Windows.Media;
using Stillbar.App.Services;
using Stillbar.App.Shell;
using Stillbar.Core.Downloads;
using Stillbar.Core.Lyrics;
using Stillbar.Core.Media;
using Stillbar.Core.Weather;

namespace Stillbar.App.Tests;

public sealed class FakeMedia : IMediaService
{
    public MediaSnapshot Current { get; private set; } = MediaSnapshot.None;
    public ImageSource? Artwork { get; set; }
    public event EventHandler? Changed;
    public List<string> Calls { get; } = [];
    public List<TimeSpan> Seeks { get; } = [];

    public static MediaSnapshot Track(string title = "Song", string artist = "Artist", bool playing = true,
        double pos = 30, double dur = 200, bool canSeek = true, bool canNext = true, bool canPrevious = true) =>
        new(title, artist, "", playing, TimeSpan.FromSeconds(pos), TimeSpan.FromSeconds(dur), DateTimeOffset.Now, "app")
        { CanSeek = canSeek, CanNext = canNext, CanPrevious = canPrevious };

    public void Set(MediaSnapshot s)
    {
        Current = s;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task StartAsync() => Task.CompletedTask;
    public Task PlayPauseAsync() { Calls.Add("playpause"); return Task.CompletedTask; }
    public Task NextAsync() { Calls.Add("next"); return Task.CompletedTask; }
    public Task PreviousAsync() { Calls.Add("previous"); return Task.CompletedTask; }
    public Task SeekAsync(TimeSpan position) { Seeks.Add(position); return Task.CompletedTask; }
}

public sealed class FakeVolume : IVolumeService
{
    public int Volume { get; private set; } = 50;
    public bool Muted { get; private set; }
    public AudioDevice? Device { get; private set; } = new("a", "Динамики (Тест)");
    public IReadOnlyList<AudioDevice> Devices { get; private set; } = [new("a", "Динамики (Тест)"), new("b", "Наушники (Тест)")];
    public event EventHandler? Changed;
    public List<int> SetCalls { get; } = [];
    public bool AllowSwitch { get; set; } = true;

    /// <summary>As if the volume changed elsewhere (keyboard keys, another app).</summary>
    public void SystemVolume(int v)
    {
        Volume = v;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveAllDevices()
    {
        Device = null;
        Devices = [];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetVolume(int percent)
    {
        SetCalls.Add(percent);
        Volume = percent;
        Changed?.Invoke(this, EventArgs.Empty); // the real endpoint echoes a notification
    }

    public void ToggleMute() => Muted = !Muted;
    public bool SetDefaultDevice(string id) => AllowSwitch;

    /// <summary>The mixer's apps; tests add and remove rows to play apps starting and stopping.</summary>
    public List<AppVolume> AppList { get; } = [new("chrome", "Google Chrome", 80, false), new("telegram", "Telegram", 40, true)];
    public List<(string Key, int Percent)> AppSetCalls { get; } = [];

    public IReadOnlyList<AppVolume> Apps() => AppList.ToList();

    public void SetAppVolume(string key, int percent)
    {
        AppSetCalls.Add((key, percent));
        int i = AppList.FindIndex(a => a.Key == key);
        if (i >= 0) AppList[i] = AppList[i] with { Volume = percent };
    }

    public void ToggleAppMute(string key)
    {
        int i = AppList.FindIndex(a => a.Key == key);
        if (i >= 0) AppList[i] = AppList[i] with { Muted = !AppList[i].Muted };
    }
}

public sealed class FakeWeather(WeatherNow? now) : IWeatherService
{
    public WeatherNow? Current { get; } = now;
    public event EventHandler? Changed { add { } remove { } }
}

/// <summary>Answers per title after an optional delay, and records what was asked.</summary>
public sealed class FakeLyrics : ILyricsService
{
    public Dictionary<string, LyricsResult> ByTitle { get; } = [];
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public List<string> Asked { get; } = [];

    public async Task<LyricsResult> GetAsync(MediaSnapshot track, CancellationToken ct)
    {
        Asked.Add(track.Title);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        return ByTitle.TryGetValue(track.Title, out var r) ? r : LyricsResult.NotFound;
    }
}

public sealed class FakeDownloads : IDownloadSource
{
    public DownloadInfo? Primary { get; private set; }
    public event EventHandler? Changed;

    public void Set(DownloadInfo? d)
    {
        Primary = d;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class FakeDock : IAppDock
{
    private readonly List<DockApp> _apps = [];
    public IReadOnlyList<DockApp> Apps => _apps;
    public event EventHandler? Changed;
    public List<string> Launched { get; } = [];

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths) _apps.Add(new DockApp(p, Path.GetFileNameWithoutExtension(p), null));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string path)
    {
        _apps.RemoveAll(a => a.Path == path);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Launch(string path) => Launched.Add(path);
}

public sealed class FakeScreen : IScreenActions
{
    public bool ClipboardWorks { get; set; } = true;
    public int Captures { get; private set; }
    public int Snips { get; private set; }

    /// <summary>What the "user" selects: null = cancelled.</summary>
    public System.Windows.Media.Imaging.BitmapSource? Snipped { get; set; } =
        System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
    public string OcrText { get; set; } = "";
    public string? QrText { get; set; }
    public List<string> ClipboardTexts { get; } = [];

    public void SnipArea() => Snips++;
    public bool CopyPrimaryScreen() { Captures++; return ClipboardWorks; }
    public Task<System.Windows.Media.Imaging.BitmapSource?> SnipImageAsync(CancellationToken ct = default) { Snips++; return Task.FromResult(Snipped); }
    public Task<string> RecognizeTextAsync(System.Windows.Media.Imaging.BitmapSource image) => Task.FromResult(OcrText);
    public string? DecodeQr(System.Windows.Media.Imaging.BitmapSource image) => QrText;
    public bool SetClipboardText(string text) { if (ClipboardWorks) ClipboardTexts.Add(text); return ClipboardWorks; }

    public string? PickedColor { get; set; }
    public Task<string?> PickColorAsync() => Task.FromResult(PickedColor);
    public List<System.Windows.Media.Imaging.BitmapSource> MarkupOpened { get; } = [];
    public void OpenMarkup(System.Windows.Media.Imaging.BitmapSource image) => MarkupOpened.Add(image);
    /// <summary>Length of the text "in the clipboard"; null means no text.</summary>
    public int? PlainTextLength { get; set; }
    public int? StripClipboardFormatting() => PlainTextLength;
    public string? ClipboardText { get; set; }
    public string? GetClipboardText() => ClipboardText;
    public event Action? VoiceStarted;
    public bool VoiceAllowed { get; set; } = true;
    public bool StartVoiceTyping() { VoiceStarted?.Invoke(); return VoiceAllowed; }
    public List<string> Typed { get; } = [];
    public void TypeText(string text) => Typed.Add(text);
}

public sealed class FakeRecorder : IScreenRecorder
{
    public bool IsRecording { get; private set; }
    public TimeSpan Elapsed { get; set; }
    public string? StartError { get; set; }
    public string? SavedFile { get; set; } = @"C:\Users\me\Videos\Stillbar\Запись.mp4";
    public event EventHandler? Changed;

    public string? Start()
    {
        if (StartError is not null) return StartError;
        IsRecording = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public Task<string?> StopAsync()
    {
        IsRecording = false;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(SavedFile);
    }
}

/// <summary>A view model wired to fakes, with the fakes at hand.</summary>
public sealed class Rig
{
    public FakeMedia Media { get; } = new();
    public FakeVolume Volume { get; } = new();
    public FakeLyrics Lyrics { get; } = new();
    public FakeDownloads Downloads { get; } = new();
    public FakeDock Dock { get; } = new();
    public FakeScreen Screen { get; } = new();
    public FakeRecorder Recorder { get; } = new();
    public BarViewModel Vm { get; }

    /// <summary>Test clock: starts at a fixed moment, moved by <see cref="Advance"/>.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.FromHours(3));
    public int Chimes { get; private set; }

    public void Advance(TimeSpan by)
    {
        Now += by;
        Vm.Tick();
    }

    /// <summary>
    /// The player reports a new state. The view model handles player events through Dispatcher.InvokeAsync
    /// (in the app they arrive on other threads), so give the dispatcher a moment before asserting.
    /// </summary>
    public async Task Play(Core.Media.MediaSnapshot s)
    {
        Media.Set(s);
        await WpfHost.Settle();
    }

    public Rig(WeatherNow? weather = null, Action<Rig>? before = null)
    {
        before?.Invoke(this);
        Vm = new BarViewModel(new BarServices(
            Media, Volume, new FakeWeather(weather), Lyrics, Downloads, Dock, Screen,
            Clock: () => Now, Chime: () => Chimes++, Recorder: Recorder), System.Windows.Threading.Dispatcher.CurrentDispatcher);
    }
}

public sealed class FakeDictation : IDictation
{
    public bool IsListening { get; private set; }
    public string Partial { get; private set; } = "";
    public string? StartError { get; set; }
    public event EventHandler? Changed;
    public event EventHandler<string>? Heard;

    public Task<string?> StartAsync(IProgress<string> status)
    {
        if (StartError is null) { IsListening = true; Changed?.Invoke(this, EventArgs.Empty); }
        return Task.FromResult(StartError);
    }

    public void Stop() { IsListening = false; Partial = ""; Changed?.Invoke(this, EventArgs.Empty); }
    public void Hear(string partial) { Partial = partial; Changed?.Invoke(this, EventArgs.Empty); }
    public void Say(string phrase) { Partial = ""; Heard?.Invoke(this, phrase); Changed?.Invoke(this, EventArgs.Empty); }
}
public sealed class FakeAgents : IAgentHub
{
    public event EventHandler<AgentMessage>? Received;

    public PermissionTicket? Send(string ev, string extra = "", string session = "s1", string cwd = "C:\\\\code\\\\stillbar")
    {
        var e = Stillbar.Core.Agents.HookEvent.Parse(ev, $$"""{ "session_id": "{{session}}", "cwd": "{{cwd}}", "hook_event_name": "{{ev}}" {{extra}} }""")!;
        var ticket = ev == "PermissionRequest" ? new PermissionTicket(e.SessionId) : null;
        Received?.Invoke(this, new AgentMessage(e, ticket));
        return ticket;
    }
}