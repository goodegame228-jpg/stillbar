using Stillbar.App.Shell;
using Stillbar.Core.Pill;

namespace Stillbar.App.Tests;

/// <summary>Claude Code in the pill: what it shows, and answering a permission request. Fake hub, fake clock.</summary>
[Collection("wpf")]
public class AgentPillTests(WpfHost host)
{
    private static (BarViewModel Vm, FakeAgents A, Func<int> Chimes, Action<TimeSpan> Advance) Make()
    {
        var a = new FakeAgents();
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        int chimes = 0;
        BarViewModel? vm = null;
        vm = new BarViewModel(new BarServices(
            new FakeMedia(), new FakeVolume(), new FakeWeather(null), new FakeLyrics(), new FakeDownloads(), new FakeDock(), new FakeScreen(),
            Clock: () => now, Chime: () => chimes++, Agents: a), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        return (vm, a, () => chimes, by => { now += by; vm.Tick(); });
    }

    [Fact]
    public Task Working_session_takes_the_pill_with_what_it_does() => host.RunAsync(async () =>
    {
        var (vm, a, _, advance) = Make();
        a.Send("UserPromptSubmit", """, "prompt": "почини" """);
        a.Send("PreToolUse", """, "tool_name": "Bash", "tool_input": { "command": "dotnet test" }""");
        await WpfHost.Settle();
        Assert.Equal(PillKind.Agent, vm.PillKind);
        Assert.Equal("stillbar · Bash: dotnet test", vm.AgentText);
        advance(TimeSpan.FromSeconds(75));
        Assert.Equal("1:15", vm.AgentElapsed);
    });

    [Fact]
    public Task Permission_request_becomes_an_alert_and_the_check_allows() => host.RunAsync(async () =>
    {
        var (vm, a, chimes, _) = Make();
        var ticket = a.Send("PermissionRequest", """, "tool_name": "Bash", "tool_input": { "command": "rm -rf b" }""")!;
        await WpfHost.Settle();
        Assert.Equal(PillKind.Alert, vm.PillKind);
        Assert.Equal("Claude спрашивает", vm.AlertTitle);
        Assert.Equal("stillbar · Bash: rm -rf b", vm.AlertDetail);
        Assert.Equal("Разрешить", vm.AlertPrimaryLabel);
        Assert.Equal("Запретить", vm.AlertSecondaryLabel);
        Assert.Equal(1, chimes());

        vm.DismissAlertCommand.Execute(null);
        Assert.True(await ticket.Answer);
        Assert.Equal(PillKind.Agent, vm.PillKind); // working again
        Assert.Equal("Разрешено", vm.StatusText);
    });

    [Fact]
    public Task Second_button_denies() => host.RunAsync(async () =>
    {
        var (vm, a, _, _) = Make();
        var ticket = a.Send("PermissionRequest", """, "tool_name": "Bash", "tool_input": { "command": "rm -rf b" }""")!;
        await WpfHost.Settle();
        vm.AlertSecondaryCommand.Execute(null);
        Assert.False(await ticket.Answer);
    });

    [Fact]
    public Task Unanswered_request_goes_back_to_claude_and_says_so() => host.RunAsync(async () =>
    {
        var (vm, a, _, _) = Make();
        var ticket = a.Send("PermissionRequest", """, "tool_name": "Bash", "tool_input": { "command": "ls" }""")!;
        await WpfHost.Settle();
        ticket.Decide(null); // the server's answer window ran out
        await WpfHost.Settle();
        Assert.NotEqual(PillKind.Alert, vm.PillKind);
        Assert.Equal("Ответьте Claude в его окне", vm.StatusText);
        Assert.Equal("stillbar · ждёт разрешения", vm.AgentText);
    });

    [Fact]
    public Task Newer_question_replaces_the_older_one() => host.RunAsync(async () =>
    {
        var (vm, a, _, _) = Make();
        var first = a.Send("PermissionRequest", """, "tool_name": "Bash", "tool_input": { "command": "one" }""")!;
        var second = a.Send("PermissionRequest", """, "tool_name": "Bash", "tool_input": { "command": "two" }""", session: "s2", cwd: "C:\\\\other")!;
        await WpfHost.Settle();
        Assert.Null(await first.Answer); // released to its own window
        Assert.Equal("other · Bash: two", vm.AlertDetail);
        vm.DismissAlertCommand.Execute(null);
        Assert.True(await second.Answer);
    });

    [Fact]
    public Task Finished_turn_is_a_toast_and_the_pill_goes_back() => host.RunAsync(async () =>
    {
        var (vm, a, _, _) = Make();
        a.Send("UserPromptSubmit");
        a.Send("Stop", """, "last_assistant_message": "Готово" """);
        await WpfHost.Settle();
        Assert.Equal("Claude закончил · stillbar", vm.StatusText);
        Assert.Equal("", vm.AgentText);
    });

    [Fact]
    public Task A_finished_timer_still_comes_first() => host.RunAsync(async () =>
    {
        var (vm, a, _, advance) = Make();
        vm.StartTimerCommand.Execute("1");
        advance(TimeSpan.FromMinutes(2));
        a.Send("PermissionRequest", """, "tool_name": "Bash", "tool_input": { "command": "ls" }""");
        await WpfHost.Settle();
        Assert.Equal("Таймер закончился", vm.AlertTitle);
        vm.DismissAlertCommand.Execute(null);
        Assert.Equal("Claude спрашивает", vm.AlertTitle); // the question was waiting behind it
    });
}
