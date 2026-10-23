using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskLog.App;

public partial class BoardWindow : Window
{
    private readonly App app;
    private readonly DesktopHost host;
    private readonly DispatcherTimer timer;
    private Native.Point dragStart;
    private Native.Rect dragBounds;
    private bool dragging;
    private bool ready;
    private DateOnly displayedDay;
    private string? workAreaSignature;
    private string? errorMessage;
    private int ticks;
    private bool hiddenToTray;
    private readonly DispatcherTimer desktopTimer;
    private Window? appearanceWindow;
    internal int TransparencyPercent { get; private set; }
    internal bool DesktopPinned { get; private set; }

    internal BoardWindow(App app)
    {
        InitializeComponent();
        this.app = app;
        // Keep WPF top-level: cross-process desktop parenting rendered black on this system.
        ShowActivated = false;
        host = new DesktopHost(this);
        DesktopPinned = !app.Windowed;
        desktopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        desktopTimer.Tick += (_, _) => MaintainDesktop();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            RestorePosition();
            ready = true;
            try
            {
                if (int.TryParse(app.Store.GetSetting("transparency"), out int value)) SetTransparency(value, false);
            }
            catch (Exception ex) { app.Log("transparency: " + ex); Report("透明度恢复失败，可在菜单中重新设置。"); }
            RefreshTasks();
            timer.Start();
            desktopTimer.Start();
            MaintainDesktop();
            app.Log($"board ready bounds={host.Bounds.Left},{host.Bounds.Top},{host.Bounds.Width},{host.Bounds.Height} desktopPinned={DesktopPinned}");
        };
        ContentRendered += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int));
            app.Log($"rendered hwnd={handle} parent={Native.GetParent(handle)} cloaked={cloaked} visible={IsVisible} state={WindowState} size={ActualWidth},{ActualHeight} style={Native.GetWindowLongPtr(handle, -16):X} exstyle={Native.GetWindowLongPtr(handle, -20):X}");
            var bounds = host.Bounds;
            nint hit = Native.WindowFromPoint(new Native.Point { X = bounds.Left + 30, Y = bounds.Top + 60 });
            app.Log($"header hit-test hwnd={hit} class={Native.ClassName(hit)} matchesBoard={hit == handle}");
            if (app.Preview)
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(this);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(app.Store.DirectoryPath, "preview.png"));
                encoder.Save(output);
            }
        };
        Closed += (_, _) => { timer.Stop(); desktopTimer.Stop(); };
        DragHeader.MouseLeftButtonDown += BeginDrag;
        DragHeader.MouseMove += MoveDrag;
        DragHeader.MouseLeftButtonUp += EndDrag;
        DragHeader.LostMouseCapture += (_, _) => dragging = false;
    }

    internal void MaintainDesktop()
    {
        if (ready && DesktopPinned && !hiddenToTray && !dragging)
        {
            try { host.MaintainDesktopLayer(); }
            catch (Exception ex) { app.Log("desktop owner: " + ex); DesktopPinned = false; Report("固定到桌面失败，已保留普通窗口，可从菜单重试。"); }
        }
    }
    private void Tick()
    {
        if (hiddenToTray) return;
        string signature = string.Join(";", Forms.Screen.AllScreens.Select(s => s.WorkingArea.ToString()));
        if (signature != workAreaSignature) { workAreaSignature = signature; ClampPosition(); }
        if (DateOnly.FromDateTime(DateTime.Now) != displayedDay) { RefreshTasks(); app.RefreshMonth(); }
        if (++ticks % 30 == 0) app.TryBackup();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (ready) Dispatcher.BeginInvoke(ClampPosition);
    }

    internal void RefreshTasks()
    {
        if (!ready) return;
        try
        {
            var all = app.Store.ReadAll();
            var pending = TaskRules.Pending(all);
            var recent = TaskRules.Recent(all);
            displayedDay = DateOnly.FromDateTime(DateTime.Now);
            DateLabel.Text = DateTime.Now.ToString("MM / dd");
            WeekLabel.Text = DateTime.Now.ToString("dddd", CultureInfo.GetCultureInfo("zh-CN"));
            PendingCount.Text = pending.Count.ToString();
            RecentCount.Text = recent.Count == 0 ? "" : $"最近 {recent.Count} 项";
            PendingEmpty.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PendingList.Children.Clear();
            RecentList.Children.Clear();
            foreach (var t in pending) PendingList.Children.Add(MakeRow(t));
            foreach (var t in recent) RecentList.Children.Add(MakeRow(t));
            errorMessage = null;
        }
        catch (Exception ex) { app.Log(ex.ToString()); errorMessage = "记录读取失败，请从菜单重试。已有记录不会被清空。"; }
        ShowWarning();
    }

    private UIElement MakeRow(TaskRecord task)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var toggle = new Button { Content = task.IsCompleted ? "✓" : "○", FontSize = 21, Padding = new Thickness(1, 5, 6, 5), Background = Brushes.Transparent,
            Foreground = task.IsCompleted ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Top,
            ToolTip = task.IsCompleted ? "恢复为待完成" : "标记完成" };
        System.Windows.Automation.AutomationProperties.SetName(toggle, (task.IsCompleted ? "恢复：" : "完成：") + task.Title);
        toggle.Click += (_, _) => app.SaveFromBoard(task.IsCompleted ? TaskRules.Reopen(task, DateTimeOffset.Now) : TaskRules.Complete(task, DateTimeOffset.Now));
        row.Children.Add(toggle);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap, FontSize = task.IsCompleted ? 12 : 14,
            Foreground = (Brush)FindResource(task.IsCompleted ? "Muted" : "Ink"), LineHeight = 21 });
        string date = task.IsCompleted ? $"{task.CompletedAt:yyyy年M月d日} 完成" : TaskRules.DueText(task.DueDate, displayedDay);
        Brush dateColor = (Brush)FindResource("Muted");
        if (!task.IsCompleted && task.DueDate is { } due && due <= displayedDay)
            dateColor = new SolidColorBrush(due < displayedDay ? Color.FromRgb(174, 62, 50) : Color.FromRgb(145, 99, 24));
        content.Children.Add(new TextBlock { Text = date, FontSize = 10, Foreground = dateColor, Margin = new Thickness(0, 3, 0, 0) });
        var edit = new Button { Content = content, Background = Brushes.Transparent, Padding = new Thickness(0, 5, 0, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = "编辑任务" };
        edit.Click += (_, _) => app.Edit(task);
        System.Windows.Automation.AutomationProperties.SetName(edit, "编辑：" + task.Title);
        Grid.SetColumn(edit, 1); row.Children.Add(edit);
        return row;
    }

    internal void Reveal()
    {
        hiddenToTray = false;
        WindowState = WindowState.Normal;
        Show();
        ClampPosition();
        RefreshTasks();
        if (DesktopPinned) MaintainDesktop(); else Activate();
    }

    internal void HideToTray()
    {
        hiddenToTray = true;
        host.ReleaseDesktopLayer();
        Hide();
    }

    internal void RefreshEnvironment()
    {
        if (hiddenToTray) return;
        ClampPosition();
        RefreshTasks();
    }

    internal void Report(string message) { errorMessage = message; ShowWarning(); }
    private void ShowWarning()
    {
        WarningLabel.Text = errorMessage ?? "";
        WarningLabel.Visibility = string.IsNullOrEmpty(WarningLabel.Text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void AddClick(object sender, RoutedEventArgs e) => app.Edit(null);
    private void MonthClick(object sender, RoutedEventArgs e) => app.OpenMonth();
    private void MenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var pin = new MenuItem { Header = "固定到桌面", IsCheckable = true, IsChecked = DesktopPinned };
        pin.Click += (_, _) => { DesktopPinned = pin.IsChecked; if (!DesktopPinned) host.ReleaseDesktopLayer(); Reveal(); };
        menu.Items.Add(pin);
        Add("透明度…", OpenAppearance);
        Add("刷新任务板", () => Reveal());
        Add("恢复到屏幕右侧", () => { RestorePosition(true); SavePosition(); });
        Add("本月记录", app.OpenMonth);
        Add("隐藏到托盘", HideToTray);
        Add("退出", app.ExitApp);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
        void Add(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    }

    internal void RevealWindowed()
    {
        DesktopPinned = false;
        host.ReleaseDesktopLayer();
        Reveal();
    }

    internal void SetTransparency(int percent, bool persist)
    {
        percent = Math.Clamp(percent, 0, 80);
        host.SetTransparency(percent);
        if (persist) app.Store.SetSetting("transparency", percent.ToString(CultureInfo.InvariantCulture));
        TransparencyPercent = percent;
    }

    internal void OpenAppearance()
    {
        if (appearanceWindow != null) { appearanceWindow.Activate(); return; }
        int original = TransparencyPercent;
        bool saved = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        var label = new TextBlock { FontSize = 18, Margin = new Thickness(0, 0, 0, 16) };
        var slider = new Slider { Minimum = 0, Maximum = 80, Value = original, TickFrequency = 5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 16) };
        System.Windows.Automation.AutomationProperties.SetName(slider, "面板透明度");
        label.Text = $"透明度 {original}%";
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
        slider.ValueChanged += (_, _) =>
        {
            try { SetTransparency((int)slider.Value, false); label.Text = $"透明度 {TransparencyPercent}%"; error.Text = ""; }
            catch (Exception ex) { app.Log(ex.ToString()); error.Text = "调整失败，请重试。"; }
        };
        panel.Children.Add(label); panel.Children.Add(slider);
        panel.Children.Add(new TextBlock { Text = "0% 完全不透明 · 80% 最透明\n调整整个任务板，包括文字。", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(error);
        var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(save);
        var dialog = new Window { Title = "透明度设置", Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel, Style = (Style)FindResource(typeof(Window)) };
        appearanceWindow = dialog;
        save.Click += (_, _) =>
        {
            try { SetTransparency((int)slider.Value, true); saved = true; dialog.Close(); }
            catch (Exception ex) { app.Log(ex.ToString()); error.Text = "保存失败，当前选择仍保留，请重试。"; }
        };
        dialog.Closed += (_, _) =>
        {
            appearanceWindow = null;
            if (!saved) { try { SetTransparency(original, false); } catch (Exception ex) { app.Log(ex.ToString()); } }
        };
        dialog.Show(); dialog.Activate();
    }

    private void RestorePosition(bool reset = false)
    {
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        double scale = host.Dpi / 96.0;
        int width = (int)(360 * scale), height = (int)Math.Min(740 * scale, area.Height - 40 * scale);
        int x = area.Right - width - (int)(24 * scale), y = area.Top + (int)(24 * scale);
        if (!reset)
        {
            try
            {
                string? saved = app.Store.GetSetting("position");
                if (saved != null) { var p = JsonSerializer.Deserialize<SavedPosition>(saved); if (p != null) { x = p.X; y = p.Y; } }
            }
            catch (Exception ex) { app.Log("position read: " + ex.Message); }
        }
        PositionClamped(x, y, width, height);
    }

    private void ClampPosition()
    {
        var bounds = host.Bounds;
        double scale = host.Dpi / 96.0;
        PositionClamped(bounds.Left, bounds.Top, (int)(360 * scale), (int)(740 * scale));
    }

    private void PositionClamped(int x, int y, int width, int height)
    {
        var screen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(x, y, width, height));
        var area = screen.WorkingArea;
        int margin = 16;
        width = Math.Min(width, area.Width - margin * 2);
        height = Math.Min(height, area.Height - margin * 2);
        x = Math.Clamp(x, area.Left + margin, area.Right - width - margin);
        y = Math.Clamp(y, area.Top + margin, area.Bottom - height - margin);
        host.Position(x, y, width, height);
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            for (DependencyObject? item = source; item != null && item != DragHeader; item = VisualTreeHelper.GetParent(item))
                if (item is Button) return;
        }
        Native.GetCursorPos(out dragStart);
        dragBounds = host.Bounds;
        dragging = DragHeader.CaptureMouse();
        e.Handled = true;
    }
    private void MoveDrag(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        Native.GetCursorPos(out var p);
        host.Position(dragBounds.Left + p.X - dragStart.X, dragBounds.Top + p.Y - dragStart.Y, dragBounds.Width, dragBounds.Height);
    }
    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false; DragHeader.ReleaseMouseCapture(); ClampPosition(); SavePosition();
    }
    private void SavePosition()
    {
        try { var b = host.Bounds; app.Store.SetSetting("position", JsonSerializer.Serialize(new SavedPosition(b.Left, b.Top))); }
        catch (Exception ex) { app.Log(ex.ToString()); Report("位置保存失败，下次启动可能恢复到默认位置。"); }
    }
    private sealed record SavedPosition(int X, int Y);
}
