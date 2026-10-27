using System.Globalization;

namespace Stillbar.Core.Keyboard;

/// <summary>What the indicator watches: the input language of the window in front, and Caps Lock.</summary>
public readonly record struct KeyboardState(int LangId, bool CapsLock);

/// <summary>
/// Turns two readings into what the pill should say, or nothing. The first reading only sets the baseline
/// (no «RU» flash when Stillbar starts). Read by polling GetKeyboardLayout/GetKeyState: no keyboard hook.
/// </summary>
public sealed class KeyboardWatch
{
    private KeyboardState? _last;

    public string? Next(KeyboardState now)
    {
        var last = _last;
        _last = now;
        if (last is not { } before) return null;
        if (now.CapsLock != before.CapsLock) return now.CapsLock ? "Caps Lock включён" : "Caps Lock выключен";
        if (now.LangId != before.LangId && now.LangId != 0) return LayoutName(now.LangId);
        return null;
    }

    /// <summary>«RU», «EN», «UK»… from a Windows language id (the low word of an HKL).</summary>
    public static string LayoutName(int langId)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(langId).TwoLetterISOLanguageName;
            return name.Length is 2 or 3 ? name.ToUpperInvariant() : $"0x{langId:X4}";
        }
        catch (CultureNotFoundException)
        {
            return $"0x{langId:X4}";
        }
    }
}
