using System.Collections.Specialized;
using System.Windows;
using Stillbar.Core.Downloads;
using Stillbar.Core.Lyrics;
using Stillbar.Core.Pill;
using Stillbar.Core.Weather;

namespace Stillbar.App.Tests;

/// <summary>View-model cases from replica/test-plan.md. Case IDs in the test names.</summary>
[Collection("wpf")]
public class ViewModelTests(WpfHost host)
{
    // ------------------------------------------------------------------ F01 music

    [Fact]
    public Task F01_H1_playing_track_shows_music_pill_and_pause_label() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Play(FakeMedia.Track("Огни", "Трамвай"));
        Assert.Equal(PillKind.Music, r.Vm.PillKind);
        Assert.True(r.Vm.ShowPillMusic);
        Assert.Equal("Огни", r.Vm.Title);
        Assert.Equal("Пауза", r.Vm.PlayLabel);
    });

    [Fact]
    public Task F01_H2_transport_buttons_reach_the_player() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Play(FakeMedia.Track());
        await r.Vm.PlayPauseCommand.ExecuteAsync(null);
        await r.Vm.NextCommand.ExecuteAsync(null);
        await r.Vm.PreviousCommand.ExecuteAsync(null);
        Assert.Equal(["playpause", "next", "previous"], r.Media.Calls);
    });

    [Fact]
    public Task F01_H3_scrub_seeks_once_and_does_not_jump_back() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Play(FakeMedia.Track(pos: 30));
        r.Vm.BeginScrub();
        r.Vm.PositionSeconds = 100; // dragged
        r.Vm.CommitScrub();
        Assert.Equal([TimeSpan.FromSeconds(100)], r.Media.Seeks);

        // The player has not reported the new position yet and still says 0:30.
        await r.Play(FakeMedia.Track(pos: 30));
        Assert.InRange(r.Vm.PositionSeconds, 100, 101);
    });

    [Fact]
    public Task F01_E1_no_player_is_idle_with_empty_state() => host.RunAsync(() =>
    {
        var r = new Rig();
        Assert.Equal(PillKind.Idle, r.Vm.PillKind);
        Assert.True(r.Vm.HasNoMedia);
    });

    [Fact]
    public Task F01_E2_player_page_closes_when_the_player_goes_away() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Play(FakeMedia.Track());
        r.Vm.OnExpandedChanged(true);
        Assert.True(r.Vm.IsPlayerPage);

        await r.Play(Core.Media.MediaSnapshot.None); // app closed while the panel is open
        Assert.False(r.Vm.IsPlayerPage);
        Assert.True(r.Vm.IsHomePage);
    });

    [Fact]
    public Task F01_E3_stream_without_seeking_cannot_be_scrubbed() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Play(FakeMedia.Track(canSeek: false, canNext: false, canPrevious: true));
        Assert.False(r.Vm.CanSeek);
        Assert.False(r.Vm.CanNext);
        Assert.True(r.Vm.CanPrevious);

        r.Vm.BeginScrub();
        r.Vm.PositionSeconds = 120;
        r.Vm.CommitScrub();
        Assert.Empty(r.Media.Seeks);
    });

    [Fact]
    public Task F01_E3_unknown_duration_cannot_be_scrubbed() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Play(FakeMedia.Track(dur: 0));
        Assert.False(r.Vm.CanSeek);
        Assert.Equal("", r.Vm.RemainingText);
    });

    // ------------------------------------------------------------------ F02 volume

    [Fact]
    public Task F02_H1_dragging_volume_sets_it_once() => host.RunAsync(() =>
    {
        var r = new Rig();
        r.Vm.VolumePercent = 30;
        Assert.Equal([30], r.Volume.SetCalls);
        Assert.Equal("30", r.Vm.VolumeText);
    });

    [Fact]
    public Task F02_H2_system_volume_is_followed_without_echo() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Volume.SystemVolume(80);
        await WpfHost.Settle();
        Assert.Equal(80, r.Vm.VolumePercent);
        Assert.Empty(r.Volume.SetCalls);
    });

    [Fact]
    public Task F02_E1_refused_switch_shows_error_and_keeps_list_open() => host.RunAsync(() =>
    {
        var r = new Rig();
        r.Volume.AllowSwitch = false;
        r.Vm.ToggleDeviceListCommand.Execute(null);
        r.Vm.SelectDeviceCommand.Execute(r.Vm.Devices.Single(d => !d.IsCurrent));
        Assert.True(r.Vm.HasDeviceError);
        Assert.True(r.Vm.IsDeviceListOpen);

        r.Volume.AllowSwitch = true;
        r.Vm.SelectDeviceCommand.Execute(r.Vm.Devices.Single(d => !d.IsCurrent));
        Assert.False(r.Vm.HasDeviceError);
        Assert.False(r.Vm.IsDeviceListOpen);
    });

    [Fact]
    public Task F02_E2_volume_changes_do_not_rebuild_device_rows() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.ToggleDeviceListCommand.Execute(null);
        int rebuilds = 0;
        r.Vm.Devices.CollectionChanged += (_, e) => { if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove) rebuilds++; };
        for (int v = 40; v <= 60; v += 5) r.Volume.SystemVolume(v); // a drag sends a stream of notifications
        await WpfHost.Settle();
        Assert.Equal(0, rebuilds);
    });

    [Fact]
    public Task F02_E3_no_output_device_hides_volume_row() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Volume.RemoveAllDevices();
        await WpfHost.Settle();
        Assert.False(r.Vm.HasAudioDevice);
    });

    // ------------------------------------------------------------------ F09 downloads

    [Fact]
    public Task F09_H1_running_then_finished_download() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Downloads.Set(new DownloadInfo(@"C:\D\a.zip.crdownload", "a.zip", 432_013_312, 22_020_096, false));
        await WpfHost.Settle();
        Assert.Equal(PillKind.Download, r.Vm.PillKind);
        Assert.Equal("412 МБ · 21 МБ/с", r.Vm.DownloadDetail);

        r.Downloads.Set(new DownloadInfo(@"C:\D\a.zip.crdownload", "a.zip", 432_013_312, 0, true));
        await WpfHost.Settle();
        Assert.Equal("Готово", r.Vm.DownloadDetail);
        Assert.Equal("\uE73E", r.Vm.DownloadGlyph);

        r.Downloads.Set(null);
        await WpfHost.Settle();
        Assert.Equal(PillKind.Idle, r.Vm.PillKind);
    });

    [Fact]
    public Task F09_E1_download_wins_over_music_and_bars_stop() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Play(FakeMedia.Track());
        r.Downloads.Set(new DownloadInfo("x.part", "x", 1, 1, false));
        await WpfHost.Settle();
        Assert.Equal(PillKind.Download, r.Vm.PillKind);
        Assert.False(r.Vm.ShowPillMusic);
    });

    // ------------------------------------------------------------------ lyrics

    private static LyricsResult Synced() => new(LyricsKind.Synced,
    [
        new LrcLine(TimeSpan.FromSeconds(10), "one"),
        new LrcLine(TimeSpan.FromSeconds(20), "two"),
        new LrcLine(TimeSpan.FromSeconds(30), "three"),
    ]);

    [Fact]
    public Task L_H1_synced_lines_follow_the_position() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Lyrics.ByTitle["Song"] = Synced();
        await r.Play(FakeMedia.Track(pos: 21, playing: false));
        await Task.Delay(700); // debounce
        Assert.True(r.Vm.HasLyrics);
        Assert.Equal(("one", "two", "three"), (r.Vm.PreviousLine, r.Vm.CurrentLine, r.Vm.NextLine));

        r.Vm.PositionSeconds = 31;
        Assert.Equal(("two", "three", ""), (r.Vm.PreviousLine, r.Vm.CurrentLine, r.Vm.NextLine));
        Assert.Equal("", r.Vm.LyricsStatus);
    });

    [Theory]
    [InlineData(LyricsKind.NotFound, "Для этой песни слов нет в LRCLIB")]
    [InlineData(LyricsKind.Instrumental, "Эта песня без слов")]
    [InlineData(LyricsKind.Unavailable, "LRCLIB не отвечает. Попробуем на следующем треке")]
    public Task L_E1_E2_no_lines_says_why(LyricsKind kind, string status) => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Lyrics.ByTitle["Song"] = new LyricsResult(kind, []);
        await r.Play(FakeMedia.Track());
        Assert.Equal("Ищем слова…", r.Vm.LyricsStatus);
        await Task.Delay(700);
        Assert.False(r.Vm.HasLyrics);
        Assert.True(r.Vm.ShowLyricsColumn);
        Assert.Equal(status, r.Vm.LyricsStatus);
    });

    [Fact]
    public Task L_E3_fast_skipping_asks_only_for_the_last_track() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Lyrics.ByTitle["B"] = Synced();
        await r.Play(FakeMedia.Track("A"));
        await Task.Delay(100);
        await r.Play(FakeMedia.Track("B", pos: 21));
        await Task.Delay(800);
        Assert.Equal(["B"], r.Lyrics.Asked);
        Assert.Equal("two", r.Vm.CurrentLine);
    });

    // ------------------------------------------------------------------ weather

    [Fact]
    public Task W_H1_clear_day() => host.RunAsync(() =>
    {
        var r = new Rig(new WeatherNow(18.2, 0, true));
        Assert.True(r.Vm.ShowPillWeatherIcon);
        Assert.Equal("18°", r.Vm.TemperatureText);
        Assert.Same(Application.Current.FindResource("Weather.Clear"), r.Vm.WeatherIcon);
    });

    [Fact]
    public Task W_E1_clear_night_frost() => host.RunAsync(() =>
    {
        var r = new Rig(new WeatherNow(-3.4, 0, false));
        Assert.Equal("\u22123°", r.Vm.TemperatureText);
        Assert.Same(Application.Current.FindResource("Weather.Clear.Night"), r.Vm.WeatherIcon);
    });

    [Fact]
    public Task W_E2_no_weather_keeps_the_clock() => host.RunAsync(() =>
    {
        var r = new Rig(weather: null);
        Assert.False(r.Vm.ShowPillWeatherIcon);
        Assert.False(r.Vm.ShowPillTemperature);
        Assert.True(r.Vm.ShowPillClock);
    });

    // ------------------------------------------------------------------ pages

    [Fact]
    public Task P_H1_H2_page_depends_on_what_the_pill_shows() => host.RunAsync(async () =>
    {
        var idle = new Rig();
        idle.Vm.OnExpandedChanged(true);
        Assert.True(idle.Vm.IsHomePage);

        var music = new Rig();
        await music.Play(FakeMedia.Track());
        music.Vm.OnExpandedChanged(true);
        Assert.True(music.Vm.IsPlayerPage);
    });

    [Fact]
    public Task P_E1_showing_again_keeps_the_chosen_page() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Play(FakeMedia.Track());
        r.Vm.OnExpandedChanged(true);
        r.Vm.IsHomePage = true; // the Home tab
        r.Vm.OnExpandedChanged(true);
        Assert.True(r.Vm.IsHomePage);
    });

    // ------------------------------------------------------------------ actions, dock

    [Fact]
    public Task A_H1_full_screen_collapses_first_then_toasts() => host.RunAsync(async () =>
    {
        var r = new Rig();
        var order = new List<string>();
        r.Vm.CollapseRequested += (_, _) => order.Add($"collapse@{r.Screen.Captures}");
        await r.Vm.CopyScreenCommand.ExecuteAsync(null);
        Assert.Equal(["collapse@0"], order);
        Assert.Equal(1, r.Screen.Captures);
        Assert.Equal("Снимок экрана скопирован", r.Vm.StatusText);
        Assert.False(r.Vm.ShowPillClock); // the toast replaces the pill content
    });

    [Fact]
    public Task A_E1_busy_clipboard_is_reported() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.ClipboardWorks = false;
        await r.Vm.CopyScreenCommand.ExecuteAsync(null);
        Assert.StartsWith("Буфер обмена занят", r.Vm.StatusText);
    });

    [Fact]
    public Task D_H1_dock_add_launch_remove() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.AddApps([@"C:\Apps\one.exe"]);
        await WpfHost.Settle();
        var app = Assert.Single(r.Vm.DockApps);
        r.Vm.LaunchAppCommand.Execute(app);
        Assert.Equal([@"C:\Apps\one.exe"], r.Dock.Launched);
        r.Vm.RemoveAppCommand.Execute(app);
        await WpfHost.Settle();
        Assert.Empty(r.Vm.DockApps);
    });
}
