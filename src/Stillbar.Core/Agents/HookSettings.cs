using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stillbar.Core.Agents;

/// <summary>
/// Adds and removes Stillbar's hooks in Claude Code's ~/.claude/settings.json without touching anything else:
/// other keys, other hooks and their order stay as they were. Our entries are recognised by the
/// «--hook» argument on our own executable, so removing them never removes someone else's hook.
/// </summary>
public static class HookSettings
{
    /// <summary>Observed events run in the background (async), so they never slow Claude Code down.</summary>
    public static readonly string[] AsyncEvents = ["UserPromptSubmit", "PreToolUse", "Notification", "Stop", "SessionEnd"];

    /// <summary>Waits for an answer from the pill; on any failure Claude Code carries on and asks as usual.</summary>
    public const string AnswerEvent = "PermissionRequest";

    /// <summary>Seconds Claude Code gives the permission hook. Stillbar answers «no decision» before that.</summary>
    public const int AnswerTimeout = 30;

    public static string Install(string? json, string exePath)
    {
        var root = Parse(json);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        RemoveOurs(hooks, exePath);
        foreach (var ev in AsyncEvents.Append(AnswerEvent))
        {
            var list = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = list;
            var handler = new JsonObject
            {
                ["type"] = "command",
                ["command"] = exePath,
                ["args"] = new JsonArray("--hook", ev),
            };
            if (ev == AnswerEvent)
            {
                handler["timeout"] = AnswerTimeout;
                handler["onFailure"] = "continue"; // without Git Bash a failed permission hook would otherwise block
            }
            else
            {
                handler["async"] = true;
                handler["timeout"] = 5;
            }
            list.Add(new JsonObject { ["hooks"] = new JsonArray(handler) });
        }
        return root.ToJsonString(Options);
    }

    public static string Uninstall(string? json, string exePath)
    {
        var root = Parse(json);
        if (root["hooks"] is JsonObject hooks)
        {
            RemoveOurs(hooks, exePath);
            if (hooks.Count == 0) root.Remove("hooks");
        }
        return root.ToJsonString(Options);
    }

    public static bool IsInstalled(string? json, string exePath) =>
        Parse(json)["hooks"] is JsonObject hooks
        && hooks.Any(kv => kv.Value is JsonArray groups && groups.Any(g => Handlers(g).Any(h => IsOurs(h, exePath))));

    private static void RemoveOurs(JsonObject hooks, string exePath)
    {
        foreach (var (ev, value) in hooks.ToList())
        {
            if (value is not JsonArray groups) continue;
            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray handlers) continue;
                foreach (var h in handlers.ToList().Where(h => IsOurs(h, exePath))) handlers.Remove(h);
                if (handlers.Count == 0) groups.Remove(group);
            }
            if (groups.Count == 0) hooks.Remove(ev);
        }
    }

    private static IEnumerable<JsonNode?> Handlers(JsonNode? group) => group?["hooks"] as JsonArray ?? [];

    /// <summary>Ours: our executable (any build location ending in Stillbar.exe) with «--hook» as the first argument.</summary>
    private static bool IsOurs(JsonNode? h, string exePath)
    {
        if (h?["command"]?.GetValueKind() != JsonValueKind.String) return false;
        var cmd = h["command"]!.GetValue<string>();
        bool ourExe = string.Equals(cmd, exePath, StringComparison.OrdinalIgnoreCase)
                      || cmd.EndsWith("Stillbar.exe", StringComparison.OrdinalIgnoreCase);
        return ourExe && h["args"] is JsonArray a && a.Count > 0 && a[0]?.GetValue<string>() == "--hook";
    }

    private static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException("settings.json is not a JSON object");
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
