using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Xml;

namespace CouchLauncher;

/// <summary>
/// The TV guide for Live Surfer: what's on now and next, and the guide grid.
/// Read from a free XMLTV file (the standard guide format PVRs use), such as
/// Matt Huisman's i.mjh.nz or xmltv.net, downloaded at most every few hours
/// and kept on disk so a failed download still leaves yesterday's.
/// </summary>
sealed class TvGuide
{
    public sealed record Programme(DateTimeOffset Start, DateTimeOffset Stop, string Title, string Episode, string Description);
    public sealed record Channel(string Id, string Name);

    /// <summary>The guides the tile editor offers, by city. {0} is the city.</summary>
    public static readonly (string Label, string Pattern)[] Sources =
    {
        ("I.MJH.NZ", "https://i.mjh.nz/au/{0}/epg.xml.gz"),
        ("XMLTV.NET", "http://xmltv.net/xml_files/{0}.xml"),
    };
    public static readonly string[] Cities = { "Sydney", "Melbourne", "Brisbane", "Adelaide", "Perth", "Hobart", "Canberra", "Darwin" };
    public const string DefaultUrl = "https://i.mjh.nz/au/Sydney/epg.xml.gz";

    /// <summary>A channel's guide set to "none" in the editor.</summary>
    public const string NoGuide = "none";

    static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static readonly Dictionary<string, TvGuide> Loaded = new();
    static readonly Dictionary<string, Task<TvGuide?>> Loading = new();

    public List<Channel> Channels { get; } = new();
    readonly Dictionary<string, List<Programme>> programmes = new();
    public DateTime FetchedAt { get; private set; }

    static TvGuide()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"CouchLauncher/{Updater.Version}");
    }

    public IReadOnlyList<Programme> For(string channelId) =>
        programmes.TryGetValue(channelId, out var list) ? list : Array.Empty<Programme>();

    /// <summary>
    /// The guide at this address: from memory, else downloaded (or from disk
    /// if that fails). Call on the UI thread; the download runs off it.
    /// </summary>
    public static Task<TvGuide?> GetAsync(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return Task.FromResult<TvGuide?>(null);
        if (Loaded.TryGetValue(url, out var guide) && DateTime.UtcNow - guide.FetchedAt < RefreshAfter)
            return Task.FromResult<TvGuide?>(guide);
        if (Loading.TryGetValue(url, out var pending)) return pending;
        var task = LoadAsync(url, Loaded.GetValueOrDefault(url));
        Loading[url] = task;
        _ = task.ContinueWith(t =>
        {
            Loading.Remove(url);
            if (t.Result != null) Loaded[url] = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return task;
    }

    static async Task<TvGuide?> LoadAsync(string url, TvGuide? stale)
    {
        var file = CacheFile(url);
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var guide = await Task.Run(() => Parse(bytes));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, bytes);
            Log.Write($"tv guide: {guide.Channels.Count} channels from {url}");
            return guide;
        }
        catch (Exception e)
        {
            Log.Write($"tv guide: couldn't download {url}: {e.Message}");
        }
        if (stale != null)
        {
            stale.FetchedAt = DateTime.UtcNow - RefreshAfter + TimeSpan.FromMinutes(30);    // try again in a while
            return stale;
        }
        try
        {
            if (!File.Exists(file)) return null;
            var guide = await Task.Run(() => Parse(File.ReadAllBytes(file)));
            guide.FetchedAt = DateTime.UtcNow - RefreshAfter + TimeSpan.FromMinutes(30);
            Log.Write($"tv guide: using the copy saved {File.GetLastWriteTime(file):d MMM HH:mm}");
            return guide;
        }
        catch (Exception e)
        {
            Log.Write($"tv guide: saved copy unreadable: {e.Message}");
            return null;
        }
    }

    static string CacheFile(string url) =>
        Path.Combine(Paths.Data, "guide", Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16] + ".xml");

    /// <summary>Reads an XMLTV file, gzipped or not. Keeps from a few hours ago to a day and a half ahead.</summary>
    public static TvGuide Parse(byte[] bytes)
    {
        Stream stream = new MemoryStream(bytes);
        if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B) stream = new GZipStream(stream, CompressionMode.Decompress);
        var guide = new TvGuide { FetchedAt = DateTime.UtcNow };
        var from = DateTimeOffset.UtcNow.AddHours(-3);
        var until = DateTimeOffset.UtcNow.AddHours(36);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true };
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Name == "channel")
            {
                var id = reader.GetAttribute("id") ?? "";
                string name = "";
                using var inner = reader.ReadSubtree();
                while (inner.Read())
                    if (inner.NodeType == XmlNodeType.Element && inner.Name == "display-name" && name.Length == 0)
                        name = inner.ReadElementContentAsString().Trim();
                if (id.Length > 0) guide.Channels.Add(new Channel(id, name.Length > 0 ? name : id));
            }
            else if (reader.Name == "programme")
            {
                var channel = reader.GetAttribute("channel") ?? "";
                var start = Time(reader.GetAttribute("start"));
                var stop = Time(reader.GetAttribute("stop"));
                string title = "", episode = "", description = "";
                using (var inner = reader.ReadSubtree())
                {
                    while (inner.Read())
                    {
                        if (inner.NodeType != XmlNodeType.Element) continue;
                        switch (inner.Name)
                        {
                            case "title" when title.Length == 0: title = inner.ReadElementContentAsString().Trim(); break;
                            case "sub-title" when episode.Length == 0: episode = inner.ReadElementContentAsString().Trim(); break;
                            case "desc" when description.Length == 0: description = inner.ReadElementContentAsString().Trim(); break;
                        }
                    }
                }
                if (start == null || stop == null || stop <= from || start >= until || title.Length == 0) continue;
                if (!guide.programmes.TryGetValue(channel, out var list)) guide.programmes[channel] = list = new();
                list.Add(new Programme(start.Value, stop.Value, title, episode, description));
            }
        }
        foreach (var list in guide.programmes.Values) list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return guide;
    }

    /// <summary>XMLTV times: "20261012183000 +1100" (the zone may be missing: then it's UTC).</summary>
    static DateTimeOffset? Time(string? text)
    {
        if (text == null || text.Length < 12) return null;
        var digits = text.Length >= 14 ? text[..14] : text[..12] + "00";
        if (!DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        var offset = TimeSpan.Zero;
        var zone = text[digits.Length..].Trim();
        if (zone.Length == 5 && (zone[0] == '+' || zone[0] == '-') &&
            int.TryParse(zone[1..3], out var h) && int.TryParse(zone[3..5], out var m))
            offset = (zone[0] == '-' ? -1 : 1) * new TimeSpan(h, m, 0);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>
    /// The guide channel for one of your channels: the one you picked, else
    /// the one whose name matches best ("7MATE" → "7mate Sydney"), else none.
    /// </summary>
    public Channel? Match(string name, string chosenId)
    {
        if (chosenId == NoGuide) return null;
        if (chosenId.Length > 0) return Channels.FirstOrDefault(c => c.Id == chosenId);
        var wanted = Normal(name);
        if (wanted.Length == 0) return null;
        var withShows = Channels.Where(c => programmes.ContainsKey(c.Id)).ToList();
        return withShows.FirstOrDefault(c => Normal(c.Name) == wanted)
            ?? withShows.Where(c => Normal(c.Name).StartsWith(wanted) || wanted.StartsWith(Normal(c.Name)))
                        .OrderBy(c => Math.Abs(Normal(c.Name).Length - wanted.Length))
                        .FirstOrDefault();
    }

    static string Normal(string name)
    {
        var letters = new string(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return letters.EndsWith("HD") && letters.Length > 3 ? letters[..^2] : letters;
    }
}
