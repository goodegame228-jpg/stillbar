using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stillbar.Core.Pill;

namespace Stillbar.App.Tests;

/// <summary>The four tiles that were "coming soon": markup, colour picker, plain text, screen recording. Fakes only.</summary>
[Collection("wpf")]
public class ScreenTilesTests(WpfHost host)
{
    private static BitmapSource Picture()
    {
        var b = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
        b.Freeze();
        return b;
    }

    [Fact]
    public Task Area_snip_opens_the_markup_editor() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.Snipped = Picture();
        await r.Vm.SnipAreaCommand.ExecuteAsync(null);
        Assert.Single(r.Screen.MarkupOpened);
        Assert.Equal("Снимок скопирован", r.Vm.StatusText);
    });

    [Fact]
    public Task Cancelled_snip_opens_nothing() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.Snipped = null; // the user pressed Esc in the snip overlay
        await r.Vm.SnipAreaCommand.ExecuteAsync(null);
        Assert.Empty(r.Screen.MarkupOpened);
        Assert.Equal("Снимок отменён", r.Vm.StatusText);
    });

    [Fact]
    public Task Picked_colour_goes_to_the_clipboard_as_hex() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.PickedColor = "#6FD3B8";
        await r.Vm.PickColorCommand.ExecuteAsync(null);
        Assert.Equal(["#6FD3B8"], r.Screen.ClipboardTexts);
        Assert.Equal("#6FD3B8 скопирован", r.Vm.StatusText);
    });

    [Fact]
    public Task Cancelled_picker_copies_nothing() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Vm.PickColorCommand.ExecuteAsync(null);
        Assert.Empty(r.Screen.ClipboardTexts);
        Assert.Equal("Цвет не выбран", r.Vm.StatusText);
    });

    [Theory]
    [InlineData(21, "Формат убран · 21 символ")]
    [InlineData(3, "Формат убран · 3 символа")]
    [InlineData(null, "В буфере нет текста")]
    public Task Plain_text_reports_what_happened(int? length, string expected) => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.PlainTextLength = length;
        r.Vm.PlainTextCommand.Execute(null);
        Assert.Equal(expected, r.Vm.StatusText);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Recording_takes_the_pill_and_the_tile_stops_it() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Vm.ToggleRecordingCommand.ExecuteAsync(null);
        await WpfHost.Settle();
        Assert.Equal(PillKind.Recording, r.Vm.PillKind);
        Assert.True(r.Vm.IsRecordingPill);
        Assert.False(r.Vm.ShowNormalPill);
        Assert.Equal("Остановить запись", r.Vm.RecordTileLabel);

        r.Recorder.Elapsed = TimeSpan.FromSeconds(75);
        r.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("1:15", r.Vm.RecordingText);

        await r.Vm.ToggleRecordingCommand.ExecuteAsync(null);
        await WpfHost.Settle();
        Assert.Equal(PillKind.Idle, r.Vm.PillKind);
        Assert.Equal("Видео экрана", r.Vm.RecordTileLabel);
        Assert.Equal("Видео сохранено в «Видео\\Stillbar»", r.Vm.StatusText);
    });

    [Fact]
    public Task Recording_without_ffmpeg_says_how_to_get_it() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Recorder.StartError = "Для записи нужен ffmpeg: winget install Gyan.FFmpeg";
        await r.Vm.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.Equal(PillKind.Idle, r.Vm.PillKind);
        Assert.Equal("Для записи нужен ffmpeg: winget install Gyan.FFmpeg", r.Vm.StatusText);
    });

    [Fact]
    public Task Voice_typing_closes_the_panel_and_hands_focus_back_first() => host.RunAsync(async () =>
    {
        var r = new Rig();
        var order = new List<string>();
        r.Vm.CollapseRequested += (_, _) => order.Add("collapse");
        r.Vm.ReturnFocusRequested += (_, _) => order.Add("focus");
        r.Screen.VoiceStarted += () => order.Add("win+h");
        await r.Vm.VoiceTypingCommand.ExecuteAsync(null);
        Assert.Equal(["collapse", "focus", "win+h"], order);
    });

    [Fact]
    public Task Voice_typing_blocked_by_policy_says_so() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.VoiceAllowed = false;
        await r.Vm.VoiceTypingCommand.ExecuteAsync(null);
        Assert.Equal("Голосовой ввод Windows выключен администратором", r.Vm.StatusText);
    });

    [Fact]
    public Task Keyboard_toast_has_its_own_icon() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.ShowKeyboard("EN");
        Assert.Equal("EN", r.Vm.StatusText);
        Assert.Equal("", r.Vm.StatusGlyph);
        r.Vm.ShowStatus("Снимок скопирован");
        Assert.Equal("", r.Vm.StatusGlyph);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Finished_timer_still_outranks_a_recording() => host.RunAsync(async () =>
    {
        var r = new Rig();
        await r.Vm.ToggleRecordingCommand.ExecuteAsync(null);
        r.Vm.StartTimerCommand.Execute("1");
        r.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(PillKind.Alert, r.Vm.PillKind);
    });
}
