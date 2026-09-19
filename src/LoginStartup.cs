using Microsoft.Win32;

namespace CouchLauncher;

/// <summary>"Start with Windows": the per-user Run key, no admin needed.</summary>
static class LoginStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "CouchLauncher";

    public static void Apply(bool enabled)
    {
        if (!enabled)
        {
            Remove();
            return;
        }
        // Only the installed copy registers itself — the installer keeps it at
        // a fixed path across updates. A build folder run by hand shouldn't.
        if (!Updater.IsInstalled || Environment.ProcessPath is not { } exe) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        catch (Exception e)
        {
            Log.Write("couldn't set start-with-windows: " + e.Message);
        }
    }

    public static void Remove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e)
        {
            Log.Write("couldn't clear start-with-windows: " + e.Message);
        }
    }
}
