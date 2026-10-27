using Stillbar.App.Shell;
using Stillbar.Core.Pill;

namespace Stillbar.App.Tests;

/// <summary>Offline dictation when Windows' voice typing is switched off by policy. Fakes: no microphone, no model.</summary>
[Collection("wpf")]
public class DictationTests(WpfHost host)
{
    private static (BarViewModel Vm, FakeScreen Screen, FakeDictation D) Make()
    {
        var screen = new FakeScreen { VoiceAllowed = false };
        var d = new FakeDictation();
        var vm = new BarViewModel(new BarServices(
            new FakeMedia(), new FakeVolume(), new FakeWeather(null), new FakeLyrics(), new FakeDownloads(), new FakeDock(), screen,
            Dictation: d), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        return (vm, screen, d);
    }

    [Fact]
    public Task Blocked_windows_voice_falls_back_to_our_dictation() => host.RunAsync(async () =>
    {
        var (vm, _, d) = Make();
        await vm.VoiceTypingCommand.ExecuteAsync(null);
        await WpfHost.Settle();
        Assert.True(d.IsListening);
        Assert.Equal(PillKind.Listening, vm.PillKind);
        Assert.Equal("Слушаю… говорите", vm.DictationText);
        Assert.Equal("Остановить диктовку", vm.VoiceTileLabel);
    });

    [Fact]
    public Task Partial_words_show_in_the_pill_and_phrases_are_typed() => host.RunAsync(async () =>
    {
        var (vm, screen, d) = Make();
        await vm.VoiceTypingCommand.ExecuteAsync(null);
        d.Hear("привет как");
        await WpfHost.Settle();
        Assert.Equal("привет как", vm.DictationText);
        d.Say("привет как дела точка");
        d.Say("всё хорошо");
        await WpfHost.Settle();
        Assert.Equal(["Привет как дела. ", "Всё хорошо "], screen.Typed);
    });

    [Fact]
    public Task Second_press_stops_and_the_pill_goes_back() => host.RunAsync(async () =>
    {
        var (vm, _, d) = Make();
        await vm.VoiceTypingCommand.ExecuteAsync(null);
        await vm.VoiceTypingCommand.ExecuteAsync(null);
        await WpfHost.Settle();
        Assert.False(d.IsListening);
        Assert.Equal(PillKind.Idle, vm.PillKind);
        Assert.Equal("Голос в текст", vm.VoiceTileLabel);
    });

    [Fact]
    public Task Stop_button_in_the_pill_stops() => host.RunAsync(async () =>
    {
        var (vm, _, d) = Make();
        await vm.VoiceTypingCommand.ExecuteAsync(null);
        vm.StopDictationCommand.Execute(null);
        await WpfHost.Settle();
        Assert.False(d.IsListening);
    });

    [Fact]
    public Task Microphone_problem_is_reported() => host.RunAsync(async () =>
    {
        var (vm, _, d) = Make();
        d.StartError = "Нет доступа к микрофону: Параметры → Конфиденциальность → Микрофон";
        await vm.VoiceTypingCommand.ExecuteAsync(null);
        Assert.Equal(d.StartError, vm.StatusText);
        Assert.NotEqual(PillKind.Listening, vm.PillKind);
    });

    [Fact]
    public Task Allowed_windows_voice_is_used_instead() => host.RunAsync(async () =>
    {
        var (vm, screen, d) = Make();
        screen.VoiceAllowed = true;
        await vm.VoiceTypingCommand.ExecuteAsync(null);
        Assert.False(d.IsListening);
    });
}
