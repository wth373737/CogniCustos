using System.Text;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

/// <summary>
/// File-backed sandbox partition. Design borrowed from dsh-plugin-sandbox-mode:
/// one document is the single source of truth, every write is an explicit section
/// replacement, and the reasoning log is append-only and separate.
/// Deliberately text-only: nothing here can be executed.
/// </summary>
public sealed class SandboxStore : ISandbox
{
    private static readonly string[] Sections = { "brief", "notes", "proposals", "output" };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SandboxStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        DocumentPath = Path.Combine(Root, "SANDBOX.md");
        ThoughtsPath = Path.Combine(Root, "THOUGHTS.md");
        if (!File.Exists(DocumentPath)) File.WriteAllText(DocumentPath, Render(new Dictionary<string, string>()), Encoding.UTF8);
        if (!File.Exists(ThoughtsPath)) File.WriteAllText(ThoughtsPath, "# THOUGHTS\n\n", Encoding.UTF8);
    }

    public string Root { get; }
    public string DocumentPath { get; }
    public string ThoughtsPath { get; }

    public async Task AppendThoughtAsync(string text, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var line = $"- [{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {text.Replace("\r", " ").Replace("\n", " ")}\n";
            await File.AppendAllTextAsync(ThoughtsPath, line, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteSectionAsync(string section, string content, CancellationToken ct = default)
    {
        section = (section ?? "").Trim().ToLowerInvariant();
        if (!Sections.Contains(section))
            throw new ArgumentException($"section must be one of: {string.Join(", ", Sections)}");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Parse(await File.ReadAllTextAsync(DocumentPath, ct).ConfigureAwait(false));
            current[section] = content ?? "";
            await File.WriteAllTextAsync(DocumentPath, Render(current), Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> ReadAsync(string? section = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var text = await File.ReadAllTextAsync(DocumentPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(section)) return text;
            var parsed = Parse(text);
            return parsed.TryGetValue(section.Trim().ToLowerInvariant(), out var v) ? v : $"<no section '{section}'>";
        }
        finally { _gate.Release(); }
    }

    private static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Sections) result[s] = "";
        var lines = text.Split('\n');
        string? current = null;
        var buffer = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (current is not null) result[current] = buffer.ToString().Trim();
                buffer.Clear();
                current = line[3..].Trim().ToLowerInvariant();
            }
            else if (current is not null)
            {
                buffer.AppendLine(line);
            }
        }
        if (current is not null && result.ContainsKey(current)) result[current] = buffer.ToString().Trim();
        return result;
    }

    private static string Render(Dictionary<string, string> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SANDBOX");
        sb.AppendLine();
        sb.AppendLine("> Text-only workspace owned by the AI. Not executable. The AI may flag anything here as possibly hallucinated.");
        sb.AppendLine();
        foreach (var s in Sections)
        {
            sb.AppendLine($"## {s}");
            sb.AppendLine();
            sb.AppendLine(sections.TryGetValue(s, out var v) ? v : "");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
