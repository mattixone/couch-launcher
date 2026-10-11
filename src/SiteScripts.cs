using System.Text.Json;

namespace CouchLauncher;

/// <summary>
/// Couch mode: scripts that make streaming sites behave like TV apps. Each
/// site gets its own small script (sites/*.js) on top of a shared one
/// (sites/couch-tv.js) that does the TV look, the on-screen channel bar and
/// list, and the D-pad keys. SiteBridge puts them into the tile's pages.
///
/// To try changes without releasing, put a file with the same name in
/// CouchLauncherData\sites\ — it's used instead, from the next page load.
/// </summary>
static class SiteScripts
{
    public sealed record Site(string Id, string Label, string[] Hosts);

    public static readonly Site[] All =
    {
        new("iview", "ABC IVIEW", new[] { "iview.abc.net.au" }),
    };

    public static string OverrideDir => Path.Combine(Paths.Data, "sites");

    /// <summary>The site a web address belongs to, if couch mode knows it.</summary>
    public static Site? For(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        return All.FirstOrDefault(s => s.Hosts.Any(h => host == h || host.EndsWith("." + h)));
    }

    /// <summary>Couch mode is on for this tile and there's a script for its site.</summary>
    public static bool Enhances(Tile tile) => tile.Kind == TileKind.Web && tile.CouchMode && For(tile.Url) != null;

    /// <summary>
    /// The script for one site, ready to run in its pages: the site's part,
    /// the shared part, and the settings they need, in one function.
    /// </summary>
    public static string? Build(Site site, object settings)
    {
        var siteJs = Read($"{site.Id}.js");
        var common = Read("couch-tv.js");
        if (siteJs == null || common == null) return null;
        var json = JsonSerializer.Serialize(settings);
        return $"(function (COUCH) {{\n'use strict';\n{siteJs}\n{common}\n}})({json});\n//# sourceURL=couch-{site.Id}.js\n";
    }

    static string? Read(string name)
    {
        try
        {
            var local = Path.Combine(OverrideDir, name);
            if (File.Exists(local)) return File.ReadAllText(local);
            using var stream = typeof(SiteScripts).Assembly.GetManifestResourceStream("CouchLauncher.sites." + name);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e)
        {
            Log.Write($"couch mode: couldn't read {name}: {e.Message}");
            return null;
        }
    }
}
