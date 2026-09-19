using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace CouchLauncher;

/// <summary>
/// Self-update from GitHub Releases. release.sh (on the Mac) publishes a new
/// release; this finds it, downloads just the difference, and restarts into it.
/// </summary>
static class Updater
{
    /// <summary>Baked in at build time by release.sh.</summary>
    public static readonly string? RepoUrl = typeof(Updater).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value is { Length: > 0 } url ? url : null;

    static UpdateManager? manager;

    static UpdateManager? Manager
    {
        get
        {
            if (manager == null && RepoUrl != null)
            {
                try { manager = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
                catch (Exception e) { Log.Write("updater unavailable: " + e.Message); }
            }
            return manager;
        }
    }

    /// <summary>False when run straight from a build folder rather than installed.</summary>
    public static bool IsInstalled => Manager?.IsInstalled ?? false;

    public static string Version =>
        (IsInstalled ? Manager!.CurrentVersion?.ToString() : null)
        ?? typeof(Updater).Assembly.GetName().Version?.ToString(3)
        ?? "dev";

    /// <summary>
    /// Checks for a newer release and, if there is one, installs it and
    /// restarts (so this only returns when there was nothing to do or it
    /// failed). <paramref name="progress"/> may be called off the UI thread.
    /// </summary>
    public static async Task<string> UpdateAsync(Action<string> progress)
    {
        var m = Manager;
        if (m == null) return "UPDATES ARE OFF IN THIS BUILD";
        if (!m.IsInstalled) return "UPDATES ONLY WORK IN THE INSTALLED APP";
        try
        {
            progress("CHECKING FOR UPDATES…");
            var update = await m.CheckForUpdatesAsync();
            if (update == null) return $"UP TO DATE (v{Version})";

            var target = update.TargetFullRelease.Version.ToString();
            Log.Write($"update {Version} -> {target}");
            await m.DownloadUpdatesAsync(update, percent => progress($"DOWNLOADING v{target}… {percent}%"));
            progress($"INSTALLING v{target}…");
            m.ApplyUpdatesAndRestart(update.TargetFullRelease, Array.Empty<string>());
            return "RESTARTING…";
        }
        catch (Exception e)
        {
            Log.Write("update failed: " + e);
            return "UPDATE FAILED — IS THE PC ONLINE?";
        }
    }
}
