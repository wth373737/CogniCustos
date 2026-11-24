using System.Text;
using CogniCustos.Contracts;
using Microsoft.Data.Sqlite;

namespace CogniCustos.Core;

/// <summary>
/// The sandbox partition (docs/PARTITION-MODEL.md §1.2): a text-only workspace the AI
/// may write freely, never executable.
///
/// Storage is itemised: every write is a row in <c>sandbox_entries</c> with a timestamp
/// and an optional source, so an earlier judgement is never silently overwritten
/// (§6.7). <c>SANDBOX.md</c> is still written, but only as a **rendered view** of the
/// latest entry per section — it is an export, not the source of truth.
/// </summary>
public sealed class SandboxStore : ISandbox
{
    private static readonly string[] Sections = { "brief", "notes", "proposals", "output" };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _connectionString;

    public SandboxStore(string root, string? dbPath = null)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        DocumentPath = Path.Combine(Root, "SANDBOX.md");
        ThoughtsPath = Path.Combine(Root, "THOUGHTS.md");

        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            var full = Path.GetFullPath(dbPath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _connectionString = new SqliteConnectionStringBuilder { DataSource = full }.ToString();

            using var conn = Open();
            SqliteSchema.EnsureUpgraded(conn);
        }

        if (!File.Exists(DocumentPath)) File.WriteAllText(DocumentPath, Render(new Dictionary<string, string>()), Encoding.UTF8);
        if (!File.Exists(ThoughtsPath)) File.WriteAllText(ThoughtsPath, "# THOUGHTS\n\n", Encoding.UTF8);
    }

    public string Root { get; }
    public string DocumentPath { get; }
    public string ThoughtsPath { get; }

    /// <summary>True when entries are recorded in the database as well as rendered to disk.</summary>
    public bool IsItemised => _connectionString is not null;

    public async Task AppendThoughtAsync(string text, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var line = $"- [{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {text.Replace("\r", " ").Replace("\n", " ")}\n";
            await File.AppendAllTextAsync(ThoughtsPath, line, Encoding.UTF8, ct).ConfigureAwait(false);
            Record("thoughts", "thought", text, null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// A section write is one entry (kind = section) that becomes the section's current
    /// text. Earlier entries stay in the table.
    /// </summary>
    public async Task WriteSectionAsync(string section, string content, CancellationToken ct = default)
    {
        section = Normalise(section);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Record(section, "section", content ?? string.Empty, null);
            await ExportAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Append one entry without replacing the section; the itemised form for findings.</summary>
    public async Task AppendEntryAsync(string section, string kind, string text, string? sourceRef = null,
        CancellationToken ct = default)
    {
        section = Normalise(section);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Record(section, kind, text, sourceRef);
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

    /// <summary>The rows behind one section, newest first (application use; not a tool).</summary>
    public IReadOnlyList<SandboxEntry> Entries(string section, int limit = 50)
    {
        if (_connectionString is null) return [];

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, section, kind, text, created_at, source_ref, status, visibility FROM sandbox_entries
            WHERE section = $s ORDER BY id DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$s", section.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$n", Math.Max(1, limit));

        var rows = new List<SandboxEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SandboxEntry(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.TryParse(reader.GetString(4), out var at) ? at : DateTimeOffset.MinValue,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? Visibility.Sandbox : reader.GetString(7)));
        }

        return rows;
    }

    // ---------- storage ----------

    private string Normalise(string section)
    {
        section = (section ?? string.Empty).Trim().ToLowerInvariant();
        if (!Sections.Contains(section))
            throw new ArgumentException($"section must be one of: {string.Join(", ", Sections)}");
        return section;
    }

    private void Record(string section, string kind, string text, string? sourceRef)
    {
        if (_connectionString is null) return;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sandbox_entries(section, kind, text, created_at, source_ref, status, visibility)
            VALUES ($s, $k, $t, $c, $r, 'open', 'S');
            """;
        cmd.Parameters.AddWithValue("$s", section);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", text ?? string.Empty);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$r", (object?)sourceRef ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Render the latest entry per section into SANDBOX.md (a view, not the store).</summary>
    private async Task ExportAsync(CancellationToken ct)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in Sections) sections[section] = "";

        if (_connectionString is not null)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT section, text FROM sandbox_entries
                WHERE id IN (SELECT MAX(id) FROM sandbox_entries GROUP BY section);
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var section = reader.GetString(0);
                if (sections.ContainsKey(section)) sections[section] = reader.GetString(1);
            }
        }

        await File.WriteAllTextAsync(DocumentPath, Render(sections), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // ---------- document view ----------

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
        sb.AppendLine("> Text-only workspace owned by the AI. Not executable. This file is an export of the latest entry per section.");
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
