using System.Windows;
using System.Windows.Threading;

namespace Stillbar.App.Tests;

/// <summary>
/// One STA thread running a WPF dispatcher with an Application that has the app's design resources merged,
/// shared by all view-model tests. Tests hand their body to <see cref="RunAsync"/>; awaits inside it resume on
/// the dispatcher, exactly as in the app.
/// </summary>
public sealed class WpfHost : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready = new();

    public WpfHost()
    {
        _thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var part in new[] { "Design/Controls.xaml", "Design/WeatherIcons.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Stillbar;component/{part}", UriKind.Absolute),
                });
            _ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "wpf-test-host" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = _ready.Task.GetAwaiter().GetResult();
    }

    public Dispatcher Dispatcher { get; }

    public Task RunAsync(Func<Task> body) => Dispatcher.InvokeAsync(body).Task.Unwrap();

    public Task RunAsync(Action body) => Dispatcher.InvokeAsync(body).Task;

    /// <summary>Lets queued dispatcher work (bindings, InvokeAsync from services) run. Call from inside RunAsync.</summary>
    public static async Task Settle(int ms = 30) => await Task.Delay(ms);

    public void Dispose() => Dispatcher.InvokeShutdown();
}

[CollectionDefinition("wpf")]
public sealed class WpfCollection : ICollectionFixture<WpfHost>;
