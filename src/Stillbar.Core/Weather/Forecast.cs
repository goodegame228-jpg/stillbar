using System.Globalization;
using System.Text.Json;

namespace Stillbar.Core.Weather;

public sealed record HourForecast(DateTime Time, double TemperatureC, int Code, bool IsDay, int PrecipChance, double PrecipMm)
{
    public WeatherKind Kind => WeatherCodes.ToKind(Code);
    public string TemperatureText => new WeatherNow(TemperatureC, Code, IsDay).TemperatureText;
}

public sealed record DayForecast(DateTime Date, double MaxC, double MinC, int Code, int PrecipChance)
{
    public WeatherKind Kind => WeatherCodes.ToKind(Code);
}

/// <summary>Precipitation in one 15-minute slot (Open-Meteo «minutely_15»): the rain warning's fine timing.</summary>
public sealed record QuarterPrecip(DateTime Time, double Mm);

/// <summary>Everything one Open-Meteo request returns: now, the next 24 hours, 7 days, the next 2 hours by quarter. Times are local to the city.</summary>
public sealed record Forecast(WeatherNow Now, IReadOnlyList<HourForecast> Hours, IReadOnlyList<DayForecast> Days, IReadOnlyList<QuarterPrecip> Quarters);

public static class ForecastApi
{
    public static string Url(double lat, double lon) =>
        "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString("0.###", CultureInfo.InvariantCulture) +
        "&longitude=" + lon.ToString("0.###", CultureInfo.InvariantCulture) +
        "&current=temperature_2m,weather_code,is_day" +
        "&hourly=temperature_2m,weather_code,precipitation_probability,precipitation,is_day&forecast_hours=25" +
        "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_days=7" +
        "&minutely_15=precipitation&forecast_minutely_15=8&timezone=auto";

    /// <summary>Null when the answer has no usable "current" block; missing hourly/daily parts just come back empty.</summary>
    public static Forecast? Parse(string json)
    {
        var now = OpenMeteo.ParseCurrent(json);
        if (now is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var hours = new List<HourForecast>();
            if (root.TryGetProperty("hourly", out var h) && Times(h) is { } ht)
                for (int i = 0; i < ht.Count; i++)
                    hours.Add(new HourForecast(ht[i], Num(h, "temperature_2m", i), (int)Num(h, "weather_code", i, 3),
                        Num(h, "is_day", i, 1) == 1, (int)Num(h, "precipitation_probability", i), Num(h, "precipitation", i)));
            var days = new List<DayForecast>();
            if (root.TryGetProperty("daily", out var d) && Times(d) is { } dt)
                for (int i = 0; i < dt.Count; i++)
                    days.Add(new DayForecast(dt[i], Num(d, "temperature_2m_max", i), Num(d, "temperature_2m_min", i),
                        (int)Num(d, "weather_code", i, 3), (int)Num(d, "precipitation_probability_max", i)));
            var quarters = new List<QuarterPrecip>();
            if (root.TryGetProperty("minutely_15", out var m) && Times(m) is { } mt)
                for (int i = 0; i < mt.Count; i++)
                    quarters.Add(new QuarterPrecip(mt[i], Num(m, "precipitation", i)));
            return new Forecast(now, hours.Take(24).ToList(), days, quarters);
        }
        catch (JsonException) { return new Forecast(now, [], [], []); }
    }

    private static List<DateTime>? Times(JsonElement block) =>
        block.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => DateTime.Parse(x.GetString()!, CultureInfo.InvariantCulture)).ToList()
            : null;

    /// <summary>A number from an hourly/daily array; nulls (the API sends them past the model's range) become the fallback.</summary>
    private static double Num(JsonElement block, string key, int i, double fallback = 0) =>
        block.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array && i < arr.GetArrayLength()
        && arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetDouble() : fallback;
}

/// <summary>
/// «Скоро дождь»: about an hour before rain starts, once per rain spell. Rain = a rainy code with a real chance
/// (≥ 50 %) or a measurable amount (≥ 0.2 mm). Not shown when it is already raining.
/// </summary>
public static class RainWarning
{
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(75);
    private const double WetMm = 0.2;
    private const int LikelyPercent = 50;

    /// <summary>When the rain is expected to start, if that is within the lead time; else null.</summary>
    public static DateTime? Upcoming(Forecast f, DateTime nowLocal)
    {
        if (IsWetNow(f) || f.Hours.Count == 0) return null;

        // 15-minute slots first: they say when within the hour.
        var quarter = f.Quarters.FirstOrDefault(q => q.Time > nowLocal && q.Time - nowLocal <= Lead && q.Mm >= WetMm);
        if (quarter is not null) return quarter.Time;

        var hour = f.Hours.FirstOrDefault(x => x.Time > nowLocal && x.Time - nowLocal <= Lead && IsWet(x));
        return hour?.Time;
    }

    /// <summary>
    /// Title «Скоро дождь», a short detail for the pill («через 40 мин · 80 %») and a sentence for the weather page.
    /// The chance is the highest of the hours the rain falls into, not the dry hour it starts from.
    /// </summary>
    public static (string Title, string Detail, string Sentence) Text(Forecast f, DateTime start, DateTime nowLocal)
    {
        var during = f.Hours.Where(h => h.Time <= start.AddHours(1) && h.Time.AddHours(1) > start).ToList();
        var kind = during.Where(IsWet).Select(h => h.Kind).DefaultIfEmpty(WeatherKind.Rain).First();
        string what = kind switch
        {
            WeatherKind.Snow => "Скоро снег",
            WeatherKind.Thunder => "Скоро гроза",
            _ => "Скоро дождь",
        };
        int minutes = Math.Max(5, (int)Math.Round((start - nowLocal).TotalMinutes / 5) * 5);
        int chance = during.Count > 0 ? during.Max(h => h.PrecipChance) : 0;
        string pct = chance > 0 ? $" · {chance} %" : "";
        string sentence = $"{what}: около {start:HH:mm}, примерно через {minutes} мин" + (chance > 0 ? $", вероятность {chance} %" : "");
        return (what, $"через {minutes} мин{pct}", sentence);
    }
    private static bool IsWet(HourForecast h) =>
        h.PrecipMm >= WetMm || (h.PrecipChance >= LikelyPercent && h.Kind is WeatherKind.Drizzle or WeatherKind.Rain or WeatherKind.Snow or WeatherKind.Thunder);

    private static bool IsWetNow(Forecast f) =>
        f.Now.Kind is WeatherKind.Drizzle or WeatherKind.Rain or WeatherKind.Snow or WeatherKind.Thunder;
}

public static class WeatherText
{
    public static string Describe(WeatherKind k) => k switch
    {
        WeatherKind.Clear => "Ясно",
        WeatherKind.PartlyCloudy => "Переменная облачность",
        WeatherKind.Cloudy => "Облачно",
        WeatherKind.Fog => "Туман",
        WeatherKind.Drizzle => "Морось",
        WeatherKind.Rain => "Дождь",
        WeatherKind.Snow => "Снег",
        WeatherKind.Thunder => "Гроза",
        _ => "",
    };

    private static readonly string[] Days = ["Вс", "Пн", "Вт", "Ср", "Чт", "Пт", "Сб"];

    /// <summary>«Сегодня», «Завтра», then «Пт 31».</summary>
    public static string DayName(DateTime date, DateTime today) =>
        date.Date == today.Date ? "Сегодня"
        : date.Date == today.Date.AddDays(1) ? "Завтра"
        : $"{Days[(int)date.DayOfWeek]} {date.Day}";

    /// <summary>Whole degrees with a real minus: «−3°».</summary>
    public static string Degrees(double c) => new WeatherNow(c, 0, true).TemperatureText;
}
