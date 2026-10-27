namespace Stillbar.Core.Notes;

/// <summary>A note: plain text, the first line is its title. Lines starting «[ ]» or «[x]» are a checklist.</summary>
public sealed record Note(string Id, string Text, DateTimeOffset Updated)
{
    public string Title => FirstLine is { Length: > 0 } t ? t : "Без названия";

    /// <summary>The second non-empty line, for the list.</summary>
    public string Preview => Lines.Select(Clean).Where(l => l.Length > 0).Skip(1).FirstOrDefault() ?? "";

    /// <summary>«2 из 5» for a note with a checklist, else empty.</summary>
    public string Progress
    {
        get
        {
            var items = Lines.Where(IsCheckItem).ToList();
            return items.Count == 0 ? "" : $"{items.Count(IsChecked)} из {items.Count}";
        }
    }

    private string FirstLine => Lines.Select(Clean).FirstOrDefault(l => l.Length > 0) ?? "";
    private IEnumerable<string> Lines => Text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim());

    internal static bool IsCheckItem(string l) => l.StartsWith("[ ]") || IsChecked(l);
    internal static bool IsChecked(string l) => l.StartsWith("[x]", StringComparison.OrdinalIgnoreCase) || l.StartsWith("[х]", StringComparison.OrdinalIgnoreCase);
    private static string Clean(string l) => (IsCheckItem(l) ? l[3..] : l).TrimStart('#', ' ', '-', '*').Trim();
}

public static class NoteList
{
    /// <summary>Newest edit first; empty notes are dropped (a «new note» that was never typed into).</summary>
    public static List<Note> Upsert(IReadOnlyList<Note> notes, Note changed)
    {
        var list = notes.Where(n => n.Id != changed.Id).ToList();
        if (changed.Text.Trim().Length > 0) list.Insert(0, changed);
        return list;
    }

    /// <summary>Toggles «[ ]» ↔ «[x]» on one line of the text (0-based line index).</summary>
    public static string ToggleCheck(string text, int line)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (line < 0 || line >= lines.Length) return text;
        var l = lines[line];
        var trimmed = l.TrimStart();
        var indent = l[..(l.Length - trimmed.Length)];
        if (trimmed.StartsWith("[ ]")) lines[line] = indent + "[x]" + trimmed[3..];
        else if (Note.IsChecked(trimmed)) lines[line] = indent + "[ ]" + trimmed[3..];
        return string.Join("\n", lines);
    }
}
