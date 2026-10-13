using System.Diagnostics;
using System.Management;

namespace CouchLauncher;

/// <summary>What the launcher knows about one tile's app right now.</summary>
sealed class TileRuntime
{
    public TileRuntime(Tile tile) { Tile = tile; }

    public Tile Tile { get; set; }
    /// <summary>Web tiles: the browser process that owns this tile's window.</summary>
    public int? BrowserPid { get; set; }
    /// <summary>Web tiles: which browser that process actually is.</summary>
    public string BrowserId { get; set; } = "";
    /// <summary>The window the tile "is"; any other of its windows are pop-ups.</summary>
    public IntPtr Main { get; set; }
    public List<WinInfo> Windows { get; } = new();
    public bool Running => Windows.Count > 0;
    /// <summary>Just launched — focus it (and fullscreen it) once its window shows.</summary>
    public bool PendingFocus { get; set; }
    public DateTime LaunchedAt { get; set; }

    // --- Live Surfer ---
    /// <summary>The channel it's on (or was last sent to), -1 for none.</summary>
    public int Channel { get; set; } = -1;
    public int PreviousChannel { get; set; } = -1;
    /// <summary>When the channel last changed, Unix milliseconds.</summary>
    public long TunedAtMs { get; set; }
    /// <summary>SCAN ALL CHANNELS, while it's going.</summary>
    public SurferScan? Scan { get; set; }
    /// <summary>A message for the next page to show (the scan's result), and when it was set.</summary>
    public (string Text, long AtMs)? Notice { get; set; }
}

/// <summary>Live Surfer visiting each channel in turn to find it in the TV guide.</summary>
sealed class SurferScan
{
    public Queue<int> Waiting { get; } = new();
    public int Total { get; init; }
    public int Index { get; set; } = -1;
    public int ReturnTo { get; init; }
    public DateTime StepStarted { get; set; }
    public string Session { get; set; } = "";
    public List<string> Found { get; } = new();
}

/// <summary>
/// Launches tiles and keeps track of their windows, so that choosing an open
/// tile brings its window back instead of starting a second copy.
///
/// Web tiles are the tricky ones. Each runs as its own browser instance with
/// its own profile folder, which is what lets us tell them apart: YouTube's
/// window belongs to the browser process started with YouTube's profile, and
/// nothing else. That process is remembered, and re-found through WMI if the
/// launcher itself restarts (after an update, say) while tiles are open.
/// </summary>
sealed class AppManager
{
    static readonly string[] SteamProcesses = { "steamwebhelper", "steam" };
    const string SteamWindowTitle = "Big Picture";

    readonly LauncherConfig config;
    readonly Dictionary<string, TileRuntime> byId = new();
    readonly Dictionary<IntPtr, DateTime> firstSeen = new();
    readonly Dictionary<IntPtr, DateTime> closeSent = new();
    /// <summary>Windows a fullscreen fix is already under way for.</summary>
    readonly HashSet<IntPtr> restoringFullscreen = new();
    /// <summary>Each open Live Surfer tile's connection to its browser, by tile id.</summary>
    readonly Dictionary<string, SiteBridge> bridges = new();
    DateTime lastDiscovery = DateTime.MinValue;
    /// <summary>After closing the Steam tile, keep sending Steam's normal
    /// window to the tray until this time.</summary>
    DateTime steamWindowCloseUntil = DateTime.MinValue;

    /// <summary>The UI thread, where the tile list may be changed.</summary>
    readonly SynchronizationContext ui;

    public AppManager(LauncherConfig config)
    {
        this.config = config;
        ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Sync();
    }

    /// <summary>Call after the tile list is edited.</summary>
    public void Sync()
    {
        var ids = config.Tiles.Select(t => t.Id).ToHashSet();
        foreach (var gone in byId.Keys.Where(id => !ids.Contains(id)).ToList()) byId.Remove(gone);
        foreach (var gone in bridges.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            bridges[gone].Dispose();
            bridges.Remove(gone);
        }
        foreach (var tile in config.Tiles) For(tile);
        // Channels may have been edited: tell any open Live Surfer.
        foreach (var id in bridges.Keys.ToList())
            if (For(id) is { } r) _ = PushSurferAsync(r);
    }

    public TileRuntime For(Tile tile)
    {
        if (!byId.TryGetValue(tile.Id, out var runtime)) byId[tile.Id] = runtime = new TileRuntime(tile);
        runtime.Tile = tile;
        return runtime;
    }

    public TileRuntime? For(string id) => byId.GetValueOrDefault(id);

    /// <summary>Re-reads every window on screen and sorts them into tiles. Runs twice a second.</summary>
    public void Refresh()
    {
        var windows = WindowScanner.Scan();
        var now = DateTime.UtcNow;
        var live = windows.Select(w => w.Hwnd).ToHashSet();
        foreach (var w in windows)
        {
            // Logged because the launcher recognises apps by process name and
            // window title — when one isn't recognised, this shows what it's
            // really called.
            if (firstSeen.TryAdd(w.Hwnd, now)) Log.Write($"window: {w.Process} — \"{w.Title}\"");
        }
        foreach (var gone in firstSeen.Keys.Where(h => !live.Contains(h)).ToList())
        {
            firstSeen.Remove(gone);
            closeSent.Remove(gone);
        }

        bool needDiscovery = false;
        foreach (var r in byId.Values)
        {
            r.Windows.Clear();
            var tile = r.Tile;
            switch (tile.Kind)
            {
                case TileKind.Web:
                case TileKind.LiveSurfer:
                    if (r.BrowserPid is int dead && !Native.ProcessAlive(dead)) r.BrowserPid = null;
                    if (r.BrowserPid is int pid) r.Windows.AddRange(windows.Where(w => w.Pid == pid));
                    // Launched, but the process we started handed off to an
                    // instance we didn't know about and quit: go find it.
                    else if (r.PendingFocus) needDiscovery = true;
                    break;
                case TileKind.App:
                case TileKind.Steam:
                    var names = MatchNames(tile);
                    var title = tile.Kind == TileKind.Steam && tile.MatchTitle.Length == 0 ? SteamWindowTitle : tile.MatchTitle;
                    if (names.Length == 0) break;
                    r.Windows.AddRange(windows.Where(w =>
                        names.Contains(w.Process, StringComparer.OrdinalIgnoreCase) &&
                        (title.Length == 0 || w.Title.Contains(title, StringComparison.OrdinalIgnoreCase))));
                    break;
            }

            if (bridges.TryGetValue(tile.Id, out var bridge) && (bridge.Finished || r.BrowserPid == null && !r.PendingFocus))
            {
                bridge.Dispose();
                bridges.Remove(tile.Id);
            }

            if (!r.Running)
            {
                r.Main = IntPtr.Zero;
                if (r.PendingFocus && (now - r.LaunchedAt).TotalSeconds > 45) r.PendingFocus = false;
                continue;
            }
            if (!r.Windows.Any(w => w.Hwnd == r.Main)) r.Main = PickMain(r);
            if (tile.BlockPopups && r.Windows.Count > 1) ClosePopups(r, now);

            // Wait a beat after a new window appears: apps often show it and
            // then resize or re-style it themselves.
            if (r.PendingFocus && firstSeen.TryGetValue(r.Main, out var seen) && (now - seen).TotalMilliseconds > 700)
            {
                r.PendingFocus = false;
                Focus(r, userAction: false);
            }
        }

        if (now < steamWindowCloseUntil) CloseSteamDesktopWindows(windows, now);

        // A scanned channel that never sent its clues (it didn't load, say): move on.
        foreach (var r in byId.Values)
        {
            if (r.Scan is not { } scan || (now - scan.StepStarted).TotalSeconds < 40) continue;
            if (!bridges.TryGetValue(r.Tile.Id, out var bridge) || bridge.Finished)
            {
                r.Scan = null;
                continue;
            }
            Log.Write($"{r.Tile.Name}: scan gave up on channel {scan.Index + 1}");
            if (scan.Index >= 0 && scan.Index < r.Tile.Channels.Count) scan.Found.Add($"{r.Tile.Channels[scan.Index].Name} ?");
            scan.StepStarted = now;
            _ = ScanNextAsync(r, bridge);
        }

        if (needDiscovery && (now - lastDiscovery).TotalSeconds > 5) _ = DiscoverAsync();
    }

    /// <summary>Which tile (if any) a window — or the dialog on top of it — belongs to.</summary>
    public TileRuntime? ByWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var root = Native.RootOwner(hwnd);
        foreach (var r in byId.Values)
            foreach (var w in r.Windows)
                if (w.Hwnd == hwnd || w.Hwnd == root) return r;

        // Fall back to the owning process, for windows the scan skips (no
        // title yet, say).
        Native.GetWindowThreadProcessId(root, out int pid);
        foreach (var r in byId.Values.Where(r => r.Running))
        {
            if (r.Tile.InBrowser && r.BrowserPid == pid) return r;
            if (!r.Tile.InBrowser && MatchNames(r.Tile).Contains(WindowScanner.ProcessName(pid), StringComparer.OrdinalIgnoreCase)) return r;
        }
        return null;
    }

    /// <summary>
    /// Opens a tile: brings its window back if it's already open, launches it
    /// if not. Returns a message for the screen if something's wrong.
    /// </summary>
    public async Task<string?> ActivateAsync(Tile tile)
    {
        var r = For(tile);
        Refresh();
        if (!r.Running && tile.InBrowser)
        {
            // Make sure it isn't open in an instance we lost track of — launching
            // again would just add a second window to it.
            await DiscoverAsync();
            Refresh();
        }

        if (r.Running)
        {
            Focus(r, userAction: true);
            return null;
        }
        if (r.PendingFocus && (DateTime.UtcNow - r.LaunchedAt).TotalSeconds < 15)
            return $"{tile.Name.ToUpperInvariant()} IS STILL OPENING…";

        try
        {
            var problem = Launch(tile, r);
            if (problem != null) return problem;
        }
        catch (Exception e)
        {
            Log.Write($"launch {tile.Name} failed: {e}");
            return $"COULDN'T OPEN {tile.Name.ToUpperInvariant()}";
        }
        r.PendingFocus = true;
        r.LaunchedAt = DateTime.UtcNow;
        return null;
    }

    public void Close(Tile tile)
    {
        var r = For(tile);
        r.PendingFocus = false;
        foreach (var w in r.Windows) Native.PostMessageW(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Log.Write($"close {tile.Name}: {r.Windows.Count} window(s)");

        // Closing Big Picture only drops Steam back to its ordinary window.
        // Send that to the tray too (Refresh does it once it appears), or you
        // land on a Steam window you never asked for.
        if (tile.Kind == TileKind.Steam) steamWindowCloseUntil = DateTime.UtcNow.AddSeconds(10);
    }

    /// <summary>Finds browser instances running with one of our tile profiles.</summary>
    public async Task DiscoverAsync()
    {
        lastDiscovery = DateTime.UtcNow;
        Dictionary<string, (int Pid, string BrowserId)> found;
        try
        {
            found = await Task.Run(() => FindBrowserInstances());
        }
        catch (Exception e)
        {
            Log.Write("browser discovery failed: " + e.Message);
            return;
        }

        foreach (var r in byId.Values.Where(r => r.Tile.InBrowser && r.BrowserPid == null))
        {
            foreach (var browser in Browsers.All)
            {
                if (!found.TryGetValue(Normalize(ProfileDir(r.Tile, browser.Id)), out var hit)) continue;
                r.BrowserPid = hit.Pid;
                r.BrowserId = hit.BrowserId;
                Log.Write($"found {r.Tile.Name} already open in {hit.BrowserId} (pid {hit.Pid})");
                // Opened before the launcher (re)started: pick Live Surfer back up.
                if (r.Tile.Kind == TileKind.LiveSurfer) StartSurfer(r, ProfileDir(r.Tile, hit.BrowserId), TimeSpan.Zero);
                break;
            }
        }
    }

    string? Launch(Tile tile, TileRuntime r)
    {
        var name = tile.Name.ToUpperInvariant();
        switch (tile.Kind)
        {
            case TileKind.Web:
            case TileKind.LiveSurfer:
            {
                var browser = Browsers.Resolve(tile.Browser);
                if (browser == null) return "NO BROWSER FOUND — INSTALL BRAVE, CHROME OR EDGE";
                bool surfer = tile.Kind == TileKind.LiveSurfer;
                var address = surfer ? LiveSurfer.StartAddress(tile) : tile.Url.Trim();
                if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Host.Length == 0)
                    return $"{name}: THE WEB ADDRESS LOOKS WRONG";

                var profile = ProfileDir(tile, browser.Id);
                var start = new ProcessStartInfo(Browsers.PathOf(browser)!) { UseShellExecute = false };
                start.ArgumentList.Add($"--user-data-dir={profile}");
                start.ArgumentList.Add($"--app={url.AbsoluteUri}");
                start.ArgumentList.Add("--start-fullscreen");
                start.ArgumentList.Add("--no-first-run");
                start.ArgumentList.Add("--no-default-browser-check");
                start.ArgumentList.Add("--hide-crash-restore-bubble");
                if (tile.TvMode) start.ArgumentList.Add($"--user-agent={config.TvUserAgent}");
                if (surfer) SiteBridge.AddLaunchFlags(start, profile);

                using var process = Process.Start(start);
                r.BrowserPid = process?.Id;
                r.BrowserId = browser.Id;
                Log.Write($"launch {tile.Name}: {browser.Id} {url} (pid {r.BrowserPid})");
                if (surfer)
                {
                    bool onChannel = tile.Channels.Count > 0;
                    r.Channel = onChannel ? Math.Clamp(tile.LastChannel, 0, tile.Channels.Count - 1) : -1;
                    r.PreviousChannel = -1;
                    r.TunedAtMs = onChannel ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
                    StartSurfer(r, profile, TimeSpan.FromSeconds(30));
                }
                return null;
            }

            case TileKind.Steam:
                Process.Start(new ProcessStartInfo("steam://open/bigpicture") { UseShellExecute = true })?.Dispose();
                Log.Write($"launch {tile.Name}: steam big picture");
                return null;

            case TileKind.App:
            {
                var target = tile.Target.Trim();
                if (target.Length == 0) return $"{name} ISN'T SET UP YET — SETTINGS › EDIT TILES";
                bool isLink = target.Contains("://");
                if (!isLink && !File.Exists(target)) return $"{name}: CAN'T FIND {Path.GetFileName(target).ToUpperInvariant()}";

                var start = new ProcessStartInfo(target) { UseShellExecute = true, Arguments = tile.Args };
                if (!isLink) start.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
                Process.Start(start)?.Dispose();
                Log.Write($"launch {tile.Name}: {target} {tile.Args}");
                return null;
            }
        }
        return null;
    }

    /// <summary>Connects a Live Surfer tile to its browser, unless it already is.</summary>
    void StartSurfer(TileRuntime r, string profile, TimeSpan wait)
    {
        var tile = r.Tile;
        if (bridges.TryGetValue(tile.Id, out var existing))
        {
            if (!existing.Finished) return;
            existing.Dispose();
        }
        var script = SurferScript(r);
        if (script == null)
        {
            Log.Write($"{tile.Name}: Live Surfer's script is missing");
            return;
        }
        var bridge = SiteBridge.Start(tile.Name, profile, script, wait);
        var id = tile.Id;
        // Pages talk on a background thread; the tile list belongs to the UI.
        bridge.Message = (session, payload) => ui.Post(_ => SurferMessage(id, session, payload), null);
        bridges[tile.Id] = bridge;
    }

    string? SurferScript(TileRuntime r) =>
        LiveSurfer.BuildScript(r.Tile, Theme.Get(config.Theme), r.Channel, r.PreviousChannel, r.TunedAtMs,
            r.Scan is { } scan ? new { index = scan.Index, step = scan.Total - scan.Waiting.Count, total = scan.Total } : null,
            r.Notice is { } notice ? new { text = notice.Text, at = notice.AtMs } : null);

    /// <summary>Gives a Live Surfer tile's pages its latest channels and settings.</summary>
    async Task PushSurferAsync(TileRuntime r)
    {
        if (!bridges.TryGetValue(r.Tile.Id, out var bridge) || bridge.Finished) return;
        var script = SurferScript(r);
        if (script != null) await bridge.SetScriptAsync(script);
    }

    /// <summary>A Live Surfer page asking for something: a channel change, or a channel added or removed.</summary>
    async void SurferMessage(string tileId, string session, string payload)
    {
        if (For(tileId) is not { } r || !bridges.TryGetValue(tileId, out var bridge)) return;
        var tile = r.Tile;
        try
        {
            using var message = System.Text.Json.JsonDocument.Parse(payload);
            var m = message.RootElement;
            string Text(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : "";
            int Number(string name) => m.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : -1;

            switch (Text("t"))
            {
                case "log":
                {
                    var line = Text("msg");
                    Log.Write($"{tile.Name}: {(line.Length > 300 ? line[..300] + "…" : line)}");
                    break;
                }
                case "tune":
                {
                    int index = Number("index");
                    if (index < 0 || index >= tile.Channels.Count) return;
                    await TuneAsync(r, bridge, session, index);
                    break;
                }
                case "clues":
                {
                    int index = Number("index");
                    if (index < 0 || index >= tile.Channels.Count) return;
                    var labels = m.TryGetProperty("labels", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? list.EnumerateArray().Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                        : Array.Empty<string>();
                    await IdentifyAsync(r, bridge, session, index, Text("why"),
                        new TvGuide.Clues(Text("url"), Text("title"), labels, Text("text")));
                    break;
                }
                case "match":
                {
                    int index = Number("index");
                    if (index < 0 || index >= tile.Channels.Count) return;
                    var id = Text("id");
                    var guide = await TvGuide.GetAsync(tile.GuideUrl);
                    var chosen = guide?.Channels.FirstOrDefault(c => c.Id == id);
                    if (chosen != null) UseGuideChannel(tile, index, chosen);
                    else tile.Channels[index].GuideId = id == TvGuide.NoGuide ? TvGuide.NoGuide : "";
                    Log.Write($"{tile.Name}: channel {index + 1} guide set to {chosen?.Name ?? (id.Length == 0 ? "automatic" : id)}");
                    config.Save();
                    await PushSurferAsync(r);
                    break;
                }
                case "scanAll":
                {
                    if (r.Scan != null || tile.Channels.Count == 0) return;
                    var guide = await TvGuide.GetAsync(tile.GuideUrl);
                    if (guide == null)
                    {
                        r.Notice = ("NO TV GUIDE TO SCAN AGAINST — CHECK SETTINGS › EDIT TILES › TV GUIDE", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        await PushSurferAsync(r);
                        return;
                    }
                    // The channels the guide doesn't know yet; all of them if it knows every one.
                    var unknown = Enumerable.Range(0, tile.Channels.Count)
                        .Where(i => tile.Channels[i].GuideId != TvGuide.NoGuide && guide.Match(tile.Channels[i].Name, tile.Channels[i].GuideId) == null)
                        .ToList();
                    if (unknown.Count == 0) unknown = Enumerable.Range(0, tile.Channels.Count).Where(i => tile.Channels[i].GuideId != TvGuide.NoGuide).ToList();
                    var scan = new SurferScan { Total = unknown.Count, ReturnTo = Math.Max(0, r.Channel), Session = session };
                    foreach (var i in unknown) scan.Waiting.Enqueue(i);
                    r.Scan = scan;
                    Log.Write($"{tile.Name}: scanning {unknown.Count} channel(s) for the TV guide");
                    await ScanNextAsync(r, bridge);
                    break;
                }
                case "scanStop":
                    if (r.Scan != null)
                    {
                        Log.Write($"{tile.Name}: scan stopped");
                        r.Scan.Waiting.Clear();
                        await ScanNextAsync(r, bridge);
                    }
                    break;
                case "add":
                {
                    var url = Text("url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
                    var name = Text("name").Trim();
                    tile.Channels.Add(new LiveChannel { Name = name.Length > 0 ? name : uri.Host.ToUpperInvariant(), Url = url });
                    r.Channel = tile.Channels.Count - 1;
                    tile.LastChannel = r.Channel;
                    config.Save();
                    Log.Write($"{tile.Name}: added channel {tile.Channels.Count}, {name}: {url}");
                    await PushSurferAsync(r);
                    break;
                }
                case "rename":
                {
                    int index = Number("index");
                    var name = Text("name").Trim();
                    if (index < 0 || index >= tile.Channels.Count || name.Length == 0) return;
                    Log.Write($"{tile.Name}: channel {index + 1} renamed {tile.Channels[index].Name} → {name}");
                    tile.Channels[index].Name = name.Length > 40 ? name[..40] : name;
                    config.Save();
                    await PushSurferAsync(r);
                    break;
                }
                case "volume":
                {
                    var level = SystemVolume.Step(Math.Sign(Number("step")));
                    if (level is { } v)
                        await bridge.EvaluateAsync(session, $"window.__couchVolume && window.__couchVolume({v.Percent}, {(v.Muted ? "true" : "false")})");
                    break;
                }
                case "guide":
                {
                    var guide = await TvGuide.GetAsync(tile.GuideUrl);
                    var json = System.Text.Json.JsonSerializer.Serialize(GuideForPage(tile, guide));
                    await bridge.EvaluateAsync(session, $"window.__couchGuide && window.__couchGuide({json})");
                    break;
                }
                case "remove":
                {
                    int index = Number("index");
                    if (index < 0 || index >= tile.Channels.Count) return;
                    Log.Write($"{tile.Name}: removed channel {index + 1}, {tile.Channels[index].Name}");
                    tile.Channels.RemoveAt(index);
                    r.Channel = Shift(r.Channel, index);
                    r.PreviousChannel = Shift(r.PreviousChannel, index);
                    tile.LastChannel = Math.Max(0, Shift(tile.LastChannel, index));
                    config.Save();
                    await PushSurferAsync(r);
                    break;
                }
            }
        }
        catch (System.Text.Json.JsonException) { }
        catch (Exception e)
        {
            Log.Write($"{tile.Name}: {e.Message}");
        }

        // After channel <removed> is taken out: the same channel's new number.
        static int Shift(int channel, int removed) => channel == removed ? -1 : channel > removed ? channel - 1 : channel;
    }

    async Task TuneAsync(TileRuntime r, SiteBridge bridge, string session, int index)
    {
        var tile = r.Tile;
        if (r.Channel >= 0 && r.Channel != index) r.PreviousChannel = r.Channel;
        r.Channel = index;
        r.TunedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        tile.LastChannel = index;
        config.Save();
        Log.Write($"{tile.Name}: channel {index + 1}, {tile.Channels[index].Name}");
        // New pages need to know where they are before they load.
        await PushSurferAsync(r);
        await bridge.NavigateAsync(session, tile.Channels[index].Url);
    }

    /// <summary>
    /// A page's clues about which channel it is: find it in the guide. Used
    /// straight away when it's clear-cut and wasn't asked about (a channel the
    /// guide didn't know, or a scan); asked about, the page gets the best
    /// few to choose from.
    /// </summary>
    async Task IdentifyAsync(TileRuntime r, SiteBridge bridge, string session, int index, string why, TvGuide.Clues clues)
    {
        var tile = r.Tile;
        var channel = tile.Channels[index];
        var guide = await TvGuide.GetAsync(tile.GuideUrl);
        var ranked = guide?.Identify(clues) ?? new();
        bool sure = TvGuide.Confident(ranked);
        Log.Write($"{tile.Name}: identify {channel.Name} ({why}): " +
                  (ranked.Count == 0 ? "no clues matched" : string.Join(", ", ranked.Take(3).Select(c => $"{c.Channel.Name} {c.Score}"))) +
                  (sure ? " — sure" : ""));

        string? matched = null;
        bool scanning = why == "scan" && r.Scan?.Index == index;
        if (sure && (scanning || (why == "auto" && channel.GuideId.Length == 0)))
        {
            UseGuideChannel(tile, index, ranked[0].Channel);
            matched = ranked[0].Channel.Name;
            config.Save();
        }
        if (scanning)
        {
            r.Scan!.Found.Add(matched != null ? $"{tile.Channels[index].Name} ✓" : $"{channel.Name} ?");
            await ScanNextAsync(r, bridge);
            return;
        }
        var reply = new
        {
            index,
            why,
            noGuide = guide == null,
            matched,
            candidates = ranked.Take(6).Select(c => new { id = c.Channel.Id, name = c.Channel.Name, now = c.Now }),
        };
        await bridge.EvaluateAsync(session, $"window.__couchScan && window.__couchScan({System.Text.Json.JsonSerializer.Serialize(reply)})");
        if (matched != null) await PushSurferAsync(r);
    }

    /// <summary>
    /// Ties a channel to a guide channel, and gives it the guide's name if
    /// its own said nothing ("WWW.SBS.COM.AU" → "SBS VICELAND").
    /// </summary>
    static void UseGuideChannel(Tile tile, int index, TvGuide.Channel guideChannel)
    {
        var channel = tile.Channels[index];
        channel.GuideId = guideChannel.Id;
        if (LiveSurfer.IsPlaceholderName(channel.Name)) channel.Name = TvGuide.StripPlace(guideChannel.Name).ToUpperInvariant();
    }

    /// <summary>The scan's next channel, or back to where it started once they're all done.</summary>
    async Task ScanNextAsync(TileRuntime r, SiteBridge bridge)
    {
        if (r.Scan is not { } scan) return;
        if (scan.Waiting.Count > 0)
        {
            scan.Index = scan.Waiting.Dequeue();
            scan.StepStarted = DateTime.UtcNow;
            await TuneAsync(r, bridge, scan.Session, scan.Index);
            return;
        }
        r.Scan = null;
        int matched = scan.Found.Count(f => f.EndsWith('✓'));
        var summary = $"SCAN DONE: {matched} OF {scan.Total} FOUND IN THE GUIDE" +
                      (matched < scan.Total ? " — FOR THE REST: B › FIND IN TV GUIDE" : "");
        Log.Write($"{r.Tile.Name}: {summary} ({string.Join(", ", scan.Found)})");
        r.Notice = (summary, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (r.Tile.Channels.Count > 0)
            await TuneAsync(r, bridge, scan.Session, Math.Min(scan.ReturnTo, r.Tile.Channels.Count - 1));
    }

    /// <summary>
    /// The guide as a Live Surfer page wants it: for each of the tile's
    /// channels, in order, its shows from an hour ago to a day ahead.
    /// </summary>
    static object GuideForPage(Tile tile, TvGuide? guide)
    {
        var from = DateTimeOffset.UtcNow.AddHours(-1);
        var until = DateTimeOffset.UtcNow.AddHours(24);
        return new
        {
            ok = guide != null,
            source = tile.GuideUrl,
            channels = tile.Channels.Select(c =>
            {
                var match = guide?.Match(c.Name, c.GuideId);
                var shows = match == null ? Enumerable.Empty<TvGuide.Programme>() : guide!.For(match.Id).Where(p => p.Stop > from && p.Start < until);
                return new
                {
                    guide = match?.Name,
                    shows = shows.Select(p => new
                    {
                        start = p.Start.ToUnixTimeMilliseconds(),
                        stop = p.Stop.ToUnixTimeMilliseconds(),
                        title = p.Title,
                        episode = p.Episode,
                        desc = p.Description.Length > 400 ? p.Description[..400] + "…" : p.Description,
                    }),
                };
            }),
        };
    }

    void Focus(TileRuntime r, bool userAction)
    {
        var h = r.Main;
        if (h == IntPtr.Zero) return;
        bool ok = Native.ForceForeground(h);
        if (!ok) Log.Write($"couldn't bring {r.Tile.Name} to the front");

        switch (r.Tile.Kind)
        {
            case TileKind.Web or TileKind.LiveSurfer when userAction:
                _ = RestoreFullscreenAsync(r, h);
                break;
            case TileKind.App when r.Tile.ForceFullscreen && !Native.CoversMonitor(h):
                Native.MakeBorderlessFullscreen(h);
                Log.Write($"{r.Tile.Name}: made borderless fullscreen");
                break;
        }
    }

    /// <summary>
    /// Browsers are started fullscreen, but drop out of it — minimised and
    /// restored, Esc out of a video, F11 pressed — and coming back to the tile
    /// then lands on a small window with a title bar. Press F11 for it, but
    /// only once it's really in front (the key goes to whatever has focus,
    /// which takes a moment to arrive) and has stopped moving (mid-way through
    /// restoring or its own fullscreen switch, F11 would undo it). Checked a
    /// few times over a few seconds rather than once straight away, but
    /// pressed only once: a second F11 would toggle a fix that worked back off.
    /// </summary>
    async Task RestoreFullscreenAsync(TileRuntime r, IntPtr h)
    {
        if (!restoringFullscreen.Add(h)) return;
        try
        {
            Native.RECT? last = null;
            for (int check = 0; check < 16; check++)
            {
                await Task.Delay(250);
                if (r.Main != h) return;
                if (Native.CoversMonitor(h)) return;
                // A brand-new window is still setting itself up fullscreen.
                if (Age(h) < 3 || Native.IsMinimised(h)) continue;
                if (Native.RootOwner(Native.GetForegroundWindow()) != h)
                {
                    last = null;
                    continue;
                }
                var rect = Native.WindowRect(h);
                bool settled = last is { } before && before.Equals(rect);
                last = rect;
                if (!settled) continue;

                Native.TapKey(Native.VK_F11);
                // Give the switch time to finish before judging it.
                await Task.Delay(1000);
                Log.Write(Native.CoversMonitor(h)
                    ? $"{r.Tile.Name} had left fullscreen — F11 put it back"
                    : $"{r.Tile.Name} had left fullscreen — F11 didn't put it back");
                return;
            }
            Log.Write($"{r.Tile.Name} isn't fullscreen, but never settled in front to fix it");
        }
        finally
        {
            restoringFullscreen.Remove(h);
        }
    }

    IntPtr PickMain(TileRuntime r)
    {
        IEnumerable<WinInfo> candidates = r.Windows;
        if (r.Tile.InBrowser)
        {
            // An app-mode window is titled with just the page title; a normal
            // browser window (a stray one, like a welcome page) ends
            // " - Brave". Prefer the app window, oldest first.
            var suffix = (Browsers.Get(r.BrowserId) ?? Browsers.Get(r.Tile.Browser))?.TitleSuffix ?? "\0";
            var appWindows = r.Windows.Where(w => !w.Title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (appWindows.Count > 0) candidates = appWindows;
            return candidates.OrderBy(w => firstSeen.GetValueOrDefault(w.Hwnd)).First().Hwnd;
        }
        // Apps: the biggest window is the real one, not a splash or a panel.
        return candidates.OrderByDescending(w => w.Area).First().Hwnd;
    }

    /// <summary>
    /// Closing Steam's main window sends it to the system tray — Steam keeps
    /// running for downloads and friends, just not on screen. Anything still
    /// titled Big Picture is the tile's own window and is left to Close().
    /// </summary>
    void CloseSteamDesktopWindows(List<WinInfo> windows, DateTime now)
    {
        foreach (var w in windows)
        {
            if (!SteamProcesses.Contains(w.Process, StringComparer.OrdinalIgnoreCase)) continue;
            if (w.Title.Contains(SteamWindowTitle, StringComparison.OrdinalIgnoreCase)) continue;
            if (closeSent.TryGetValue(w.Hwnd, out var sent) && (now - sent).TotalSeconds < 3) continue;
            closeSent[w.Hwnd] = now;
            Native.PostMessageW(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Log.Write($"steam: sent \"{w.Title}\" to the tray");
        }
    }

    void ClosePopups(TileRuntime r, DateTime now)
    {
        foreach (var w in r.Windows)
        {
            if (w.Hwnd == r.Main) continue;
            if (closeSent.TryGetValue(w.Hwnd, out var sent) && (now - sent).TotalSeconds < 5) continue;
            closeSent[w.Hwnd] = now;
            Native.PostMessageW(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Log.Write($"{r.Tile.Name}: closed pop-up \"{w.Title}\"");
        }
    }

    double Age(IntPtr h) =>
        firstSeen.TryGetValue(h, out var seen) ? (DateTime.UtcNow - seen).TotalSeconds : 0;

    static string[] MatchNames(Tile tile)
    {
        if (tile.Kind == TileKind.Steam && tile.MatchProcess.Length == 0) return SteamProcesses;
        return tile.MatchProcess
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .ToArray();
    }

    /// <summary>
    /// One profile folder per tile AND browser: Chrome and Brave can't share
    /// one, so switching a tile's browser starts it a fresh profile.
    /// </summary>
    static string ProfileDir(Tile tile, string browserId) => Path.Combine(Paths.Profiles, $"{tile.Id}-{browserId}");

    static string Normalize(string dir) => Path.GetFullPath(dir).TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>
    /// Asks Windows (WMI) for every running browser and reads which profile
    /// folder each was started with. Slow-ish (up to a second), so it runs off
    /// the UI thread and only when needed.
    /// </summary>
    static Dictionary<string, (int Pid, string BrowserId)> FindBrowserInstances()
    {
        var found = new Dictionary<string, (int, string)>();
        var ours = Normalize(Paths.Profiles);
        foreach (var browser in Browsers.All)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{browser.Exe}'");
                using var results = searcher.Get();
                foreach (ManagementObject process in results)
                {
                    using (process)
                    {
                        // Helper processes (renderers, GPU…) carry --type=; only
                        // the main browser process owns the windows.
                        if (process["CommandLine"] is not string commandLine || commandLine.Contains("--type=")) continue;
                        var dir = UserDataDir(commandLine);
                        if (dir == null) continue;
                        var key = Normalize(dir);
                        if (!key.StartsWith(ours)) continue;
                        found[key] = ((int)(uint)process["ProcessId"], browser.Id);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write($"wmi {browser.Exe}: {e.Message}");
            }
        }
        return found;
    }

    /// <summary>Pulls the --user-data-dir value out of a command line, quoted or not.</summary>
    static string? UserDataDir(string commandLine)
    {
        const string flag = "--user-data-dir=";
        int at = commandLine.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = commandLine[(at + flag.Length)..];
        bool quotedWhole = at > 0 && commandLine[at - 1] == '"';
        int end;
        if (rest.StartsWith('"'))
        {
            rest = rest[1..];
            end = rest.IndexOf('"');
        }
        else if (quotedWhole)
        {
            end = rest.IndexOf('"');
        }
        else
        {
            end = rest.IndexOfAny(new[] { ' ', '\t' });
        }
        return end < 0 ? rest : rest[..end];
    }
}
