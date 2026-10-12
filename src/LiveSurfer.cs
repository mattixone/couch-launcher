using System.Text.Json;

namespace CouchLauncher;

/// <summary>
/// Live Surfer tiles: live channels from any of the streaming sites, in one
/// window, changed with the D-pad. The work in the pages is done by
/// sites/live-surfer.js, which SiteBridge puts into each one; this builds it
/// with the tile's channels and the launcher's colours.
///
/// To try changes to the script without releasing, put a copy in
/// CouchLauncherData\sites\live-surfer.js — it's used instead, from the next
/// page load.
/// </summary>
static class LiveSurfer
{
    public sealed record Service(string Name, string Url);

    /// <summary>Where BROWSE A SERVICE goes: each site's home or live page.</summary>
    public static readonly Service[] Services =
    {
        new("ABC IVIEW", "https://iview.abc.net.au/"),
        new("SBS ON DEMAND", "https://www.sbs.com.au/ondemand/live"),
        new("7PLUS", "https://7plus.com.au/"),
        new("9NOW", "https://www.9now.com.au/"),
        new("10", "https://10.com.au/"),
    };

    public static string OverrideFile => Path.Combine(Paths.Data, "sites", "live-surfer.js");

    static int version;

    /// <summary>Where the tile opens: the last channel watched, else its start page, else iview.</summary>
    public static string StartAddress(Tile tile)
    {
        if (tile.Channels.Count > 0)
            return tile.Channels[Math.Clamp(tile.LastChannel, 0, tile.Channels.Count - 1)].Url;
        return Uri.TryCreate(tile.Url.Trim(), UriKind.Absolute, out var url) && url.Host.Length > 0
            ? url.AbsoluteUri
            : Services[0].Url;
    }

    /// <summary>
    /// The script for a tile's pages, carrying its settings. <paramref name="current"/>
    /// and <paramref name="previous"/> are channel numbers (-1 for none);
    /// <paramref name="tunedAtMs"/> is when the channel was last changed (Unix ms).
    /// </summary>
    public static string? BuildScript(Tile tile, Theme palette, int current, int previous, long tunedAtMs)
    {
        var code = Read();
        if (code == null) return null;
        var settings = new
        {
            version = Interlocked.Increment(ref version),
            tile = tile.Name.ToUpperInvariant(),
            binding = SiteBridge.BindingName,
            fg = Hex(palette.Phosphor),
            bg = Hex(palette.Background),
            channels = tile.Channels.Select(c => new { name = c.Name.ToUpperInvariant(), url = c.Url }),
            current,
            previous,
            tunedAt = tunedAtMs,
            services = Services.Select(s => new { name = s.Name, url = s.Url }),
        };
        return $"(function (COUCH) {{\n'use strict';\n{code}\n}})({JsonSerializer.Serialize(settings)});\n//# sourceURL=live-surfer.js\n";
    }

    static string Hex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static string? Read()
    {
        try
        {
            if (File.Exists(OverrideFile)) return File.ReadAllText(OverrideFile);
            using var stream = typeof(LiveSurfer).Assembly.GetManifestResourceStream("CouchLauncher.sites.live-surfer.js");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e)
        {
            Log.Write("live surfer: couldn't read its script: " + e.Message);
            return null;
        }
    }
}
