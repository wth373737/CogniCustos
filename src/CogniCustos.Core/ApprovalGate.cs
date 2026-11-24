using System.Text.Json;
using System.Text.Json.Serialization;
using CogniCustos.Contracts;
using Microsoft.Data.Sqlite;

namespace CogniCustos.Core;

/// <summary>
/// The confirmation handler (docs/PARTITION-MODEL.md §1.3 and §6.2).
///
/// - a proposal is a **change set**: one card, one decision, one transaction;
/// - database targets are applied inside one SQLite transaction, so a set lands
///   completely or not at all;
/// - a failed set stays Pending, ready for the user to look at it again;
/// - proposals live in the database; <c>proposals.json</c> is an export for humans,
///   never read back.
/// </summary>
public sealed class ApprovalGate : IApprovalGate
{
    private readonly string _connectionString;
    private readonly string _exportPath;
    private readonly object _sync = new();

    /// <summary>Applied inside the set's transaction (main database targets).</summary>
    private readonly Dictionary<string, Func<SqliteConnection, SqliteTransaction, string, ToolResult>> _dbAppliers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Applied outside a transaction (file-backed targets, e.g. the knowledge base).</summary>
    private readonly Dictionary<string, Func<string, ToolResult>> _appliers =
        new(StringComparer.OrdinalIgnoreCase);

    public ApprovalGate(string dbPath, string? exportPath = null)
    {
        var full = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full }.ToString();
        _exportPath = exportPath ?? Path.Combine(dir ?? ".", "proposals.json");

        using var conn = Open();
        SqliteSchema.EnsureUpgraded(conn);
        Export();
    }

    // ---------- registration ----------

    public void RegisterApplier(string target, Func<string, ToolResult> apply)
    {
        lock (_sync) _appliers[target] = apply;
    }

    /// <summary>Register a target that can be applied inside the change set's transaction.</summary>
    public void RegisterTransactionalApplier(string target,
        Func<SqliteConnection, SqliteTransaction, string, ToolResult> apply)
    {
        lock (_sync) _dbAppliers[target] = apply;
    }

    // ---------- proposing ----------

    public Proposal Propose(string title, string summary, string target, string payload) =>
        ProposeSet(title, summary, [new Change(target, payload)]);

    public Proposal ProposeSet(string title, string summary, IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0) throw new ArgumentException("a proposal needs at least one change", nameof(changes));

        var proposal = new Proposal(
            Id: NewId(),
            Title: title,
            Summary: summary,
            Changes: changes.ToList(),
            Status: ProposalStatus.Pending,
            CreatedAt: DateTimeOffset.Now);

        lock (_sync)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO proposals(id, title, summary, status, created_at, decided_at)
                    VALUES ($id, $t, $s, 'Pending', $c, NULL);
                    """;
                cmd.Parameters.AddWithValue("$id", proposal.Id);
                cmd.Parameters.AddWithValue("$t", title);
                cmd.Parameters.AddWithValue("$s", summary);
                cmd.Parameters.AddWithValue("$c", proposal.CreatedAt.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            for (var i = 0; i < changes.Count; i++)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO proposal_changes(proposal_id, ord, target, payload)
                    VALUES ($p, $o, $t, $l);
                    """;
                cmd.Parameters.AddWithValue("$p", proposal.Id);
                cmd.Parameters.AddWithValue("$o", i);
                cmd.Parameters.AddWithValue("$t", changes[i].Target);
                cmd.Parameters.AddWithValue("$l", changes[i].Payload);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            Export();
        }

        return proposal;
    }

    // ---------- queries ----------

    public IReadOnlyList<Proposal> All()
    {
        lock (_sync)
        {
            using var conn = Open();
            return Query(conn, null, null);
        }
    }

    public IReadOnlyList<Proposal> Pending()
    {
        lock (_sync)
        {
            using var conn = Open();
            return Query(conn, ProposalStatus.Pending, null);
        }
    }

    public Proposal? Get(string id)
    {
        lock (_sync)
        {
            using var conn = Open();
            var list = Query(conn, null, id);
            return list.Count > 0 ? list[0] : null;
        }
    }

    // ---------- deciding ----------

    public ToolResult Approve(string id)
    {
        lock (_sync)
        {
            var proposal = Get(id);
            if (proposal is null) return ToolResult.Fail($"no proposal '{id}'");
            if (proposal.Status != ProposalStatus.Pending)
                return ToolResult.Fail($"proposal '{id}' is already {proposal.Status}");

            var unknown = proposal.Changes.FirstOrDefault(c =>
                !_dbAppliers.ContainsKey(c.Target) && !_appliers.ContainsKey(c.Target));
            if (unknown is not null)
                return ToolResult.Fail($"no applier registered for target '{unknown.Target}'");

            // A file-backed target cannot be rolled back, so it may not share a set.
            if (proposal.Changes.Count > 1 && proposal.Changes.Any(c => !_dbAppliers.ContainsKey(c.Target)))
                return ToolResult.Fail(
                    "a change set with a file-backed target cannot be applied atomically; propose it alone");

            using var conn = Open();
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var change in proposal.Changes)
                {
                    var result = _dbAppliers.TryGetValue(change.Target, out var dbApply)
                        ? dbApply(conn, tx, change.Payload)
                        : _appliers[change.Target](change.Payload);

                    if (!result.Ok)
                    {
                        tx.Rollback();
                        return ToolResult.Fail($"change '{change.Target}' failed: {result.Content}; nothing was written");
                    }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE proposals SET status = 'Approved', decided_at = $d WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$d", DateTimeOffset.Now.ToString("O"));
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                return ToolResult.Fail($"change set failed: {ex.Message}; nothing was written");
            }

            Export();
            return ToolResult.Ok0($"approved {id} ({proposal.Changes.Count} change(s))");
        }
    }

    public ToolResult Reject(string id)
    {
        lock (_sync)
        {
            var proposal = Get(id);
            if (proposal is null) return ToolResult.Fail($"no proposal '{id}'");
            if (proposal.Status != ProposalStatus.Pending)
                return ToolResult.Fail($"proposal '{id}' is already {proposal.Status}");

            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE proposals SET status = 'Rejected', decided_at = $d WHERE id = $id;";
                cmd.Parameters.AddWithValue("$d", DateTimeOffset.Now.ToString("O"));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }

            Export();
            return ToolResult.Ok0($"rejected {id}");
        }
    }

    // ---------- preview ----------

    /// <summary>
    /// The rough dry run: render every change, then apply the database ones inside a
    /// transaction that is always rolled back. A change that would fail is reported
    /// before the user decides (docs/PARTITION-MODEL.md §6.3).
    /// </summary>
    public ProposalPreview Preview(string id)
    {
        lock (_sync)
        {
            var proposal = Get(id);
            if (proposal is null)
                return new ProposalPreview(false, [], $"no proposal '{id}'");

            var narratives = proposal.Changes.Select(ChangeRenderer.Describe).ToList();
            var renderedProblem = narratives.FirstOrDefault(n => !n.Ok)?.Problem;
            if (renderedProblem is not null)
                return new ProposalPreview(false, narratives, renderedProblem);

            if (proposal.Changes.Any(c => !_dbAppliers.ContainsKey(c.Target)))
                return new ProposalPreview(true, narratives, "文件型目标：无法预演（确认时会直接执行）");

            using var conn = Open();
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var change in proposal.Changes)
                {
                    var result = _dbAppliers[change.Target](conn, tx, change.Payload);
                    if (!result.Ok)
                        return new ProposalPreview(false, narratives, $"预演失败：{result.Content}");
                }

                return new ProposalPreview(true, narratives, null);
            }
            catch (Exception ex)
            {
                return new ProposalPreview(false, narratives, $"预演异常：{ex.Message}");
            }
            finally
            {
                try { tx.Rollback(); } catch { /* already gone */ }
            }
        }
    }

    // ---------- storage ----------

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static List<Proposal> Query(SqliteConnection conn, ProposalStatus? status, string? id)
    {
        var proposals = new List<Proposal>();
        var changesByProposal = new Dictionary<string, List<Change>>(StringComparer.Ordinal);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, title, summary, status, created_at FROM proposals
                WHERE ($s IS NULL OR status = $s) AND ($id IS NULL OR id = $id)
                ORDER BY created_at DESC;
                """;
            cmd.Parameters.AddWithValue("$s", status is null ? DBNull.Value : status.ToString());
            cmd.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pid = reader.GetString(0);
                proposals.Add(new Proposal(
                    pid,
                    reader.GetString(1),
                    reader.GetString(2),
                    [],
                    Enum.TryParse<ProposalStatus>(reader.GetString(3), out var parsed) ? parsed : ProposalStatus.Pending,
                    DateTimeOffset.TryParse(reader.GetString(4), out var at) ? at : DateTimeOffset.MinValue));
                changesByProposal[pid] = [];
            }
        }

        if (proposals.Count == 0) return proposals;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT proposal_id, target, payload FROM proposal_changes
                WHERE proposal_id IN (SELECT id FROM proposals
                    WHERE ($s IS NULL OR status = $s) AND ($id IS NULL OR id = $id))
                ORDER BY proposal_id, ord;
                """;
            cmd.Parameters.AddWithValue("$s", status is null ? DBNull.Value : status.ToString());
            cmd.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pid = reader.GetString(0);
                if (changesByProposal.TryGetValue(pid, out var list))
                    list.Add(new Change(reader.GetString(1), reader.GetString(2)));
            }
        }

        return proposals
            .Select(p => p with { Changes = changesByProposal.TryGetValue(p.Id, out var list) ? list : [] })
            .ToList();
    }

    /// <summary>Human-readable export; never read back.</summary>
    private void Export()
    {
        try
        {
            var json = JsonSerializer.Serialize(All(), new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            });
            File.WriteAllText(_exportPath, json);
        }
        catch
        {
            // the export is a convenience; failing to write it must not hide a decision
        }
    }

    private static string NewId() =>
        "p" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("0") + "-" + Guid.NewGuid().ToString("N")[..4];
}
