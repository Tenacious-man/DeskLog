using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DeskLog.Core;

public sealed class TaskStore
{
    public string DirectoryPath { get; }
    public string DatabasePath => Path.Combine(DirectoryPath, "desklog.db");
    public TaskStore(string directory) => DirectoryPath = Path.GetFullPath(directory);

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 3 }.ToString());
        db.Open();
        return db;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(DirectoryPath);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=DELETE;
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, notes TEXT NOT NULL,
                due_date TEXT, is_completed INTEGER NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, completed_at TEXT,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                CHECK ((is_completed=1 AND completed_at IS NOT NULL) OR (is_completed=0 AND completed_at IS NULL))
            );
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<TaskRecord> ReadAll()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,title,notes,due_date,is_completed,created_at,updated_at,completed_at,is_deleted FROM tasks";
        using var r = cmd.ExecuteReader();
        var result = new List<TaskRecord>();
        while (r.Read()) result.Add(new TaskRecord
        {
            Id = r.GetString(0), Title = r.GetString(1), Notes = r.GetString(2),
            DueDate = r.IsDBNull(3) ? null : DateOnly.ParseExact(r.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            IsCompleted = r.GetBoolean(4), CreatedAt = ParseTime(r.GetString(5)), UpdatedAt = ParseTime(r.GetString(6)),
            CompletedAt = r.IsDBNull(7) ? null : ParseTime(r.GetString(7)), IsDeleted = r.GetBoolean(8)
        });
        return result;
    }

    public void Save(TaskRecord task)
    {
        TaskRules.Validate(task);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO tasks (id,title,notes,due_date,is_completed,created_at,updated_at,completed_at,is_deleted)
            VALUES ($id,$title,$notes,$due,$done,$created,$updated,$completed,$deleted)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title, notes=excluded.notes,
            due_date=excluded.due_date,is_completed=excluded.is_completed,updated_at=excluded.updated_at,
            completed_at=excluded.completed_at,is_deleted=excluded.is_deleted;
            """;
        cmd.Parameters.AddWithValue("$id", task.Id);
        cmd.Parameters.AddWithValue("$title", task.Title.Trim());
        cmd.Parameters.AddWithValue("$notes", task.Notes);
        cmd.Parameters.AddWithValue("$due", (object?)task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$done", task.IsCompleted);
        cmd.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", task.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", (object?)task.CompletedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deleted", task.IsDeleted);
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    public string? GetSetting(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public void Backup(DateOnly today)
    {
        string folder = Path.Combine(DirectoryPath, "backups");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"desklog-{today:yyyy-MM-dd}.db");
        if (File.Exists(path)) return;
        string temporary = path + ".tmp";
        using (var source = Open())
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }
        File.Move(temporary, path, true);
        foreach (string old in Directory.GetFiles(folder, "desklog-????-??-??.db").OrderDescending().Skip(7)) File.Delete(old);
    }

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
