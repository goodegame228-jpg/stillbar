using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stillbar.App.Design;
using Stillbar.App.Services;
using Stillbar.App.Shell;
using Stillbar.Core.Settings;
using Stillbar.Core.Weather;
using Windows.System.Power;

namespace Stillbar.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private CoreAudioVolumeService? _volume;
    private OpenMeteoWeatherService? _weather;
    private DownloadWatcher? _downloads;
    private Watchdog? _watchdog;
    private ClipboardService? _clipboard;
    private FfmpegRecorder? _recorder;
    private VoskDictation? _dictation;
    private AgentServer? _agents;
    private EventWaitHandle? _quit;
    private BarWindow? _window;
    private CitySetting? _weatherCity;

    /// <summary>The monitor the bar is on (before the window exists: the primary one).</summary>
    private Stillbar.Core.Display.PixelRect BarArea() =>
        _window?.MonitorBounds is { Right: > 0 } r ? r
        : new(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --snapshot <file.png> [--view <name>]: render offscreen and exit. Views: see Snapshot().
        string? snapshot = Arg(e.Args, "--snapshot");
        if (snapshot is not null)
        {
            Theme.Apply(Arg(e.Args, "--theme") ?? "dark");
            Snapshot(Arg(e.Args, "--view") ?? "gallery", snapshot);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--voice-check"))
        {
            // Diagnostics: download/load the dictation model without a microphone; result in the log.
            var frame = new DispatcherFrame();
            int code = 0;
            _ = Task.Run(async () =>
            {
                try { Log.Info("voice", await VoskDictation.SelfCheckAsync(new Progress<string>(s => Log.Info("voice", s)))); }
                catch (Exception ex) { Log.Warn("voice", "voice check failed: " + ex); code = 1; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
            Shutdown(code);
            return;
        }

        if (e.Args.Contains("--gallery"))
        {
            MainWindow = new DesignGallery();
            MainWindow.Show();
            return;
        }

        // Started by the watchdog: the old process may still be on its way out and holding the lock.
        if (int.TryParse(Arg(e.Args, "--after"), out int oldPid)) Watchdog.WaitFor(oldPid);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Stillbar.Bar", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }

        // The installer (update) and the uninstaller ask a running Stillbar to close through this event.
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEventName);
        new Thread(() =>
        {
            _quit.WaitOne();
            Dispatcher.InvokeAsync(Shutdown);
        }) { IsBackground = true, Name = "Stillbar quit" }.Start();

        var settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stillbar");
        MoveFromWorkingName(Environment.SpecialFolder.ApplicationData);      // settings.json, dock.json
        MoveFromWorkingName(Environment.SpecialFolder.LocalApplicationData); // lyrics cache, logs
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stillbar");
        var store = new JsonSettingsStore(settingsDir);
        var settings = store.Load();
        Theme.Apply(settings.Theme); // before the first window: styles pick their colours on first use
        // No settings screen yet: write the defaults once so the file exists to be edited (city, delays, ...).
        if (!File.Exists(store.FilePath)) store.Save(settings);

        var media = new GsmtcMediaService();
        _volume = new CoreAudioVolumeService(Dispatcher);
        _weather = new OpenMeteoWeatherService(settings.City);
        _downloads = new DownloadWatcher(Dispatcher);
        var vm = new BarViewModel(new BarServices(
            media, _volume, _weather, new LrcLibLyricsService(), _downloads, new AppDock(settingsDir), new ScreenActions(BarArea),
            settings.LyricsEnabled,
            Clipboard: settings.ClipboardHistory ? _clipboard = new ClipboardService(settingsDir, cacheDir) : null,
            Shelf: new ShelfService(settingsDir),
            Recorder: _recorder = new FfmpegRecorder(BarArea),
            Notes: new FileListStore<Stillbar.Core.Notes.Note>(settingsDir, "notes.json"),
            Reminders: new FileListStore<Stillbar.Core.Reminders.Reminder>(settingsDir, "reminders.json"),
            Controls: new SystemControls(),
            CityName: () => _window?.Settings.City.Name ?? settings.City.Name,
            Dictation: _dictation = new VoskDictation(),
            Agents: _agents = new AgentServer()), Dispatcher) { RainWarningsEnabled = settings.RainWarnings };
        _agents.Start();
        var view = new BarView(settings.HoverExpandDelayMs, settings.CollapseDelayMs) { DataContext = vm };
        var window = new BarWindow(view, settings, store.Save);
        _window = window;
        var launchIndex = new LaunchIndex();
        _ = launchIndex.Items; // start indexing the Start menu now, so the first Alt+Space has results
        void OpenCommandBar() => CommandBar.Open(vm, launchIndex, window.CurrentMonitor);
        window.CommandHotkey += (_, _) => OpenCommandBar();
        vm.CommandBarRequested += (_, _) => OpenCommandBar();
        MainWindow = window;
        window.Show();
        view.ShowInstant(expanded: false);

        if (settings.RestartOnHang)
        {
            _watchdog = new Watchdog(Dispatcher);
            _watchdog.Start();
            DispatcherUnhandledException += (_, ex) =>
            {
                Log.Warn("crash", ex.Exception.ToString());
                Watchdog.RestartNow("crashed: " + ex.Exception.GetType().Name);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            {
                Log.Warn("crash", ex.ExceptionObject.ToString() ?? "unknown");
                Watchdog.RestartNow("crashed off the UI thread");
            };
        }
        // Manual check of the watchdog: STILLBAR_TEST_HANG=<seconds> freezes the UI thread after that long.
        if (int.TryParse(Environment.GetEnvironmentVariable("STILLBAR_TEST_HANG"), out int hangAfter))
            new DispatcherTimer(TimeSpan.FromSeconds(hangAfter), DispatcherPriority.Normal, (_, _) => Thread.Sleep(Timeout.Infinite), Dispatcher).Start();

        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Warn("task", ex.Exception.ToString());
            ex.SetObserved();
        };

        // Battery saver: the endless motion (open panel's wave, equaliser) rests.
        void ApplyEnergySaver() => view.ReduceMotion = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        ApplyEnergySaver();
        PowerManager.EnergySaverStatusChanged += (_, _) => Dispatcher.InvokeAsync(ApplyEnergySaver);

        // Always running: the full-screen rule and the excluded apps are read from the live settings on every poll.
        var fullscreen = new FullscreenDetector(Dispatcher, () => window.MonitorName,
            () => Stillbar.Core.Controls.Exclusions.Parse(window.Settings.ExcludedApps), () => window.Settings.HideInFullscreen);
        fullscreen.Changed += (_, _) =>
        {
            if (fullscreen.IsFullscreen)
            {
                view.ShowInstant(expanded: false);
                window.Hide();
            }
            else
            {
                window.Show(); // ShowActivated=false: SW_SHOWNA, still no focus
            }
        };
        fullscreen.Start();

        var keyboard = new KeyboardIndicator(Dispatcher, () => window.Settings.KeyboardIndicator);
        keyboard.Changed += (_, text) => vm.ShowKeyboard(text);
        keyboard.Start();

        window.SettingsRequested += (_, _) => SettingsWindow.Open(window);
        vm.SettingsRequested += (_, _) => SettingsWindow.Open(window);
        window.SettingsChanged += (_, s) =>
        {
            if (s.City != _weatherCity) { _weatherCity = s.City; _weather.SetCity(s.City); }
            vm.RainWarningsEnabled = s.RainWarnings;
        };
        _weatherCity = settings.City;
        vm.Start();
        _weather.Start();
        _downloads.Start();
        _ = media.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Dispose();
        _clipboard?.Dispose();
        _dictation?.Dispose();
        _agents?.Dispose();
        if (_recorder?.IsRecording == true) _recorder.StopAsync().Wait(TimeSpan.FromSeconds(8)); // finish the file, do not leave ffmpeg running
        _downloads?.Dispose();
        _weather?.Dispose();
        _volume?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Before the rename the app kept its files under the working name "Capsule". Copy them once into the new folder
    /// so nobody loses their city, dock or lyrics cache. Files already in the new folder win; the old folder is left
    /// alone; a marker file makes it a one-time move even if the new folder existed already (logs, test runs).
    /// </summary>
    private static void MoveFromWorkingName(Environment.SpecialFolder root)
    {
        var baseDir = Environment.GetFolderPath(root);
        var from = Path.Combine(baseDir, "Capsule");
        var to = Path.Combine(baseDir, "Stillbar");
        var marker = Path.Combine(to, ".moved-from-capsule");
        if (!Directory.Exists(from) || File.Exists(marker)) return;
        try
        {
            int copied = 0;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                copied++;
            }
            Directory.CreateDirectory(to);
            File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
            Log.Info("startup", $"moved {copied} files from the old working-name folder in {root}");
        }
        catch (IOException ex)
        {
            Log.Warn("startup", $"could not copy old settings: {ex.Message}");
        }
    }

    /// <summary>A believable autumn day: rain in about 40 minutes, cooler days later in the week.</summary>
    private static Forecast SampleForecast(WeatherNow now, DateTime at)
    {
        var start = at.Date.AddHours(at.Hour);
        double[] temps = [now.TemperatureC, 9.2, 8.8, 8.1, 7.4, 6.9, 6.2, 5.8, 5.5, 5.1, 4.8, 4.6, 4.4, 4.5, 5.2, 6.4, 7.8, 9.1, 10.2, 10.9, 11.3, 11.0, 10.1, 9.0];
        int[] codes = [3, 61, 63, 61, 3, 3, 2, 2, 1, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 3, 61, 61, 3];
        int[] rain = [10, 80, 90, 70, 30, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 10, 20, 30, 40, 60, 70, 30];
        var hours = Enumerable.Range(0, 24).Select(i => new HourForecast(start.AddHours(i), temps[i], codes[i],
            start.AddHours(i).Hour is >= 7 and < 18, rain[i], codes[i] >= 61 ? 1.2 : 0)).ToList();
        var days = new[] { (12.0, 4.4, 63, 90), (11.3, 3.1, 3, 40), (8.2, 1.0, 2, 10), (6.5, -1.2, 0, 0), (4.1, -2.6, 71, 60), (5.0, -0.4, 3, 20), (7.8, 2.2, 61, 50) }
            .Select((d, i) => new DayForecast(at.Date.AddDays(i), d.Item1, d.Item2, d.Item3, d.Item4)).ToList();
        var quarters = Enumerable.Range(1, 8).Select(i => new QuarterPrecip(at.AddMinutes(i * 15 - at.Minute % 15), i >= 3 ? 0.6 : 0)).ToList();
        return new Forecast(now with { Code = 3 }, hours, days, quarters);
    }

    private static Stillbar.Core.Notes.Note[] SampleNotes(DateTimeOffset now) =>
    [
        new("n1", "Покупки\n[x] хлеб\n[ ] молоко\n[ ] сыр к ужину", now.AddMinutes(-20)),
        new("n2", "Идеи для отпуска\nКарелия в августе, взять палатку", now.AddDays(-2)),
    ];

    private static Stillbar.Core.Reminders.Reminder[] SampleReminders(DateTimeOffset now) =>
    [
        new("r1", "Позвонить маме", now.AddMinutes(25)),
        new("r2", "Стендап", now.Date.AddDays(1).AddHours(10), Stillbar.Core.Reminders.Repeat.Weekdays),
    ];

    /// <summary>Runs the dispatcher loop for a while without returning to the caller (snapshots only).</summary>
    private void PumpFor(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Background, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            frame.Continue = false;
        }, Dispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// Views: gallery | pill (idle, sunny) | pill-night (clear night) | pill-rain | music | download | toast |
    /// timer (counting down) | alert (timer finished) | panel (teal cover) | panel-pink (warm cover) |
    /// panel-timer (Home with a running timer) | empty (nothing playing) | devices (output list open) |
    /// player (compact mini player with lyrics). Any view with "-bottom" appended: the same, on the bottom edge.
    /// </summary>
    private void Snapshot(string view, string path)
    {
        bool bottom = view.EndsWith("-bottom", StringComparison.Ordinal);
        if (bottom) view = view[..^"-bottom".Length];
        FrameworkElement root;
        if (view == "settings")
        {
            root = SettingsWindow.Render(new BarWindow(new BarView()));
        }
        else if (view == "gallery")
        {
            var gallery = new DesignGallery();
            root = (FrameworkElement)gallery.Content;
            gallery.Content = null;
            gallery.Close();
        }
        else
        {
            IMediaService media = view switch
            {
                "pill" or "pill-night" or "pill-rain" or "download" or "toast" or "timer" or "alert" or "panel-timer" or "rain" or "listening" or "agent" or "agent-ask"
                    => new SampleMediaService(playing: false),
                "empty" => new SampleMediaService(empty: true),
                "panel-pink" or "player" => SampleMediaService.Pink(),
                _ => new SampleMediaService(playing: true),
            };
            var nowWeather = view switch
            {
                "pill-night" => new WeatherNow(-3.4, 0, IsDay: false),
                "pill-rain" => new WeatherNow(9.6, 63, IsDay: true),
                _ => new WeatherNow(18.2, 0, IsDay: true),
            };
            var sampleForecast = view is "weather" or "rain" ? SampleForecast(nowWeather, DateTime.Now) : null;
            var weather = new SampleWeatherService(sampleForecast?.Now ?? nowWeather, sampleForecast); // the real service reports Now from the same answer
            var clock = DateTimeOffset.Now;
            var vm = new BarViewModel(new BarServices(
                media, new SampleVolumeService(), weather, new SampleLyricsService(),
                view == "download" ? new SampleDownloadSource() : new NoDownloads(),
                new SampleDock(), new NoScreenActions(), Clock: () => clock, Chime: () => { },
                Clipboard: new SampleClipboard(), Shelf: new SampleShelf(),
                Notes: new MemoryListStore<Stillbar.Core.Notes.Note>(SampleNotes(clock)),
                Reminders: new MemoryListStore<Stillbar.Core.Reminders.Reminder>(SampleReminders(clock)),
                Controls: new SampleControls(), CityName: () => "Москва",
                Dictation: view == "listening" ? new SampleDictation() : null), Dispatcher);
            if (view == "toast") vm.ShowStatus("Текст скопирован · 128 символов");
            if (view is "agent" or "agent-ask")
            {
                static Stillbar.Core.Agents.HookEvent Ev(string name, string extra) => Stillbar.Core.Agents.HookEvent.Parse(name,
                    "{ \"session_id\": \"s1\", \"cwd\": \"C:\\\\code\\\\stillbar\", \"hook_event_name\": \"" + name + "\"" + extra + " }")!;
                vm.OnAgent(new AgentMessage(Ev("PreToolUse", ", \"tool_name\": \"Bash\", \"tool_input\": { \"command\": \"dotnet test Stillbar.slnx\" }"), null));
                if (view == "agent-ask")
                    vm.OnAgent(new AgentMessage(Ev("PermissionRequest", ", \"tool_name\": \"Bash\", \"tool_input\": { \"command\": \"git push origin main\" }"), new PermissionTicket("s1")));
            }
            if (view is "timer" or "alert" or "panel-timer")
            {
                vm.StartTimerCommand.Execute("5");
                clock += view == "alert" ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(48);
                vm.Tick();
            }

            var bar = new BarView { DataContext = vm };
            if (bottom) bar.Edge = Stillbar.Core.Display.BarEdge.Bottom;
            // Lyrics arrive after a 500 ms debounce: keep the dispatcher running past it (blocking would deadlock the await).
            if (view == "player") PumpFor(TimeSpan.FromMilliseconds(700));
            // Bindings attach through the dispatcher queue; drain it so Visibility bindings apply before we measure.
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            // Size the shape before the first layout pass of the backdrop, so that pass sees the final shape.
            bar.ShowInstant(expanded: view is "panel" or "panel-pink" or "empty" or "devices" or "mixer" or "clipboard" or "notes" or "system" or "weather" or "player" or "panel-timer");
            if (view is "panel" or "panel-pink" or "devices" or "mixer" or "clipboard" or "notes" or "system" or "weather")
            {
                // A playing track opens the player page; these views are about Home.
                vm.ShowHomeCommand.Execute(null);
                if (view == "devices") vm.ToggleDeviceListCommand.Execute(null);
                if (view == "mixer") vm.ToggleMixerCommand.Execute(null);
                if (view == "clipboard") vm.IsClipboardPage = true;
                if (view == "notes") { vm.IsNotesPage = true; vm.OpenNoteCommand.Execute(vm.NoteRows[0]); }
                if (view == "system") { vm.IsSystemPage = true; vm.RefreshControls().Wait(); }
                if (view == "weather") vm.IsWeatherPage = true;
                Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                bar.ShowInstant(expanded: true);
            }

            // Same desktop-like backdrop as the gallery, so the black shape reads the way it will on screen.
            root = new Border
            {
                Width = 800,
                Padding = bottom ? new Thickness(0, 24, 0, 0) : new Thickness(0, 0, 0, 24),
                Background = new LinearGradientBrush(Color.FromRgb(0x2D, 0x5F, 0xA8), Color.FromRgb(0x6E, 0x9A, 0xD2), 90),
                Child = bar,
            };
        }

        root.Measure(new Size(root.Width > 0 ? root.Width : double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        png.Save(fs);
    }
}
