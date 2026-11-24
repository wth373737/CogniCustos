using System.Security.Cryptography;
using System.Text;
using CogniCustos.Contracts;
using Microsoft.Data.Sqlite;

namespace CogniCustos.Core;

/// <summary>
/// The raw store: what was fetched, kept as bytes on disk and indexed by content hash
/// (docs/PARTITION-MODEL.md §1.2 and §6.6).
///
/// - content addressed: the same text put twice is one artifact;
/// - append only: there is no update or delete here on purpose;
/// - the R1 summary always exists, even with no model available, so the rest of the
///   pipeline can run offline (summary_kind = 'fallback' in that case).
/// </summary>
public sealed class SqliteRawStore : IRawStore
{
    private const int SummaryChars = 200;

    private readonly string _stateDir;
    private readonly string _connectionString;

    public SqliteRawStore(string stateDir)
    {
        _stateDir = Path.GetFullPath(stateDir);
        Directory.CreateDirectory(_stateDir);
        Directory.CreateDirectory(Path.Combine(_stateDir, "raw"));

        var dbPath = Path.Combine(_stateDir, "cognicustos.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        using var conn = Open();
        SqliteSchema.EnsureUpgraded(conn);
    }

    public string Root => Path.Combine(_stateDir, "raw");

    public RawArtifact Put(string kind, string source, string text, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("nothing to store", nameof(text));

        var hash = Hash(text);
        using var conn = Open();

        var existing = FindByHash(conn, hash);
        if (existing is not null) return existing;

        var relative = Path.Combine("raw", hash[..2], hash + ".txt");
        var absolute = Path.Combine(_stateDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, text, Encoding.UTF8);

        var fallbackTitle = string.IsNullOrWhiteSpace(title) ? FirstLine(text, source) : title.Trim();
        var summary = FallbackSummary(text);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO raw_artifacts(hash, kind, source, title, summary, summary_kind, bytes_path, chars, fetched_at, status)
            VALUES ($h, $k, $s, $t, $m, 'fallback', $p, $c, $f, 'kept');
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$t", fallbackTitle);
        cmd.Parameters.AddWithValue("$m", summary);
        cmd.Parameters.AddWithValue("$p", relative);
        cmd.Parameters.AddWithValue("$c", text.Length);
        cmd.Parameters.AddWithValue("$f", DateTimeOffset.Now.ToString("O"));

        var id = Convert.ToInt32(cmd.ExecuteScalar());
        return new RawArtifact(
            id.ToString(), hash, kind, source, fallbackTitle, summary, "fallback",
            relative, text.Length, DateTimeOffset.Now, "kept");
    }

    public RawArtifact? Get(string id) => int.TryParse(id, out var key) ? Read(Open(), key) : null;

    public IReadOnlyList<RawArtifact> List(int limit = 20)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} ORDER BY id DESC LIMIT {Math.Max(1, limit)};";
        return ReadAll(cmd);
    }

    /// <summary>Only summaries are searchable: R1 is discoverable, the bytes are not.</summary>
    public IReadOnlyList<RawArtifact> SearchSummaries(string query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SelectColumns}
            WHERE title LIKE $q OR summary LIKE $q OR source LIKE $q
            ORDER BY id DESC LIMIT {Math.Max(1, limit)};
            """;
        cmd.Parameters.AddWithValue("$q", "%" + query.Trim() + "%");
        return ReadAll(cmd);
    }

    /// <summary>The bytes are readable by the application only, never by a retrieval tool.</summary>
    public string? ReadText(string id)
    {
        var artifact = Get(id);
        if (artifact?.BytesPath is null) return null;

        var path = Path.Combine(_stateDir, artifact.BytesPath);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    // ---------- helpers ----------

    private const string SelectColumns = """
        SELECT id, hash, kind, source, title, summary, summary_kind, bytes_path, chars, fetched_at, status
        FROM raw_artifacts
        """;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static RawArtifact? FindByHash(SqliteConnection conn, string hash)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE hash = $h;";
        cmd.Parameters.AddWithValue("$h", hash);
        var rows = ReadAll(cmd);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static RawArtifact? Read(SqliteConnection conn, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var rows = ReadAll(cmd);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static List<RawArtifact> ReadAll(SqliteCommand cmd)
    {
        var rows = new List<RawArtifact>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RawArtifact(
                reader.GetInt32(0).ToString(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8),
                DateTimeOffset.TryParse(reader.GetString(9), out var at) ? at : DateTimeOffset.MinValue,
                reader.GetString(10)));
        }

        return rows;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Deterministic R1 summary: no model, no hallucination, runs offline.</summary>
    private static string FallbackSummary(string text)
    {
        var flat = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= SummaryChars ? flat : flat[..SummaryChars] + "…";
    }

    private static string FirstLine(string text, string source)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed.Length <= 80 ? trimmed : trimmed[..80];
        }

        return source;
    }
}
