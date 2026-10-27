using System.Text.RegularExpressions;
using Stillbar.Core.Reminders;

namespace Stillbar.Core.Commands;

public enum CommandKind { Calc, Convert, Timer, Reminder, TextTool, App, File, Note }

/// <summary>
/// One line in the command bar. Title is what is shown big, Detail under it, Payload is what Enter acts on
/// (the number to copy, the minutes, the reminder, the text tool id, the path).
/// </summary>
public sealed record CommandResult(CommandKind Kind, string Title, string Detail, object? Payload = null);

/// <summary>A launchable thing found on the machine (Start menu shortcut, file).</summary>
public sealed record Launchable(string Name, string Path, bool IsApp);

/// <summary>
/// Turns what was typed into results, best first: an answer (calculation, conversion), an action that the words
/// ask for (timer, reminder, note, text tool), then apps and files whose names match.
/// </summary>
public static partial class CommandRouter
{
    public const int MaxResults = 8;

    public static IReadOnlyList<CommandResult> Route(string input, DateTimeOffset now, IEnumerable<Launchable> launchables)
    {
        var q = input.Trim();
        if (q.Length == 0) return [];
        var lower = q.ToLowerInvariant().Replace('ё', 'е');
        var results = new List<CommandResult>();

        if (Calculator.Evaluate(q) is { } v)
            results.Add(new(CommandKind.Calc, "= " + Calculator.Format(v), "Enter — скопировать результат", Calculator.Format(v)));

        if (Units.Convert(q) is var (value, unit))
            results.Add(new(CommandKind.Convert, $"{Calculator.Format(Math.Round(value, 6))} {unit}", "Enter — скопировать результат", Calculator.Format(Math.Round(value, 6))));

        // «таймер 7 мин», «таймер 1:30»
        var timer = TimerWords().Match(lower);
        if (timer.Success && ReminderParser.Duration(timer.Groups["d"].Value) is { } d && d > TimeSpan.Zero && d <= TimeSpan.FromHours(24))
            results.Add(new(CommandKind.Timer, $"Таймер на {DurationText(d)}", "Enter — запустить", d));

        // «напомни через 10 минут позвонить», or just a phrase that reads as a reminder
        bool asksReminder = ReminderWords().IsMatch(lower);
        if ((asksReminder || lower.StartsWith("через ")) && ReminderParser.Parse(lower, now) is { } r)
            results.Add(new(CommandKind.Reminder, $"Напомнить: {r.Text}", ReminderText.When(r, now), r));

        // «заметка купить хлеб»
        var note = NoteWords().Match(q);
        if (note.Success && note.Groups["t"].Value.Trim().Length > 0)
            results.Add(new(CommandKind.Note, $"Новая заметка: {note.Groups["t"].Value.Trim()}", "Enter — сохранить в «Заметки»", note.Groups["t"].Value.Trim()));

        foreach (var tool in TextTools.All)
            if (tool.Words.Any(w => lower.Contains(w)) || (lower.Length >= 3 && tool.Title.ToLowerInvariant().StartsWith(lower)))
                results.Add(new(CommandKind.TextTool, tool.Title, "К тексту в буфере обмена", tool.Id));
        if (lower.Length >= 3 && ("статистика текста".StartsWith(lower) || lower.Contains("символ") || lower.Contains("посчитать слова")))
            results.Add(new(CommandKind.TextTool, "Посчитать символы и слова", "Текст в буфере обмена", "stats"));

        // Apps and files: names that start with the query beat names that only contain it; apps beat files.
        if (results.Count == 0 || lower.Length >= 2)
        {
            var found = launchables
                .Select(l => (l, score: Score(l.Name.ToLowerInvariant().Replace('ё', 'е'), lower)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score + (x.l.IsApp ? 0.5 : 0))
                .ThenBy(x => x.l.Name.Length)
                .Select(x => x.l)
                .DistinctBy(l => l.Name.ToLowerInvariant())
                .Take(MaxResults);
            foreach (var l in found)
                results.Add(new(l.IsApp ? CommandKind.App : CommandKind.File, l.Name, l.IsApp ? "Приложение" : l.Path, l.Path));
        }
        return results.Take(MaxResults).ToList();
    }

    /// <summary>3: the name starts with the query; 2: a word in it does; 1: the letters are in it in order (initials «вс» → «Visual Studio» does not count, «блкнт» → «Блокнот» does).</summary>
    internal static double Score(string name, string q)
    {
        if (name.StartsWith(q, StringComparison.Ordinal)) return 3;
        if (name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(q, StringComparison.Ordinal))) return 2;
        if (q.Length >= 3 && name.Contains(q, StringComparison.Ordinal)) return 1.5;
        if (q.Length < 3) return 0;
        int i = 0;
        foreach (var c in name)
            if (i < q.Length && c == q[i]) i++;
        return i == q.Length ? 1 : 0;
    }

    public static string DurationText(TimeSpan d)
    {
        var parts = new List<string>();
        if (d.Hours > 0 || d.Days > 0) parts.Add($"{(int)d.TotalHours} ч");
        if (d.Minutes > 0) parts.Add($"{d.Minutes} мин");
        if (d.Seconds > 0) parts.Add($"{d.Seconds} с");
        return parts.Count == 0 ? "0 с" : string.Join(" ", parts);
    }

    [GeneratedRegex(@"^(?:таймер|timer|засеки)\s+(?:на\s+)?(?<d>.+)$")] private static partial Regex TimerWords();
    [GeneratedRegex(@"^(?:напомни|напомнить|напоминание|remind)\b")] private static partial Regex ReminderWords();
    [GeneratedRegex(@"^(?:заметка|запиши|note)[:\s]+(?<t>.+)$", RegexOptions.IgnoreCase)] private static partial Regex NoteWords();
}
