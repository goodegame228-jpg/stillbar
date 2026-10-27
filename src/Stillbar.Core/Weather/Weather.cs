using System.Globalization;
using System.Text.Json;

namespace Stillbar.Core.Weather;

public enum WeatherKind
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    Snow,
    Thunder,
}

public sealed record WeatherNow(double TemperatureC, int Code, bool IsDay)
{
    public WeatherKind Kind => WeatherCodes.ToKind(Code);

    /// <summary>18°, −3°. Rounded half away from zero, real minus sign.</summary>
    public string TemperatureText
    {
        get
        {
            int t = (int)Math.Round(TemperatureC, MidpointRounding.AwayFromZero);
            return (t < 0 ? "−" + (-t) : t.ToString(CultureInfo.InvariantCulture)) + "°";
        }
    }
}

/// <summary>A place from the city search.</summary>
public sealed record Place(string Name, string? Region, string? Country, double Lat, double Lon)
{
    /// <summary>«Казань, Татарстан, Россия»; the region is left out when it repeats the name (Москва, Москва).</summary>
    public string Label => string.Join(", ", new[] { Name, Region == Name ? null : Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static class WeatherCodes
{
    /// <summary>WMO weather interpretation codes, as Open-Meteo returns them.</summary>
    public static WeatherKind ToKind(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        >= 51 and <= 57 => WeatherKind.Drizzle,
        (>= 61 and <= 67) or (>= 80 and <= 82) => WeatherKind.Rain,
        (>= 71 and <= 77) or 85 or 86 => WeatherKind.Snow,
        >= 95 and <= 99 => WeatherKind.Thunder,
        _ => WeatherKind.Cloudy,
    };
}

public static class OpenMeteo
{
    public static string CurrentUrl(double lat, double lon) => string.Create(CultureInfo.InvariantCulture,
        $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.###}&longitude={lon:0.###}&current=temperature_2m,weather_code,is_day&timezone=auto");

    /// <summary>City search for the settings window (Open-Meteo geocoding, Russian names).</summary>
    public static string GeocodeUrl(string name) =>
        $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=6&language=ru&format=json";

    /// <summary>Places found: «Казань, Татарстан, Россия» with coordinates. Empty if none or not understood.</summary>
    public static IReadOnlyList<Place> ParseGeocode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
            var list = new List<Place>();
            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("name", out var n) || !r.TryGetProperty("latitude", out var la) || !r.TryGetProperty("longitude", out var lo)) continue;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                list.Add(new Place(n.GetString() ?? "", S("admin1"), S("country"), la.GetDouble(), lo.GetDouble()));
            }
            return list;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Reads the "current" block. Null if the response is not what we expect.</summary>
    public static WeatherNow? ParseCurrent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("current", out var c)) return null;
            if (!c.TryGetProperty("temperature_2m", out var t) || t.ValueKind != JsonValueKind.Number) return null;
            int code = c.TryGetProperty("weather_code", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 3;
            bool day = !c.TryGetProperty("is_day", out var d) || d.ValueKind != JsonValueKind.Number || d.GetInt32() == 1;
            return new WeatherNow(t.GetDouble(), code, day);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
