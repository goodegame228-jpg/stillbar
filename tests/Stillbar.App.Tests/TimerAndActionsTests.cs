using Stillbar.Core.Lyrics;
using Stillbar.Core.Pill;

namespace Stillbar.App.Tests;

/// <summary>Cases added with the v1 gap fixes: timer (T), text/QR from screen (A-H2/A-H3), lyrics toggle (L-H2).</summary>
[Collection("wpf")]
public class TimerAndActionsTests(WpfHost host)
{
    [Fact]
    public Task T_H1_preset_starts_a_countdown_in_the_pill() => host.RunAsync(() =>
    {
        var r = new Rig();
        r.Vm.StartTimerCommand.Execute("5");
        Assert.Equal(PillKind.Timer, r.Vm.PillKind);
        Assert.True(r.Vm.ShowPillTimer);
        Assert.False(r.Vm.ShowPillClock); // the countdown takes the centre
        Assert.Equal("5:00", r.Vm.TimerText);
        r.Advance(TimeSpan.FromSeconds(48));
        Assert.Equal("4:12", r.Vm.TimerText);
    });

    [Fact]
    public Task T_H2_pause_holds_and_resume_continues() => host.RunAsync(() =>
    {
        var r = new Rig();
        r.Vm.StartTimerCommand.Execute("10");
        r.Advance(TimeSpan.FromMinutes(1));
        r.Vm.PauseResumeTimerCommand.Execute(null);
        Assert.Equal("Продолжить", r.Vm.TimerPauseLabel);
        r.Advance(TimeSpan.FromHours(1));
        Assert.Equal("9:00", r.Vm.TimerText);
        r.Vm.PauseResumeTimerCommand.Execute(null);
        r.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("8:30", r.Vm.TimerText);
    });

    [Fact]
    public Task T_H3_finish_alerts_once_then_plus_one_minute_or_done() => host.RunAsync(() =>
    {
        var r = new Rig();
        r.Vm.StartTimerCommand.Execute("5");
        r.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(PillKind.Alert, r.Vm.PillKind);
        Assert.True(r.Vm.IsAlertPill);
        Assert.Equal("Был поставлен на 5 мин", r.Vm.AlertDetail);
        r.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, r.Chimes); // one sound, not one per tick

        r.Vm.TimerAddMinuteCommand.Execute(null);
        Assert.Equal(PillKind.Timer, r.Vm.PillKind);
        Assert.Equal("1:00", r.Vm.TimerText);

        r.Advance(TimeSpan.FromMinutes(1));
        Assert.True(r.Vm.IsAlertPill);
        r.Vm.DismissAlertCommand.Execute(null);
        Assert.Equal(PillKind.Idle, r.Vm.PillKind);
        Assert.Equal(2, r.Chimes);
    });

    [Fact]
    public Task T_E1_reset_turns_it_off() => host.RunAsync(() =>
    {
        var r = new Rig();
        r.Vm.StartTimerCommand.Execute("25");
        r.Vm.ResetTimerCommand.Execute(null);
        Assert.Equal(PillKind.Idle, r.Vm.PillKind);
        Assert.True(r.Vm.IsTimerOff);
    });

    [Fact]
    public Task T_E2_timer_beats_music_but_not_a_download() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Play(FakeMedia.Track());
        r.Vm.StartTimerCommand.Execute("5");
        Assert.Equal(PillKind.Timer, r.Vm.PillKind);
        r.Downloads.Set(new Core.Downloads.DownloadInfo("x.part", "x", 1, 1, false));
        await WpfHost.Settle();
        Assert.Equal(PillKind.Download, r.Vm.PillKind);
    });

    [Fact]
    public Task A_H2_text_from_screen_goes_to_the_clipboard() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.OcrText = "Привет, мир";
        int collapses = 0;
        r.Vm.CollapseRequested += (_, _) => collapses++;
        await r.Vm.TextFromScreenCommand.ExecuteAsync(null);
        Assert.Equal(1, collapses);
        Assert.Equal(["Привет, мир"], r.Screen.ClipboardTexts);
        Assert.Equal("Текст скопирован · 11 символов", r.Vm.StatusText);
    });

    [Theory]
    [InlineData(true, "", "На снимке нет текста")]
    [InlineData(false, "x", "Снимок отменён")]
    public Task A_E2_text_from_screen_says_what_went_wrong(bool snipped, string ocr, string status) => host.RunAsync(async () =>
    {
        var r = new Rig();
        if (!snipped) r.Screen.Snipped = null;
        r.Screen.OcrText = ocr;
        await r.Vm.TextFromScreenCommand.ExecuteAsync(null);
        Assert.Empty(r.Screen.ClipboardTexts);
        Assert.Equal(status, r.Vm.StatusText);
    });

    [Theory]
    [InlineData("https://example.com/a?b=1", "Ссылка скопирована · example.com")]
    [InlineData("WIFI:S:home;T:WPA;P:secret;;", "Содержимое QR скопировано")]
    [InlineData(null, "На снимке нет QR-кода")]
    public Task A_H3_qr_from_screen(string? qr, string status) => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.QrText = qr;
        await r.Vm.QrFromScreenCommand.ExecuteAsync(null);
        Assert.Equal(status, r.Vm.StatusText);
        Assert.Equal(qr is null ? 0 : 1, r.Screen.ClipboardTexts.Count);
    });

    [Theory]
    [InlineData(1, "символ")]
    [InlineData(3, "символа")]
    [InlineData(11, "символов")]
    [InlineData(21, "символ")]
    [InlineData(112, "символов")]
    public Task A_H2_russian_plurals(int n, string word) => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.OcrText = new string('a', n);
        await r.Vm.TextFromScreenCommand.ExecuteAsync(null);
        Assert.Equal($"Текст скопирован · {n} {word}", r.Vm.StatusText);
    });

    [Fact]
    public Task L_H2_lyrics_toggle_hides_and_shows_the_column() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Lyrics.ByTitle["Song"] = new LyricsResult(LyricsKind.Synced, [new LrcLine(TimeSpan.Zero, "la")]);
        await r.Play(FakeMedia.Track());
        await Task.Delay(700);
        Assert.True(r.Vm.ShowLyricsColumn);
        r.Vm.ToggleLyricsCommand.Execute(null);
        Assert.False(r.Vm.ShowLyricsColumn);
        r.Vm.ToggleLyricsCommand.Execute(null);
        Assert.True(r.Vm.ShowLyricsColumn);
    });
}
