using System.Globalization;
using System.Text.Json;
using CogniCustos.Contracts;
using CogniCustos.Core;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Sample;

/// <summary>
/// Maps the real runtime onto the shell: tasks become the reminder line, schedule
/// blocks become the plan card, pending proposals become the incoming card and the
/// sandbox notes become the note card. Confirming goes through the approval gate, so
/// the write is the same one the runtime's own rules allow.
/// </summary>
public sealed class RuntimeShellDataSource : IShellDataSource
{
    private readonly CogniCustosRuntime _rt;
    private readonly List<string> _notes = [];

    public RuntimeShellDataSource(CogniCustosRuntime runtime)
    {
        _rt = runtime;
        SeedIfEmpty();
        LoadNotes();
    }

    public string DataRoot => _rt.Root;

    // ---------- reminder line ----------

    public IReadOnlyList<ReminderItem> Today()
    {
        var today = DateTime.Today;
        var items = new List<ReminderItem>();

        foreach (var task in _rt.Tasks.ListAsync().GetAwaiter().GetResult().Where(t => !t.Done))
        {
            var urgent = IsTodayOrOverdue(task.Due, today);
            items.Add(new ReminderItem(Describe(task), urgent));
        }

        if (items.Count == 0)
        {
            items.Add(new ReminderItem("No open plan items yet — confirm something from the incoming card.", false));
        }

        return items;
    }

    // ---------- plan card ----------

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) CalendarCard()
    {
        var from = DateTime.Today;
        var to = from.AddDays(14);
        var blocks = _rt.Schedule
            .ListRangeAsync(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"))
            .GetAwaiter().GetResult();

        var items = blocks
            .OrderBy(b => b.Date, StringComparer.Ordinal)
            .ThenBy(b => b.Start, StringComparer.Ordinal)
            .Select(b => new CardItem(b.Title, FormatWhen(b.Date, b.Start, b.End)) { Ref = b.Id.ToString() })
            .ToList();

        return (from.ToString("yy/MM/dd"), "Nothing planned in the next two weeks.", items);
    }

    // ---------- incoming card ----------

    /// <summary>
    /// One proposal is one card entry with one row per landing point: the line is what
    /// would really be written, and the dry run says whether it would go through.
    /// </summary>
    public (string Title, string Empty, IReadOnlyList<CardItem> Items) PendingCard()
    {
        var items = new List<CardItem>();

        foreach (var proposal in _rt.Gate.Pending())
        {
            var preview = _rt.Gate.Preview(proposal.Id);

            var count = preview.Changes.Count;
            for (var i = 0; i < count; i++)
            {
                var change = preview.Changes[i];
                var status = i switch
                {
                    0 when preview.Problem is not null && !preview.Ok => "✗ " + preview.Problem,
                    0 => preview.Problem ?? "✓ 可确认",
                    _ => null,
                };

                var head = i == 0
                    ? (count > 1 ? $"{proposal.Title} · 共 {count} 处写入" : proposal.Title)
                    : string.Empty;

                items.Add(new CardItem(change.Line, head, change.Label, status) { Ref = proposal.Id });
            }
        }

        return ("Incoming", "Nothing waiting for a decision.", items);
    }

    // ---------- note card ----------

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) NoteCard()
    {
        LoadNotes();
        var items = _notes
            .Select((text, index) => new CardItem(text) { Ref = index.ToString(CultureInfo.InvariantCulture) })
            .ToList();

        return ("Notes", "The sandbox notes section is empty.", items);
    }

    // ---------- actions ----------

    public string? IncomingConfirmed(CardItem item)
    {
        if (string.IsNullOrEmpty(item.Ref)) return "this row has no proposal behind it";

        var result = _rt.Gate.Approve(item.Ref);
        return result.Ok ? null : result.Content;
    }

    public string? IncomingIgnored(CardItem item)
    {
        if (string.IsNullOrEmpty(item.Ref)) return "this row has no proposal behind it";

        var result = _rt.Gate.Reject(item.Ref);
        return result.Ok ? null : result.Content;
    }

    public void NoteEdited(CardItem item, string text)
    {
        if (!int.TryParse(item.Ref, out var index) || index < 0 || index >= _notes.Count) return;

        _notes[index] = text;
        _rt.Sandbox.WriteSectionAsync("notes", string.Join("\n\n", _notes)).GetAwaiter().GetResult();
    }

    public string LocalModelStatus() => _rt.LocalModels.StatusText();

    public string? EnableOllama(string model)
    {
        var (ok, message) = _rt.LocalModels.UseOllama(model);
        return ok ? null : message;
    }

    public string? InstallBuiltIn(IProgress<double> progress)
    {
        var (ok, message) = _rt.LocalModels.InstallBuiltInAsync(progress).GetAwaiter().GetResult();
        return ok ? null : message;
    }

    public string? DeleteBuiltIn(out string message)
    {
        var ok = _rt.LocalModels.DeleteBuiltIn(out message);
        return ok ? null : message;
    }

    public (string Model, string Url, bool Configured) CloudSettings()
    {
        var cloud = _rt.CloudModels;
        return (cloud.Model, cloud.BaseUrl, cloud.IsConfigured);
    }

    public string? SaveCloud(string model, string url, string apiKey)
    {
        var (ok, message) = _rt.CloudModels.UseCloud(model, url, apiKey);
        return ok ? null : message;
    }

    public bool GpuEnabled() => _rt.LocalModels.UseGpu;

    public string? SetGpu(bool enabled)
    {
        _rt.LocalModels.UseGpu = enabled;
        return null;
    }

    public string? SwitchToBuiltIn()
    {
        var (ok, message) = _rt.SwitchToBuiltIn();
        return ok ? null : message;
    }

    public async Task<string?> AskAsync(string instruction)
    {
        var answer = await _rt.ChatAsync(instruction);
        if (string.IsNullOrWhiteSpace(answer)) return null;

        var line = answer.Trim().Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    // ---------- helpers ----------

    private static bool IsTodayOrOverdue(string? due, DateTime today)
    {
        if (string.IsNullOrWhiteSpace(due)) return false;
        if (!DateTime.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
        return date.Date <= today;
    }

    private static string Describe(TaskItem task)
    {
        if (string.IsNullOrWhiteSpace(task.Due)) return task.Title;
        return DateTime.TryParse(task.Due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)
            ? $"{task.Title} — due {due:MM/dd}"
            : task.Title;
    }

    private static string FormatWhen(string date, string start, string end)
    {
        var day = DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("MM/dd", CultureInfo.InvariantCulture)
            : date;
        return $"{day} {Clock(start)} – {Clock(end)}";
    }

    private static string Clock(string hhmm)
    {
        if (!TimeOnly.TryParse(hhmm, CultureInfo.InvariantCulture, out var time)) return hhmm;

        var hour = time.Hour % 12;
        if (hour == 0) hour = 12;
        var suffix = time.Hour < 12 ? "am" : "pm";
        return time.Minute == 0 ? $"{hour}{suffix}" : $"{hour}:{time.Minute:00}{suffix}";
    }

    /// <summary>"From x · File y" → ("From x", "File y").</summary>
    private static (string? Source, string? File) SplitSummary(string summary)
    {
        var parts = summary.Split('·', 2, StringSplitOptions.TrimEntries);
        var source = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : null;
        var file = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        return (source, file);
    }

    private void LoadNotes()
    {
        _notes.Clear();
        var text = _rt.Sandbox.ReadAsync("notes").GetAwaiter().GetResult();
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (paragraph.Length > 0) _notes.Add(paragraph);
        }

        if (_notes.Count == 0)
        {
            _notes.Add("Type below to talk to the runtime, or confirm an incoming item to turn it into a plan.");
            _rt.Sandbox.WriteSectionAsync("notes", string.Join("\n\n", _notes)).GetAwaiter().GetResult();
        }
    }

    /// <summary>First run: give the window something real to look at and to decide on.</summary>
    private void SeedIfEmpty()
    {
        var tasks = _rt.Tasks.ListAsync().GetAwaiter().GetResult();
        if (tasks.Count == 0)
        {
            _rt.Tasks.AddPlannedAsync("Review AKP202w chapters 3-4", DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"), "short", null)
                .GetAwaiter().GetResult();
            _rt.Tasks.AddPlannedAsync("Draft the group project outline", DateTime.Today.AddDays(3).ToString("yyyy-MM-dd"), "mid", null)
                .GetAwaiter().GetResult();
        }

        var blocks = _rt.Schedule
            .ListRangeAsync(DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.AddDays(14).ToString("yyyy-MM-dd"))
            .GetAwaiter().GetResult();
        if (blocks.Count == 0)
        {
            var day = DateTime.Today.AddDays(5).ToString("yyyy-MM-dd");
            _rt.Schedule.AddAsync(day, "15:00", "17:00", "AKP202w Organizational Behavior Test 1", null)
                .GetAwaiter().GetResult();
        }

        if (_rt.Gate.Pending().Count == 0)
        {
            var date = DateTime.Today.AddDays(6).ToString("yyyy-MM-dd");
            var payload = JsonSerializer.Serialize(new
            {
                date,
                start = "15:00",
                end = "17:00",
                title = "AKP202w Organizational Behavior Test 1",
            });

            _rt.Gate.Propose(
                "AKP202w Organizational Behavior Test 1",
                "From elearning.usm.my · File Teaching Plan AKP202W 2026-2027",
                PlannerTools.TargetScheduleAdd,
                payload);
        }
    }
}
