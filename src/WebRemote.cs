using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CouchLauncher;

/// <summary>
/// Serves the phone remote (github.com/mattixone/CouchControllerWeb) to
/// guests on the Wi-Fi, so the Pico doesn't have to.
///
/// The page sends its buttons over a WebSocket, to the Pico when the Pico
/// runs the web remote (it's a real USB keyboard, so UNLOCK works at the lock
/// screen), otherwise to this server's own /ws, where PhoneInput presses the
/// keys from Windows. The page is told which to try first: this writes the
/// addresses into it on the way out, Pico first only if it answered lately.
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

    /// <summary>Runs one message from a phone; returns an error to send back, or null.</summary>
    public static Func<JsonElement, string?> Handler { get; set; } = _ => null;
    /// <summary>A phone went away: let go of anything it was holding.</summary>
    public static Action Disconnected { get; set; } = () => { };

    static bool allowPower = true;
    static bool? picoHasRemote;
    static DateTime picoProbedAt;
    static int probing;

    public static int Port { get; private set; }
    public static bool Running => listener != null;
    /// <summary>Why it isn't running, for the settings screen. Empty when fine.</summary>
    public static string Problem { get; private set; } = "";

    /// <summary>Start or stop to match the config. Safe to call repeatedly.</summary>
    public static void Apply(LauncherConfig config)
    {
        picoHost = config.PicoAddress.Trim();
        picoIp = null;
        picoHasRemote = null;
        allowPower = config.PhoneRemotePower;
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
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                bool gzip = headers.TryGetValue("Accept-Encoding", out var encodings)
                            && encodings.Contains("gzip", StringComparison.OrdinalIgnoreCase);

                if (path == "/ws" && method == "GET"
                    && headers.TryGetValue("Upgrade", out var upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                    await Socket(stream, headers, timeout.Token);
                else if (method is not ("GET" or "HEAD"))
                    await Respond(stream, "405 Method Not Allowed", "text/plain", "GET only"u8.ToArray(), false, true, timeout.Token);
                else if (path is "/" or "/index.html")
                    await Respond(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(await Page()), gzip, method == "GET", timeout.Token);
                else
                    await Respond(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray(), false, method == "GET", timeout.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or WebSocketException)
            {
                // Phone gave up or wandered off; nothing to do.
            }
            catch (Exception e)
            {
                Log.Write("phone remote: " + e.Message);
            }
        }
    }

    /// <summary>
    /// A phone's button presses, for when the Pico isn't taking them. Lives
    /// as long as the phone stays connected.
    /// </summary>
    static async Task Socket(NetworkStream stream, Dictionary<string, string> headers, CancellationToken handshakeToken)
    {
        // Only pages we (or the Pico) served may drive the PC, so a website a
        // guest happens to have open can't connect here behind their back.
        if (headers.TryGetValue("Origin", out var origin) && !OriginAllowed(origin, headers.GetValueOrDefault("Host", "")))
        {
            Log.Write("phone remote: refused a connection from " + origin);
            await Respond(stream, "403 Forbidden", "text/plain", "Forbidden"u8.ToArray(), false, true, handshakeToken);
            return;
        }
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key)) return;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), handshakeToken);

        using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(20),
        });
        await SendJson(socket, new { t = "hello", name = "launcher", power = allowPower, unlock = false });

        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                int length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length) return; // nothing legitimate is this big
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), CancellationToken.None);
                    length += result.Count;
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text) continue;

                string? error;
                try
                {
                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, length));
                    error = Handler(doc.RootElement);
                }
                catch (JsonException)
                {
                    continue;
                }
                catch (Exception e)
                {
                    Log.Write("phone remote: command failed: " + e.Message);
                    error = "COMMAND FAILED";
                }
                if (error != null) await SendJson(socket, new { t = "err", msg = error });
            }
        }
        finally
        {
            Disconnected();
        }
    }

    static Task SendJson(WebSocket socket, object message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    static bool OriginAllowed(string origin, string host)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
        // The same page served by the Pico, falling back to us.
        return uri.Host.Equals(picoHost, StringComparison.OrdinalIgnoreCase)
               || (picoIp != null && uri.Host == picoIp);
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
        // "@self" is this server. Pico first when it's running the web remote.
        var pico = await PicoHosts();
        _ = ProbePico(pico);
        var order = picoHasRemote == true ? pico.Append("@self") : pico.Prepend("@self");
        var hosts = WebUtility.HtmlEncode(string.Join(",", order));
        return html.Replace(PicoMeta, $"<meta name=\"couch-pico\" content=\"{hosts}\">");
    }

    /// <summary>
    /// Checks in the background whether the Pico answers the web remote, so
    /// pages can try it first. In the background so a missing Pico never
    /// slows the page down; the answer is used from the next page load.
    /// </summary>
    static async Task ProbePico(List<string> hosts)
    {
        if (hosts.Count == 0 || DateTime.UtcNow - picoProbedAt < TimeSpan.FromMinutes(1)) return;
        if (Interlocked.Exchange(ref probing, 1) == 1) return;
        try
        {
            bool found = false;
            foreach (var host in hosts)
            {
                try
                {
                    using var socket = new ClientWebSocket();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.ConnectAsync(new Uri($"ws://{host}/ws"), timeout.Token);
                    found = true;
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token); } catch { }
                    break;
                }
                catch
                {
                    // Not there, or not running the web remote yet.
                }
            }
            if (found != picoHasRemote) Log.Write($"phone remote: pico web remote {(found ? "found" : "not found")}, buttons go to the {(found ? "pico" : "launcher")}");
            picoHasRemote = found;
            picoProbedAt = DateTime.UtcNow;
        }
        finally
        {
            Interlocked.Exchange(ref probing, 0);
        }
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
