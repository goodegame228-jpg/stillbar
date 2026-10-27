using System.Diagnostics;
using System.Net.Http;
using Stillbar.Core.Settings;
using Stillbar.Core.Weather;

namespace Stillbar.App.Services;

public interface IWeatherService
{
    /// <summary>Null until the first answer, and again once the last answer is too old to show.</summary>
    WeatherNow? Current { get; }
    /// <summary>24 hours, 7 days and the next two hours of rain, from the same request; null until the first answer.</summary>
    Forecast? Forecast => null;
    event EventHandler? Changed;
}

/// <summary>Open-Meteo, no key. Every 15 min; one retry after 2 min on failure; hides data older than 2 h.</summary>
public sealed class OpenMeteoWeatherService : IWeatherService, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    private static readonly HttpClient Http = CreateClient();
    private CitySetting _city;
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _fetchedAt;

    public OpenMeteoWeatherService(CitySetting city) => _city = city;

    public WeatherNow? Current { get; private set; }
    public Forecast? Forecast { get; private set; }
    public event EventHandler? Changed;

    public void Start() => _ = RunAsync(_stop.Token);

    /// <summary>A new city from the settings: fetch for it at once.</summary>
    public void SetCity(CitySetting city)
    {
        _city = city;
        _ = FetchAsync(_stop.Token);
    }

    /// <summary>The settings window's city search; empty when offline or nothing matched.</summary>
    public static async Task<IReadOnlyList<Place>> SearchAsync(string name, CancellationToken ct = default)
    {
        if (name.Trim().Length < 2) return [];
        try { return OpenMeteo.ParseGeocode(await Http.GetStringAsync(OpenMeteo.GeocodeUrl(name), ct)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return []; }
    }

    public void Dispose() => _stop.Cancel();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool ok = await FetchAsync(ct);
            try { await Task.Delay(ok ? Every : RetryAfter, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> FetchAsync(CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync(ForecastApi.Url(_city.Lat, _city.Lon), ct);
            var forecast = ForecastApi.Parse(json);
            if (forecast is null) return false;
            Current = forecast.Now;
            Forecast = forecast;
            _fetchedAt = DateTimeOffset.Now;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Debug.WriteLine($"weather: {ex.Message}");
            if (Current is not null && DateTimeOffset.Now - _fetchedAt > MaxAge)
            {
                Current = null;
                Forecast = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Stillbar/0.1");
        return http;
    }
}

/// <summary>Fixed weather for --snapshot renders.</summary>
public sealed class SampleWeatherService(WeatherNow? now, Forecast? forecast = null) : IWeatherService
{
    public WeatherNow? Current { get; } = now;
    public Forecast? Forecast { get; } = forecast;
    public event EventHandler? Changed { add { } remove { } }
}
