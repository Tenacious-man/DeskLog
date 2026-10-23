using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace DeskLog.App;

/// <summary>Positions WPF without reparenting it into Explorer's renderer.</summary>
internal sealed class DesktopHost
{
    private readonly Window window;
    private nint originalOwner;
    private nint desktopOwner;
    private HwndSource? source;
    private nint Handle => new WindowInteropHelper(window).EnsureHandle();
    public DesktopHost(Window window) => this.window = window;

    public void MaintainDesktopLayer()
    {
        nint desktop = Native.GetShellWindow();
        Native.EnumWindows((candidate, _) =>
        {
            if (Native.IsWindowVisible(candidate) && Native.FindWindowEx(candidate, 0, "SHELLDLL_DefView", null) != 0)
            { desktop = candidate; return false; }
            return true;
        }, 0);
        if (desktop == 0) return;
        if (desktopOwner == desktop && Native.GetWindowLongPtr(Handle, -8) == desktop) return;
        if (desktopOwner == 0) originalOwner = Native.GetWindowLongPtr(Handle, -8);
        // An owner relationship preserves a separate top-level WPF surface.
        // Windows keeps owned windows above their owner during the shell's own Z-order transition.
        Marshal.SetLastPInvokeError(0);
        nint previous = Native.SetWindowLongPtr(Handle, -8, desktop);
        int error = Marshal.GetLastWin32Error();
        if (previous == 0 && error != 0) throw new System.ComponentModel.Win32Exception(error);
        desktopOwner = desktop;
        Native.SetWindowLongPtr(Handle, -20, (nint)((Native.GetWindowLongPtr(Handle, -20).ToInt64() | 0x80L) & ~0x40000L));
        source ??= HwndSource.FromHwnd(Handle);
        source?.RemoveHook(WindowMessage);
        source?.AddHook(WindowMessage);
        // Bottom placement is constrained by the owner; no recurring restore or temporary topmost.
        Native.SetWindowPos(Handle, 1, 0, 0, 0, 0, 0x0013 | 0x0200);
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (desktopOwner != 0 && message == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF020)
            handled = true; // A pinned desktop panel has no minimize operation.
        return 0;
    }

    public void ReleaseDesktopLayer()
    {
        if (desktopOwner == 0) return;
        source?.RemoveHook(WindowMessage);
        Native.SetWindowLongPtr(Handle, -8, Native.IsWindow(originalOwner) ? originalOwner : 0);
        desktopOwner = 0;
        Native.SetWindowPos(Handle, -2, 0, 0, 0, 0, 0x0013 | 0x0200);
    }
    public void SetTransparency(int percent)
    {
        window.Opacity = (100 - percent) / 100.0;
    }

    public void Position(int screenX, int screenY, int width, int height)
    {
        double scale = Dpi / 96.0;
        window.Left = screenX / scale;
        window.Top = screenY / scale;
        window.Width = width / scale;
        window.Height = height / scale;
    }
    public Native.Rect Bounds { get { Native.GetWindowRect(Handle, out var rect); return rect; } }
    public uint Dpi => Math.Max(96, Native.GetDpiForWindow(Handle));
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nint GetShellWindow();
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    internal delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int length);
    internal static string ClassName(nint hwnd) { var text = new StringBuilder(256); GetClassName(hwnd, text, text.Capacity); return text.ToString(); }
}