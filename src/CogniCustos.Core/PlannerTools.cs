using System.Globalization;
using System.Text;
using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// Short / mid / long-term planning and the day/week calendar.
/// Every write is a proposal; the calendar and the hierarchy are read back from SQLite.
/// </summary>
public static class PlannerTools
{
    public const string TargetPlanAdd = "plans.add";
    public const string TargetScheduleAdd = "schedule.add";

    public static IEnumerable<ITool> Create(ISandbox sandbox, IApprovalGate gate, ITaskStore tasks, IScheduleStore schedule)
    {
        yield return new DelegateTool(
            new ToolDescriptor("plan.add",
                "Propose a plan item at a horizon: long (semester goal), mid (this month/week), short (today/tomorrow). Requires confirmation.",
                new[]
                {
                    new ToolParameter("title", "string", "What the plan item is", true),
                    new ToolParameter("level", "string", "long | mid | short", true),
                    new ToolParameter("due", "string", "Target date, e.g. 2026-12-27"),
                    new ToolParameter("parentId", "int", "Id of the longer-horizon item this belongs to"),
                }, ToolKind.Write),
            async (args, _) =>
            {
                args.TryGetValue("title", out var title);
                if (string.IsNullOrWhiteSpace(title)) return ToolResult.Fail("title is required");
                args.TryGetValue("level", out var rawLevel);
                if (!TryParseLevel(rawLevel, out var level))
                    return ToolResult.Fail($"level 只能是 long / mid / short（收到 '{rawLevel}'）");
                args.TryGetValue("due", out var due);
                int? parent = args.TryGetValue("parentId", out var p) && int.TryParse(p, out var pid) ? pid : null;

                var payload = JsonSerializer.Serialize(new { title, due, level, parentId = parent });
                var proposal = gate.Propose($"[{level}] {title}", $"horizon={level}" + (due is null ? "" : $", due {due}"), TargetPlanAdd, payload);
                await sandbox.AppendThoughtAsync($"proposed plan item '{title}' ({level}) -> {proposal.Id}").ConfigureAwait(false);
                return ToolResult.Ok0($"staged as proposal {proposal.Id}. Waiting for confirmation.");
            });

        yield return new DelegateTool(
            new ToolDescriptor("plan.tree", "Show the plan as three horizons: long-term goals, mid-term items, short-term actions.",
                Array.Empty<ToolParameter>(), ToolKind.Read),
            async (_, ct) =>
            {
                var items = await tasks.ListAsync(ct).ConfigureAwait(false);
                if (items.Count == 0) return ToolResult.Ok0("(no plan items)");

                var sb = new StringBuilder();
                foreach (var (level, label) in new[] { ("long", "长期"), ("mid", "中期"), ("short", "短期") })
                {
                    var group = items.Where(t => t.Level == level).ToList();
                    if (group.Count == 0) continue;
                    sb.AppendLine($"{label}（{level}，{group.Count} 项）");
                    foreach (var t in group)
                    {
                        var parent = t.ParentId is null ? "" : $"  ↳ 属于 #{t.ParentId}";
                        var due = string.IsNullOrWhiteSpace(t.Due) ? "" : $"  due {t.Due}";
                        sb.AppendLine($"  #{t.Id} {(t.Done ? "[x]" : "[ ]")} {t.Title}{due}{parent}");
                    }
                    sb.AppendLine();
                }
                return ToolResult.Ok0(sb.ToString().TrimEnd(), items);
            });

        yield return new DelegateTool(
            new ToolDescriptor("schedule.add",
                "Propose a calendar block. Requires confirmation, like every other write. "
                + "date MUST be the literal form yyyy-MM-dd (example: 2026-12-27). "
                + "start and end MUST be the literal form HH:mm (example: 09:00), never a full date-time.",
                new[]
                {
                    new ToolParameter("date", "string", "yyyy-MM-dd, e.g. 2026-12-27", true),
                    new ToolParameter("start", "string", "HH:mm, e.g. 09:00", true),
                    new ToolParameter("end", "string", "HH:mm, e.g. 11:00", true),
                    new ToolParameter("title", "string", "What happens in this block", true),
                    new ToolParameter("taskId", "int", "Optional task this block serves"),
                }, ToolKind.Write),
            async (args, ct) =>
            {
                args.TryGetValue("date", out var dateRaw);
                args.TryGetValue("start", out var startRaw);
                args.TryGetValue("end", out var endRaw);
                args.TryGetValue("title", out var title);

                if (!TryNormalizeWhen(dateRaw, startRaw, out var date, out var start, out var startNote))
                    return ToolResult.Fail($"start 无法解析为 HH:mm：'{startRaw}'");
                if (!TryNormalizeWhen(dateRaw, endRaw, out var date2, out var end, out _))
                    return ToolResult.Fail($"end 无法解析为 HH:mm：'{endRaw}'");
                if (date is null) date = date2;
                if (date is null) return ToolResult.Fail("date 缺失，且无法从 start/end 推导");
                if (string.IsNullOrWhiteSpace(title)) return ToolResult.Fail("title is required");

                // End before start means the block crosses midnight; keep it explicit rather than guessing.
                if (TimeSpan.TryParseExact(start, @"hh\:mm", CultureInfo.InvariantCulture, out var s) &&
                    TimeSpan.TryParseExact(end, @"hh\:mm", CultureInfo.InvariantCulture, out var e) &&
                    e <= s)
                    return ToolResult.Fail($"end ({end}) 必须晚于 start ({start})");

                int? taskId = args.TryGetValue("taskId", out var t) && int.TryParse(t, out var tid) ? tid : null;
                // Serialize as plain strings: a DateTime would round-trip as "...T00:00:00"
                // and never match a stored "yyyy-MM-dd" key.
                var payload = JsonSerializer.Serialize(new { date, start, end, title, taskId });
                var note = startNote is null ? "" : $"（{startNote}）";
                var proposal = gate.Propose($"Block {date} {start}-{end}: {title}", "calendar block" + note, TargetScheduleAdd, payload);
                await sandbox.AppendThoughtAsync($"proposed block {date} {start}-{end} '{title}' -> {proposal.Id}").ConfigureAwait(false);
                return ToolResult.Ok0($"staged as proposal {proposal.Id}: {date} {start}-{end} {title}. Waiting for confirmation.");
            });

        yield return new DelegateTool(
            new ToolDescriptor("schedule.day", "Show one day's calendar blocks and the tasks due that day.",
                new[] { new ToolParameter("date", "string", "yyyy-MM-dd; defaults to today") },
                ToolKind.Read),
            async (args, ct) =>
            {
                var date = args.TryGetValue("date", out var d) && TryParseDate(d, out var parsed)
                    ? parsed
                    : DateTime.Today;
                var key = date.ToString("yyyy-MM-dd");
                var blocks = await schedule.ListDayAsync(key, ct).ConfigureAwait(false);
                var dueTasks = (await tasks.ListAsync(ct).ConfigureAwait(false))
                    .Where(t => !t.Done && SameDay(t.Due, date)).ToList();

                var sb = new StringBuilder($"{key}（{WeekdayZh(date)}）\n");
                if (blocks.Count == 0) sb.AppendLine("  日程：空");
                foreach (var b in blocks)
                    sb.AppendLine($"  {b.Start}-{b.End}  {b.Title}{(b.TaskId is null ? "" : $"  (#{b.TaskId})")}");
                if (dueTasks.Count > 0)
                {
                    sb.AppendLine("  当日到期：" + string.Join("；", dueTasks.Select(t => $"#{t.Id} {t.Title}")));
                }
                return ToolResult.Ok0(sb.ToString().TrimEnd(), blocks);
            });

        yield return new DelegateTool(
            new ToolDescriptor("schedule.week", "Show the week containing a date: blocks per day plus items due.",
                new[] { new ToolParameter("date", "string", "Any date in the week, yyyy-MM-dd; defaults to today") },
                ToolKind.Read),
            async (args, ct) =>
            {
                var anchor = args.TryGetValue("date", out var d) && TryParseDate(d, out var parsed) ? parsed : DateTime.Today;
                var monday = anchor.AddDays(-((int)anchor.DayOfWeek + 6) % 7);
                var sunday = monday.AddDays(6);
                var blocks = await schedule.ListRangeAsync(monday.ToString("yyyy-MM-dd"), sunday.ToString("yyyy-MM-dd"), ct)
                    .ConfigureAwait(false);
                var items = await tasks.ListAsync(ct).ConfigureAwait(false);

                var sb = new StringBuilder($"本周 {monday:MM-dd} ~ {sunday:MM-dd}\n");
                for (var i = 0; i < 7; i++)
                {
                    var day = monday.AddDays(i);
                    var dayBlocks = blocks.Where(b => b.Date == day.ToString("yyyy-MM-dd")).ToList();
                    var dayTasks = items.Where(t => !t.Done && SameDay(t.Due, day)).ToList();
                    if (dayBlocks.Count == 0 && dayTasks.Count == 0) continue;
                    sb.AppendLine($"{day:MM-dd} {WeekdayZh(day)}");
                    foreach (var b in dayBlocks) sb.AppendLine($"   {b.Start}-{b.End} {b.Title}");
                    foreach (var t in dayTasks) sb.AppendLine($"   due #{t.Id} {t.Title}");
                }
                if (sb.Length < 40) sb.AppendLine("（本周没有安排）");
                return ToolResult.Ok0(sb.ToString().TrimEnd(), blocks);
            });
    }

    private static bool TryParseDate(string? value, out DateTime date)
        => DateTime.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryParseTime(string? value, out TimeSpan time)
        => TimeSpan.TryParseExact(value?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time);

    /// <summary>
    /// Strict on purpose: a wrong level must be reported, not silently downgraded to 'short'.
    /// Numeric indices are accepted because small models frequently emit an enum index
    /// (0 = longest horizon) instead of the word, and refusing them wastes every turn.
    /// </summary>
    private static bool TryParseLevel(string? raw, out string level)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "long" or "长期" or "long-term" or "0":
                level = "long"; return true;
            case "mid" or "medium" or "中期" or "mid-term" or "1":
                level = "mid"; return true;
            case "short" or "短期" or "2":
                level = "short"; return true;
            default:
                level = ""; return false;
        }
    }

    /// <summary>
    /// Small models put a full date-time where HH:mm belongs ("2026-12-27T09:00:00")
    /// or leave date empty. Normalise instead of failing, and say so in the proposal.
    /// </summary>
    private static bool TryNormalizeWhen(string? dateRaw, string? whenRaw, out string? date, out string time, out string? note)
    {
        date = null;
        time = "";
        note = null;

        if (TryParseDate(dateRaw, out var d)) date = d.ToString("yyyy-MM-dd");

        var s = whenRaw?.Trim();
        if (string.IsNullOrEmpty(s)) return false;

        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts) && ts < TimeSpan.FromDays(1))
        {
            time = $"{(int)ts.TotalHours:00}:{ts.Minutes:00}";
            return true;
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            time = dt.ToString("HH:mm");
            if (date is null)
            {
                date = dt.ToString("yyyy-MM-dd");
                note = $"从 '{s}' 推导日期与时间";
            }
            return true;
        }

        return false;
    }

    private static bool SameDay(string? due, DateTime day)
        => DateTime.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Date == day.Date;

    private static string WeekdayZh(DateTime d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };
}
