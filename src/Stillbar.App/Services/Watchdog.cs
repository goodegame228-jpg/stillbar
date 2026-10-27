using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Stillbar.Core.Health;

namespace Stillbar.App.Services;

/// <summary>
/// Restarts Stillbar when its window stops answering (UI thread stuck for 15 s) or when it crashes. The new
/// process is started with <c>--after &lt;pid&gt;</c> and waits for this one to be gone before it takes the
/// single-instance lock. At most 3 restarts in 10 minutes: past that, it stays closed and says so in the log.
/// </summary>
public sealed class Watchdog : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BudgetWindow = TimeSpan.FromMinutes(10);
    private const int BudgetMax = 3;

    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stillbar", "restarts.txt");

    private readonly Dispatcher _ui;
    private readonly HangDetector _detector = new(Limit, Every);
    private readonly Thread _thread;
    private readonly object _gate = new();
    private volatile bool _stopped;

    public Watchdog(Dispatcher ui)
    {
        _ui = ui;
        _thread = new Thread(Run) { IsBackground = true, Name = "Stillbar watchdog", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    public void Dispose() => _stopped = true;

    private void Run()
    {
        while (!_stopped)
        {
            bool hung;
            lock (_gate) hung = _detector.Check(DateTimeOffset.Now);
            if (hung)
            {
                RestartNow("the window stopped answering for 15 s");
                return;
            }
            _ui.BeginInvoke(DispatcherPriority.Background, () => { lock (_gate) _detector.Pong(DateTimeOffset.Now); });
            Thread.Sleep(Every);
        }
    }

    /// <summary>Starts a fresh Stillbar (if the budget allows) and ends this process at once, without running any more of its code.</summary>
    public static void RestartNow(string why)
    {
        var now = DateTimeOffset.Now;
        var past = ReadHistory();
        if (!RestartBudget.CanRestart(past, now, BudgetMax, BudgetWindow))
        {
            Log.Warn("watchdog", $"{why}; not restarting: already restarted {BudgetMax} times in {BudgetWindow.TotalMinutes} min");
        }
        else
        {
            WriteHistory(RestartBudget.Record(past, now, BudgetWindow));
            try
            {
                var exe = Environment.ProcessPath!;
                var start = new ProcessStartInfo(exe, $"--after {Environment.ProcessId}") { UseShellExecute = false };
                start.Environment.Remove("STILLBAR_TEST_HANG"); // the manual test hangs once, not in a loop
                Process.Start(start);
                Log.Warn("watchdog", $"{why}; restarted");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warn("watchdog", $"{why}; restart failed: {ex.Message}");
            }
        }
        // A stuck UI thread would never let a normal shutdown finish.
        Process.GetCurrentProcess().Kill();
    }

    /// <summary>Called by a process started with --after: lets the old one finish dying first (up to 5 s).</summary>
    public static void WaitFor(int pid)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(5000);
        }
        catch (ArgumentException) { } // already gone
    }

    private static List<DateTimeOffset> ReadHistory()
    {
        try
        {
            return File.Exists(HistoryPath)
                ? File.ReadAllLines(HistoryPath).Select(l => DateTimeOffset.TryParse(l, out var t) ? t : (DateTimeOffset?)null)
                    .OfType<DateTimeOffset>().ToList()
                : [];
        }
        catch (IOException) { return []; }
    }

    private static void WriteHistory(IEnumerable<DateTimeOffset> times)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllLines(HistoryPath, times.Select(t => t.ToString("O")));
        }
        catch (IOException) { }
    }
}
