using CogniCustos.Contracts;
using Microsoft.Data.Sqlite;

namespace CogniCustos.Core;

/// <summary>
/// SQLite main database: tasks and deadlines. AI writes here are refused by design.
/// Scheme is migrated in place so an existing database keeps working.
/// </summary>
public sealed class SqliteTaskStore : ITaskStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public SqliteTaskStore(string dbPath)
    {
        _dbPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        Init();
    }

    public string DbPath => _dbPath;

    private void Init()
    {
        using var conn = Open();
        SqliteSchema.EnsureUpgraded(conn);
    }

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

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public Task<int> AddAsync(string title, string? due, CancellationToken ct = default)
        => AddPlannedAsync(title, due, "short", null, ct);

    public Task<int> AddPlannedAsync(string title, string? due, string level, int? parentId, CancellationToken ct = default)
    {
        using var conn = Open();
        return Task.FromResult(AddPlanned(conn, null, title, due, level, parentId));
    }

    /// <summary>
    /// The transactional form: pass the change set's connection and transaction so the
    /// insert joins it and can be rolled back with the rest (docs/PARTITION-MODEL.md §6.2).
    /// </summary>
    internal int AddPlanned(SqliteConnection conn, System.Data.Common.DbTransaction? tx,
        string title, string? due, string level, int? parentId)
    {
        level = NormalizeLevel(level);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction?)tx;
        cmd.CommandText = """
            INSERT INTO tasks(title, due, level, parent_id, created) VALUES ($t, $d, $l, $p, $c);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$d", (object?)due ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", level);
        cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.Now.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Levels are an enumeration, not a hint: an unknown value is an error.
    /// Silently mapping it to "short" is how wrong data enters the authoritative store
    /// (docs/PARTITION-MODEL.md §6.3 "解析失败不猜").
    /// </summary>
    public static bool TryNormalizeLevel(string? level, out string normalized)
    {
        switch ((level ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "long" or "长期" or "long-term":
                normalized = "long";
                return true;
            case "mid" or "中期" or "mid-term" or "medium":
                normalized = "mid";
                return true;
            case "short" or "短期" or "short-term":
                normalized = "short";
                return true;
            case "":
                normalized = "short";
                return true;
            default:
                normalized = string.Empty;
                return false;
        }
    }

    /// <summary>Throwing form for call sites that cannot continue with a wrong level.</summary>
    public static string NormalizeLevel(string? level) =>
        TryNormalizeLevel(level, out var normalized)
            ? normalized
            : throw new ArgumentException($"unknown level '{level}' (expected long | mid | short)", nameof(level));

    public Task<IReadOnlyList<TaskItem>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<TaskItem>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, due, done, level, parent_id FROM tasks
            ORDER BY done ASC,
                     CASE level WHEN 'long' THEN 0 WHEN 'mid' THEN 1 ELSE 2 END,
                     (due IS NULL), due ASC, id DESC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TaskItem(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.IsDBNull(4) ? "short" : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }
        return Task.FromResult<IReadOnlyList<TaskItem>>(list);
    }

    public Task<bool> CompleteAsync(int id, CancellationToken ct = default)
    {
        using var conn = Open();
        return Task.FromResult(Complete(conn, null, id));
    }

    internal bool Complete(SqliteConnection conn, System.Data.Common.DbTransaction? tx, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction?)tx;
        cmd.CommandText = "UPDATE tasks SET done = 1 WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Opens the same database the gate uses, for a change set transaction.</summary>
    internal SqliteConnection OpenForTransaction() => Open();
}

/// <summary>Schedule blocks live in the same SQLite file as tasks, under their own table.</summary>
public sealed class SqliteScheduleStore : IScheduleStore
{
    private readonly string _connectionString;

    public SqliteScheduleStore(string dbPath)
    {
        var path = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureUpgraded(conn);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public Task<int> AddAsync(string date, string start, string end, string title, int? taskId, CancellationToken ct = default)
    {
        using var conn = Open();
        return Task.FromResult(Add(conn, null, date, start, end, title, taskId));
    }

    /// <summary>Transactional form: joins the change set's transaction when one is given.</summary>
    internal int Add(Microsoft.Data.Sqlite.SqliteConnection conn, System.Data.Common.DbTransaction? tx,
        string date, string start, string end, string title, int? taskId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction?)tx;
        cmd.CommandText = """
            INSERT INTO schedule_blocks(date, start, end, title, task_id) VALUES ($d, $s, $e, $t, $id);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$d", date);
        cmd.Parameters.AddWithValue("$s", start);
        cmd.Parameters.AddWithValue("$e", end);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$id", (object?)taskId ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public Task<IReadOnlyList<ScheduleBlock>> ListDayAsync(string date, CancellationToken ct = default)
        => ListRangeAsync(date, date, ct);

    public Task<IReadOnlyList<ScheduleBlock>> ListRangeAsync(string fromDate, string toDate, CancellationToken ct = default)
    {
        var list = new List<ScheduleBlock>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, date, start, end, title, task_id FROM schedule_blocks
            WHERE date >= $from AND date <= $to
            ORDER BY date ASC, start ASC;
            """;
        cmd.Parameters.AddWithValue("$from", fromDate);
        cmd.Parameters.AddWithValue("$to", toDate);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ScheduleBlock(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }
        return Task.FromResult<IReadOnlyList<ScheduleBlock>>(list);
    }
}
