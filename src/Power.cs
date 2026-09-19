using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CouchLauncher;

static class Power
{
    public static void Sleep()
    {
        // Sleeping needs the shutdown privilege switched on in our own token
        // first; every user has it, but it starts disabled.
        EnableShutdownPrivilege();
        if (!SetSuspendState(false, false, false))
            Log.Write("sleep failed, error " + Marshal.GetLastWin32Error());
    }

    public static void Restart() => RunShutdown("/r");
    public static void ShutDown() => RunShutdown("/s");

    static void RunShutdown(string mode)
    {
        try
        {
            var start = new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add("/t");
            start.ArgumentList.Add("0");
            Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            Log.Write($"shutdown {mode} failed: {e.Message}");
        }
    }

    const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out long luid)) return;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
