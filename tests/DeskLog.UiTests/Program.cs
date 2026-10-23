using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskLog.App;
using DeskLog.Core;
using Microsoft.Data.Sqlite;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        int code = 1;
        var app = new App();
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var board = app.Windows.OfType<BoardWindow>().Single();
                Check(board.PendingCount.Text == "0", "empty board");
                app.Edit(null);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var editor = app.Windows.OfType<TaskEditor>().Single();
                Click(editor, "保存任务");
                Check(editor.ErrorLabel.Text.Length > 0 && app.Store.ReadAll().Count == 0, "empty title keeps editor open");
                editor.TitleInput.Text = "整理本月项目汇报材料";
                editor.NotesInput.Text = "汇总测试结果\n补充完成情况与后续安排";
                editor.DueInput.SelectedDate = DateTime.Today.AddDays(1);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                SaveImage(editor, app.Store.DirectoryPath, "editor.png");
                using (var lockedDb = new SqliteConnection($"Data Source={app.Store.DatabasePath};Pooling=False"))
                {
                    lockedDb.Open();
                    using var command = lockedDb.CreateCommand();
                    command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
                    Click(editor, "保存任务");
                    Check(editor.IsVisible && editor.TitleInput.Text == "整理本月项目汇报材料" && editor.ErrorLabel.Text.StartsWith("保存失败"), "failed save retains editor input");
                    command.CommandText = "ROLLBACK"; command.ExecuteNonQuery();
                }
                Click(editor, "保存任务");
                Check(app.Store.ReadAll().Single().Title == "整理本月项目汇报材料" && board.PendingCount.Text == "1", "editor save updates board");
                app.SaveFromBoard(TaskRules.Complete(app.Store.ReadAll().Single(), DateTimeOffset.Now));
                Check(board.PendingCount.Text == "0" && board.RecentList.Children.Count == 1, "complete updates both lists");
                app.OpenMonth();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var month = app.Windows.OfType<MonthWindow>().Single();
                Check(month.RecordsList.Children.Count == 1, "completed record in current month");
                var record = app.Store.ReadAll().Single();
                app.Edit(record);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                editor = app.Windows.OfType<TaskEditor>().Single();
                editor.CompletedDateInput.SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1);
                Click(editor, "保存任务");
                Check(month.RecordsList.Children.Count == 0, "date correction removes old month entry");
                Click(month, "‹");
                Check(month.RecordsList.Children.Count == 1, "corrected record appears in previous month");
                app.SaveFromBoard(TaskRules.Reopen(app.Store.ReadAll().Single(), DateTimeOffset.Now));
                Check(board.PendingCount.Text == "1" && month.RecordsList.Children.Count == 0, "reopen updates board and month");
                app.SaveFromBoard(TaskRules.Complete(app.Store.ReadAll().Single(), DateTimeOffset.Now));
                Click(month, "回到本月");
                for (int i = 0; i < 7; i++)
                    app.Store.Save(new TaskRecord { Title = i == 0 ? "检查动态目标跟踪效果，补充长标题换行与不同截止日期的展示" : $"测试待办事项 {i + 1}", DueDate = i == 6 ? null : DateOnly.FromDateTime(DateTime.Today.AddDays(i - 2)) });
                app.NotifyChanged();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                SaveImage(board, app.Store.DirectoryPath, "board.png");
                SaveImage(month, app.Store.DirectoryPath, "month.png");
                Check(board.PendingList.Children.Count == 7, "pending rows retained and scrollable");
                var hwnd = new System.Windows.Interop.WindowInteropHelper(board).Handle;
                Check(Native.GetAncestor(hwnd, 2) == hwnd && (Native.GetWindowLongPtr(hwnd, -16).ToInt64() & 0x40000000L) == 0 && !board.ShowInTaskbar, "panel stays top-level and out of taskbar, avoiding Explorer renderer");
                Check(board.DesktopPinned, "desktop mode enabled by default");
                var desktopOwner = Native.GetWindowLongPtr(hwnd, -8);
                Check(desktopOwner != 0 && (desktopOwner == Native.GetShellWindow() || Native.FindWindowEx(desktopOwner, 0, "SHELLDLL_DefView", null) != 0), "desktop owns panel without child parenting");
                int minimizeEvents = 0;
                board.StateChanged += (_, _) => { if (board.WindowState == WindowState.Minimized) minimizeEvents++; };
                Native.SendMessage(hwnd, 0x0112, 0xF020, 0);
                Check(board.WindowState == WindowState.Normal && minimizeEvents == 0, "minimize command blocked before visibility transition");
                board.HideToTray();
                board.MaintainDesktop();
                board.RefreshEnvironment();
                Check(!board.IsVisible, "hide to tray removes board from screen");
                Check(Native.GetWindowLongPtr(hwnd, -8) != desktopOwner, "tray hide detaches desktop ownership");
                board.Reveal();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(board.IsVisible && board.WindowState == WindowState.Normal && !board.ShowInTaskbar && !month.ShowInTaskbar, "tray restore shows panel without taskbar entry");
                board.SetTransparency(40, true);
                Check(Math.Abs(board.Opacity - 0.6) < 0.001 && app.Store.GetSetting("transparency") == "40", "transparency applies window opacity and persists");
                board.SetTransparency(200, false);
                Check(board.TransparencyPercent == 80, "transparency cannot make panel completely invisible");
                board.SetTransparency(40, false);
                board.OpenAppearance();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var appearance = app.Windows.Cast<Window>().Single(w => w.Title == "透明度设置");
                Descendants(appearance).OfType<Slider>().Single().Value = 60;
                Check(board.TransparencyPercent == 60, "slider previews transparency immediately");
                appearance.Close();
                Check(board.TransparencyPercent == 40, "closing without save restores transparency");
                var restored = new BoardWindow(app);
                restored.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(restored.TransparencyPercent == 40, "new board restores saved transparency");
                restored.Close();
                board.SetTransparency(0, true);
                Console.WriteLine("PASS: 22 WPF integration scenarios (in-process control events, not physical desktop input).");
                code = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return code;
    }

    private static void Click(DependencyObject root, string text)
    {
        var button = Descendants(root).OfType<Button>().First(b => b.Content as string == text);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void SaveImage(Window window, string directory, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
}
