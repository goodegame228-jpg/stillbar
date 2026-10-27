using Stillbar.Core.Weather;

namespace Stillbar.Tests;

public class ForecastTests
{
    private const string Json = """
    {
      "current": { "time": "2026-10-27T14:00", "temperature_2m": 7.5, "weather_code": 3, "is_day": 1 },
      "hourly": {
        "time": ["2026-10-27T14:00","2026-10-27T15:00","2026-10-27T16:00","2026-10-27T17:00"],
        "temperature_2m": [7.5, 7.0, null, 5.0],
        "weather_code": [3, 61, 63, 3],
        "precipitation_probability": [10, 80, 90, 20],
        "precipitation": [0.0, 0.8, 2.1, 0.0],
        "is_day": [1, 1, 1, 0]
      },
      "daily": {
        "time": ["2026-10-27","2026-10-28"],
        "weather_code": [63, 0],
        "temperature_2m_max": [8.4, 11.6],
        "temperature_2m_min": [-0.6, 3.1],
        "precipitation_probability_max": [90, 0]
      },
      "minutely_15": {
        "time": ["2026-10-27T14:15","2026-10-27T14:30","2026-10-27T14:45","2026-10-27T15:00"],
        "precipitation": [0.0, 0.0, 0.3, 0.6]
      }
    }
    """;

    private static readonly DateTime Now = new(2026, 10, 27, 14, 5, 0);

    [Fact]
    public void Parses_now_hours_days_and_quarters()
    {
        var f = ForecastApi.Parse(Json)!;
        Assert.Equal(7.5, f.Now.TemperatureC);
        Assert.Equal(4, f.Hours.Count);
        Assert.Equal(80, f.Hours[1].PrecipChance);
        Assert.Equal(0, f.Hours[2].TemperatureC);          // null in the API → fallback, not a crash
        Assert.False(f.Hours[3].IsDay);
        Assert.Equal(2, f.Days.Count);
        Assert.Equal("−1°", WeatherText.Degrees(f.Days[0].MinC));
        Assert.Equal(4, f.Quarters.Count);
    }

    [Fact]
    public void Missing_blocks_are_empty_not_an_error()
    {
        var f = ForecastApi.Parse("""{ "current": { "temperature_2m": 1, "weather_code": 0, "is_day": 1 } }""")!;
        Assert.Empty(f.Hours);
        Assert.Empty(f.Days);
        Assert.Null(RainWarning.Upcoming(f, Now));
    }

    [Fact]
    public void No_current_means_no_forecast() => Assert.Null(ForecastApi.Parse("""{ "hourly": {} }"""));

    [Fact]
    public void Rain_warning_uses_the_quarter_when_rain_starts()
    {
        var f = ForecastApi.Parse(Json)!;
        var start = RainWarning.Upcoming(f, Now);
        Assert.Equal(new DateTime(2026, 10, 27, 14, 45, 0), start);
        var (title, detail, sentence) = RainWarning.Text(f, start!.Value, Now);
        Assert.Equal("Скоро дождь", title);
        Assert.Equal("через 40 мин · 80 %", detail);            // the wet hour's chance, not the dry hour's 10 %
        Assert.Equal("Скоро дождь: около 14:45, примерно через 40 мин, вероятность 80 %", sentence);
    }

    [Fact]
    public void Rain_warning_falls_back_to_hours_without_quarters()
    {
        var f = ForecastApi.Parse(Json)! with { Quarters = [] };
        Assert.Equal(new DateTime(2026, 10, 27, 15, 0, 0), RainWarning.Upcoming(f, Now));
    }

    [Fact]
    public void Rain_too_far_ahead_is_not_announced_yet()
    {
        var f = ForecastApi.Parse(Json)! with { Quarters = [] };
        Assert.Null(RainWarning.Upcoming(f, new DateTime(2026, 10, 27, 13, 0, 0).AddMinutes(-30)));
    }

    [Fact]
    public void No_warning_while_it_already_rains()
    {
        var f = ForecastApi.Parse(Json)!;
        f = f with { Now = f.Now with { Code = 61 } };
        Assert.Null(RainWarning.Upcoming(f, Now));
    }

    [Fact]
    public void Snow_is_called_snow()
    {
        var f = ForecastApi.Parse(Json)!;
        f = f with { Hours = f.Hours.Select(h => h with { Code = 73 }).ToList() };
        Assert.Equal("Скоро снег", RainWarning.Text(f, new DateTime(2026, 10, 27, 15, 0, 0), Now).Title);
    }

    [Theory]
    [InlineData(0, "Сегодня")]
    [InlineData(1, "Завтра")]
    [InlineData(4, "Сб 31")]
    public void Day_names(int plus, string expected) => Assert.Equal(expected, WeatherText.DayName(Now.Date.AddDays(plus), Now));
}
