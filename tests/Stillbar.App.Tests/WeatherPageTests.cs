using Stillbar.App.Services;
using Stillbar.App.Shell;
using Stillbar.Core.Pill;
using Stillbar.Core.Weather;

namespace Stillbar.App.Tests;

/// <summary>The forecast page and the rain alert, on a fixed forecast and the fake clock.</summary>
[Collection("wpf")]
public class WeatherPageTests(WpfHost host)
{
    private sealed class FakeForecastWeather(Forecast f) : IWeatherService
    {
        public WeatherNow? Current => Forecast?.Now;
        public Forecast? Forecast { get; set; } = f;
        public event EventHandler? Changed;
        public void Set(Forecast? f) { Forecast = f; Changed?.Invoke(this, EventArgs.Empty); }
    }

    // The rig's clock starts at 12:00 (+3). Rain begins at 12:45 by quarter, 13:00 by hour.
    private static Forecast Sample(DateTime noon, bool rain = true)
    {
        var hours = Enumerable.Range(0, 24).Select(i => new HourForecast(noon.AddHours(i), 5 + i % 6, rain && i is 1 or 2 ? 61 : 3, true, rain && i is 1 or 2 ? 80 : 0, rain && i is 1 or 2 ? 1 : 0)).ToList();
        var days = Enumerable.Range(0, 7).Select(i => new DayForecast(noon.Date.AddDays(i), 10 - i, 2 - i, 3, i * 10)).ToList();
        var quarters = new[] { 15, 30, 45, 60 }.Select(m => new QuarterPrecip(noon.AddMinutes(m), rain && m >= 45 ? 0.5 : 0)).ToList();
        return new Forecast(new WeatherNow(5, 3, true), hours, days, quarters);
    }

    private static (BarViewModel Vm, Rig R, FakeForecastWeather W) Make(bool rain = true)
    {
        var r = new Rig();
        var w = new FakeForecastWeather(Sample(r.Now.LocalDateTime.Date.AddHours(r.Now.LocalDateTime.Hour), rain));
        int chimes = 0;
        var vm = new BarViewModel(new BarServices(
            new FakeMedia(), new FakeVolume(), w, new FakeLyrics(), new FakeDownloads(), new FakeDock(), new FakeScreen(),
            Clock: () => r.Now, Chime: () => chimes++, CityName: () => "Казань"), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        return (vm, r, w);
    }

    [Fact]
    public Task Page_lists_24_hours_and_7_days() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make(rain: false);
        Assert.True(vm.HasForecast);
        Assert.Equal(24, vm.HourRows.Count);
        Assert.Equal("Сейчас", vm.HourRows[0].Label);
        Assert.Equal(7, vm.DayRows.Count);
        Assert.Equal(["Сегодня", "Завтра"], vm.DayRows.Take(2).Select(d => d.Name));
        Assert.Equal("Казань", vm.WeatherPlace);
        Assert.Equal("Облачно", vm.WeatherSummary);
        Assert.False(vm.HasRainNote);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Day_bars_share_one_scale() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make(rain: false);
        // Week: min −4 (day 7), max 10 (day 1). Day 1 runs 2..10, so it ends at the right edge.
        var first = vm.DayRows[0];
        Assert.Equal(150, first.BarOffset.Left + first.BarWidth, 3);
        Assert.Equal(0, vm.DayRows[6].BarOffset.Left, 3);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Rain_soon_takes_the_pill_once_and_forecast_opens_the_page() => host.RunAsync(async () =>
    {
        var (vm, r, _) = Make();
        Assert.Equal(PillKind.Alert, vm.PillKind);
        Assert.Equal("Скоро дождь", vm.AlertTitle);
        Assert.Equal("через 45 мин · 80 %", vm.AlertDetail);
        Assert.Equal("Прогноз", vm.AlertSecondaryLabel);
        Assert.True(vm.HasRainNote);

        int expanded = 0;
        vm.ExpandRequested += (_, _) => expanded++;
        vm.AlertSecondaryCommand.Execute(null);
        Assert.Equal(1, expanded);
        Assert.True(vm.IsWeatherPage);
        Assert.NotEqual(PillKind.Alert, vm.PillKind);

        r.Advance(TimeSpan.FromMinutes(10)); // the same rain is not announced twice
        Assert.NotEqual(PillKind.Alert, vm.PillKind);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Warnings_can_be_turned_off() => host.RunAsync(async () =>
    {
        var (vm, r, w) = Make(rain: false);
        vm.RainWarningsEnabled = false;
        w.Set(Sample(r.Now.LocalDateTime.Date.AddHours(r.Now.LocalDateTime.Hour)));
        await WpfHost.Settle();
        Assert.NotEqual(PillKind.Alert, vm.PillKind);
        Assert.True(vm.HasRainNote); // the page still says it
    });

    [Fact]
    public Task Done_dismisses_the_rain_alert() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make();
        vm.DismissAlertCommand.Execute(null);
        Assert.NotEqual(PillKind.Alert, vm.PillKind);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Losing_the_forecast_leaves_the_page() => host.RunAsync(async () =>
    {
        var (vm, _, w) = Make(rain: false);
        vm.IsWeatherPage = true;
        w.Set(null);
        await WpfHost.Settle();
        Assert.False(vm.HasForecast);
        Assert.True(vm.IsHomePage);
    });
}
