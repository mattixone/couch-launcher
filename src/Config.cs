using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouchLauncher;

enum TileKind { Web, App, Steam, Power, Settings, LiveSurfer, Folder, Back }

/// <summary>
/// One tile on the home screen. A single flat record for every kind — only
/// the fields that matter to its <see cref="Kind"/> are used, the rest just
/// sit there, so switching a tile between kinds in the editor loses nothing.
/// </summary>
sealed class Tile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "NEW TILE";
    public TileKind Kind { get; set; } = TileKind.Web;

    // --- Web page ---
    public string Url { get; set; } = "https://";
    /// <summary>"brave", "chrome" or "edge" — see Browsers.All.</summary>
    public string Browser { get; set; } = "brave";
    /// <summary>Tells the site we're a smart TV. youtube.com/tv only shows its
    /// TV interface to TVs and redirects everything else.</summary>
    public bool TvMode { get; set; }

    // --- Live Surfer (also uses Url, as the page it opens with no channels yet, and Browser) ---
    /// <summary>Live channels from any streaming site, in channel order.</summary>
    public List<LiveChannel> Channels { get; set; } = new();
    /// <summary>The channel it was on last, which it opens on next time.</summary>
    public int LastChannel { get; set; }
    /// <summary>The TV guide page X (or A twice) opens: Freeview's, unless you pick another.</summary>
    public string GuidePage { get; set; } = LiveSurfer.DefaultGuidePage;

    // --- App ---
    /// <summary>Program to run: an .exe, or a steam:// style link.</summary>
    public string Target { get; set; } = "";
    public string Args { get; set; } = "";
    /// <summary>Process name(s), comma separated, whose windows count as this
    /// app being open. Filled in automatically when you pick the program.</summary>
    public string MatchProcess { get; set; } = "";
    /// <summary>Optional: only windows whose title contains this.</summary>
    public string MatchTitle { get; set; } = "";
    public bool ForceFullscreen { get; set; } = true;

    // --- Any kind ---
    /// <summary>Kept in the tile list but left off the home screen and the
    /// switcher, for tiles you might want back.</summary>
    public bool Hidden { get; set; }

    /// <summary>What the Retroid's A button (a Play/Pause media key) does while
    /// this tile's app is in front: null = automatic (Enter for TV-mode and
    /// Live Surfer tiles),
    /// true = press Enter instead, false = leave it as Play/Pause. Menus in
    /// apps like YouTube TV only respond to Enter.</summary>
    public bool? ASelects { get; set; }
    [JsonIgnore] public bool AButtonSelects => ASelects ?? (TvMode || Kind == TileKind.LiveSurfer);
    /// <summary>Opens in its own browser window, which the launcher tracks by profile.</summary>
    [JsonIgnore] public bool InBrowser => Kind is TileKind.Web or TileKind.LiveSurfer;

    /// <summary>Close any extra windows the app opens. Turn off while signing
    /// in for the first time — some logins happen in a pop-up.</summary>
    public bool BlockPopups { get; set; } = true;
    /// <summary>Custom icon (image or .exe). Empty = work it out.</summary>
    public string IconPath { get; set; } = "";
    /// <summary>Folder on the home screen. Tiles with the same name (any
    /// case) share one folder tile; empty = straight on the home screen.</summary>
    public string Folder { get; set; } = "";

    [JsonIgnore] public string FolderKey => Folder.Trim().ToUpperInvariant();
    /// <summary>Made up by the home screen, never saved: power, settings, and folders' tiles.</summary>
    [JsonIgnore] public bool IsBuiltIn => Kind is TileKind.Power or TileKind.Settings or TileKind.Folder or TileKind.Back;
}

/// <summary>One of a Live Surfer tile's channels: the page where it plays.</summary>
sealed class LiveChannel
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>The two tiles every home screen ends with. Not saved or editable.</summary>
static class BuiltIn
{
    public static readonly Tile Power = new() { Id = "builtin-power", Name = "POWER", Kind = TileKind.Power };
    public static readonly Tile Settings = new() { Id = "builtin-settings", Name = "SETTINGS", Kind = TileKind.Settings };
    /// <summary>First tile inside a folder: back out to the home screen.</summary>
    public static readonly Tile Back = new() { Id = "builtin-back", Name = "BACK", Kind = TileKind.Back };

    /// <summary>The tile standing in for a folder on the home screen.</summary>
    public static Tile Folder(string name) =>
        new() { Id = "folder-" + name.Trim().ToUpperInvariant(), Name = name.Trim(), Kind = TileKind.Folder, Folder = name };
}

sealed class LauncherConfig
{
    public const string DefaultTvUserAgent =
        "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.5) AppleWebKit/537.36 (KHTML, like Gecko) 85.0.4183.93/6.5 TV Safari/537.36";

    public string Theme { get; set; } = "FALLOUT";
    public bool StartAtLogin { get; set; } = true;
    /// <summary>LARGE, MEDIUM or SMALL: 4, 5 or 6 tiles to a row.</summary>
    public string TileSize { get; set; } = "LARGE";
    public string HomeHotkey { get; set; } = "Ctrl+Alt+Shift+H";
    public string SwitchHotkey { get; set; } = "Ctrl+Alt+Shift+S";
    /// <summary>Xbox-style controllers drive the launcher too.</summary>
    public bool Controller { get; set; } = true;
    /// <summary>Controller buttons to hold, from inside any app, to come back
    /// to the tiles. Names: A B X Y LB RB Back Start LeftThumb RightThumb DpadUp…</summary>
    public string ControllerHomeCombo { get; set; } = "Back+Start";
    /// <summary>How long the combo must be held, so a stray press in a game doesn't count.</summary>
    public int ControllerHoldMs { get; set; } = 800;
    /// <summary>What TV-mode tiles claim to be. Here rather than hard-coded so
    /// it can be swapped if YouTube stops accepting this one.</summary>
    public string TvUserAgent { get; set; } = DefaultTvUserAgent;
    /// <summary>Serve the phone remote page to guests on the Wi-Fi.</summary>
    public bool PhoneRemote { get; set; } = true;
    public int PhoneRemotePort { get; set; } = 80;
    /// <summary>Where phones send their button presses: the Pico's name or IP.</summary>
    public string PicoAddress { get; set; } = "couchcommander.local";
    /// <summary>Let the phone remote's POWER menu sleep, restart or shut down the PC.</summary>
    public bool PhoneRemotePower { get; set; } = true;
    public List<Tile> Tiles { get; set; } = new();

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var loaded = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(Paths.ConfigFile), Json);
                if (loaded != null)
                {
                    loaded.Tiles.RemoveAll(t => t.IsBuiltIn);
                    return loaded;
                }
            }
        }
        catch (Exception e)
        {
            Log.Write("config unreadable, starting fresh: " + e.Message);
            try { File.Copy(Paths.ConfigFile, Paths.ConfigFile + ".broken", overwrite: true); } catch { }
        }

        var seeded = Seed();
        seeded.Save();
        return seeded;
    }

    public void Save()
    {
        try
        {
            // Write-then-swap so a crash mid-save can't leave half a file.
            var tmp = Paths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, Paths.ConfigFile, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Write("couldn't save config: " + e.Message);
        }
    }

    /// <summary>First-run tiles: the setup described when this was planned.</summary>
    static LauncherConfig Seed()
    {
        var config = new LauncherConfig();
        config.Tiles.Add(new Tile { Name = "YOUTUBE", Url = "https://www.youtube.com", Browser = "brave" });
        config.Tiles.Add(new Tile { Name = "YOUTUBE TV", Url = "https://www.youtube.com/tv", Browser = "brave", TvMode = true });

        var stremio = AppCatalog.FindStremio();
        config.Tiles.Add(new Tile
        {
            Name = "STREMIO",
            Kind = TileKind.App,
            Target = stremio ?? "",
            MatchProcess = stremio != null ? Path.GetFileNameWithoutExtension(stremio) : "stremio",
            BlockPopups = false,
        });

        config.Tiles.Add(new Tile { Name = "STEAM", Kind = TileKind.Steam, BlockPopups = false });
        config.Tiles.Add(new Tile { Name = "SLITHER.IO", Url = "https://slither.io", Browser = "chrome" });

        Log.Write($"first run: seeded default tiles (stremio {(stremio ?? "not found")})");
        return config;
    }
}
