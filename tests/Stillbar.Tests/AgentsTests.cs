using System.Text.Json.Nodes;
using Stillbar.Core.Agents;

namespace Stillbar.Tests;

public class AgentsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static HookEvent E(string name, string extra = "") => HookEvent.Parse(name,
        $$"""{ "session_id": "s1", "cwd": "C:\\code\\stillbar", "hook_event_name": "{{name}}" {{extra}} }""")!;

    [Fact]
    public void Parses_tool_events_into_short_text()
    {
        Assert.Equal("Bash: npm test", E("PreToolUse", """, "tool_name": "Bash", "tool_input": { "command": "npm test" }""").Detail);
        Assert.Equal("Edit: BarView.xaml", E("PreToolUse", """, "tool_name": "Edit", "tool_input": { "file_path": "C:\\x\\BarView.xaml" }""").Detail);
        Assert.Equal("WebFetch: docs.claude.com", E("PermissionRequest", """, "tool_name": "WebFetch", "tool_input": { "url": "https://docs.claude.com/a" }""").Detail);
        Assert.Equal("search_docs", E("PreToolUse", """, "tool_name": "mcp__docs__search_docs", "tool_input": {}""").Detail);
        Assert.Equal("stillbar", E("Stop").Project);
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("""{ "hook_event_name": "Stop" }""")] // no session
    public void Broken_input_is_ignored(string json) => Assert.Null(HookEvent.Parse("Stop", json));

    [Fact]
    public void Long_commands_are_cut_to_one_line() =>
        Assert.EndsWith("…", HookEvent.ToolText("Bash", new JsonObject { ["command"] = "echo " + new string('x', 200) + "\nnext" }));

    [Fact]
    public void A_turn_goes_working_asking_working_done()
    {
        var b = new AgentBoard();
        b.Apply(E("UserPromptSubmit", """, "prompt": "почини тесты" """), T0);
        Assert.Equal(AgentState.Working, b.Current(T0)!.State);

        b.Apply(E("PreToolUse", """, "tool_name": "Bash", "tool_input": { "command": "dotnet test" }"""), T0.AddSeconds(5));
        Assert.Equal("Bash: dotnet test", b.Current(T0.AddSeconds(5))!.Detail);
        Assert.Equal(T0, b.Current(T0.AddSeconds(5))!.Since); // still the same stretch of work

        b.Apply(E("PermissionRequest", """, "tool_name": "Bash", "tool_input": { "command": "rm -rf b" }"""), T0.AddSeconds(9));
        Assert.Equal(AgentState.Asking, b.Current(T0.AddSeconds(9))!.State);
        b.Answered("s1", T0.AddSeconds(12));
        Assert.Equal(AgentState.Working, b.Current(T0.AddSeconds(12))!.State);

        var (s, finished) = b.Apply(E("Stop", """, "last_assistant_message": "Готово, тесты зелёные." """), T0.AddSeconds(30));
        Assert.True(finished);
        Assert.Equal("Готово, тесты зелёные.", s!.Detail);
        Assert.Null(b.Current(T0.AddSeconds(30))); // done: a toast, not a pill
    }

    [Fact]
    public void Asking_beats_working_across_sessions()
    {
        var b = new AgentBoard();
        b.Apply(E("UserPromptSubmit"), T0);
        b.Apply(HookEvent.Parse("PermissionRequest", """{ "session_id": "s2", "cwd": "C:\\other", "hook_event_name": "PermissionRequest", "tool_name": "Bash", "tool_input": { "command": "ls" } }""")!, T0.AddSeconds(1));
        b.Apply(E("PreToolUse", """, "tool_name": "Read", "tool_input": {}"""), T0.AddSeconds(2));
        Assert.Equal("s2", b.Current(T0.AddSeconds(2))!.Id);
    }

    [Fact]
    public void Idle_notification_means_waiting_and_session_end_forgets()
    {
        var b = new AgentBoard();
        b.Apply(E("Stop"), T0);
        b.Apply(E("Notification", """, "notification_type": "idle_prompt", "message": "Claude is waiting" """), T0.AddMinutes(1));
        Assert.Equal(AgentState.Waiting, b.Current(T0.AddMinutes(1))!.State);
        b.Apply(E("SessionEnd"), T0.AddMinutes(2));
        Assert.Empty(b.Sessions);
    }

    [Fact]
    public void Quiet_sessions_are_dropped_after_ten_minutes()
    {
        var b = new AgentBoard();
        b.Apply(E("UserPromptSubmit"), T0);
        Assert.Null(b.Current(T0.AddMinutes(11)));
    }

    [Fact]
    public void Decision_json_matches_the_documented_shape()
    {
        var allow = JsonNode.Parse(AgentBoard.Decision(true))!;
        Assert.Equal("PermissionRequest", allow["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>());
        Assert.Equal("allow", allow["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>());
        var deny = JsonNode.Parse(AgentBoard.Decision(false))!;
        Assert.Equal("deny", deny["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- settings.json

    private const string Exe = @"C:\Apps\Stillbar\Stillbar.exe";

    [Fact]
    public void Install_adds_our_hooks_and_keeps_everything_else()
    {
        const string existing = """
        {
          "model": "opus",
          // a comment the user wrote
          "hooks": {
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "lint.sh" } ] } ]
          }
        }
        """;
        var json = HookSettings.Install(existing, Exe);
        var root = JsonNode.Parse(json)!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count);
        Assert.Equal("lint.sh", pre[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        var ours = pre[1]!["hooks"]![0]!;
        Assert.Equal(Exe, ours["command"]!.GetValue<string>());
        Assert.Equal(["--hook", "PreToolUse"], ours["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.True(ours["async"]!.GetValue<bool>());

        var perm = root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!;
        Assert.Equal("continue", perm["onFailure"]!.GetValue<string>());
        Assert.Null(perm["async"]);
        Assert.True(HookSettings.IsInstalled(json, Exe));
    }

    [Fact]
    public void Installing_twice_does_not_duplicate()
    {
        var once = HookSettings.Install(null, Exe);
        var twice = HookSettings.Install(once, Exe);
        Assert.Single(JsonNode.Parse(twice)!["hooks"]!["Stop"]!.AsArray());
    }

    [Fact]
    public void Uninstall_removes_only_ours()
    {
        var json = HookSettings.Install("""{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "notify.sh" } ] } ] } }""", Exe);
        var back = JsonNode.Parse(HookSettings.Uninstall(json, Exe))!;
        Assert.Equal("notify.sh", back["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Null(back["hooks"]!["PermissionRequest"]);
        Assert.False(HookSettings.IsInstalled(back.ToJsonString(), Exe));
        Assert.Null(JsonNode.Parse(HookSettings.Uninstall(HookSettings.Install(null, Exe), Exe))!["hooks"]); // nothing left: no empty "hooks"
    }

    [Fact]
    public void A_moved_build_is_still_recognised_as_ours()
    {
        var json = HookSettings.Install(null, @"D:\old\place\Stillbar.exe");
        Assert.True(HookSettings.IsInstalled(json, Exe));
        Assert.Null(JsonNode.Parse(HookSettings.Uninstall(json, Exe))!["hooks"]);
    }

    [Theory]
    [InlineData(@"C:\code\stillbar", "", "stillbar")]
    [InlineData(@"C:\Users\me\AppData\Local\Claude-Data\x\scratch-workspaces\a\b\scratch-2026-10-27-278310", "", "")]
    [InlineData(@"C:\Users\me\AppData\Local\Claude-Data\x\scratch-workspaces\a\b\scratch-2026-10-27-278310", "Погода в пилюле", "Погода в пилюле")]
    [InlineData("", "", "")]
    public void Project_names(string cwd, string title, string expected) => Assert.Equal(expected, HookEvent.ProjectName(cwd, title));

    [Fact]
    public void Prompt_is_shown_until_the_first_tool()
    {
        var b = new AgentBoard();
        b.Apply(E("UserPromptSubmit", """, "prompt": "о чем думаешь?\nи ещё строка" """), T0);
        Assert.Equal("«о чем думаешь? и ещё строка»", b.Current(T0)!.Detail);
        b.Apply(E("UserPromptSubmit", """, "prompt": "" """), T0);
        Assert.Equal("думает", b.Current(T0)!.Detail);
    }
}