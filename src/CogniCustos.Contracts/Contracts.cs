namespace CogniCustos.Contracts;

/// <summary>Read tools only inspect state; write tools must go through the approval gate.</summary>
public enum ToolKind { Read, Write }

/// <summary>Every model the system can reach is selected by role, never by name at the call site.</summary>
public enum ModelRole { Reasoning, Embedding, SpeechToText, Vision }

public sealed record ToolParameter(string Name, string Type, string Description, bool Required = false);

public sealed record ToolDescriptor(
    string Name,
    string Description,
    IReadOnlyList<ToolParameter> Parameters,
    ToolKind Kind);

public sealed record ToolResult(bool Ok, string Content, object? Data = null)
{
    public static ToolResult Fail(string message) => new(false, message);
    public static ToolResult Ok0(string message, object? data = null) => new(true, message, data);
}

/// <summary>
/// The single shape every capability has. This is deliberately MCP-shaped
/// (name + description + JSON-schema-ish parameters + one invoke entry point)
/// so a real MCP server can be layered on top without touching the tools.
/// </summary>
public interface ITool
{
    ToolDescriptor Describe();
    Task<ToolResult> InvokeAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct = default);
}

public interface IToolRegistry
{
    void Register(ITool tool);
    IReadOnlyList<ITool> All { get; }
    ITool? Find(string name);
    /// <summary>Invoke by name. Errors are returned as a failed ToolResult, never thrown.</summary>
    Task<ToolResult> InvokeAsync(string name, IReadOnlyDictionary<string, string> args, CancellationToken ct = default);
    /// <summary>Compact catalog injected into the reasoning prompt.</summary>
    string DescribeForPrompt();
}

public interface IModelProvider
{
    string Id { get; }
    ModelRole Role { get; }
    bool IsAvailable { get; }
    Task<string> CompleteAsync(string system, string user, CancellationToken ct = default);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

public interface IModelRouter
{
    IReadOnlyList<IModelProvider> All { get; }
    IModelProvider Resolve(ModelRole role);
}

public enum ProposalStatus { Pending, Approved, Rejected }

/// <summary>One landing point of a proposal: a target plus the payload its applier understands.</summary>
public sealed record Change(string Target, string Payload);

/// <summary>
/// A change the AI wants to make. It lives in the sandbox until a human approves it.
/// One proposal = one change set = one card, one decision, one transaction
/// (docs/PARTITION-MODEL.md §1.4, §6.2).
/// </summary>
public sealed record Proposal(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<Change> Changes,
    ProposalStatus Status,
    DateTimeOffset CreatedAt)
{
    /// <summary>Convenience for the single-change case (the card shows one line).</summary>
    public string Target => Changes.Count > 0 ? Changes[0].Target : string.Empty;

    public string Payload => Changes.Count > 0 ? Changes[0].Payload : string.Empty;

    /// <summary>True when confirming this proposal writes to more than one place.</summary>
    public bool IsChangeSet => Changes.Count > 1;
}

/// <summary>
/// The confirmation handler. The AI can only propose; the application applies.
/// This is the one rule the whole architecture is built around.
/// </summary>
/// <summary>One change rendered for the confirmation card (docs/PARTITION-MODEL.md §6.4).</summary>
public sealed record ChangeNarrative(string Target, string Label, string Line, string? Problem)
{
    public bool Ok => Problem is null;
}

/// <summary>What confirming this proposal would do, computed without writing anything.</summary>
public sealed record ProposalPreview(bool Ok, IReadOnlyList<ChangeNarrative> Changes, string? Problem)
{
    public string Text => string.Join("\n", Changes.Select(c => c.Line));
}

public interface IApprovalGate
{
    Proposal Propose(string title, string summary, string target, string payload);

    /// <summary>One card, several landing points, applied together or not at all.</summary>
    Proposal ProposeSet(string title, string summary, IReadOnlyList<Change> changes);

    /// <summary>Dry run: render every change and check it can be applied, writing nothing.</summary>
    ProposalPreview Preview(string id);
    IReadOnlyList<Proposal> All();
    IReadOnlyList<Proposal> Pending();
    Proposal? Get(string id);
    ToolResult Approve(string id);
    ToolResult Reject(string id);
    void RegisterApplier(string target, Func<string, ToolResult> apply);
}

/// <summary>The AI's own text-only workspace: free to write, never executable, always readable.</summary>
public interface ISandbox
{
    string Root { get; }
    string DocumentPath { get; }
    string ThoughtsPath { get; }
    Task AppendThoughtAsync(string text, CancellationToken ct = default);
    Task WriteSectionAsync(string section, string content, CancellationToken ct = default);
    Task<string> ReadAsync(string? section = null, CancellationToken ct = default);
}

public sealed record TaskItem(int Id, string Title, string? Due, bool Done, string Level = "short", int? ParentId = null);

public interface ITaskStore
{
    Task<int> AddAsync(string title, string? due, CancellationToken ct = default);
    Task<int> AddPlannedAsync(string title, string? due, string level, int? parentId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskItem>> ListAsync(CancellationToken ct = default);
    Task<bool> CompleteAsync(int id, CancellationToken ct = default);
}

/// <summary>One block on the calendar. Times are local and stored as "HH:mm".</summary>
public sealed record ScheduleBlock(int Id, string Date, string Start, string End, string Title, int? TaskId);

public interface IScheduleStore
{
    Task<int> AddAsync(string date, string start, string end, string title, int? taskId, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduleBlock>> ListDayAsync(string date, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduleBlock>> ListRangeAsync(string fromDate, string toDate, CancellationToken ct = default);
}

/// <summary>One itemised sandbox write: never overwritten, always timestamped.</summary>
public sealed record SandboxEntry(
    int Id,
    string Section,
    string Kind,
    string Text,
    DateTimeOffset CreatedAt,
    string? SourceRef,
    string Status);

/// <summary>One fetched artifact in the raw store: low trust, append only, never citable.</summary>
public sealed record RawArtifact(
    string Id,
    string Hash,
    string Kind,
    string Source,
    string Title,
    string Summary,
    string SummaryKind,
    string? BytesPath,
    int Chars,
    DateTimeOffset FetchedAt,
    string Status);

/// <summary>
/// The information side of the partition model. Only the application writes here;
/// retrieval may read summaries, never the bytes (docs/PARTITION-MODEL.md §3.1).
/// </summary>
public interface IRawStore
{
    string Root { get; }
    RawArtifact Put(string kind, string source, string text, string? title = null);
    RawArtifact? Get(string id);
    IReadOnlyList<RawArtifact> List(int limit = 20);
    IReadOnlyList<RawArtifact> SearchSummaries(string query, int limit = 10);
    /// <summary>Application-only path to the bytes; no tool is allowed to expose this.</summary>
    string? ReadText(string id);
}

public sealed record KnowledgeHit(string Source, string Snippet, double Score);

public interface IKnowledgeBase
{
    Task IndexAsync(string source, string text, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default);
}

public sealed record Reminder(string Id, string Text, DateTimeOffset DueAt, bool Fired);

/// <summary>Result of the local check that runs before anything is sent to a cloud model.</summary>
public sealed record SensitiveCheckResult(bool Flagged, IReadOnlyList<string> Reasons, string RedactedPreview)
{
    public static SensitiveCheckResult Clean(string text) =>
        new(false, Array.Empty<string>(), text.Length <= 200 ? text : text[..200] + "…");
}

/// <summary>
/// The privacy gatekeeper: local checks decide whether text may leave the device.
/// A cloud provider must never be reachable without passing through this.
/// </summary>
public interface ISensitiveContentChecker
{
    Task<SensitiveCheckResult> CheckAsync(string text, CancellationToken ct = default);
}

/// <summary>Every outbound attempt is recorded, including the ones that were refused.</summary>
public sealed record OutboundRecord(
    DateTimeOffset At,
    string Provider,
    string TextHash,
    int Length,
    bool Flagged,
    string Decision,
    IReadOnlyList<string> Reasons);

public interface IOutboundAudit
{
    void Record(OutboundRecord record);
    IReadOnlyList<OutboundRecord> Recent(int count = 20);
}

/// <summary>A model that leaves the device. Always wrapped by the outbound guard.</summary>
public interface ICloudModelProvider : IModelProvider
{
    string Endpoint { get; }
}

/// <summary>Search over the application's own documentation and source, so the AI can explain the system.</summary>
public interface ISelfDocs
{
    Task<int> IndexAsync(CancellationToken ct = default);
    IReadOnlyList<KnowledgeHit> Search(string query, int topK = 5);
    string Root { get; }
}
