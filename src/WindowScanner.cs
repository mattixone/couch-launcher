namespace CouchLauncher;

sealed record WinInfo(IntPtr Hwnd, int Pid, string Process, string Title, Native.RECT Rect)
{
    public long Area => (long)Rect.Width * Rect.Height;
}

/// <summary>Snapshot of every app window on screen right now.</summary>
static class WindowScanner
{
    static readonly Dictionary<int, string> Names = new();
    static readonly int OwnPid = Environment.ProcessId;

    public static List<WinInfo> Scan()
    {
        var windows = new List<WinInfo>();
        var pids = new HashSet<int>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsAppWindow(h, out var title, out var rect)) return true;
            Native.GetWindowThreadProcessId(h, out int pid);
            if (pid == OwnPid) return true;
            pids.Add(pid);
            windows.Add(new WinInfo(h, pid, ProcessName(pid), title, rect));
            return true;
        }, IntPtr.Zero);

        // Forget processes that have gone, so a recycled PID can't inherit
        // a stale name.
        foreach (var gone in Names.Keys.Where(p => !pids.Contains(p)).ToList()) Names.Remove(gone);
        return windows;
    }

    /// <summary>"brave" for brave.exe. Cached — this runs twice a second.</summary>
    public static string ProcessName(int pid)
    {
        if (Names.TryGetValue(pid, out var name)) return name;
        var path = Native.ProcessPath(pid);
        name = path != null ? Path.GetFileNameWithoutExtension(path) : "";
        Names[pid] = name;
        return name;
    }
}
