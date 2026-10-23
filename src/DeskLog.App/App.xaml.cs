using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DeskLog.App;

public partial class App : Application
{
    internal TaskStore Store { get; private set; } = null!;
    internal bool Preview { get; private set; }
    internal bool Windowed { get; private set; }
    private BoardWindow? board;
    private TaskEditor? editor;
    private MonthWindow? monthWindow;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Mutex? mutex;
    private EventWaitHandle? activationEvent;
    private RegisteredWaitHandle? activationWait;
    private EventWaitHandle? windowActivationEvent;
    private RegisteredWaitHandle? windowActivationWait;
    private bool ownsMutex;
    private bool exiting;
    private DateOnly? backedUpDay;
    private DateTime nextBackupAttempt;
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The desktop panel uses a small layered surface; software rendering also works over remote sessions.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        Preview = e.Args.Contains("--preview");
        Windowed = e.Args.Contains("--window");
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskLog");
        int dataIndex = Array.IndexOf(e.Args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < e.Args.Length) directory = Path.GetFullPath(e.Args[dataIndex + 1]);
        Store = new TaskStore(directory);
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..20];
        mutex = new Mutex(true, @"Local\DeskLog-" + identity, out ownsMutex);
        activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DeskLog-Activate-" + identity);
        windowActivationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DeskLog-Window-" + identity);
        if (!ownsMutex) { (Windowed ? windowActivationEvent : activationEvent).Set(); Shutdown(); return; }
        try { Store.Initialize(); Store.ReadAll(); }
        catch (Exception ex)
        {
            Log(ex.ToString());
            MessageBox.Show("无法打开本地记录，程序未修改已有数据。\n\n" + directory + "\n\n请检查磁盘、文件权限，或按使用说明恢复备份。", "桌面任务记录板", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(); return;
        }
        Log("startup " + Environment.OSVersion + " preview=" + Preview);
        CreateTray();
        CreateBoard();
        activationWait = ThreadPool.RegisterWaitForSingleObject(activationEvent, (_, _) => Dispatcher.BeginInvoke(() => board?.Reveal()), null, Timeout.Infinite, false);
        windowActivationWait = ThreadPool.RegisterWaitForSingleObject(windowActivationEvent, (_, _) => Dispatcher.BeginInvoke(() => board?.RevealWindowed()), null, Timeout.Infinite, false);
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        TryBackup();
    }

    private void CreateBoard()
    {
        if (exiting) return;
        board = new BoardWindow(this);
        MainWindow = board;
        board.Closed += (_, _) =>
        {
            board = null;
            // Closing the panel must not leave the tray without a usable board.
            if (!exiting) Dispatcher.BeginInvoke(async () => { await System.Threading.Tasks.Task.Delay(1500); if (!exiting && board == null) CreateBoard(); });
        };
        board.Show();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示任务板", null, (_, _) => board?.Reveal());
        menu.Items.Add("透明度…", null, (_, _) => board?.OpenAppearance());
        menu.Items.Add("隐藏到托盘", null, (_, _) => board?.HideToTray());
        menu.Items.Add("添加任务", null, (_, _) => Edit(null));
        menu.Items.Add("本月记录", null, (_, _) => OpenMonth());
        var autoStart = new Forms.ToolStripMenuItem("开机启动") { Checked = IsAutoStart() };
        autoStart.Click += (_, _) =>
        {
            try
            {
                bool enabled = !autoStart.Checked;
                using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
                if (enabled) key.SetValue("DeskLog", "\"" + Environment.ProcessPath + "\"");
                else key.DeleteValue("DeskLog", false);
                autoStart.Checked = enabled;
            }
            catch (Exception ex) { Log(ex.ToString()); MessageBox.Show("开机启动设置失败，请稍后重试。", "桌面任务记录板"); }
        };
        // Test instances must not change the installed user's startup setting.
        autoStart.Enabled = !Preview;
        menu.Items.Add(autoStart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        using var iconStream = GetResourceStream(new Uri("/DeskLog;component/Assets/DeskLog.ico", UriKind.Relative)).Stream;
        using var icon = new System.Drawing.Icon(iconStream);
        trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Text = "桌面任务记录板", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => board?.Reveal();
    }

    private static bool IsAutoStart()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(StartupKey); return key?.GetValue("DeskLog") is string value && value == "\"" + Environment.ProcessPath + "\""; }
        catch { return false; }
    }

    internal void Edit(TaskRecord? task)
    {
        if (editor != null) { editor.WindowState = WindowState.Normal; editor.Activate(); return; }
        editor = new TaskEditor(this, task);
        editor.Closed += (_, _) => editor = null;
        editor.Show(); editor.Activate();
    }
    internal void OpenMonth()
    {
        if (monthWindow == null)
        {
            monthWindow = new MonthWindow(this);
            monthWindow.Closed += (_, _) => monthWindow = null;
            monthWindow.Show();
        }
        monthWindow.WindowState = WindowState.Normal;
        monthWindow.Activate();
    }
    internal void RefreshMonth() => monthWindow?.RefreshRecords();
    internal void NotifyChanged() { board?.RefreshTasks(); RefreshMonth(); }
    internal void SaveFromBoard(TaskRecord task)
    {
        if (editor != null) { editor.Activate(); return; }
        try { Store.Save(task); NotifyChanged(); }
        catch (Exception ex) { Log(ex.ToString()); board?.Report("保存失败，任务状态未改变，请稍后重试。"); }
    }
    internal void TryBackup()
    {
        var day = DateOnly.FromDateTime(DateTime.Now);
        if (backedUpDay == day || DateTime.Now < nextBackupAttempt) return;
        try { Store.Backup(day); backedUpDay = day; }
        catch (Exception ex) { nextBackupAttempt = DateTime.Now.AddMinutes(5); Log("backup: " + ex); board?.Report("自动备份失败，当前记录仍保存在本机，将稍后重试。"); }
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => board?.RefreshEnvironment());
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => { board?.RefreshEnvironment(); NotifyChanged(); TryBackup(); });
    }
    internal void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Store.DirectoryPath);
            string path = Path.Combine(Store.DirectoryPath, "app.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine);
        }
        catch { /* Logging must never prevent saving or startup. */ }
    }
    internal void ExitApp()
    {
        if (editor != null)
        {
            editor.Activate();
            if (MessageBox.Show(editor, "编辑窗口仍然打开，退出将放弃尚未保存的输入。确定退出？", "退出", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        exiting = true;
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        exiting = true;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        activationWait?.Unregister(null);
        windowActivationWait?.Unregister(null);
        activationEvent?.Dispose();
        windowActivationEvent?.Dispose();
        tray?.Dispose();
        trayIcon?.Dispose();
        if (ownsMutex) mutex?.ReleaseMutex();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
