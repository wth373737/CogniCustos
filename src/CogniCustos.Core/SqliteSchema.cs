using Microsoft.Data.Sqlite;

namespace CogniCustos.Core;

/// <summary>
/// The one place that owns the database shape. Every store calls
/// <see cref="EnsureUpgraded"/> before using the file, and a step only ever runs
/// once: <c>PRAGMA user_version</c> remembers how far the file has got.
///
/// Rules borrowed from docs/PARTITION-MODEL.md §6:
/// - every row answers four questions (partition / author / source_ref / representation);
/// - nothing is silently defaulted: a schema step either runs or the file is newer than us.
/// </summary>
public static class SqliteSchema
{
    /// <summary>Bump this and add a step below; never edit a released step.</summary>
    public const int Version = 7;

    public static void EnsureUpgraded(SqliteConnection conn)
    {
        var version = UserVersion(conn);
        if (version > Version)
            throw new InvalidOperationException(
                $"database schema v{version} is newer than this build (v{Version})");

        if (version == Version) return;

        using var tx = conn.BeginTransaction();

        if (version < 1) Step1Baseline(conn);
        if (version < 2) Step2Provenance(conn);
        if (version < 3) Step3RawAndSandbox(conn);
        if (version < 4) Step4Proposals(conn);
        if (version < 5) Step5Visibility(conn);
        if (version < 6) Step6IngestLedger(conn);
        if (version < 7) Step7Label(conn);

        Exec(conn, $"PRAGMA user_version = {Version};");
        tx.Commit();
    }

    /// <summary>
    /// One row per message ever read, keyed by its Message-ID: the daily fetch can run
    /// twice and the same mail will not be ingested twice (docs §6.5: raw keeps what
    /// arrived, but arriving again is not new).
    /// </summary>
    private static void Step6IngestLedger(SqliteConnection conn) => Exec(conn, """
        CREATE TABLE IF NOT EXISTS ingested_messages (
            key          TEXT PRIMARY KEY,
            source       TEXT NOT NULL,
            ingested_at  TEXT NOT NULL
        );
        """);

    /// <summary>
    /// A first-pass label on raw material ("important" / "routine" / "promo"). It is what
    /// decides whether an item gets a finer summary and a proposal at all.
    /// </summary>
    private static void Step7Label(SqliteConnection conn)
    {
        if (!Columns(conn, "raw_artifacts").Contains("label"))
            Exec(conn, "ALTER TABLE raw_artifacts ADD COLUMN label TEXT;");
    }

    public static int UserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---------- v1: the tables the skeleton already had ----------

    private static void Step1Baseline(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS tasks (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                title   TEXT NOT NULL,
                due     TEXT NULL,
                done    INTEGER NOT NULL DEFAULT 0,
                created TEXT NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS schedule_blocks (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                date    TEXT NOT NULL,
                start   TEXT NOT NULL,
                end     TEXT NOT NULL,
                title   TEXT NOT NULL,
                task_id INTEGER NULL
            );
            """);

        var columns = Columns(conn, "tasks");
        if (!columns.Contains("level"))
            Exec(conn, "ALTER TABLE tasks ADD COLUMN level TEXT NOT NULL DEFAULT 'short';");
        if (!columns.Contains("parent_id"))
            Exec(conn, "ALTER TABLE tasks ADD COLUMN parent_id INTEGER NULL;");
    }

    // ---------- v2: every row answers the four questions ----------

    private static void Step2Provenance(SqliteConnection conn)
    {
        foreach (var table in new[] { "tasks", "schedule_blocks" })
        {
            var columns = Columns(conn, table);
            if (!columns.Contains("partition"))
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN partition TEXT NOT NULL DEFAULT 'main';");
            if (!columns.Contains("author"))
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN author TEXT NOT NULL DEFAULT 'user';");
            if (!columns.Contains("source_ref"))
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN source_ref TEXT NULL;");
            if (!columns.Contains("representation"))
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN representation TEXT NOT NULL DEFAULT 'value';");
        }
    }

    // ---------- v3: the raw store and itemised sandbox ----------

    private static void Step3RawAndSandbox(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS raw_artifacts (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                hash         TEXT NOT NULL UNIQUE,
                kind         TEXT NOT NULL,
                source       TEXT NOT NULL,
                title        TEXT NOT NULL,
                summary      TEXT NOT NULL,
                summary_kind TEXT NOT NULL DEFAULT 'fallback',
                bytes_path   TEXT NULL,
                chars        INTEGER NOT NULL DEFAULT 0,
                fetched_at   TEXT NOT NULL,
                status       TEXT NOT NULL DEFAULT 'kept'
            );
            """);

        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_raw_fetched ON raw_artifacts(fetched_at);");

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS sandbox_entries (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                section     TEXT NOT NULL,
                kind        TEXT NOT NULL DEFAULT 'note',
                text        TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                source_ref  TEXT NULL,
                status      TEXT NOT NULL DEFAULT 'open'
            );
            """);

        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sandbox_section ON sandbox_entries(section, id);");
    }

    // ---------- v4: proposals are change sets ----------

    private static void Step4Proposals(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS proposals (
                id         TEXT PRIMARY KEY,
                title      TEXT NOT NULL,
                summary    TEXT NOT NULL,
                status     TEXT NOT NULL DEFAULT 'Pending',
                created_at TEXT NOT NULL,
                decided_at TEXT NULL
            );
            """);

        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_proposals_status ON proposals(status, created_at);");

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS proposal_changes (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                proposal_id TEXT NOT NULL,
                ord         INTEGER NOT NULL,
                target      TEXT NOT NULL,
                payload     TEXT NOT NULL
            );
            """);

        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_changes_proposal ON proposal_changes(proposal_id, ord);");
    }

    // ---------- v5: the visibility ladder becomes a stored column ----------

    private static void Step5Visibility(SqliteConnection conn)
    {
        var raw = Columns(conn, "raw_artifacts");
        if (!raw.Contains("visibility"))
            Exec(conn, "ALTER TABLE raw_artifacts ADD COLUMN visibility TEXT NOT NULL DEFAULT 'R1';");

        var sandbox = Columns(conn, "sandbox_entries");
        if (!sandbox.Contains("visibility"))
            Exec(conn, "ALTER TABLE sandbox_entries ADD COLUMN visibility TEXT NOT NULL DEFAULT 'S';");
    }

    // ---------- helpers ----------

    private static HashSet<string> Columns(SqliteConnection conn, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetString(1));
        return set;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
