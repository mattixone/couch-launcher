using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace CouchLauncher;

/// <summary>
/// Settings › DRM CHECK: which copy protection WebView2 — the browser engine
/// Windows lets apps build in — can play. Streaming sites only play in a
/// browser that has the DRM they use; Brave, Chrome and Edge have Google's
/// Widevine, WebView2 is known to lack it. If WebView2 has what the sites ask
/// for (couch mode logs what they ask for), a browser built into the launcher
/// would be an option.
/// </summary>
static class DrmCheck
{
    static readonly string[] KeySystems =
    {
        "com.widevine.alpha",
        "com.microsoft.playready.recommendation",
        "com.microsoft.playready.recommendation.3000",
        "com.microsoft.playready",
    };

    const string Probe = """
        (async () => {
          const config = [{
            initDataTypes: ['cenc'],
            videoCapabilities: [{ contentType: 'video/mp4; codecs="avc1.42E01E"' }],
            audioCapabilities: [{ contentType: 'audio/mp4; codecs="mp4a.40.2"' }],
          }];
          const answers = {};
          for (const keySystem of KEYS) {
            try { await navigator.requestMediaKeySystemAccess(keySystem, config); answers[keySystem] = 'yes'; }
            catch (e) { answers[keySystem] = 'no (' + e.name + ')'; }
          }
          return JSON.stringify(answers);
        })()
        """;

    /// <summary>A line for the screen; the details go in the log.</summary>
    public static async Task<string> RunAsync(IntPtr parentWindow)
    {
        CoreWebView2Controller? controller = null;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.Data, "webview2"));
            controller = await env.CreateCoreWebView2ControllerAsync(parentWindow);
            controller.IsVisible = false;
            var web = controller.CoreWebView2;

            // DRM is only offered to secure (https) pages, so serve a blank
            // one from a made-up https address.
            var folder = Path.Combine(Paths.Data, "webview2-check");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "index.html"), "<!doctype html><title>DRM check</title>");
            web.SetVirtualHostNameToFolderMapping("couch.check", folder, CoreWebView2HostResourceAccessKind.Deny);
            var loaded = new TaskCompletionSource<bool>();
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            web.Navigate("https://couch.check/index.html");
            if (await Task.WhenAny(loaded.Task, Task.Delay(15000)) != loaded.Task || !loaded.Task.Result)
                return "DRM CHECK: WEBVIEW2 DIDN'T LOAD THE TEST PAGE";

            var expression = Probe.Replace("KEYS", JsonSerializer.Serialize(KeySystems));
            var reply = await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }));
            using var doc = JsonDocument.Parse(reply);
            var json = doc.RootElement.GetProperty("result").GetProperty("value").GetString() ?? "{}";
            var answers = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            Log.Write($"DRM check, WebView2 {env.BrowserVersionString}: " +
                      string.Join(", ", answers.Select(a => $"{a.Key} {a.Value}")));

            bool widevine = answers.GetValueOrDefault(KeySystems[0]) == "yes";
            bool playready = answers.Where(a => a.Key.Contains("playready")).Any(a => a.Value == "yes");
            return $"WEBVIEW2:  WIDEVINE {(widevine ? "YES" : "NO")}   PLAYREADY {(playready ? "YES" : "NO")}" +
                   "  (THE LOG SAYS WHICH ONE EACH SITE ASKS FOR)";
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return "DRM CHECK: WEBVIEW2 ISN'T INSTALLED ON THIS PC";
        }
        catch (Exception e)
        {
            Log.Write("DRM check failed: " + e);
            return "DRM CHECK FAILED — SEE THE LOG";
        }
        finally
        {
            controller?.Close();
        }
    }
}
