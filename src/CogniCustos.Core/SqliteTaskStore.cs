using CogniCustos.Contracts;
using Microsoft.Data.Sqlite;

namespace CogniCustos.Core;

/// <summary>SQLite main database: tasks and deadlines. AI writes here are refused by design.</summary>
public sealed class SqliteTaskStore : ITaskStore
{
    private readonly string _connectionString;

    public SqliteTaskStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Init();
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tasks (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                title   TEXT NOT NULL,
                due     TEXT NULL,
                done    INTEGER NOT NULL DEFAULT 0,
                created TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public Task<int> AddAsync(string title, string? due, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO tasks(title, due, created) VALUES ($t, $d, $c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$d", (object?)due ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.Now.ToString("O"));
        var id = Convert.ToInt32(cmd.ExecuteScalar());
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<TaskItem>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<TaskItem>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, due, done FROM tasks ORDER BY done ASC, (due IS NULL), due ASC, id DESC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TaskItem(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3) != 0));
        }
        return Task.FromResult<IReadOnlyList<TaskItem>>(list);
    }

    public Task<bool> CompleteAsync(int id, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET done = 1 WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return Task.FromResult(cmd.ExecuteNonQuery() > 0);
    }
}
