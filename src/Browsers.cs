using Microsoft.Win32;

namespace CouchLauncher;

/// <summary>
/// The browsers web tiles can use. All three are Chromium underneath, which
/// is the point: they all take the same app-mode / separate-profile /
/// fullscreen switches, so each web tile gets its own chromeless window the
/// launcher can find, focus and close.
/// </summary>
static class Browsers
{
    public sealed record Info(string Id, string Label, string Exe, string TitleSuffix, string[] Folders);

    public static readonly Info[] All =
    {
        new("brave", "BRAVE", "brave.exe", "Brave", new[] { @"BraveSoftware\Brave-Browser\Application" }),
        new("chrome", "CHROME", "chrome.exe", "Google Chrome", new[] { @"Google\Chrome\Application" }),
        new("edge", "EDGE", "msedge.exe", "Edge", new[] { @"Microsoft\Edge\Application" }),
    };

    static readonly Dictionary<string, string?> Found = new();

    public static Info? Get(string id) => All.FirstOrDefault(b => b.Id == id);

    public static string? PathOf(Info browser)
    {
        if (Found.TryGetValue(browser.Id, out var cached)) return cached;

        var path = FromAppPaths(browser.Exe);
        if (path == null)
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            path = roots
                .SelectMany(root => browser.Folders.Select(folder => Path.Combine(root, folder, browser.Exe)))
                .FirstOrDefault(File.Exists);
        }
        Found[browser.Id] = path;
        return path;
    }

    public static bool Installed(Info browser) => PathOf(browser) != null;

    /// <summary>The tile's own choice if it's installed, else the first that is.</summary>
    public static Info? Resolve(string preferred)
    {
        var choice = Get(preferred);
        if (choice != null && Installed(choice)) return choice;
        return All.FirstOrDefault(Installed);
    }

    /// <summary>Re-check next time — e.g. after Brave gets installed.</summary>
    public static void Forget() => Found.Clear();

    static string? FromAppPaths(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (key?.GetValue(null) is string value)
                {
                    var path = value.Trim('"');
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
        }
        return null;
    }
}
