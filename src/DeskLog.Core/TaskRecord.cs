namespace DeskLog.Core;

public sealed record TaskRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "";
    public string Notes { get; init; } = "";
    public DateOnly? DueDate { get; init; }
    public bool IsCompleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; init; }
    public bool IsDeleted { get; init; }
}

public static class TaskRules
{
    public static string DueText(DateOnly? due, DateOnly today)
    {
        if (due is null) return "未设截止日期";
        int days = due.Value.DayNumber - today.DayNumber;
        return days < 0 ? $"已逾期 {-days} 天" : days == 0 ? "今天截止" : $"{due:yyyy年M月d日} 截止";
    }

    public static IReadOnlyList<TaskRecord> Pending(IEnumerable<TaskRecord> tasks) => tasks
        .Where(t => !t.IsDeleted && !t.IsCompleted).OrderBy(t => t.DueDate ?? DateOnly.MaxValue)
        .ThenBy(t => t.CreatedAt).ToList();

    public static IReadOnlyList<TaskRecord> Recent(IEnumerable<TaskRecord> tasks) => tasks
        .Where(t => !t.IsDeleted && t.IsCompleted).OrderByDescending(t => t.CompletedAt).Take(5).ToList();

    // Keep the local calendar date recorded at completion, even after a timezone change.
    public static IReadOnlyList<TaskRecord> InMonth(IEnumerable<TaskRecord> tasks, int year, int month) => tasks
        .Where(t => !t.IsDeleted && t.IsCompleted && t.CompletedAt is { } d && d.Year == year && d.Month == month)
        .OrderByDescending(t => t.CompletedAt).ToList();

    public static TaskRecord Complete(TaskRecord task, DateTimeOffset now) => task with
        { IsCompleted = true, CompletedAt = now, UpdatedAt = now };
    public static TaskRecord Reopen(TaskRecord task, DateTimeOffset now) => task with
        { IsCompleted = false, CompletedAt = null, UpdatedAt = now };

    public static void Validate(TaskRecord task)
    {
        if (string.IsNullOrWhiteSpace(task.Title)) throw new ArgumentException("请填写任务名称。");
        if (task.Title.Trim().Length > 200) throw new ArgumentException("任务名称最多 200 个字。");
        if (task.Notes.Length > 10000) throw new ArgumentException("备注最多 10000 个字。");
        if (task.IsCompleted != task.CompletedAt.HasValue) throw new ArgumentException("完成状态和完成日期不一致。");
    }
}
