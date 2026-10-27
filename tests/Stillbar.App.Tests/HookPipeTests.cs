using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Stillbar.App.Services;

namespace Stillbar.App.Tests;

/// <summary>
/// The real «Stillbar.exe --hook» against an AgentServer on a private pipe (STILLBAR_PIPE), exactly as Claude Code
/// would run it: JSON on stdin, the decision on stdout, exit code 0. The user's Stillbar and ~/.claude are not involved.
/// </summary>
public class HookPipeTests
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "Stillbar.exe");

    private static (int Code, string Stdout, TimeSpan Took) RunHook(string pipe, string ev, string json)
    {
        // UTF-8 both ways, as Claude Code (Node) does — not the console code page.
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("--hook");
        psi.ArgumentList.Add(ev);
        psi.Environment["STILLBAR_PIPE"] = pipe;
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(json);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(40_000), "hook did not exit");
        return (p.ExitCode, stdout, sw.Elapsed);
    }

    private const string Request = """
    { "session_id": "s1", "cwd": "C:\\code\\stillbar", "hook_event_name": "PermissionRequest",
      "tool_name": "Bash", "tool_input": { "command": "dotnet test" } }
    """;

    [Fact]
    public void Without_stillbar_the_hook_is_silent_and_quick()
    {
        var (code, stdout, took) = RunHook("Stillbar.Test." + Guid.NewGuid().ToString("N"), "PermissionRequest", Request);
        Assert.Equal(0, code);
        Assert.Equal("", stdout);
        Assert.True(took < TimeSpan.FromSeconds(3), $"took {took}");
    }

    [Theory]
    [InlineData(true, "allow")]
    [InlineData(false, "deny")]
    public async Task Answer_from_the_pill_reaches_claude_code(bool allow, string behavior)
    {
        var pipe = "Stillbar.Test." + Guid.NewGuid().ToString("N");
        using var server = new AgentServer(pipe);
        AgentMessage? got = null;
        server.Received += (_, m) => { got = m; m.Ticket?.Decide(allow); };
        server.Start();
        await Task.Delay(200);

        var (code, stdout, _) = await Task.Run(() => RunHook(pipe, "PermissionRequest", Request));

        Assert.Equal(0, code);
        Assert.Equal("Bash: dotnet test", got!.Event.Detail);
        Assert.Equal(behavior, JsonNode.Parse(stdout)!["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>());
    }

    [Fact]
    public async Task Russian_text_arrives_intact()
    {
        var pipe = "Stillbar.Test." + Guid.NewGuid().ToString("N");
        using var server = new AgentServer(pipe);
        var seen = new TaskCompletionSource<string>();
        server.Received += (_, m) => seen.TrySetResult(m.Event.Detail + "|" + m.Event.Project);
        server.Start();
        await Task.Delay(200);
        await Task.Run(() => RunHook(pipe, "UserPromptSubmit",
            """{ "session_id": "s1", "cwd": "C:\\код\\пилюля", "hook_event_name": "UserPromptSubmit", "prompt": "Видео с экрана «ёлка»" }"""));
        Assert.Equal("Видео с экрана «ёлка»|пилюля", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Observed_events_do_not_wait_for_anything()
    {
        var pipe = "Stillbar.Test." + Guid.NewGuid().ToString("N");
        using var server = new AgentServer(pipe);
        var seen = new TaskCompletionSource<string>();
        server.Received += (_, m) => seen.TrySetResult(m.Event.Event + "|" + m.Event.Detail);
        server.Start();
        await Task.Delay(200);

        var (code, stdout, _) = await Task.Run(() => RunHook(pipe, "PreToolUse",
            """{ "session_id": "s1", "cwd": "C:\\x", "hook_event_name": "PreToolUse", "tool_name": "Read", "tool_input": { "file_path": "C:\\x\\a.cs" } }"""));

        Assert.Equal(0, code);
        Assert.Equal("", stdout);
        Assert.Equal("PreToolUse|Read: a.cs", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
