namespace CouchLauncher;

/// <summary>
/// Plain text log in the data folder. The PC is across the room from whoever
/// is debugging it, so anything worth knowing about a failure goes here.
/// </summary>
static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var file = Paths.LogFile;
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be the thing that takes the launcher down.
        }
    }
}
