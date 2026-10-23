using DeskLog.Core;
using Microsoft.Data.Sqlite;

int passed = 0;
string root = Path.Combine(Path.GetTempPath(), "DeskLogTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var now = new DateTimeOffset(2026, 9, 15, 12, 30, 0, TimeSpan.FromHours(8));
var today = new DateOnly(2026, 9, 15);
var store = new TaskStore(root);
store.Initialize();

Run("empty database", () => Equal(0, store.ReadAll().Count));
var task = new TaskRecord { Title = "测试任务 中文 📝", Notes = "第一行\n第二行 ' \"", DueDate = today, CreatedAt = now, UpdatedAt = now };
Run("create, update, reopen database", () =>
{
    store.Save(task);
    var reopened = new TaskStore(root);
    Equal(task, reopened.ReadAll().Single());
    store.Save(task with { Title = "更新任务", Notes = "修改后的备注" });
    Equal("更新任务", store.ReadAll().Single().Title);
    Equal(1, store.ReadAll().Count);
});
Run("date reminders at day, month and year boundaries", () =>
{
    Equal("未设截止日期", TaskRules.DueText(null, today));
    Equal("今天截止", TaskRules.DueText(today, today));
    Equal("已逾期 1 天", TaskRules.DueText(new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 1)));
    Equal("已逾期 2 天", TaskRules.DueText(new DateOnly(2024, 2, 28), new DateOnly(2024, 3, 1)));
    Equal("2026年9月16日 截止", TaskRules.DueText(today.AddDays(1), today));
});
Run("pending retains old tasks, sorts missing deadline last", () =>
{
    var items = new[] { new TaskRecord { Title = "no date" }, new TaskRecord { Title = "today", DueDate = today }, new TaskRecord { Title = "old", DueDate = today.AddDays(-40) }, TaskRules.Complete(task, now), task with { IsDeleted = true } };
    Equal("old,today,no date", string.Join(",", TaskRules.Pending(items).Select(t => t.Title)));
});
Run("complete, month uses completion date, reopen and complete again", () =>
{
    task = TaskRules.Complete(task, now);
    store.Save(task);
    Equal(1, TaskRules.InMonth(store.ReadAll(), 2026, 9).Count);
    Equal(0, TaskRules.Pending(store.ReadAll()).Count);
    task = TaskRules.Reopen(task, now.AddDays(1));
    store.Save(task);
    Equal(null, store.ReadAll().Single().CompletedAt);
    Equal(0, TaskRules.InMonth(store.ReadAll(), 2026, 9).Count);
    task = TaskRules.Complete(task, new DateTimeOffset(2026, 10, 1, 0, 15, 0, TimeSpan.FromHours(8)));
    store.Save(task);
    Equal(1, TaskRules.InMonth(store.ReadAll(), 2026, 10).Count);
    Equal(0, TaskRules.InMonth(store.ReadAll(), 2026, 9).Count);
});
Run("correct date moves historical month", () =>
{
    task = task with { CompletedAt = new DateTimeOffset(2025, 12, 31, 23, 30, 0, TimeSpan.FromHours(8)) };
    store.Save(task);
    Equal(1, TaskRules.InMonth(store.ReadAll(), 2025, 12).Count);
    Equal(0, TaskRules.InMonth(store.ReadAll(), 2026, 10).Count);
});
Run("soft delete excluded but persisted", () =>
{
    store.Save(task with { IsDeleted = true });
    Equal(1, store.ReadAll().Count);
    Equal(0, TaskRules.Recent(store.ReadAll()).Count);
    Equal(0, TaskRules.InMonth(store.ReadAll(), 2025, 12).Count);
});
Run("five most recent only", () =>
{
    var items = Enumerable.Range(0, 12).Select(i => TaskRules.Complete(new TaskRecord { Title = i.ToString() }, now.AddDays(-i)));
    Equal("0,1,2,3,4", string.Join(",", TaskRules.Recent(items).Select(t => t.Title)));
});
Run("invalid title and inconsistent status rejected without modifying stored data", () =>
{
    Throws<ArgumentException>(() => store.Save(task with { Title = "  " }));
    Throws<ArgumentException>(() => store.Save(task with { IsCompleted = false }));
    Equal(1, store.ReadAll().Count);
});
Run("database lock fails safely then retry succeeds", () =>
{
    using (var db = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
    {
        db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "BEGIN EXCLUSIVE"; cmd.ExecuteNonQuery();
        Throws<SqliteException>(() => store.Save(new TaskRecord { Title = "retry after lock", Id = "retry" }));
        cmd.CommandText = "ROLLBACK"; cmd.ExecuteNonQuery();
    }
    Equal(1, store.ReadAll().Count);
    store.Save(new TaskRecord { Title = "retry after lock", Id = "retry" });
    Equal(2, store.ReadAll().Count);
});
Run("settings persist", () => { store.SetSetting("position", "{\"X\":120,\"Y\":80}"); Equal("{\"X\":120,\"Y\":80}", new TaskStore(root).GetSetting("position")); });
Run("daily backup is consistent, seven retained, restore works", () =>
{
    for (int i = 0; i < 10; i++) store.Backup(today.AddDays(i));
    store.Backup(today.AddDays(9));
    var backups = Directory.GetFiles(Path.Combine(root, "backups"), "*.db");
    Equal(7, backups.Length);
    var restored = Path.Combine(root, "restore"); Directory.CreateDirectory(restored);
    File.Copy(backups.OrderDescending().First(), Path.Combine(restored, "desklog.db"));
    Equal(2, new TaskStore(restored).ReadAll().Count);
});
Console.WriteLine($"PASS: {passed} scenarios. Data isolated at {root}");

void Run(string label, Action action) { action(); passed++; Console.WriteLine("PASS " + label); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected '{expected}', got '{actual}'"); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
