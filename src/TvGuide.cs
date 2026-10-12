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
        if (IsVague(name)) return null;
        var wanted = Normal(name);
        if (wanted.Length == 0) return null;
        var withShows = Channels.Where(c => programmes.ContainsKey(c.Id)).ToList();
        return withShows.FirstOrDefault(c => Normal(c.Name) == wanted)
            ?? withShows.Where(c => Normal(c.Name).StartsWith(wanted) || wanted.StartsWith(Normal(c.Name)))
                        .OrderBy(c => Math.Abs(Normal(c.Name).Length - wanted.Length))
                        .FirstOrDefault();
    }

    /// <summary>What a channel's page shows, for <see cref="Identify"/>.</summary>
    public sealed record Clues(string Url, string Title, string[] Labels, string Text);

    public sealed record Candidate(Channel Channel, int Score, string Now);

    /// <summary>
    /// Which guide channel a page is showing, from what's on it. The strong
    /// clue: only one channel has a given show on right now, and the page
    /// nearly always shows the current show's title somewhere. Then the
    /// channel's own name in the address, title or labels breaks ties
    /// (simulcasts, a show on two channels). Best first.
    /// </summary>
    public List<Candidate> Identify(Clues clues)
    {
        var now = DateTimeOffset.UtcNow;
        var text = " " + Words(clues.Text) + " ";
        var title = Words(clues.Title);
        var labels = clues.Labels.Select(Words).ToList();
        var address = Uri.TryCreate(clues.Url, UriKind.Absolute, out var uri)
            ? Words(Uri.UnescapeDataString(uri.Host + " " + uri.AbsolutePath + " " + uri.Query))
            : Words(clues.Url);
        var everywhere = " " + string.Join(" ", new[] { title, address }.Concat(labels)) + " ";

        var scored = new List<Candidate>();
        foreach (var channel in Channels)
        {
            if (!programmes.TryGetValue(channel.Id, out var shows)) continue;
            int score = 0;

            // The show on now (guides drift a few minutes either side), and the ones either side of it.
            var onNow = shows.FirstOrDefault(p => p.Start <= now.AddMinutes(5) && now.AddMinutes(-5) < p.Stop);
            int at = onNow == null ? -1 : shows.IndexOf(onNow);
            score += TitleScore(onNow, text, everywhere, 10);
            if (at > 0) score += TitleScore(shows[at - 1], text, everywhere, 3);
            if (at >= 0 && at + 1 < shows.Count) score += TitleScore(shows[at + 1], text, everywhere, 4);

            // The channel's name: "7mate Sydney" → "7mate", in the address or on the page.
            var key = Words(StripPlace(channel.Name));
            if (key.Length > 0)
            {
                var compactKey = key.Replace(" ", "");
                bool shortKey = compactKey.Length < 3;    // "7", "9", "10": whole words only
                if (Contains(address, key, compactKey, shortKey)) score += 6;
                if (Contains(title, key, compactKey, shortKey)) score += 5;
                if (labels.Any(l => Contains(l, key, compactKey, shortKey))) score += 3;
            }
            if (score > 0) scored.Add(new Candidate(channel, score, onNow?.Title ?? ""));
        }

        // Of two equally good names, the shorter is the plainer channel ("SBS", not "SBS Food").
        return scored.OrderByDescending(c => c.Score).ThenBy(c => c.Channel.Name.Length).ToList();
    }

    /// <summary>Sure enough to use without asking: clearly ahead of the rest.</summary>
    public static bool Confident(List<Candidate> ranked) =>
        ranked.Count > 0 && ranked[0].Score >= 8 && (ranked.Count == 1 || ranked[0].Score >= ranked[1].Score + 3);

    static int TitleScore(Programme? show, string text, string everywhere, int points)
    {
        if (show == null) return 0;
        var title = Words(show.Title);
        // Short or generic titles ("News", "Movie") say little.
        if (title.Length < 4 || title is "news" or "movie" or "the movie" or "programmes resume" or "to be advised") return 0;
        var padded = " " + title + " ";
        return everywhere.Contains(padded) ? points + 2 : text.Contains(padded) ? points : 0;
    }

    static bool Contains(string haystack, string key, string compactKey, bool wholeWord) =>
        wholeWord
            ? (" " + haystack + " ").Contains(" " + key + " ")
            : (" " + haystack + " ").Contains(" " + key + " ") || haystack.Replace(" ", "").Contains(compactKey);

    static readonly string[] Places =
    {
        "sydney", "melbourne", "brisbane", "adelaide", "perth", "hobart", "canberra", "darwin",
        "nsw", "vic", "qld", "sa", "wa", "tas", "act", "nt", "hd", "regional", "gold coast", "newcastle",
    };

    /// <summary>A guide channel's name without its city: "7mate Sydney" → "7mate".</summary>
    public static string StripPlace(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && Places.Contains(words[^1].ToLowerInvariant().Trim('(', ')'))) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>Lower case, letters and digits only, single spaces.</summary>
    static string Words(string text)
    {
        var chars = text.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A channel name that says nothing about which channel it is — a web
    /// address, a service's own name ("SBS ON DEMAND"), "CHANNEL 3" — so it
    /// mustn't be matched by name (and the guide's name can replace it).
    /// </summary>
    public static bool IsVague(string name)
    {
        var plain = name.Trim().ToUpperInvariant();
        if (plain.Length < 3 || plain.StartsWith("WWW") || plain.StartsWith("CHANNEL ")) return true;
        if (plain.Contains('.') && !plain.Contains(' ')) return true;
        var squashed = new string(plain.Where(char.IsLetterOrDigit).ToArray());
        return squashed is "ABCIVIEW" or "IVIEW" or "SBSONDEMAND" or "ONDEMAND" or "7PLUS" or "9NOW" or "10PLAY" or "TENPLAY"
            or "ABC" or "SBS" or "TV" or "LIVETV" or "LIVE" or "SEVEN" or "NINE" or "TEN" or "WATCHLIVE";
    }

    static string Normal(string name)
    {
        var letters = new string(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return letters.EndsWith("HD") && letters.Length > 3 ? letters[..^2] : letters;
    }
}
