using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stillbar.Core.Agents;

public enum AgentState { Working, Asking, Waiting, Done }

/// <summary>One Claude Code session as the pill sees it.</summary>
public sealed record AgentSession(string Id, string Project, AgentState State, string Detail, DateTimeOffset Since, DateTimeOffset Updated);

/// <summary>A hook event from Claude Code: the event name and the JSON Claude Code sent on stdin.</summary>
public sealed record HookEvent(string Event, string SessionId, string Project, string Detail, string? NotificationType)
{
    /// <summary>Reads Claude Code's hook JSON. Unknown or broken input gives null (and is ignored).</summary>
    public static HookEvent? Parse(string eventName, string json)
    {
        try
        {
            var o = JsonNode.Parse(json)?.AsObject();
            if (o is null) return null;
            string Str(string k) => o[k]?.GetValueKind() == JsonValueKind.String ? o[k]!.GetValue<string>() : "";
            var name = Str("hook_event_name") is { Length: > 0 } n ? n : eventName;
            var session = Str("session_id");
            if (session.Length == 0) return null;
            var cwd = Str("cwd");
            var project = ProjectName(cwd, Str("session_title"));
            string detail = name switch
            {
                "UserPromptSubmit" => OneLine(Str("prompt"), 60),
                "PreToolUse" or "PermissionRequest" => ToolText(Str("tool_name"), o["tool_input"] as JsonObject),
                "Notification" => OneLine(Str("message"), 80),
                "Stop" => OneLine(Str("last_assistant_message"), 100),
                _ => "",
            };
            return new HookEvent(name, session, project, detail, Str("notification_type") is { Length: > 0 } t ? t : null);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The folder the session works in, as a short name. The Claude desktop app runs sessions without a project in
    /// scratch folders («scratch-2026-10-27-278310»): those are called by the session's title if it has one, else
    /// nothing (the pill then says «Claude»).
    /// </summary>
    public static string ProjectName(string cwd, string sessionTitle = "")
    {
        var folder = cwd.Length > 0 ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : "";
        bool scratch = folder.StartsWith("scratch-", StringComparison.OrdinalIgnoreCase) || cwd.Contains("scratch-workspaces", StringComparison.OrdinalIgnoreCase);
        if (!scratch) return folder;
        return sessionTitle.Trim() is { Length: > 0 } title ? OneLine(title, 30) : "";
    }

    /// <summary>«Bash: npm test», «Edit: BarView.xaml», «WebFetch: docs.claude.com».</summary>
    public static string ToolText(string tool, JsonObject? input)
    {
        if (tool.Length == 0) return "";
        string? S(string k) => input?[k]?.GetValueKind() == JsonValueKind.String ? input[k]!.GetValue<string>() : null;
        var what = tool switch
        {
            "Bash" or "PowerShell" => S("command"),
            "Edit" or "Write" or "Read" or "MultiEdit" or "NotebookEdit" => S("file_path") is { } p ? Path.GetFileName(p) : null,
            "WebFetch" => S("url") is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : null,
            "WebSearch" => S("query"),
            "Grep" or "Glob" => S("pattern"),
            "Task" or "Agent" => S("description"),
            _ => null,
        };
        var name = tool.StartsWith("mcp__", StringComparison.Ordinal) ? tool.Split("__").Last() : tool;
        return what is { Length: > 0 } ? $"{name}: {OneLine(what, 70)}" : name;
    }

    internal static string OneLine(string s, int max)
    {
        var line = string.Join(' ', s.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }
}

/// <summary>
/// All Claude Code sessions that reported in, and what the pill should say. Asking (a permission request) beats
/// waiting for input, which beats working; a finished session is shown once and then forgotten. Sessions that go
/// quiet for 10 minutes are dropped (Claude Code was closed without a SessionEnd).
/// </summary>
public sealed class AgentBoard
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, AgentSession> _sessions = [];

    public IReadOnlyCollection<AgentSession> Sessions => _sessions.Values;

    /// <summary>Applies an event; returns the session after it (null if it ended), and whether it just finished a turn.</summary>
    public (AgentSession? Session, bool Finished) Apply(HookEvent e, DateTimeOffset now)
    {
        _sessions.TryGetValue(e.SessionId, out var s);
        var project = e.Project.Length > 0 ? e.Project : s?.Project ?? "";
        AgentSession Set(AgentState state, string detail)
        {
            var since = s is not null && s.State == state && state == AgentState.Working ? s.Since : now;
            return _sessions[e.SessionId] = new AgentSession(e.SessionId, project, state, detail, since, now);
        }

        switch (e.Event)
        {
            case "SessionEnd":
                _sessions.Remove(e.SessionId);
                return (null, false);
            case "UserPromptSubmit":
                // Until Claude picks up a tool, the pill shows what it was asked.
                return (Set(AgentState.Working, e.Detail.Length > 0 ? $"«{e.Detail}»" : "думает"), false);
            case "PreToolUse":
            case "PostToolUse":
                return (Set(AgentState.Working, e.Detail), false);
            case "PermissionRequest":
                return (Set(AgentState.Asking, e.Detail), false);
            case "Notification" when e.NotificationType is "idle_prompt" or "agent_needs_input" or "elicitation_dialog":
                return (Set(AgentState.Waiting, "ждёт вашего ответа"), false);
            case "Notification" when e.NotificationType is "permission_prompt" && s?.State != AgentState.Asking:
                return (Set(AgentState.Asking, s?.Detail ?? "спрашивает разрешение"), false);
            case "Stop":
                var done = Set(AgentState.Done, e.Detail);
                return (done, true);
            default:
                return (s, false);
        }
    }

    /// <summary>After an answer from the pill the session works again (Claude Code sends no event for that).</summary>
    public void Answered(string sessionId, DateTimeOffset now)
    {
        if (_sessions.TryGetValue(sessionId, out var s) && s.State == AgentState.Asking)
            _sessions[sessionId] = s with { State = AgentState.Working, Since = now, Updated = now };
    }

    /// <summary>What the pill shows: the most urgent live session, or null. Done sessions are not shown here (they get a toast).</summary>
    public AgentSession? Current(DateTimeOffset now)
    {
        foreach (var dead in _sessions.Values.Where(x => now - x.Updated > Stale).Select(x => x.Id).ToList())
            _sessions.Remove(dead);
        return _sessions.Values
            .Where(x => x.State != AgentState.Done)
            .OrderBy(x => x.State switch { AgentState.Asking => 0, AgentState.Waiting => 1, _ => 2 })
            .ThenByDescending(x => x.Updated)
            .FirstOrDefault();
    }

    /// <summary>The answer Claude Code expects from a PermissionRequest hook.</summary>
    public static string Decision(bool allow) => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "PermissionRequest",
            decision = allow
                ? (object)new { behavior = "allow" }
                : new { behavior = "deny", message = "Запрещено в Stillbar" },
        },
    });
}
