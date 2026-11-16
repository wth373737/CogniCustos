using System.Text.Json;
using System.Text.Json.Nodes;

namespace CogniCustos.Core;

/// <summary>
/// The wire protocol between the reasoning model and the tool registry.
/// Kept deliberately tiny and strict: a model that cannot produce this JSON gets
/// its text shown verbatim instead of being guessed at.
/// </summary>
public static class AgentProtocol
{
    public enum Kind { ToolCall, Answer }

    public sealed record Call(Kind Kind, string ToolName, IReadOnlyDictionary<string, string> Args, string Text);

    public static Call? Parse(string raw, Func<string, bool>? isKnownTool = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJsonObject(raw);
        if (json is null) return null;

        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj) return null;
            var action = obj["action"]?.GetValue<string>()?.Trim();
            var toolField = obj["tool"]?.GetValue<string>()?.Trim();

            // Tolerate the shapes small models actually emit:
            //   {"action":"tool","tool":"task.add","args":{...}}   canonical
            //   {"tool":"task.add","args":{...}}                    no action
            //   {"action":"task.add","args":{...}}                  tool name in action
            //   {"action":"answer","text":"..."} / {"text":"..."}   final answer
            var toolName =
                !string.IsNullOrWhiteSpace(toolField) ? toolField
                : (!string.IsNullOrEmpty(action) && !action.Equals("tool", StringComparison.OrdinalIgnoreCase)
                    && !action.Equals("answer", StringComparison.OrdinalIgnoreCase)
                    && isKnownTool?.Invoke(action) == true) ? action
                : null;

            if (toolName is not null)
                return new Call(Kind.ToolCall, toolName, ReadArgs(obj), "");

            if (string.Equals(action, "answer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "text", StringComparison.OrdinalIgnoreCase)
                || obj["text"] is not null)
                return new Call(Kind.Answer, "", new Dictionary<string, string>(), obj["text"]?.ToString() ?? obj["answer"]?.ToString() ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static Dictionary<string, string> ReadArgs(JsonObject obj)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (obj["args"] is JsonObject argObj)
        {
            foreach (var kv in argObj)
            {
                var v = kv.Value;
                args[kv.Key] = v is null ? "" : (v is JsonValue jv ? jv.ToString() : v.ToJsonString());
            }
        }
        return args;
    }

    /// <summary>Pulls the first balanced {...} block, tolerating code fences and prose around it.</summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }
        return null;
    }
}
