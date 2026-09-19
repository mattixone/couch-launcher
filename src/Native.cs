using System.Runtime.InteropServices;
using System.Text;

namespace CouchLauncher;

/// <summary>
/// Win32 calls. The launcher's whole job is juggling OTHER programs' windows,
/// which WPF has no API for.
/// </summary>
static class Native
{
    public const int WM_CLOSE = 0x0010;
    public const int WM_HOTKEY = 0x0312;
    public const byte VK_F11 = 0x7A;

    const int SW_RESTORE = 9;
    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const long WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L,
               WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L;
    const long WS_EX_TOOLWINDOW = 0x00000080L;
    const uint SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020,
               SWP_SHOWWINDOW = 0x0040, SWP_NOOWNERZORDER = 0x0200;
    const uint GW_OWNER = 4, GA_ROOTOWNER = 3;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
    const byte VK_MENU = 0x12;
    const int DWMWA_CLOAKED = 14;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint STILL_ACTIVE = 259;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

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
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder path, ref int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);

    /// <summary>
    /// Plain system-DPI awareness, set before WPF starts so every window
    /// coordinate we read from other apps is in real screen pixels.
    /// </summary>
    public static void UseSystemDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-2)); } catch { }
    }

    /// <summary>
    /// True for what a person would call "an app window": visible, top-level
    /// (not a dialog owned by another window), not a tool palette, not
    /// cloaked (the invisible shells Store apps leave around), with a title.
    /// </summary>
    public static bool IsAppWindow(IntPtr h, out string title, out RECT rect)
    {
        title = "";
        rect = default;
        if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        if ((GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        GetWindowRect(h, out rect);
        // Minimised windows shrink to a stub off-screen but still count as open.
        if (!IsIconic(h) && (rect.Width < 160 || rect.Height < 120)) return false;
        title = WindowTitle(h);
        return title.Length > 0;
    }

    public static string WindowTitle(IntPtr h)
    {
        int length = GetWindowTextLengthW(h);
        if (length <= 0) return "";
        var text = new StringBuilder(length + 1);
        GetWindowTextW(h, text, text.Capacity);
        return text.ToString();
    }

    public static string ClassName(IntPtr h)
    {
        var text = new StringBuilder(256);
        GetClassNameW(h, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>The top-level window a dialog or pop-up belongs to.</summary>
    public static IntPtr RootOwner(IntPtr h) => h == IntPtr.Zero ? h : GetAncestor(h, GA_ROOTOWNER);

    /// <summary>
    /// Nothing, the bare desktop or the taskbar has focus — i.e. whatever the
    /// user was in has gone away and they're looking at Windows itself.
    /// </summary>
    public static bool IsDesktopish(IntPtr h) =>
        h == IntPtr.Zero || ClassName(h) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    /// <summary>
    /// Brings a window to the front. Windows deliberately makes this hard for
    /// background programs, so escalate: the polite call, then borrowing the
    /// foreground thread's input state, then the synthetic-Alt unlock.
    /// </summary>
    public static bool ForceForeground(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (GetForegroundWindow() == h) return true;

        SetForegroundWindow(h);
        if (GetForegroundWindow() == h) return true;

        uint me = GetCurrentThreadId();
        uint them = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        if (them != 0 && them != me)
        {
            AttachThreadInput(me, them, true);
            SetForegroundWindow(h);
            BringWindowToTop(h);
            AttachThreadInput(me, them, false);
        }
        if (GetForegroundWindow() == h) return true;

        keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        SetForegroundWindow(h);
        keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        return GetForegroundWindow() == h;
    }

    public static void TapKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    static RECT MonitorRect(IntPtr h)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), ref info);
        return info.rcMonitor;
    }

    /// <summary>Already filling the whole screen (true fullscreen or borderless).</summary>
    public static bool CoversMonitor(IntPtr h)
    {
        if (!GetWindowRect(h, out var r)) return false;
        var m = MonitorRect(h);
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>
    /// Strips the title bar and borders and stretches the window over the
    /// whole screen. Windows then treats it as fullscreen and drops the
    /// taskbar behind it.
    /// </summary>
    public static void MakeBorderlessFullscreen(IntPtr h)
    {
        if (CoversMonitor(h)) return;
        if (IsZoomed(h) || IsIconic(h)) ShowWindow(h, SW_RESTORE);
        long style = GetWindowLongPtrW(h, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        SetWindowLongPtrW(h, GWL_STYLE, new IntPtr(style));
        var m = MonitorRect(h);
        SetWindowPos(h, IntPtr.Zero, m.Left, m.Top, m.Width, m.Height,
            SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
    }

    public static bool ProcessAlive(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetExitCodeProcess(h, out uint code) && code == STILL_ACTIVE; }
        finally { CloseHandle(h); }
    }

    public static string? ProcessPath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageNameW(h, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
