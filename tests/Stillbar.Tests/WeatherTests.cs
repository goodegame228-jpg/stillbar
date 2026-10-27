using Stillbar.Core.Weather;

namespace Stillbar.Tests;

public class WeatherTests
{
    [Fact]
    public void Parses_open_meteo_current_block()
    {
        const string json = """
            {"latitude":55.75,"current_units":{"temperature_2m":"°C"},
             "current":{"time":"2026-10-26T22:00","interval":900,"temperature_2m":6.8,"weather_code":61,"is_day":0}}
            """;
        var w = OpenMeteo.ParseCurrent(json)!;
        Assert.Equal(6.8, w.TemperatureC);
        Assert.Equal(WeatherKind.Rain, w.Kind);
        Assert.False(w.IsDay);
        Assert.Equal("7°", w.TemperatureText);
    }

    [Theory]
    [InlineData("""{"error":true,"reason":"bad"}""")]
    [InlineData("""{"current":{"weather_code":1}}""")]
    [InlineData("not json")]
    public void Unexpected_response_is_null(string json) => Assert.Null(OpenMeteo.ParseCurrent(json));

    [Theory]
    [InlineData(0, WeatherKind.Clear)]
    [InlineData(2, WeatherKind.PartlyCloudy)]
    [InlineData(3, WeatherKind.Cloudy)]
    [InlineData(48, WeatherKind.Fog)]
    [InlineData(53, WeatherKind.Drizzle)]
    [InlineData(81, WeatherKind.Rain)]
    [InlineData(75, WeatherKind.Snow)]
    [InlineData(86, WeatherKind.Snow)]
    [InlineData(95, WeatherKind.Thunder)]
    [InlineData(1234, WeatherKind.Cloudy)]
    public void Wmo_codes_map_to_kinds(int code, WeatherKind kind) => Assert.Equal(kind, WeatherCodes.ToKind(code));

    [Theory]
    [InlineData(-2.6, "−3°")]
    [InlineData(-0.4, "0°")]
    [InlineData(17.5, "18°")]
    public void Temperature_text(double t, string expected) =>
        Assert.Equal(expected, new WeatherNow(t, 0, true).TemperatureText);

    [Fact]
    public void Url_uses_dot_decimals_whatever_the_culture()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
        try
        {
            Assert.Contains("latitude=55.752&longitude=37.617", OpenMeteo.CurrentUrl(55.752, 37.617));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prev;
        }
    }
}
