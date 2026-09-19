using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace CouchLauncher;

/// <summary>Finding installed programs, so setting up a tile is picking from a list.</summary>
static class AppCatalog
{
    public sealed record Entry(string Name, string ShortcutPath);

    static readonly string[] Junk =
        { "uninstall", "readme", "read me", "help", "website", "documentation", "release notes", "license" };

    /// <summary>Everything in the Start menu (yours and all-users), minus uninstallers and docs.</summary>
    public static List<Entry> StartMenuApps()
    {
        var byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileNameWithoutExtension(file);
                if (Junk.Any(j => name.Contains(j, StringComparison.OrdinalIgnoreCase))) continue;
                byName.TryAdd(name, new Entry(name, file));
            }
        }
        return byName.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// What a shortcut actually runs. .lnk → the program and its arguments;
    /// .url → the link (Steam games are steam:// links). Falls back to the
    /// shortcut itself, which Windows can still open.
    /// </summary>
    public static (string Target, string Args) Resolve(string path)
    {
        try
        {
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var link = ShellLink.Read(path);
                if (!string.IsNullOrWhiteSpace(link.Target)) return link;
            }
            else if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                var line = File.ReadLines(path).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase));
                if (line != null) return (line[4..].Trim(), "");
            }
        }
        catch (Exception e)
        {
            Log.Write($"couldn't read shortcut {path}: {e.Message}");
        }
        return (path, "");
    }

    public static string? FindStremio()
    {
        try
        {
            var entry = StartMenuApps().FirstOrDefault(e => e.Name.Contains("Stremio", StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                var (target, _) = Resolve(entry.ShortcutPath);
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) return target;
            }
        }
        catch (Exception e)
        {
            Log.Write("stremio search failed: " + e.Message);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[]
        {
            Path.Combine(local, @"Programs\LNV\Stremio-4\stremio.exe"),
            Path.Combine(local, @"Programs\Stremio\stremio.exe"),
            Path.Combine(programs, @"Stremio\stremio.exe"),
        }.FirstOrDefault(File.Exists);
    }

    public static string? SteamExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamExe") is string value)
            {
                var path = value.Replace('/', '\\');
                if (File.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }
}

/// <summary>Reads .lnk files through the shell's own COM object.</summary>
static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    public static (string Target, string Args) Read(string lnkPath)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(2048);
            link.GetArguments(args, args.Capacity);
            return (target.ToString(), args.ToString());
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}
