using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>Small adapter so a tool is one descriptor plus one delegate.</summary>
public sealed class DelegateTool : ITool
{
    private readonly ToolDescriptor _descriptor;
    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ToolResult>> _invoke;

    public DelegateTool(ToolDescriptor descriptor, Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ToolResult>> invoke)
    {
        _descriptor = descriptor;
        _invoke = invoke;
    }

    public ToolDescriptor Describe() => _descriptor;
    public Task<ToolResult> InvokeAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct = default) => _invoke(args, ct);
}

public static class BuiltinTools
{
    public const string TargetTasksAdd = "tasks.add";
    public const string TargetTasksComplete = "tasks.complete";
    public const string TargetKbIndex = "kb.index";

    public static IEnumerable<ITool> Create(
        ISandbox sandbox,
        IApprovalGate gate,
        ITaskStore tasks,
        IKnowledgeBase kb,
        Func<IReadOnlyList<Proposal>> pending,
        Func<IReadOnlyList<IModelProvider>> models)
    {
        yield return new DelegateTool(
            new ToolDescriptor("time.now", "Return the current local date and time.", Array.Empty<ToolParameter>(), ToolKind.Read),
            (_, _) => Task.FromResult(ToolResult.Ok0(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))));

        yield return new DelegateTool(
            new ToolDescriptor("task.list", "List the student's tasks and deadlines from the main database.", Array.Empty<ToolParameter>(), ToolKind.Read),
            async (_, ct) =>
            {
                var items = await tasks.ListAsync(ct).ConfigureAwait(false);
                if (items.Count == 0) return ToolResult.Ok0("(no tasks)");
                var text = string.Join("\n", items.Select(t =>
                    $"#{t.Id} {(t.Done ? "[x]" : "[ ]")} {t.Title}{(string.IsNullOrWhiteSpace(t.Due) ? "" : "  due " + t.Due)}"));
                return ToolResult.Ok0(text, items);
            });

        yield return new DelegateTool(
            new ToolDescriptor("task.add", "Propose a new task. It is staged in the sandbox; the student must approve it before it reaches the database.",
                new[]
                {
                    new ToolParameter("title", "string", "Task title", true),
                    new ToolParameter("due", "string", "Due date, e.g. 2026-11-20 or 'Friday'"),
                    new ToolParameter("level", "string", "long | mid | short (default short)"),
                }, ToolKind.Write),
            async (args, _) =>
            {
                args.TryGetValue("title", out var title);
                args.TryGetValue("due", out var due);
                args.TryGetValue("level", out var rawLevel);
                if (string.IsNullOrWhiteSpace(title)) return ToolResult.Fail("title is required");

                var level = SqliteTaskStore.NormalizeLevel(rawLevel);
                var payload = JsonSerializer.Serialize(new { title, due, level });
                var p = gate.Propose($"[{level}] Add task: {title}", $"Due: {(string.IsNullOrWhiteSpace(due) ? "(none)" : due)}", TargetTasksAdd, payload);
                await sandbox.WriteSectionAsync("proposals", $"- [{p.CreatedAt:HH:mm:ss}] {p.Title}  ({p.Id})\n", CancellationToken.None).ConfigureAwait(false);
                await sandbox.AppendThoughtAsync($"proposed task '{title}' -> {p.Id}", CancellationToken.None).ConfigureAwait(false);
                return ToolResult.Ok0($"staged as proposal {p.Id}: \"{title}\". Waiting for the student to confirm.", p);
            });

        yield return new DelegateTool(
            new ToolDescriptor("task.complete", "Propose marking a task as done. Requires confirmation.",
                new[] { new ToolParameter("id", "int", "Task id from task.list", true) }, ToolKind.Write),
            async (args, _) =>
            {
                if (!args.TryGetValue("id", out var raw) || !int.TryParse(raw, out var id))
                    return ToolResult.Fail("id must be an integer");
                var items = await tasks.ListAsync().ConfigureAwait(false);
                var target = items.FirstOrDefault(t => t.Id == id);
                if (target is null) return ToolResult.Fail($"no task #{id}");
                var payload = JsonSerializer.Serialize(new { id });
                var p = gate.Propose($"Complete task #{id}: {target.Title}", "Mark as done", TargetTasksComplete, payload);
                await sandbox.AppendThoughtAsync($"proposed completing #{id} -> {p.Id}").ConfigureAwait(false);
                return ToolResult.Ok0($"staged as proposal {p.Id}. Waiting for confirmation.");
            });

        yield return new DelegateTool(
            new ToolDescriptor("sandbox.write", "Write one section of the AI's own sandbox document. This is free writing: no approval needed, text only.",
                new[]
                {
                    new ToolParameter("section", "string", "brief | notes | proposals | output", true),
                    new ToolParameter("content", "string", "Full replacement text for that section", true),
                }, ToolKind.Write),
            async (args, ct) =>
            {
                args.TryGetValue("section", out var section);
                args.TryGetValue("content", out var content);
                if (string.IsNullOrWhiteSpace(section)) return ToolResult.Fail("section is required");
                await sandbox.WriteSectionAsync(section!, content ?? "", ct).ConfigureAwait(false);
                return ToolResult.Ok0($"wrote section '{section}' ({content?.Length ?? 0} chars) to {sandbox.DocumentPath}");
            });

        yield return new DelegateTool(
            new ToolDescriptor("sandbox.read", "Read the sandbox document, or one section of it.",
                new[] { new ToolParameter("section", "string", "Optional section name") }, ToolKind.Read),
            async (args, ct) =>
            {
                args.TryGetValue("section", out var section);
                var text = await sandbox.ReadAsync(string.IsNullOrWhiteSpace(section) ? null : section, ct).ConfigureAwait(false);
                return ToolResult.Ok0(text);
            });

        yield return new DelegateTool(
            new ToolDescriptor("kb.search", "Search the curated knowledge base for passages relevant to a query.",
                new[]
                {
                    new ToolParameter("query", "string", "Search text", true),
                    new ToolParameter("topk", "int", "How many passages (default 5)"),
                }, ToolKind.Read),
            async (args, ct) =>
            {
                args.TryGetValue("query", out var query);
                if (string.IsNullOrWhiteSpace(query)) return ToolResult.Fail("query is required");
                var topK = args.TryGetValue("topk", out var k) && int.TryParse(k, out var n) ? n : 5;
                var hits = await kb.SearchAsync(query!, topK, ct).ConfigureAwait(false);
                if (hits.Count == 0) return ToolResult.Ok0("(no matches)");
                var text = string.Join("\n\n", hits.Select(h => $"[{h.Source}] score={h.Score:0.00}\n{h.Snippet}"));
                return ToolResult.Ok0(text, hits);
            });

        yield return new DelegateTool(
            new ToolDescriptor("kb.index", "Propose adding a document to the curated knowledge base. Requires confirmation.",
                new[]
                {
                    new ToolParameter("source", "string", "Source label or path", true),
                    new ToolParameter("text", "string", "Document text", true),
                }, ToolKind.Write),
            async (args, _) =>
            {
                args.TryGetValue("source", out var source);
                args.TryGetValue("text", out var text);
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(text))
                    return ToolResult.Fail("source and text are required");
                var payload = JsonSerializer.Serialize(new { source, text });
                var p = gate.Propose($"Index into knowledge base: {source}", $"{text!.Length} chars", TargetKbIndex, payload);
                await sandbox.AppendThoughtAsync($"proposed indexing '{source}' -> {p.Id}").ConfigureAwait(false);
                return ToolResult.Ok0($"staged as proposal {p.Id}. Waiting for confirmation.");
            });

        yield return new DelegateTool(
            new ToolDescriptor("proposal.list", "List pending proposals that are waiting for the student's confirmation.", Array.Empty<ToolParameter>(), ToolKind.Read),
            (_, _) =>
            {
                var pendingItems = pending();
                if (pendingItems.Count == 0) return Task.FromResult(ToolResult.Ok0("(no pending proposals)"));
                var text = string.Join("\n", pendingItems.Select(p => $"{p.Id}  {p.Title}  -> {p.Target}"));
                return Task.FromResult(ToolResult.Ok0(text, pendingItems));
            });

        yield return new DelegateTool(
            new ToolDescriptor("model.list", "List the model providers the system can use right now.", Array.Empty<ToolParameter>(), ToolKind.Read),
            (_, _) =>
            {
                var list = models();
                var text = list.Count == 0
                    ? "(none configured; a standby provider answers)"
                    : string.Join("\n", list.Select(m => $"{m.Id}  role={m.Role}  available={m.IsAvailable}"));
                return Task.FromResult(ToolResult.Ok0(text));
            });
    }
}
