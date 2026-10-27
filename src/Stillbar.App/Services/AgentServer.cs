using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Stillbar.Core.Agents;

namespace Stillbar.App.Services;

/// <summary>
/// A permission request waiting for an answer from the pill. Decide(true/false) answers Claude Code; Decide(null)
/// or the timeout means «no decision», and Claude Code asks in its own window as usual.
/// </summary>
public sealed class PermissionTicket(string sessionId)
{
    private readonly TaskCompletionSource<bool?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string SessionId { get; } = sessionId;
    public bool IsOpen => !_answer.Task.IsCompleted;
    public Task<bool?> Answer => _answer.Task;

    /// <summary>Raised once when the ticket closes without an answer from the pill.</summary>
    public event Action? Expired;

    public void Decide(bool? allow)
    {
        if (_answer.TrySetResult(allow) && allow is null) Expired?.Invoke();
    }
}

/// <summary>One hook event, and for a permission request the ticket to answer it with.</summary>
public sealed record AgentMessage(HookEvent Event, PermissionTicket? Ticket);

public interface IAgentHub
{
    /// <summary>Raised on a background thread.</summary>
    event EventHandler<AgentMessage>? Received;
}

/// <summary>
/// The pipe «Stillbar.Agents» that «Stillbar.exe --hook» talks to: one line of JSON in ({"event", "payload"}),
/// and for PermissionRequest one line back (the decision JSON, or empty for «ask in Claude Code»).
/// The pipe is open to the current user only. Stillbar answers before Claude Code's hook timeout.
/// </summary>
public sealed class AgentServer(string? pipeName = null) : IAgentHub, IDisposable
{
    /// <summary>«Stillbar.Agents»; tests set STILLBAR_PIPE so they never meet the Stillbar the user is running.</summary>
    public static string PipeName => Environment.GetEnvironmentVariable("STILLBAR_PIPE") is { Length: > 0 } p ? p : "Stillbar.Agents";
    /// <summary>Under Claude Code's 30 s hook timeout, with room for the client to start and write.</summary>
    public static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(24);

    private readonly CancellationTokenSource _stop = new();

    public event EventHandler<AgentMessage>? Received;

    public void Start() => _ = Task.Run(AcceptLoop);

    public void Dispose() => _stop.Cancel();

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName ?? PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException ex)
            {
                Log.Warn("agents", $"pipe: {ex.Message}");
                await Task.Delay(1000);
                continue;
            }
            _ = Task.Run(() => Serve(pipe)); // next client can connect while this one waits for an answer
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(_stop.Token);
            if (line is null) return;
            using var doc = JsonDocument.Parse(line);
            var name = doc.RootElement.GetProperty("event").GetString() ?? "";
            var payload = doc.RootElement.GetProperty("payload").GetString() ?? "";
            if (HookEvent.Parse(name, payload) is not { } e) return;

            if (e.Event != HookSettings.AnswerEvent)
            {
                Received?.Invoke(this, new AgentMessage(e, null));
                return;
            }

            var ticket = new PermissionTicket(e.SessionId);
            Received?.Invoke(this, new AgentMessage(e, ticket));
            var answered = await Task.WhenAny(ticket.Answer, Task.Delay(AnswerWindow, _stop.Token));
            if (answered != ticket.Answer) ticket.Decide(null);
            var reply = ticket.Answer.Result is { } allow ? AgentBoard.Decision(allow) : "";
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(reply);
            await writer.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
        {
            Log.Warn("agents", ex.Message);
        }
    }
}

/// <summary>
/// «Stillbar.exe --hook EventName»: what Claude Code runs for each hook. Reads Claude Code's JSON from stdin,
/// passes it to the running Stillbar and, for a permission request, prints its answer. Runs before any WPF
/// start-up, so it is quick, and it always exits 0: when Stillbar is not running it does nothing at all, and
/// Claude Code carries on as if there were no hook.
/// </summary>
public static class HookClient
{
    public static int Run(string eventName)
    {
        try
        {
            // Claude Code writes UTF-8. Console.In would decode with the console code page (1251/866 here) and
            // turn «Видео» into «Р'РёРґРµРѕ», so read the raw bytes.
            string payload;
            using (var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))) payload = stdin.ReadToEnd();
            using var pipe = new NamedPipeClientStream(".", AgentServer.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(300);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new { @event = eventName, payload }));
            if (eventName != HookSettings.AnswerEvent) return 0;

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var reply = reader.ReadLineAsync().WaitAsync(AgentServer.AnswerWindow + TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(reply))
            {
                using var stdout = Console.OpenStandardOutput();
                var bytes = new UTF8Encoding(false).GetBytes(reply);
                stdout.Write(bytes);
            }
        }
        catch (Exception)
        {
            // Stillbar not running, pipe busy, a timeout: say nothing, Claude Code goes on as usual.
        }
        return 0;
    }
}
