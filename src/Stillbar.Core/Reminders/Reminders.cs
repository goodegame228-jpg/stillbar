using System.Globalization;
using System.Text.RegularExpressions;

namespace Stillbar.Core.Reminders;

public enum Repeat { None, Daily, Weekdays, Weekly }

/// <summary>A reminder: what, when next, and whether it comes back.</summary>
public sealed record Reminder(string Id, string Text, DateTimeOffset At, Repeat Repeat = Repeat.None)
{
    /// <summary>The next time after <paramref name="now"/> for a repeating reminder; null for a one-off.</summary>
    public DateTimeOffset? Next(DateTimeOffset now)
    {
        if (Repeat == Repeat.None) return null;
        var t = At;
        while (t <= now || (Repeat == Repeat.Weekdays && IsWeekend(t)))
            t = Repeat == Repeat.Weekly ? t.AddDays(7) : t.AddDays(1);
        return t;
    }

    internal static bool IsWeekend(DateTimeOffset t) => t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}

/// <summary>
/// Reads a reminder from a Russian phrase, the way people type it:
/// «через 10 минут позвонить маме», «через полчаса чай», «в 15:30 созвон», «завтра в 9 спортзал»,
/// «каждый день в 9:00 таблетки», «по будням в 10 стендап», «каждый понедельник в 11 отчёт».
/// The time part may come first or last; what is left is the text.
/// </summary>
public static partial class ReminderParser
{
    public static Reminder? Parse(string phrase, DateTimeOffset now, Func<string>? newId = null)
    {
        // Match on a lower-case copy, cut the same spans from the original: «позвонить Маше» keeps its capital.
        var text = Regex.Replace(phrase.Trim(), @"\s+", " ");
        var s = text.ToLowerInvariant();
        if (s.Length == 0) return null;
        newId ??= () => Guid.NewGuid().ToString("N")[..12];

        // 1) relative: «через 10 минут», «через полчаса», «через 1 ч 30 мин»
        var rel = Relative().Match(s);
        if (rel.Success && Duration(rel.Groups["d"].Value) is { } d && d > TimeSpan.Zero)
            return Make(Cut(ref s, ref text, rel), now + d, Repeat.None);

        // 2) repeat and day: «каждый день», «по будням», «каждый понедельник», «завтра», «послезавтра»
        var repeat = Repeat.None;
        DayOfWeek? weekday = null;
        int dayOffset = 0;
        var rep = RepeatWords().Match(s);
        if (rep.Success)
        {
            var w = rep.Value;
            if (w.Contains("будн")) repeat = Repeat.Weekdays;
            else if (WeekdayOf(w) is { } wd) { repeat = Repeat.Weekly; weekday = wd; }
            else repeat = Repeat.Daily;
            Cut(ref s, ref text, rep);
        }
        var day = DayWords().Match(s);
        if (day.Success)
        {
            dayOffset = day.Value.StartsWith("послезавтра") ? 2 : day.Value.StartsWith("завтра") ? 1 : 0;
            if (WeekdayOf(day.Value) is { } wd) weekday = wd;
            Cut(ref s, ref text, day);
        }

        // 3) clock time: «в 15:30», «в 9», «в 9 утра», «в 7 вечера», «к 18:00»
        var at = ClockTime().Match(s);
        if (!at.Success)
        {
            if (repeat == Repeat.None && dayOffset == 0 && weekday is null) return null;
            at = Match.Empty;
        }
        int hour = at.Success ? int.Parse(at.Groups["h"].Value, CultureInfo.InvariantCulture) : 9;
        int minute = at.Success && at.Groups["m"].Success ? int.Parse(at.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var part = at.Success ? at.Groups["part"].Value : "";
        if ((part.StartsWith("веч") || part.StartsWith("дня")) && hour < 12) hour += 12;
        if (part.StartsWith("ноч") && hour == 12) hour = 0;
        if (hour > 23 || minute > 59) return null;
        if (at.Success) Cut(ref s, ref text, at);

        var local = now.Date.AddDays(dayOffset).AddHours(hour).AddMinutes(minute);
        var when = new DateTimeOffset(local, now.Offset);
        if (weekday is { } target)
        {
            int ahead = ((int)target - (int)when.DayOfWeek + 7) % 7;
            when = when.AddDays(ahead);
        }
        // «в 9» said at 10 means tomorrow; a weekday already gone this week means next week.
        while (when <= now || (repeat == Repeat.Weekdays && Reminder.IsWeekend(when)))
            when = when.AddDays(weekday is null ? 1 : 7);
        return Make(text, when, repeat);

        Reminder? Make(string what, DateTimeOffset t, Repeat r)
        {
            what = Tidy(what);
            return new Reminder(newId(), what.Length == 0 ? "Напоминание" : what, t, r);
        }
    }

    /// <summary>«7», «7 мин», «90 с», «1:30», «1 ч 20 мин», «полчаса», «полторы минуты» → a duration; null if not one.</summary>
    public static TimeSpan? Duration(string text)
    {
        var s = Normalize(text);
        if (s.Length == 0) return null;
        if (s is "полчаса") return TimeSpan.FromMinutes(30);
        if (s.StartsWith("полтора час") || s.StartsWith("полтора ч")) return TimeSpan.FromMinutes(90);
        if (s.StartsWith("полторы минут") || s.StartsWith("полторы мин")) return TimeSpan.FromSeconds(90);
        if (s.StartsWith("час") && !s.Contains(' ')) return TimeSpan.FromHours(1);
        if (s.StartsWith("минут") && !s.Contains(' ')) return TimeSpan.FromMinutes(1);

        var clock = Regex.Match(s, @"^(\d{1,2}):(\d{2})(?::(\d{2}))?$");
        if (clock.Success)
        {
            int a = int.Parse(clock.Groups[1].Value), b = int.Parse(clock.Groups[2].Value);
            return clock.Groups[3].Success
                ? new TimeSpan(a, b, int.Parse(clock.Groups[3].Value))
                : TimeSpan.FromMinutes(a) + TimeSpan.FromSeconds(b); // «1:30» on a timer is a minute and a half
        }

        var total = TimeSpan.Zero;
        bool any = false;
        foreach (Match m in DurationPart().Matches(s))
        {
            var n = double.Parse(m.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var u = m.Groups["u"].Value;
            total += u.StartsWith('ч') ? TimeSpan.FromHours(n)
                   : u.StartsWith('с') ? TimeSpan.FromSeconds(n)
                   : TimeSpan.FromMinutes(n); // no unit: minutes, as on every kitchen timer
            any = true;
        }
        var rest = DurationPart().Replace(s, "").Replace("и", "").Trim();
        return any && rest.Length == 0 ? total : null;
    }

    private static string Normalize(string s) => Regex.Replace(s.Trim().ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ");

    private static string Cut(ref string low, ref string original, Match m)
    {
        low = (low[..m.Index] + " " + low[(m.Index + m.Length)..]).Trim();
        original = (original[..m.Index] + " " + original[(m.Index + m.Length)..]).Trim();
        return original;
    }

    /// <summary>Strips leftovers like a lone «в», «что», «надо», commas and dashes at the edges.</summary>
    private static string Tidy(string s)
    {
        s = Regex.Replace(s, @"^(напомни(ть)?( мне)?|что|чтобы|надо|нужно)\b\s*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', ',', '—', '-', ':', '.');
        return s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];
    }

    private static DayOfWeek? WeekdayOf(string w) =>
        w.Contains("понедельн") ? DayOfWeek.Monday
        : w.Contains("вторник") ? DayOfWeek.Tuesday
        : w.Contains("сред") ? DayOfWeek.Wednesday
        : w.Contains("четверг") ? DayOfWeek.Thursday
        : w.Contains("пятниц") ? DayOfWeek.Friday
        : w.Contains("суббот") ? DayOfWeek.Saturday
        : w.Contains("воскресень") ? DayOfWeek.Sunday
        : null;

    [GeneratedRegex(@"\bчерез (?<d>полчаса|полтора часа|полторы минуты|час\b|минуту\b|(?:\d+(?:[.,]\d+)?\s*(?:ч|час[а-я]*|мин[а-я]*|м|сек[а-я]*|с)\b\s*(?:и\s*)?)+)")]
    private static partial Regex Relative();

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>ч|час[а-я]*|мин[а-я]*|м|сек[а-я]*|с)?\b")]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"\b(каждый день|ежедневно|каждое утро|по будням|в будни|кажд(?:ый|ую|ое) (?:понедельник|вторник|среду|четверг|пятницу|субботу|воскресенье)|по (?:понедельникам|вторникам|средам|четвергам|пятницам|субботам|воскресеньям))\b")]
    private static partial Regex RepeatWords();

    [GeneratedRegex(@"\b(послезавтра|завтра|сегодня|в (?:понедельник|вторник|среду|четверг|пятницу|субботу|воскресенье))\b")]
    private static partial Regex DayWords();

    [GeneratedRegex(@"\b(?:в|к|на) (?<h>\d{1,2})(?:[:.](?<m>\d{2}))?(?: ?(?:ч|час[а-я]*))?(?: (?<part>утра|дня|вечера|ночи))?(?=\s|$)")]
    private static partial Regex ClockTime();
}

/// <summary>«через 10 мин», «сегодня в 15:30», «завтра в 9:00», «пн, 27 окт в 11:00», plus «каждый день» etc.</summary>
public static class ReminderText
{
    private static readonly string[] Days = ["вс", "пн", "вт", "ср", "чт", "пт", "сб"];
    private static readonly string[] Months = ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

    public static string When(Reminder r, DateTimeOffset now)
    {
        var d = r.At - now;
        string when = d > TimeSpan.Zero && d < TimeSpan.FromHours(1) ? $"через {Math.Max(1, (int)Math.Ceiling(d.TotalMinutes))} мин"
            : r.At.Date == now.Date ? $"сегодня в {r.At:HH:mm}"
            : r.At.Date == now.Date.AddDays(1) ? $"завтра в {r.At:HH:mm}"
            : $"{Days[(int)r.At.DayOfWeek]}, {r.At.Day} {Months[r.At.Month - 1]} в {r.At:HH:mm}";
        return r.Repeat switch
        {
            Repeat.Daily => $"каждый день · {when}",
            Repeat.Weekdays => $"по будням · {when}",
            Repeat.Weekly => $"каждую неделю · {when}",
            _ => when,
        };
    }
}
