using Stillbar.App.Services;
using Stillbar.Core.Commands;
using Stillbar.Core.Notes;
using Stillbar.Core.Pill;
using Stillbar.Core.Reminders;

namespace Stillbar.App.Tests;

/// <summary>Notes, reminders and the command bar's actions, on in-memory stores and the fake clock.</summary>
[Collection("wpf")]
public class NotesAndCommandsTests(WpfHost host)
{
    [Fact]
    public Task Typing_creates_a_note_and_saves_it() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.IsNotesPage = true;
        Assert.True(r.Vm.HasNoNotes);
        r.Vm.NoteText = "Покупки\n[ ] хлеб";
        r.Vm.SaveNote();
        await WpfHost.Settle();
        var row = Assert.Single(r.Vm.NoteRows);
        Assert.Equal("Покупки", row.Title);
        Assert.Equal("0 из 1 · хлеб", row.Detail);
        Assert.True(row.IsCurrent);
    });

    [Fact]
    public Task Switching_notes_keeps_both_and_new_note_clears_the_editor() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.NoteText = "Первая";
        r.Vm.NewNoteCommand.Execute(null);
        Assert.Equal("", r.Vm.NoteText);
        r.Vm.NoteText = "Вторая";
        r.Vm.SaveNote();
        await WpfHost.Settle();
        Assert.Equal(["Вторая", "Первая"], r.Vm.NoteRows.Select(n => n.Title));
        r.Vm.OpenNoteCommand.Execute(r.Vm.NoteRows[1]);
        Assert.Equal("Первая", r.Vm.NoteText);
    });

    [Fact]
    public Task Delete_removes_the_note_and_empties_the_editor() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.NoteText = "Удалить меня";
        r.Vm.SaveNote();
        await WpfHost.Settle();
        r.Vm.DeleteNoteCommand.Execute(r.Vm.NoteRows[0]);
        await WpfHost.Settle();
        Assert.Empty(r.Vm.NoteRows);
        Assert.Equal("", r.Vm.NoteText);
    });

    [Fact]
    public Task Reminder_phrase_is_added_and_rejected_phrase_is_explained() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.ReminderInput = "через 10 минут чай";
        r.Vm.AddReminderFromInputCommand.Execute(null);
        await WpfHost.Settle();
        var row = Assert.Single(r.Vm.ReminderRows);
        Assert.Equal("Чай", row.Text);
        Assert.Equal("через 10 мин", row.When);
        Assert.Equal("", r.Vm.ReminderInput);

        r.Vm.ReminderInput = "купить хлеб";
        r.Vm.AddReminderFromInputCommand.Execute(null);
        Assert.Equal("Не понял, когда напомнить", r.Vm.StatusText);
        Assert.Equal("купить хлеб", r.Vm.ReminderInput); // kept, so it can be fixed
    });

    [Fact]
    public Task Due_reminder_takes_the_pill_once_with_a_chime() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.AddReminder(new Reminder("1", "Позвонить маме", r.Now.AddMinutes(5)));
        r.Advance(TimeSpan.FromMinutes(4));
        Assert.NotEqual(PillKind.Alert, r.Vm.PillKind);
        r.Advance(TimeSpan.FromMinutes(1));
        r.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PillKind.Alert, r.Vm.PillKind);
        Assert.Equal("Позвонить маме", r.Vm.AlertTitle);
        Assert.Equal("Через 10 мин", r.Vm.AlertSecondaryLabel);
        Assert.Equal(1, r.Chimes);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Snooze_moves_it_ten_minutes_and_done_removes_it() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.AddReminder(new Reminder("1", "Чай", r.Now.AddMinutes(1)));
        r.Advance(TimeSpan.FromMinutes(1));
        r.Vm.AlertSecondaryCommand.Execute(null);
        await WpfHost.Settle();
        Assert.NotEqual(PillKind.Alert, r.Vm.PillKind);
        Assert.Equal("через 10 мин", Assert.Single(r.Vm.ReminderRows).When);

        r.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(PillKind.Alert, r.Vm.PillKind);
        r.Vm.DismissAlertCommand.Execute(null);
        await WpfHost.Settle();
        Assert.Empty(r.Vm.ReminderRows);
        Assert.NotEqual(PillKind.Alert, r.Vm.PillKind);
    });

    [Fact]
    public Task Repeating_reminder_comes_back_tomorrow() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.AddReminder(new Reminder("1", "Таблетки", r.Now.AddMinutes(1), Repeat.Daily));
        r.Advance(TimeSpan.FromMinutes(1));
        r.Vm.DismissAlertCommand.Execute(null);
        await WpfHost.Settle();
        Assert.Equal("каждый день · завтра в 12:01", Assert.Single(r.Vm.ReminderRows).When);
    });

    [Fact]
    public Task Finished_timer_goes_first_then_the_reminder() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.StartTimerCommand.Execute("1");
        r.Vm.AddReminder(new Reminder("1", "Чай", r.Now.AddSeconds(30)));
        r.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("Таймер закончился", r.Vm.AlertTitle);
        r.Vm.DismissAlertCommand.Execute(null);
        Assert.Equal(PillKind.Alert, r.Vm.PillKind);
        Assert.Equal("Чай", r.Vm.AlertTitle);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Command_results_do_their_thing() => host.RunAsync(async () =>
    {
        var r = new Rig();
        var results = CommandRouter.Route("2+2*2", r.Now, []);
        Assert.True(r.Vm.ExecuteCommand(results[0]));
        Assert.Equal(["6"], r.Screen.ClipboardTexts);

        r.Vm.ExecuteCommand(CommandRouter.Route("таймер 1:30", r.Now, [])[0]);
        Assert.True(r.Vm.IsTimerSet);
        Assert.Equal("1:30", r.Vm.TimerText);

        r.Vm.ExecuteCommand(CommandRouter.Route("заметка купить хлеб", r.Now, [])[0]);
        r.Vm.ExecuteCommand(CommandRouter.Route("напомни через 2 часа забрать посылку", r.Now, [])[0]);
        await WpfHost.Settle();
        Assert.Equal("купить хлеб", Assert.Single(r.Vm.NoteRows).Title);
        Assert.Equal("Забрать посылку", Assert.Single(r.Vm.ReminderRows).Text);

        r.Vm.ExecuteCommand(new CommandResult(CommandKind.App, "Блокнот", "", @"C:\menu\Блокнот.lnk"));
        Assert.Equal([@"C:\menu\Блокнот.lnk"], r.Dock.Launched);
    });

    [Fact]
    public Task Text_tools_work_on_the_clipboard_text() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Screen.ClipboardText = "ghbdtn";
        Assert.True(r.Vm.ExecuteCommand(new CommandResult(CommandKind.TextTool, "", "", "layout")));
        Assert.Equal("привет", r.Screen.ClipboardTexts.Last());

        r.Screen.ClipboardText = "{нет";
        Assert.False(r.Vm.ExecuteCommand(new CommandResult(CommandKind.TextTool, "", "", "json")));
        Assert.Equal("В буфере не JSON", r.Vm.StatusText);

        r.Screen.ClipboardText = "раз два";
        r.Vm.ExecuteCommand(new CommandResult(CommandKind.TextTool, "", "", "stats"));
        Assert.Equal("7 символов · 2 слова · 1 строка", r.Vm.StatusText);

        r.Screen.ClipboardText = null;
        Assert.False(r.Vm.ExecuteCommand(new CommandResult(CommandKind.TextTool, "", "", "upper")));
        Assert.Equal("В буфере нет текста", r.Vm.StatusText);
        await Task.CompletedTask;
    });
}
