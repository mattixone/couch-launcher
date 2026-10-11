using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CouchLauncher;

/// <summary>
/// Puts couch mode's script into every page of one web tile, through the
/// browser's DevTools connection — the one automation tools use — rather than
/// an extension. Chrome no longer lets a program load an extension when it
/// starts the browser, and an extension would need installing once per tile
/// profile; this needs nothing installed and ships with the launcher.
///
/// The tile's browser is started listening on a port it picks itself, on
/// 127.0.0.1 only, and writes the port into its profile folder. Chrome only
/// allows this for a profile that isn't the default one, which every tile's
/// is. The script is registered to run before the site's own code on every
/// page, including new pages and pop-ups.
/// </summary>
sealed class SiteBridge : IDisposable
{
    /// <summary>The function pages call to send the launcher a message.</summary>
    public const string BindingName = "couchLauncher";
    const string PortFile = "DevToolsActivePort";

    readonly string tileName;
    readonly string profileDir;
    readonly string script;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim sending = new(1, 1);
    readonly Dictionary<int, string> pending = new();
    ClientWebSocket? socket;
    int nextId;

    /// <summary>A message from a page (JSON text). Called on a background thread.</summary>
    public Action<string>? Message { get; set; }
    public bool Finished { get; private set; }

    SiteBridge(string tileName, string profileDir, string script)
    {
        this.tileName = tileName;
        this.profileDir = profileDir;
        this.script = script;
    }

    /// <summary>The browser switches couch mode needs. Call before starting the browser.</summary>
    public static void AddLaunchFlags(ProcessStartInfo start, string profileDir)
    {
        // A leftover from last time would point at a port nobody's on.
        try { File.Delete(Path.Combine(profileDir, PortFile)); } catch { }
        start.ArgumentList.Add("--remote-debugging-port=0");
        // Changing channel loads a new page, and a new page may not start
        // playing with sound until it's been clicked on.
        start.ArgumentList.Add("--autoplay-policy=no-user-gesture-required");
    }

    /// <summary>
    /// Connects to the browser using this profile folder, waiting up to
    /// <paramref name="wait"/> for it to start, and keeps its pages scripted
    /// until it closes.
    /// </summary>
    public static SiteBridge Start(string tileName, string profileDir, string script, TimeSpan wait)
    {
        var bridge = new SiteBridge(tileName, profileDir, script);
        _ = bridge.RunAsync(wait);
        return bridge;
    }

    public void Dispose()
    {
        Finished = true;
        stop.Cancel();
        try { socket?.Abort(); } catch { }
    }

    async Task RunAsync(TimeSpan wait)
    {
        try
        {
            var endpoint = await WaitForEndpointAsync(wait);
            if (endpoint == null)
            {
                Log.Write($"{tileName}: couch mode couldn't find the browser's DevTools port " +
                          "(it may have been opened by an older launcher — close it and open it again)");
                return;
            }
            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(endpoint), stop.Token);
            Log.Write($"{tileName}: couch mode connected");
            // Browser-wide: attach to every page now open and every one opened
            // later, each paused until it's been given the script.
            await SendAsync("Target.setAutoAttach", new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true });
            await ReceiveAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            Log.Write($"{tileName}: couch mode connection: {e.Message}");
        }
        finally
        {
            Finished = true;
            socket?.Dispose();
        }
    }

    async Task<string?> WaitForEndpointAsync(TimeSpan wait)
    {
        var file = Path.Combine(profileDir, PortFile);
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                if (File.Exists(file))
                {
                    var lines = (await File.ReadAllLinesAsync(file, stop.Token)).Where(l => l.Length > 0).ToArray();
                    if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0)
                        return $"ws://127.0.0.1:{port}{lines[1].Trim()}";
                }
            }
            catch (IOException) { }      // still being written
            if (DateTime.UtcNow > until) return null;
            await Task.Delay(250, stop.Token);
        }
    }

    async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (socket!.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, stop.Token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try
            {
                await HandleAsync(JsonNode.Parse(text));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                Log.Write($"{tileName}: couch mode didn't understand the browser: {e.Message}");
            }
        }
    }

    async Task HandleAsync(JsonNode? message)
    {
        if (message == null) return;
        if (message["id"] is JsonNode idNode)
        {
            int id = idNode.GetValue<int>();
            string? method;
            lock (pending)
            {
                pending.Remove(id, out method);
            }
            if (message["error"] is JsonNode error)
                Log.Write($"{tileName}: couch mode: {method} failed: {error["message"]}");
            return;
        }

        var parameters = message["params"];
        switch ((string?)message["method"])
        {
            case "Target.attachedToTarget":
            {
                var session = (string?)parameters?["sessionId"];
                if (session == null) return;
                var type = (string?)parameters?["targetInfo"]?["type"];
                if (type == "page")
                {
                    bool waiting = (bool?)parameters?["waitingForDebugger"] ?? false;
                    // Both needed: Page for the script to be run on new
                    // documents, Runtime for the page's messages to arrive.
                    await SendAsync("Page.enable", new { }, session);
                    await SendAsync("Runtime.enable", new { }, session);
                    await SendAsync("Runtime.addBinding", new { name = BindingName }, session);
                    await SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = script }, session);
                    // Already-open pages missed the start of their document;
                    // the script copes with arriving late.
                    if (!waiting) await SendAsync("Runtime.evaluate", new { expression = script }, session);
                }
                // Anything else (workers and so on) just carries on.
                await SendAsync("Runtime.runIfWaitingForDebugger", new { }, session);
                break;
            }
            case "Runtime.bindingCalled":
                if ((string?)parameters?["name"] == BindingName && (string?)parameters?["payload"] is string payload)
                    Message?.Invoke(payload);
                break;
        }
    }

    async Task SendAsync(string method, object parameters, string? session = null)
    {
        int id = Interlocked.Increment(ref nextId);
        lock (pending)
        {
            pending[id] = method;
        }
        var json = session == null
            ? JsonSerializer.Serialize(new { id, method, @params = parameters })
            : JsonSerializer.Serialize(new { id, method, @params = parameters, sessionId = session });
        await sending.WaitAsync(stop.Token);
        try
        {
            await socket!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, stop.Token);
        }
        finally
        {
            sending.Release();
        }
    }
}
