namespace CouchLauncher;

static class Paths
{
    /// <summary>
    /// Everything the launcher keeps: settings, log, icon cache and the
    /// browser profiles (your YouTube sign-in lives in there).
    ///
    /// Deliberately NOT %LocalAppData%\CouchLauncher — that's the installer's
    /// own folder, which it rewrites on every update.
    /// </summary>
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchLauncherData");

    public static string ConfigFile => Path.Combine(Data, "config.json");
    public static string LogFile => Path.Combine(Data, "launcher.log");
    public static string Profiles => Path.Combine(Data, "browser-profiles");
    public static string Icons => Path.Combine(Data, "icons");

    public static void Ensure()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Icons);
    }
}
