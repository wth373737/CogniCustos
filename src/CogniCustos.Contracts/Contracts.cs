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

/// <summary>A change the AI wants to make. It lives in the sandbox until a human approves it.</summary>
public sealed record Proposal(
    string Id,
    string Title,
    string Summary,
    string Target,
    string Payload,
    ProposalStatus Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// The confirmation handler. The AI can only propose; the application applies.
/// This is the one rule the whole architecture is built around.
/// </summary>
public interface IApprovalGate
{
    Proposal Propose(string title, string summary, string target, string payload);
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

public sealed record TaskItem(int Id, string Title, string? Due, bool Done);

public interface ITaskStore
{
    Task<int> AddAsync(string title, string? due, CancellationToken ct = default);
    Task<IReadOnlyList<TaskItem>> ListAsync(CancellationToken ct = default);
    Task<bool> CompleteAsync(int id, CancellationToken ct = default);
}

public sealed record KnowledgeHit(string Source, string Snippet, double Score);

public interface IKnowledgeBase
{
    Task IndexAsync(string source, string text, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default);
}

public sealed record Reminder(string Id, string Text, DateTimeOffset DueAt, bool Fired);
