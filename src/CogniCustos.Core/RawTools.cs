using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// Read-only access to the raw store for the model (docs/PARTITION-MODEL.md §3.1):
/// it may see that something exists and read its summary (R1), but never the bytes.
/// There is no tool for <see cref="IRawStore.ReadText"/> on purpose.
/// </summary>
public static class RawTools
{
    public static IEnumerable<ITool> Create(IRawStore raw) =>
    [
        new RawListTool(raw),
        new RawSearchTool(raw),
    ];

    private sealed class RawListTool(IRawStore raw) : ITool
    {
        public ToolDescriptor Describe() => new(
            "raw.list",
            "List recently fetched material in the raw store (information side). Returns summaries only, never the original bytes.",
            [new ToolParameter("limit", "number", "how many entries (default 10)")],
            ToolKind.Read);

        public Task<ToolResult> InvokeAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct = default)
        {
            var limit = args.TryGetValue("limit", out var text) && int.TryParse(text, out var parsed)
                ? Math.Clamp(parsed, 1, 50)
                : 10;

            var items = raw.List(limit);
            if (items.Count == 0) return Task.FromResult(ToolResult.Ok0("raw store is empty"));

            var lines = items.Select(a =>
                $"#{a.Id} {a.FetchedAt:MM-dd HH:mm} [{a.Kind}] {a.Title} — 来源 {a.Source}" +
                $"（{Visibility.Label(a.Visibility)}，摘要 {a.SummaryKind}，{a.Chars} 字）");
            return Task.FromResult(ToolResult.Ok0(string.Join('\n', lines), items));
        }
    }

    private sealed class RawSearchTool(IRawStore raw) : ITool
    {
        public ToolDescriptor Describe() => new(
            "raw.search",
            "Search raw-store summaries by keyword. This is a discovery step: the result is not confirmed material and must not be treated as a fact.",
            [new ToolParameter("query", "string", "keyword", true)],
            ToolKind.Read);

        public Task<ToolResult> InvokeAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct = default)
        {
            if (!args.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
                return Task.FromResult(ToolResult.Fail("query is required"));

            var hits = raw.SearchSummaries(query, 10);
            if (hits.Count == 0) return Task.FromResult(ToolResult.Ok0($"no raw material matches '{query}'"));

            var lines = hits.Select(a => $"#{a.Id} {a.Title} — {a.Summary}");
            return Task.FromResult(ToolResult.Ok0(
                "unconfirmed material (summaries only):\n" + string.Join('\n', lines), hits));
        }
    }
}
