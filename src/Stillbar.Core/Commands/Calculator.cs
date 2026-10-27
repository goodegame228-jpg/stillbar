using System.Globalization;
using System.Text.RegularExpressions;

namespace Stillbar.Core.Commands;

/// <summary>
/// The command bar's arithmetic: + − × ÷ ^ %, brackets, decimal comma or point, spaces in numbers
/// («1 500 * 3»), «20% от 300». Recursive descent, no eval, no surprises.
/// </summary>
public static class Calculator
{
    public static double? Evaluate(string input)
    {
        var s = input.Trim().ToLowerInvariant()
            .Replace('×', '*').Replace('·', '*').Replace('÷', '/').Replace('−', '-').Replace('–', '-')
            .Replace(':', '/');
        // «20% от 300» → 300 * 20 / 100
        var pct = Regex.Match(s, @"^\s*(?<p>[\d\s.,]+)\s*%\s*(от|of)\s*(?<x>.+)$");
        if (pct.Success)
            return (Number(pct.Groups["x"].Value) ?? Evaluate(pct.Groups["x"].Value)) is { } x && Number(pct.Groups["p"].Value) is { } p ? x * p / 100 : null;
        // «1 500» → 1500, «2,5» → 2.5 (a comma between digits is a decimal point here)
        s = Regex.Replace(s, @"(?<=\d) (?=\d{3}\b)", "");
        s = Regex.Replace(s, @"(?<=\d),(?=\d)", ".");
        if (Regex.IsMatch(s, @"\d\s+\d")) return null; // «2 2» is two numbers, not 22
        if (!Regex.IsMatch(s, @"^[\d\s.+\-*/^()%]+$") || !Regex.IsMatch(s, @"\d")) return null;
        // A bare number is not a calculation worth showing.
        if (Regex.IsMatch(s.Trim(), @"^-?\d+(\.\d+)?$")) return null;
        try
        {
            var p = new Parser(s.Replace(" ", ""));
            var v = p.Expr();
            return p.Done && double.IsFinite(v) ? v : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>«12 345,67»: grouped thousands, comma decimals, at most 10 significant digits.</summary>
    public static string Format(double v)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        double r = Math.Round(v, 10);
        if (Math.Abs(r) >= 1e15 || (Math.Abs(r) < 1e-6 && r != 0)) return r.ToString("0.######E+0", ru);
        return r.ToString("#,0.##########", ru).Replace(' ', ' ');
    }

    internal static double? Number(string s) =>
        double.TryParse(s.Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private sealed class Parser(string s)
    {
        private int _i;
        public bool Done => _i == s.Length;

        public double Expr()
        {
            double v = Term();
            while (_i < s.Length && s[_i] is '+' or '-')
                v = s[_i++] == '+' ? v + Term() : v - Term();
            return v;
        }

        private double Term()
        {
            double v = Power();
            while (_i < s.Length && s[_i] is '*' or '/')
            {
                char op = s[_i++];
                double r = Power();
                if (op == '/' && r == 0) throw new FormatException("division by zero");
                v = op == '*' ? v * r : v / r;
            }
            return v;
        }

        private double Power()
        {
            double v = Unary();
            if (_i < s.Length && s[_i] == '^') { _i++; v = Math.Pow(v, Power()); } // right-associative
            return v;
        }

        private double Unary()
        {
            if (_i < s.Length && s[_i] == '-') { _i++; return -Unary(); }
            if (_i < s.Length && s[_i] == '+') { _i++; return Unary(); }
            double v = Atom();
            if (_i < s.Length && s[_i] == '%') { _i++; v /= 100; }
            return v;
        }

        private double Atom()
        {
            if (_i < s.Length && s[_i] == '(')
            {
                _i++;
                double v = Expr();
                if (_i >= s.Length || s[_i] != ')') throw new FormatException("missing )");
                _i++;
                return v;
            }
            int start = _i;
            while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.')) _i++;
            if (start == _i) throw new FormatException("number expected");
            return double.Parse(s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>«5 км в милях», «100 f в c», «2.5 кг в фунтах», «1 гб в мб»: length, mass, temperature, data, volume, speed.</summary>
public static partial class Units
{
    private sealed record Unit(string Dimension, double Factor, double Offset = 0);

    // Factor to the base unit of its dimension (m, kg, °C via offset, byte, l, m/s).
    private static readonly (string[] Names, Unit Unit)[] Table =
    [
        (["мм", "mm", "миллиметр"], new("length", 0.001)),
        (["см", "cm", "сантиметр"], new("length", 0.01)),
        (["м", "m", "метр"], new("length", 1)),
        (["км", "km", "километр"], new("length", 1000)),
        (["дюйм", "in", "inch", "\""], new("length", 0.0254)),
        (["фут", "ft", "foot", "feet"], new("length", 0.3048)),
        (["ярд", "yd", "yard"], new("length", 0.9144)),
        (["миля", "мили", "миль", "милях", "mi", "mile", "miles"], new("length", 1609.344)),
        (["г", "g", "грамм"], new("mass", 0.001)),
        (["кг", "kg", "килограмм"], new("mass", 1)),
        (["т", "t", "тонн"], new("mass", 1000)),
        (["фунт", "lb", "lbs", "pound"], new("mass", 0.45359237)),
        (["унция", "унции", "унций", "oz", "ounce"], new("mass", 0.028349523125)),
        (["c", "с", "°c", "°с", "цельсий", "градус"], new("temp", 1)),
        (["f", "°f", "фаренгейт"], new("temp", 5.0 / 9, -32)),
        (["k", "кельвин"], new("temp", 1, -273.15)),
        (["б", "b", "байт", "byte"], new("data", 1)),
        (["кб", "kb", "килобайт"], new("data", 1024)),
        (["мб", "mb", "мегабайт"], new("data", 1024d * 1024)),
        (["гб", "gb", "гигабайт"], new("data", 1024d * 1024 * 1024)),
        (["тб", "tb", "терабайт"], new("data", 1024d * 1024 * 1024 * 1024)),
        (["мл", "ml", "миллилитр"], new("volume", 0.001)),
        (["л", "l", "литр"], new("volume", 1)),
        (["галлон", "gal", "gallon"], new("volume", 3.785411784)),
        (["км/ч", "kmh", "km/h"], new("speed", 1 / 3.6)),
        (["м/с", "m/s"], new("speed", 1)),
        (["mph", "миль/ч"], new("speed", 0.44704)),
    ];

    /// <summary>The converted value and the target unit as typed, or null if this is not a conversion.</summary>
    public static (double Value, string Unit)? Convert(string input)
    {
        var m = Pattern().Match(input.Trim().ToLowerInvariant().Replace('ё', 'е'));
        if (!m.Success || Calculator.Number(m.Groups["n"].Value) is not { } n) return null;
        if (Find(m.Groups["from"].Value) is not { } from || Find(m.Groups["to"].Value) is not { } to) return null;
        if (from.Dimension != to.Dimension) return null;
        double baseValue = (n + from.Offset) * from.Factor;
        return (baseValue / to.Factor - to.Offset, m.Groups["to"].Value);
    }

    /// <summary>Exact name first; then a word that starts with a known name of 3+ letters («километров» → км).</summary>
    private static Unit? Find(string name)
    {
        name = name.Trim().TrimEnd('.');
        foreach (var (names, unit) in Table)
            if (names.Contains(name)) return unit;
        foreach (var (names, unit) in Table)
            if (names.Any(n => n.Length >= 3 && name.StartsWith(n, StringComparison.Ordinal))) return unit;
        return null;
    }

    [GeneratedRegex(@"^(?<n>-?[\d\s]+(?:[.,]\d+)?)\s*(?<from>[^\s\d]+(?:/[^\s\d]+)?)\s+(?:в|во|to|in|=)\s+(?<to>[^\s\d]+(?:/[^\s\d]+)?)$")]
    private static partial Regex Pattern();
}
