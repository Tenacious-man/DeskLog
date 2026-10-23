using System.Globalization;

namespace DeskLog.App;

public partial class TaskEditor : Window
{
    private readonly App app;
    private readonly TaskRecord original;
    private bool ready;
    private bool saving;
    private bool invalidDue;
    private bool invalidCompletion;

    internal TaskEditor(App app, TaskRecord? task)
    {
        InitializeComponent();
        this.app = app;
        original = task ?? new TaskRecord();
        Title = task == null ? "添加任务" : "编辑任务";
        Heading.Text = task == null ? "记下下一件事" : "更新这件事";
        TitleInput.Text = original.Title;
        NotesInput.Text = original.Notes;
        DueInput.SelectedDate = original.DueDate?.ToDateTime(TimeOnly.MinValue);
        CompletedDateInput.SelectedDate = original.CompletedAt?.Date ?? DateTime.Today;
        CompletedInput.IsChecked = original.IsCompleted;
        CompletedSection.Visibility = original.IsCompleted ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = task == null ? Visibility.Collapsed : Visibility.Visible;
        DueInput.DateValidationError += (_, e) => { e.ThrowException = false; invalidDue = true; ErrorLabel.Text = "截止日期无效，请选择日期或清除。"; };
        CompletedDateInput.DateValidationError += (_, e) => { e.ThrowException = false; invalidCompletion = true; ErrorLabel.Text = "完成日期无效，请重新选择。"; };
        DueInput.SelectedDateChanged += (_, _) => invalidDue = false;
        CompletedDateInput.SelectedDateChanged += (_, _) => invalidCompletion = false;
        DueInput.PreviewKeyDown += (_, _) => invalidDue = false;
        CompletedDateInput.PreviewKeyDown += (_, _) => invalidCompletion = false;
        DueInput.CalendarClosed += (_, _) => invalidDue = false;
        CompletedDateInput.CalendarClosed += (_, _) => invalidCompletion = false;
        ready = true;
        TitleInput.TextChanged += (_, _) => ErrorLabel.Text = "";
        Loaded += (_, _) => TitleInput.Focus();
    }

    private void CompletedChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        CompletedSection.Visibility = CompletedInput.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ClearDueClick(object sender, RoutedEventArgs e) { DueInput.SelectedDate = null; DueInput.Text = ""; invalidDue = false; }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();

    private static DateTime? ReadDate(DatePicker picker, string label, bool required)
    {
        string text = picker.Text.Trim();
        if (text.Length == 0)
        {
            if (required) throw new ArgumentException($"请选择{label}。");
            return null;
        }
        if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date))
            throw new ArgumentException($"{label}无效，请通过日历选择。");
        return date.Date;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        try
        {
            saving = true;
            if (invalidDue) throw new ArgumentException("截止日期无效，请选择日期或清除后再保存。");
            if (CompletedInput.IsChecked == true && invalidCompletion) throw new ArgumentException("完成日期无效，请重新选择后再保存。");
            var now = DateTimeOffset.Now;
            DateTime? due = ReadDate(DueInput, "截止日期", false);
            bool completed = CompletedInput.IsChecked == true;
            DateTimeOffset? completion = null;
            if (completed)
            {
                var date = ReadDate(CompletedDateInput, "完成日期", true)!.Value;
                if (date > DateTime.Today) throw new ArgumentException("完成日期不能晚于今天。");
                completion = original.IsCompleted && original.CompletedAt!.Value.Date == date
                    ? original.CompletedAt
                    : date == DateTime.Today ? now : new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local));
            }
            var updated = original with { Title = TitleInput.Text.Trim(), Notes = NotesInput.Text,
                DueDate = due.HasValue ? DateOnly.FromDateTime(due.Value) : null,
                IsCompleted = completed, CompletedAt = completion, UpdatedAt = now };
            app.Store.Save(updated);
            app.NotifyChanged();
            Close();
        }
        catch (ArgumentException ex) { ErrorLabel.Text = ex.Message; }
        catch (Exception ex) { app.Log(ex.ToString()); ErrorLabel.Text = "保存失败，输入已保留。请检查磁盘空间或文件占用后重试。"; }
        finally { saving = false; }
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "删除后，这项任务将不再显示在待办和月度记录中。确定删除？", "删除任务", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { app.Store.Save(original with { IsDeleted = true, UpdatedAt = DateTimeOffset.Now }); app.NotifyChanged(); Close(); }
        catch (Exception ex) { app.Log(ex.ToString()); ErrorLabel.Text = "删除失败，记录仍保留，请稍后重试。"; }
    }
}
