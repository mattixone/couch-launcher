using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CouchLauncher;

/// <summary>
/// Serves the phone remote (github.com/mattixone/CouchControllerWeb) to
/// guests on the Wi-Fi, so the Pico doesn't have to.
///
/// Only the page comes from here. Once loaded, the phone talks straight to
/// the Pico over a WebSocket, exactly as if the Pico had served it; this just
/// writes the Pico's address into the page on the way out.
///
/// A bare TcpListener rather than HttpListener: HttpListener needs an admin
/// URL reservation to listen on anything but localhost, and this is one page.
/// </summary>
static class WebRemote
{
    /// <summary>Drop a page here to try changes without a new release.</summary>
    public static string OverrideFile => Path.Combine(Paths.Data, "web", "index.html");

    const string PicoMeta = "<meta name=\"couch-pico\" content=\"\">";

    static readonly Lazy<string> Embedded = new(() =>
    {
        using var stream = typeof(WebRemote).Assembly.GetManifestResourceStream("CouchLauncher.web.index.html");
        if (stream == null) return "<h1>Phone remote page missing from this build</h1>";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    static TcpListener? listener;
    static string picoHost = "";
    static string? picoIp;
    static DateTime picoResolvedAt;
    static bool loggedResolveFailure;

    public static int Port { get; private set; }
    public static bool Running => listener != null;
    /// <summary>Why it isn't running, for the settings screen. Empty when fine.</summary>
    public static string Problem { get; private set; } = "";

    /// <summary>Start or stop to match the config. Safe to call repeatedly.</summary>
    public static void Apply(LauncherConfig config)
    {
        picoHost = config.PicoAddress.Trim();
        picoIp = null;
        if (!config.PhoneRemote)
        {
            Stop();
            Problem = "";
            return;
        }
        if (listener != null) return;

        // 80 keeps the address short to type; 8080 if something else has it.
        foreach (var port in new[] { config.PhoneRemotePort, 8080 }.Distinct())
        {
            try
            {
                var candidate = new TcpListener(IPAddress.Any, port);
                candidate.Start();
                listener = candidate;
                Port = port;
                break;
            }
            catch (SocketException e)
            {
                Log.Write($"phone remote: port {port} unavailable ({e.SocketErrorCode})");
            }
        }
        if (listener == null)
        {
            Problem = "COULDN'T START: PORT IN USE";
            return;
        }
        Problem = "";
        _ = AcceptLoop(listener);
        _ = PicoHosts();
        // The first listen is when Windows Firewall asks whether to allow it.
        Log.Write($"phone remote serving on {string.Join(", ", Urls())} (pico: {picoHost})");
    }

    public static void Stop()
    {
        var running = listener;
        listener = null;
        running?.Stop();
    }

    /// <summary>Addresses a phone on the same network can open.</summary>
    public static List<string> Urls()
    {
        string suffix = Port == 80 ? "" : $":{Port}";
        return LanAddresses().Select(a => $"http://{a}{suffix}").ToList();
    }

    static IEnumerable<IPAddress> LanAddresses()
    {
        try
        {
            // Adapters with a gateway are the real network; that leaves out
            // Hyper-V, VPN and other virtual adapters that phones can't reach.
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(p => p.UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => u.Address)
                .ToList();
        }
        catch (Exception e)
        {
            Log.Write("phone remote: couldn't list network addresses: " + e.Message);
            return Array.Empty<IPAddress>();
        }
    }

    static async Task AcceptLoop(TcpListener server)
    {
        while (listener == server)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (listener == server) Log.Write("phone remote stopped: " + e.Message);
                return;
            }
            _ = Serve(client);
        }
    }

    static async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var head = await ReadHead(stream, timeout.Token);
                if (head == null) return;

                var lines = head.Split("\r\n");
                var request = lines[0].Split(' ');
                if (request.Length < 2) return;
                string method = request[0], path = request[1].Split('?')[0];
                bool gzip = lines.Any(l => l.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase)
                                           && l.Contains("gzip", StringComparison.OrdinalIgnoreCase));

                if (method is not ("GET" or "HEAD"))
                    await Respond(stream, "405 Method Not Allowed", "text/plain", "GET only"u8.ToArray(), false, true, timeout.Token);
                else if (path is "/" or "/index.html")
                    await Respond(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(await Page()), gzip, method == "GET", timeout.Token);
                else
                    await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray(), false, method == "GET", timeout.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                // Phone gave up or wandered off; nothing to do.
            }
            catch (Exception e)
            {
                Log.Write("phone remote: " + e.Message);
            }
        }
    }

    /// <summary>The request line and headers, or null if it isn't HTTP.</summary>
    static async Task<string?> ReadHead(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return null;
            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return null;
    }

    static async Task Respond(NetworkStream stream, string status, string type, byte[] body, bool gzip, bool includeBody, CancellationToken token)
    {
        if (gzip)
        {
            using var packed = new MemoryStream();
            using (var zip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zip.Write(body);
            body = packed.ToArray();
        }
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {status}\r\n")
            .Append($"Content-Type: {type}\r\n")
            .Append($"Content-Length: {body.Length}\r\n")
            .Append(gzip ? "Content-Encoding: gzip\r\n" : "")
            // Small enough to fetch every time, and a changed page shows up at once.
            .Append("Cache-Control: no-cache\r\n")
            .Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token);
        if (includeBody) await stream.WriteAsync(body, token);
    }

    static async Task<string> Page()
    {
        string html = Embedded.Value;
        try
        {
            if (File.Exists(OverrideFile)) html = await File.ReadAllTextAsync(OverrideFile);
        }
        catch (Exception e)
        {
            Log.Write("phone remote: couldn't read " + OverrideFile + ": " + e.Message);
        }
        var hosts = WebUtility.HtmlEncode(string.Join(",", await PicoHosts()));
        return html.Replace(PicoMeta, $"<meta name=\"couch-pico\" content=\"{hosts}\">");
    }

    /// <summary>
    /// Where the page should look for the Pico, best first. Windows can look
    /// up couchcommander.local even when a guest's phone can't (plenty of
    /// Android phones don't do .local names), so the page gets the IP too.
    /// </summary>
    static async Task<List<string>> PicoHosts()
    {
        var hosts = new List<string>();
        if (picoHost.Length == 0) return hosts;
        if (!IPAddress.TryParse(picoHost, out _))
        {
            if (picoIp == null || DateTime.UtcNow - picoResolvedAt > TimeSpan.FromMinutes(2))
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var found = await Dns.GetHostAddressesAsync(picoHost, AddressFamily.InterNetwork, timeout.Token);
                    if (found.Length > 0)
                    {
                        picoIp = found[0].ToString();
                        picoResolvedAt = DateTime.UtcNow;
                        loggedResolveFailure = false;
                    }
                }
                catch (Exception e)
                {
                    // Keep the last known IP, if any: a Pico that's mid-reboot
                    // usually comes back on the same address.
                    if (!loggedResolveFailure) Log.Write($"phone remote: can't find {picoHost}: {e.Message}");
                    loggedResolveFailure = true;
                }
            }
            if (picoIp != null) hosts.Add(picoIp);
        }
        hosts.Add(picoHost);
        return hosts;
    }
}
