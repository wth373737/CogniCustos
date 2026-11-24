using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// Turns a change into the sentence a human confirms (docs/PARTITION-MODEL.md §6.4:
/// render the fields that are about to be written, not a summary).
///
/// The renderer never applies anything: it only reads the payload, so a malformed
/// payload shows up as a problem here instead of after the user pressed confirm.
/// </summary>
public static class ChangeRenderer
{
    public static ChangeNarrative Describe(Change change)
    {
        try
        {
            // the constants, not literals: "plans.add" vs "plan.add" is exactly the kind
            // of typo that would leave a card saying "unknown change" while applying fine
            return change.Target.ToLowerInvariant() switch
            {
                BuiltinTools.TargetTasksAdd => TasksAdd(change),
                PlannerTools.TargetPlanAdd => PlanAdd(change),
                PlannerTools.TargetScheduleAdd => ScheduleAdd(change),
                BuiltinTools.TargetTasksComplete => TasksComplete(change),
                BuiltinTools.TargetKbIndex => KbIndex(change),
                _ => new ChangeNarrative(change.Target, change.Target, $"未知变更类型 {change.Target}", "目标没有对应的渲染规则"),
            };
        }
        catch (Exception ex)
        {
            return new ChangeNarrative(change.Target, change.Target, $"无法解析 {change.Target}", ex.Message);
        }
    }

    private static ChangeNarrative TasksAdd(Change change)
    {
        using var doc = JsonDocument.Parse(change.Payload);
        var title = Required(doc, "title");
        var due = Optional(doc, "due");
        var level = Optional(doc, "level") ?? "short";
        var line = $"新增任务「{title}」" + (due is null ? "" : $"，截止 {due}") + $"（{Level(level)}）";
        return new ChangeNarrative(change.Target, "任务", line, null);
    }

    private static ChangeNarrative PlanAdd(Change change)
    {
        using var doc = JsonDocument.Parse(change.Payload);
        var title = Required(doc, "title");
        var level = Optional(doc, "level") ?? "short";
        var due = Optional(doc, "due");
        var parent = doc.RootElement.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32().ToString()
            : null;
        var line = $"新增计划「{title}」（{Level(level)}）"
                   + (due is null ? "" : $"，截止 {due}")
                   + (parent is null ? "" : $"，属于 #{parent}");
        return new ChangeNarrative(change.Target, "计划", line, null);
    }

    private static ChangeNarrative ScheduleAdd(Change change)
    {
        using var doc = JsonDocument.Parse(change.Payload);
        var date = Required(doc, "date");
        var start = Required(doc, "start");
        var end = Required(doc, "end");
        var title = Required(doc, "title");
        var day = DateTime.TryParse(date, out var parsed) ? parsed.ToString("MM/dd") : date;
        return new ChangeNarrative(change.Target, "日程", $"日程 {day} {start}–{end}「{title}」", null);
    }

    private static ChangeNarrative TasksComplete(Change change)
    {
        using var doc = JsonDocument.Parse(change.Payload);
        var id = doc.RootElement.GetProperty("id").GetInt32();
        return new ChangeNarrative(change.Target, "完成", $"完成任务 #{id}", null);
    }

    private static ChangeNarrative KbIndex(Change change)
    {
        using var doc = JsonDocument.Parse(change.Payload);
        var source = Required(doc, "source");
        var text = Optional(doc, "text") ?? "";
        return new ChangeNarrative(change.Target, "知识库", $"收录到知识库「{source}」（{text.Length} 字）", null);
    }

    private static string Required(JsonDocument doc, string name)
    {
        if (!doc.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"缺少字段 '{name}'");

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException($"字段 '{name}' 为空");
        return text;
    }

    private static string? Optional(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Level(string level) => level.ToLowerInvariant() switch
    {
        "long" => "长期",
        "mid" => "中期",
        "short" => "短期",
        _ => $"未知级别 {level}",
    };
}
