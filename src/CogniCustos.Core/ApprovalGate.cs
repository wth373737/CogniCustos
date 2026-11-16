using System.Text.Json;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// The confirmation handler. Nothing written by the AI reaches the main database
/// or the curated knowledge base unless a human approves it here first.
/// Persisted as JSON so a restart does not silently drop pending proposals.
/// </summary>
public sealed class ApprovalGate : IApprovalGate
{
    private readonly List<Proposal> _proposals = new();
    private readonly Dictionary<string, Func<string, ToolResult>> _appliers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly object _sync = new();

    public ApprovalGate(string statePath)
    {
        _path = statePath;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Load();
    }

    public void RegisterApplier(string target, Func<string, ToolResult> apply) => _appliers[target] = apply;

    public Proposal Propose(string title, string summary, string target, string payload)
    {
        var p = new Proposal(
            Id: "p" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() + "-" + Guid.NewGuid().ToString("N")[..4],
            Title: title,
            Summary: summary,
            Target: target,
            Payload: payload,
            Status: ProposalStatus.Pending,
            CreatedAt: DateTimeOffset.Now);
        lock (_sync) { _proposals.Add(p); Save(); }
        return p;
    }

    public IReadOnlyList<Proposal> All() { lock (_sync) return _proposals.ToList(); }

    public IReadOnlyList<Proposal> Pending() { lock (_sync) return _proposals.Where(p => p.Status == ProposalStatus.Pending).ToList(); }

    public Proposal? Get(string id) { lock (_sync) return _proposals.FirstOrDefault(p => p.Id == id); }

    public ToolResult Approve(string id)
    {
        Proposal? p;
        lock (_sync)
        {
            p = _proposals.FirstOrDefault(x => x.Id == id);
            if (p is null) return ToolResult.Fail($"no proposal '{id}'");
            if (p.Status != ProposalStatus.Pending) return ToolResult.Fail($"proposal '{id}' is already {p.Status}");
        }

        if (!_appliers.TryGetValue(p.Target, out var apply))
            return ToolResult.Fail($"no applier registered for target '{p.Target}'");

        var result = apply(p.Payload);
        lock (_sync)
        {
            var idx = _proposals.FindIndex(x => x.Id == id);
            _proposals[idx] = p with { Status = result.Ok ? ProposalStatus.Approved : p.Status };
            Save();
        }
        return result;
    }

    public ToolResult Reject(string id)
    {
        lock (_sync)
        {
            var idx = _proposals.FindIndex(x => x.Id == id);
            if (idx < 0) return ToolResult.Fail($"no proposal '{id}'");
            if (_proposals[idx].Status != ProposalStatus.Pending) return ToolResult.Fail($"proposal '{id}' is already {_proposals[idx].Status}");
            _proposals[idx] = _proposals[idx] with { Status = ProposalStatus.Rejected };
            Save();
            return ToolResult.Ok0($"rejected {id}");
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_proposals, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<Proposal>>(File.ReadAllText(_path));
            if (list is not null) _proposals.AddRange(list);
        }
        catch { /* a corrupt state file must not stop the app from starting */ }
    }
}
