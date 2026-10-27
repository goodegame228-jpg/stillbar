namespace Stillbar.Core.Controls;

/// <summary>Windows' own VPN connections (Settings → VPN), read from the phonebook files and rasdial.</summary>
public static class Vpn
{
    /// <summary>Connection names in a rasphone.pbk: every [section] is one connection.</summary>
    public static IReadOnlyList<string> PhonebookNames(string text) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 2 && l[0] == '[' && l[^1] == ']')
            .Select(l => l[1..^1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// rasdial with no arguments prints a header line, one line per connected entry, and a closing line —
    /// in the language of Windows («Connected to» / «Подключено к»). Only the shape is relied on, not the words.
    /// </summary>
    public static IReadOnlyList<string> ConnectedNames(string rasdialOutput)
    {
        var lines = rasdialOutput.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count <= 2 ? [] : lines.Skip(1).Take(lines.Count - 2).ToList();
    }
}

/// <summary>
/// Restart and shut down are one click away from losing someone's work: the first click asks («Точно?»),
/// a second click within a few seconds does it.
/// </summary>
public sealed class ConfirmGate(TimeSpan window)
{
    private (string Action, DateTimeOffset At)? _armed;

    /// <summary>True when this click should act; false when it only armed the confirmation.</summary>
    public bool Click(string action, DateTimeOffset now)
    {
        if (_armed is { } a && a.Action == action && now - a.At <= window)
        {
            _armed = null;
            return true;
        }
        _armed = (action, now);
        return false;
    }

    public string? Armed(DateTimeOffset now) => _armed is { } a && now - a.At <= window ? a.Action : null;
}

/// <summary>Hide the bar while one of these apps is in front (games in a window, a presentation tool, a video call).</summary>
public static class Exclusions
{
    /// <summary>«obs64.exe», «OBS64», « zoom » all mean the same process name.</summary>
    public static string Normalize(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return n.EndsWith(".exe", StringComparison.Ordinal) ? n[..^4] : n;
    }

    public static bool Matches(IEnumerable<string> excluded, string? foregroundProcess) =>
        foregroundProcess is { Length: > 0 } p && excluded.Any(e => Normalize(e) == Normalize(p));

    /// <summary>The settings field: one name per line or separated by commas.</summary>
    public static IReadOnlyList<string> Parse(string text) =>
        text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(n => n.Length > 0).Distinct().ToList();
}
