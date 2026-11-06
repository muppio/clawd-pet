using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClawdPet;

/// The few Win32 calls WPF doesn't wrap: window styles, the global hotkey,
/// the cursor, monitors, and enumerating other apps' windows (the Codex pet).
static class Native
{
    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_CAPTION = 0x00C00000;

    public const int WM_HOTKEY = 0x0312;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int MA_NOACTIVATE = 3;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_C = 0x43;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"({Left},{Top}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);

    public static long GetExStyle(IntPtr hWnd) => GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
    public static long GetStyle(IntPtr hWnd) => GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();

    public static void AddExStyle(IntPtr hWnd, long bits) =>
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(GetExStyle(hWnd) | bits));

    public static string Title(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// Hidden by DWM (another virtual desktop, a suspended UWP window) though "visible".
    public static bool IsCloaked(IntPtr hWnd) =>
        DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// The monitor a window is on, in physical pixels.
    public static RECT MonitorRect(IntPtr hWnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST), ref info);
        return info.rcMonitor;
    }

    /// Back above other always-on-top windows, without taking focus.
    public static void KeepOnTop(IntPtr hWnd) =>
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// A GUI app has no console: borrow the terminal's when one started us
    /// (output piped into /pet or `| Out-String` needs nothing).
    public static void ConsoleOut()
    {
        if (!Console.IsOutputRedirected) AttachConsole(-1);
    }
}
