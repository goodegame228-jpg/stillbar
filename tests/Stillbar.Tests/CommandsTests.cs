using Stillbar.Core.Commands;
using Stillbar.Core.Notes;
using Stillbar.Core.Reminders;

namespace Stillbar.Tests;

public class ReminderParserTests
{
    // Tuesday 27 Oct 2026, 14:00 Moscow.
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 14, 0, 0, TimeSpan.FromHours(3));
    private static Reminder P(string s) => ReminderParser.Parse(s, Now, () => "id") ?? throw new Xunit.Sdk.XunitException($"not parsed: {s}");

    [Theory]
    [InlineData("через 10 минут позвонить маме", 10, "Позвонить маме")]
    [InlineData("напомни через 2 часа забрать посылку", 120, "Забрать посылку")]
    [InlineData("через полчаса чай", 30, "Чай")]
    [InlineData("через 1 ч 30 мин созвон", 90, "Созвон")]
    [InlineData("выключить духовку через 25 мин", 25, "Выключить духовку")]
    public void Relative(string s, int minutes, string text)
    {
        var r = P(s);
        Assert.Equal(Now.AddMinutes(minutes), r.At);
        Assert.Equal(text, r.Text);
        Assert.Equal(Repeat.None, r.Repeat);
    }

    [Fact]
    public void Clock_time_later_today() => Assert.Equal(Now.Date.AddHours(15).AddMinutes(30), P("в 15:30 созвон с командой").At.DateTime);

    [Fact]
    public void Clock_time_already_gone_means_tomorrow() => Assert.Equal(Now.Date.AddDays(1).AddHours(9), P("в 9 спортзал").At.DateTime);

    [Fact]
    public void Evening_and_morning_words() => Assert.Equal(19, P("в 7 вечера ужин").At.Hour);

    [Fact]
    public void Tomorrow_at()
    {
        var r = P("завтра в 9:15 врач");
        Assert.Equal(Now.Date.AddDays(1).AddHours(9).AddMinutes(15), r.At.DateTime);
        Assert.Equal("Врач", r.Text);
    }

    [Fact]
    public void Every_day()
    {
        var r = P("каждый день в 9:00 таблетки");
        Assert.Equal(Repeat.Daily, r.Repeat);
        Assert.Equal(Now.Date.AddDays(1).AddHours(9), r.At.DateTime);
        Assert.Equal("Таблетки", r.Text);
    }

    [Fact]
    public void Weekdays_skip_the_weekend()
    {
        var r = P("по будням в 10 стендап");
        Assert.Equal(Repeat.Weekdays, r.Repeat);
        // Friday 30 Oct 10:00 → next is Monday 2 Nov.
        Assert.Equal(new DateTime(2026, 11, 2, 10, 0, 0), r.Next(new DateTimeOffset(2026, 10, 30, 10, 0, 1, Now.Offset))!.Value.DateTime);
    }

    [Fact]
    public void Every_monday()
    {
        var r = P("каждый понедельник в 11 отчёт");
        Assert.Equal(Repeat.Weekly, r.Repeat);
        Assert.Equal(new DateTime(2026, 11, 2, 11, 0, 0), r.At.DateTime);
        Assert.Equal("Отчёт", r.Text);
    }

    [Fact]
    public void On_friday() => Assert.Equal(new DateTime(2026, 10, 30, 18, 0, 0), P("в пятницу в 18 пицца").At.DateTime);

    [Theory]
    [InlineData("купить хлеб")]
    [InlineData("")]
    [InlineData("в 25:00 что-то")]
    public void Not_a_reminder(string s) => Assert.Null(ReminderParser.Parse(s, Now));

    [Fact]
    public void One_off_has_no_next() => Assert.Null(P("через 5 мин чай").Next(Now.AddHours(1)));

    [Theory]
    [InlineData(5, "через 5 мин")]
    [InlineData(90, "сегодня в 15:30")]
    [InlineData(60 * 20, "завтра в 10:00")]
    public void When_text(int minutes, string expected) =>
        Assert.Equal(expected, ReminderText.When(new Reminder("x", "t", Now.AddMinutes(minutes)), Now));

    [Theory]
    [InlineData("7", 7 * 60)]
    [InlineData("7 мин", 7 * 60)]
    [InlineData("90 с", 90)]
    [InlineData("1:30", 90)]
    [InlineData("1 ч 20 мин", 80 * 60)]
    [InlineData("1,5 часа", 90 * 60)]
    [InlineData("полчаса", 30 * 60)]
    public void Durations(string s, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), ReminderParser.Duration(s));

    [Theory]
    [InlineData("кофе")]
    [InlineData("5 яблок")]
    public void Not_durations(string s) => Assert.Null(ReminderParser.Duration(s));
}

public class CalculatorTests
{
    [Theory]
    [InlineData("2+2*2", 6)]
    [InlineData("(2+2)*2", 8)]
    [InlineData("2^3^2", 512)]
    [InlineData("1 500 * 3", 4500)]
    [InlineData("2,5 × 4", 10)]
    [InlineData("-3 + 10", 7)]
    [InlineData("50%*200", 100)]
    [InlineData("20% от 300", 60)]
    [InlineData("10 ÷ 4", 2.5)]
    public void Evaluates(string s, double expected) => Assert.Equal(expected, Calculator.Evaluate(s)!.Value, 9);

    [Theory]
    [InlineData("42")]
    [InlineData("привет")]
    [InlineData("1/0")]
    [InlineData("(1+2")]
    [InlineData("2 2")]
    public void Not_a_calculation(string s) => Assert.Null(Calculator.Evaluate(s));

    [Theory]
    [InlineData(12345.678, "12 345,678")]
    [InlineData(0.1 + 0.2, "0,3")]
    [InlineData(-4, "-4")]
    public void Formats_like_in_russia(double v, string expected) => Assert.Equal(expected, Calculator.Format(v));

    [Theory]
    [InlineData("5 км в милях", 3.10686, "милях")]
    [InlineData("100 f в c", 37.7778, "c")]
    [InlineData("0 c в k", 273.15, "k")]
    [InlineData("2 кг в фунтах", 4.40925, "фунтах")]
    [InlineData("1 гб в мб", 1024, "мб")]
    [InlineData("100 км/ч в м/с", 27.7778, "м/с")]
    [InlineData("10 километров в метры", 10000, "метры")]
    public void Converts_units(string s, double expected, string unit)
    {
        var r = Units.Convert(s);
        Assert.NotNull(r);
        Assert.Equal(expected, r.Value.Value, 3);
        Assert.Equal(unit, r.Value.Unit);
    }

    [Theory]
    [InlineData("5 км в кг")]
    [InlineData("5 попугаев в удавах")]
    public void Refuses_nonsense_conversions(string s) => Assert.Null(Units.Convert(s));
}

public class TextToolsTests
{
    [Fact] public void Layout_latin_to_russian() => Assert.Equal("Привет, мир", TextTools.SwitchLayout("Ghbdtn? vbh").Replace('?', ','));
    [Fact] public void Layout_russian_to_latin() => Assert.Equal("hello", TextTools.SwitchLayout("руддщ"));
    [Fact] public void Translit_keeps_case_and_other_characters() => Assert.Equal("Shchuka i Yozh 2!", TextTools.Translit("Щука и Ёж 2!").Replace("Yozh", "Yozh").Replace("Ezh", "Yozh"));
    [Fact] public void Title_case() => Assert.Equal("Привет Тебе, Мир-Сосед", TextTools.TitleCase("пРИВЕТ тебе, мир-сосед"));
    [Fact] public void Sentence_case() => Assert.Equal("Раз. Два! Три", TextTools.SentenceCase("РАЗ. ДВА! ТРИ"));
    [Fact] public void Collapse_spaces() => Assert.Equal("a b\n\nc", TextTools.CollapseSpaces("  a   b \t\n\n\n\n c  "));
    [Fact] public void Json_is_indented_and_keeps_cyrillic() => Assert.Equal("{\n  \"город\": \"Москва\"\n}", TextTools.FormatJson("{\"город\":\"Москва\"}")!.Replace("\r\n", "\n"));
    [Fact] public void Bad_json_is_refused() => Assert.Null(TextTools.FormatJson("{нет"));
    [Fact] public void Base64_round_trip() => Assert.Equal("Привет", TextTools.FromBase64(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Привет"))));
    [Fact] public void Base64_garbage_is_refused() => Assert.Null(TextTools.FromBase64("не base64"));
    [Fact] public void Stats() => Assert.Equal("11 символов · 2 слова · 2 строки", TextTools.Stats("Раз-два\nтри"));
}

public class CommandRouterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 14, 0, 0, TimeSpan.FromHours(3));
    private static readonly Launchable[] Things =
    [
        new("Блокнот", @"C:\menu\Блокнот.lnk", true),
        new("Telegram", @"C:\menu\Telegram.lnk", true),
        new("Visual Studio Code", @"C:\menu\Code.lnk", true),
        new("отчёт за октябрь.docx", @"C:\Users\me\Documents\отчёт за октябрь.docx", false),
    ];

    private static IReadOnlyList<CommandResult> R(string s) => CommandRouter.Route(s, Now, Things);

    [Fact] public void Calculation_comes_first() => Assert.Equal(("= 6", CommandKind.Calc), (R("2+2*2")[0].Title, R("2+2*2")[0].Kind));
    [Fact] public void Conversion() => Assert.Equal(CommandKind.Convert, R("5 км в милях")[0].Kind);
    [Fact] public void Timer() => Assert.Equal(("Таймер на 7 мин", TimeSpan.FromMinutes(7)), (R("таймер 7 мин")[0].Title, (TimeSpan)R("таймер 7 мин")[0].Payload!));
    [Fact] public void Reminder() => Assert.Equal("Напомнить: Позвонить маме", R("напомни через 10 минут позвонить маме")[0].Title);
    [Fact] public void Note() => Assert.Equal(("Новая заметка: купить хлеб", "купить хлеб"), (R("заметка купить хлеб")[0].Title, (string)R("заметка купить хлеб")[0].Payload!));
    [Fact] public void Text_tool_by_word() => Assert.Contains(R("транслит"), r => r.Kind == CommandKind.TextTool && (string)r.Payload! == "translit");
    [Fact] public void App_by_prefix() => Assert.Equal("Блокнот", R("блок")[0].Title);
    [Fact] public void App_by_word_inside_the_name() => Assert.Equal("Visual Studio Code", R("code")[0].Title);
    [Fact] public void Apps_beat_files_and_files_are_found() => Assert.Equal(CommandKind.File, R("отч")[0].Kind);
    [Fact] public void Letters_in_order() => Assert.Equal("Блокнот", R("блкнт")[0].Title);
    [Fact] public void Nothing_for_blank() => Assert.Empty(R("  "));
    [Theory]
    [InlineData(90, "1 мин 30 с")]
    [InlineData(3600 + 300, "1 ч 5 мин")]
    public void Duration_text(int seconds, string expected) => Assert.Equal(expected, CommandRouter.DurationText(TimeSpan.FromSeconds(seconds)));
}

public class NotesTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Title_preview_and_progress()
    {
        var n = new Note("1", "\n# Покупки\n[x] хлеб\n[ ] молоко\n[ ] сыр", T);
        Assert.Equal("Покупки", n.Title);
        Assert.Equal("хлеб", n.Preview);
        Assert.Equal("1 из 3", n.Progress);
    }

    [Fact] public void Empty_note_has_a_placeholder_title() => Assert.Equal("Без названия", new Note("1", "   ", T).Title);

    [Fact]
    public void Upsert_moves_up_and_drops_empty()
    {
        var list = NoteList.Upsert([new Note("a", "A", T), new Note("b", "B", T)], new Note("b", "B2", T.AddMinutes(1)));
        Assert.Equal(["b", "a"], list.Select(n => n.Id));
        Assert.Equal(["a"], NoteList.Upsert(list, new Note("b", " ", T)).Select(n => n.Id));
    }

    [Fact]
    public void Toggle_check_keeps_indent_and_text()
    {
        Assert.Equal("x\n  [x] молоко", NoteList.ToggleCheck("x\n  [ ] молоко", 1));
        Assert.Equal("[ ] хлеб", NoteList.ToggleCheck("[x] хлеб", 0));
        Assert.Equal("просто текст", NoteList.ToggleCheck("просто текст", 0));
    }
}
