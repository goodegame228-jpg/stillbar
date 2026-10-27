using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stillbar.Core.Commands;

/// <summary>One text transformation offered in the command bar; applied to the text in the clipboard.</summary>
public sealed record TextTool(string Id, string Title, string[] Words, Func<string, string?> Apply);

/// <summary>Case, keyboard layout, transliteration, spaces, JSON, Base64 and counts. Pure functions.</summary>
public static partial class TextTools
{
    public static readonly IReadOnlyList<TextTool> All =
    [
        new("upper", "ВЕРХНИЙ РЕГИСТР", ["верхний", "заглавные", "капс", "upper"], t => t.ToUpper(CultureInfo.CurrentCulture)),
        new("lower", "нижний регистр", ["нижний", "строчные", "lower"], t => t.ToLower(CultureInfo.CurrentCulture)),
        new("title", "Каждое Слово С Заглавной", ["заглавной", "каждое слово", "title"], TitleCase),
        new("sentence", "Как в предложении", ["предложени", "sentence"], SentenceCase),
        new("layout", "Сменить раскладку (ghbdtn → привет)", ["раскладк", "layout", "ghbdtn"], SwitchLayout),
        new("translit", "Транслит (привет → privet)", ["транслит", "translit", "латиниц"], Translit),
        new("spaces", "Убрать лишние пробелы", ["пробел", "spaces", "trim"], CollapseSpaces),
        new("json", "Форматировать JSON", ["json", "джейсон"], FormatJson),
        new("base64e", "В Base64", ["base64", "бейс64", "закодировать"], t => Convert.ToBase64String(Encoding.UTF8.GetBytes(t))),
        new("base64d", "Из Base64", ["base64", "бейс64", "раскодировать", "декод"], FromBase64),
    ];

    public static string TitleCase(string t) =>
        WordStart().Replace(t.ToLower(CultureInfo.CurrentCulture), m => m.Value.ToUpper(CultureInfo.CurrentCulture));

    public static string SentenceCase(string t)
    {
        var lower = t.ToLower(CultureInfo.CurrentCulture);
        return SentenceStart().Replace(lower, m => m.Value.ToUpper(CultureInfo.CurrentCulture));
    }

    private const string Latin = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?";
    private const string Cyrillic = "ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,";

    /// <summary>Typed in the wrong layout: each key is swapped to the other one. The direction follows the letters in the text.</summary>
    public static string SwitchLayout(string t)
    {
        int lat = t.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
        int cyr = t.Count(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');
        var (from, to) = lat >= cyr ? (Latin, Cyrillic) : (Cyrillic, Latin);
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
        {
            int i = from.IndexOf(c);
            sb.Append(i >= 0 ? to[i] : c);
        }
        return sb.ToString();
    }

    private static readonly Dictionary<char, string> TranslitMap = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z",
        ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    /// <summary>Russian letters to Latin (close to the passport rules, without the apostrophes).</summary>
    public static string Translit(string t)
    {
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
        {
            var lower = char.ToLower(c, CultureInfo.InvariantCulture);
            if (!TranslitMap.TryGetValue(lower, out var latin)) { sb.Append(c); continue; }
            if (c != lower && latin.Length > 0) latin = char.ToUpperInvariant(latin[0]) + latin[1..];
            sb.Append(latin);
        }
        return sb.ToString();
    }

    /// <summary>Runs of spaces and tabs become one space, ends of lines are trimmed, three or more line breaks become two.</summary>
    public static string CollapseSpaces(string t)
    {
        var lines = t.Replace("\r\n", "\n").Split('\n').Select(l => Spaces().Replace(l, " ").Trim());
        return ManyBreaks().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    public static string? FormatJson(string t)
    {
        try
        {
            using var doc = JsonDocument.Parse(t);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (JsonException) { return null; }
    }

    public static string? FromBase64(string t)
    {
        try
        {
            var bytes = Convert.FromBase64String(t.Trim());
            var s = new UTF8Encoding(false, true).GetString(bytes);
            return s;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return null; }
    }

    /// <summary>«128 символов · 21 слово · 3 строки».</summary>
    public static string Stats(string t)
    {
        int chars = t.Length;
        int words = Words().Matches(t).Count;
        int lines = t.Length == 0 ? 0 : t.Replace("\r\n", "\n").Split('\n').Length;
        return $"{chars} {Plural(chars, "символ", "символа", "символов")} · {words} {Plural(words, "слово", "слова", "слов")} · {lines} {Plural(lines, "строка", "строки", "строк")}";
    }

    public static string Plural(int n, string one, string few, string many) =>
        (n % 10, n % 100) switch
        {
            (1, not 11) => one,
            (>= 2 and <= 4, < 12 or > 14) => few,
            _ => many,
        };

    [GeneratedRegex(@"(?<![\p{L}\p{N}'])\p{L}")] private static partial Regex WordStart();
    [GeneratedRegex(@"(^|[.!?…]\s+)\p{L}")] private static partial Regex SentenceStart();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex ManyBreaks();
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-'][\p{L}\p{N}]+)*")] private static partial Regex Words();
}
