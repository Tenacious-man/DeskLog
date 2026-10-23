namespace DeskLog.App;

public partial class MonthWindow : Window
{
    private readonly App app;
    private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool updating;

    internal MonthWindow(App app)
    {
        InitializeComponent(); this.app = app;
        MonthInput.ItemsSource = Enumerable.Range(1, 12).ToList();
        RefreshRecords();
    }

    internal void RefreshRecords()
    {
        try
        {
            var all = app.Store.ReadAll();
            int firstYear = Math.Min(DateTime.Today.Year - 5, all.Where(t => t.CompletedAt.HasValue).Select(t => t.CompletedAt!.Value.Year).DefaultIfEmpty(DateTime.Today.Year).Min());
            firstYear = Math.Min(firstYear, month.Year);
            updating = true;
            YearInput.ItemsSource = Enumerable.Range(firstYear, DateTime.Today.Year - firstYear + 1).ToList();
            YearInput.SelectedItem = month.Year;
            MonthInput.SelectedItem = month.Month;
            updating = false;
            NextButton.IsEnabled = month < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            var tasks = TaskRules.InMonth(all, month.Year, month.Month);
            CountLabel.Text = $"{month:yyyy年M月}  ·  共完成 {tasks.Count} 项任务";
            EmptyLabel.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RecordsList.Children.Clear();
            foreach (var task in tasks)
            {
                var content = new StackPanel();
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
                header.ColumnDefinitions.Add(new ColumnDefinition());
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(new TextBlock { Text = $"{task.CompletedAt:MM / dd}", Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center });
                var name = new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                Grid.SetColumn(name, 1); header.Children.Add(name);
                var edit = new Button { Content = "编辑", Padding = new Thickness(9, 6, 9, 6), FontSize = 11 };
                edit.Click += (_, _) => app.Edit(task); Grid.SetColumn(edit, 2); header.Children.Add(edit);
                content.Children.Add(header);
                if (!string.IsNullOrEmpty(task.Notes))
                {
                    content.Children.Add(new Expander { Header = "查看备注", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(72, 10, 0, 0),
                        Content = new TextBlock { Text = task.Notes, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 8, 0, 0), LineHeight = 23 } });
                }
                RecordsList.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(235, 240, 229)), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 10), Child = content });
            }
            ErrorLabel.Text = "";
        }
        catch (Exception ex) { updating = false; app.Log(ex.ToString()); ErrorLabel.Text = "记录读取失败，请重新选择月份以重试。"; }
    }
    private void PreviousClick(object sender, RoutedEventArgs e) { if (month.Year == 1 && month.Month == 1) return; month = month.AddMonths(-1); RefreshRecords(); }
    private void NextClick(object sender, RoutedEventArgs e) { month = month.AddMonths(1); RefreshRecords(); }
    private void TodayClick(object sender, RoutedEventArgs e) { month = new(DateTime.Today.Year, DateTime.Today.Month, 1); RefreshRecords(); }
    private void MonthSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updating || YearInput.SelectedItem is not int year || MonthInput.SelectedItem is not int number) return;
        month = new DateTime(year, number, 1); RefreshRecords();
    }
}
