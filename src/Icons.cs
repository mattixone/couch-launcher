using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CouchLauncher;

/// <summary>
/// Tile artwork. Every icon is re-drawn in the theme's single phosphor
/// colour — brightness becomes intensity — so a red YouTube logo and a blue
/// Steam logo sit on the same screen like they belong to one display.
/// </summary>
static class Icons
{
    /// <summary>A web icon finished downloading; the home screen should redraw.</summary>
    public static event Action? Changed;

    static readonly Dictionary<string, BitmapSource?> Raw = new();
    static readonly Dictionary<string, ImageSource?> Tinted = new();
    static readonly HashSet<string> Fetching = new();
    static readonly HttpClient Http = CreateHttp();

    public static ImageSource? For(Tile tile, Theme theme)
    {
        var key = SourceKey(tile);
        if (key == null) return null;
        var tintKey = key + "|" + theme.Id;
        if (Tinted.TryGetValue(tintKey, out var cached)) return cached;

        var raw = LoadRaw(key);
        if (raw == null) return null;       // web icon still downloading, or none
        var tinted = Tint(raw, theme);
        Tinted[tintKey] = tinted;
        return tinted;
    }

    static string? SourceKey(Tile tile)
    {
        if (!string.IsNullOrWhiteSpace(tile.IconPath)) return "file:" + tile.IconPath;
        switch (tile.Kind)
        {
            case TileKind.App:
                return string.IsNullOrWhiteSpace(tile.Target) || tile.Target.Contains("://") ? null : "file:" + tile.Target;
            case TileKind.Steam:
                return AppCatalog.SteamExe() is { } steam ? "file:" + steam : null;
            case TileKind.Web:
                return Uri.TryCreate(tile.Url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? "web:" + uri.Host : null;
            default:
                return null;
        }
    }

    static BitmapSource? LoadRaw(string key)
    {
        if (Raw.TryGetValue(key, out var hit)) return hit;
        BitmapSource? bitmap = null;
        try
        {
            if (key.StartsWith("file:"))
            {
                var path = key[5..];
                bitmap = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? FromExe(path)
                    : FromImageFile(path);
            }
            else
            {
                var host = key[4..];
                var file = WebIconFile(host);
                if (!File.Exists(file))
                {
                    Fetch(host);
                    return null;            // not cached: Fetch fires Changed when done
                }
                bitmap = FromImageFile(file);
            }
        }
        catch (Exception e)
        {
            Log.Write($"icon {key}: {e.Message}");
        }
        Raw[key] = bitmap;
        return bitmap;
    }

    static BitmapSource? FromExe(string path)
    {
        if (!File.Exists(path)) return null;
        var icons = new IntPtr[1];
        var ids = new uint[1];
        uint count = Native.PrivateExtractIconsW(path, 0, 256, 256, icons, ids, 1, 0);
        if (count == 0 || count == uint.MaxValue || icons[0] == IntPtr.Zero) return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            Native.DestroyIcon(icons[0]);
        }
    }

    static BitmapSource? FromImageFile(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        // .ico files hold several sizes; take the biggest.
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        frame?.Freeze();
        return frame;
    }

    static BitmapSource Tint(BitmapSource source, Theme theme)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight, stride = width * 4;
        var pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        var ink = theme.Phosphor;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            if (alpha == 0) continue;
            double luminance = (0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2]) / 255.0;
            // Bright parts glow brightest on dark panels; on the pale LCD
            // panels the ink is dark, so dark parts get the most ink instead.
            double strength = 0.3 + 0.7 * (theme.LightPanel ? 1 - luminance : luminance);
            pixels[i] = ink.B;
            pixels[i + 1] = ink.G;
            pixels[i + 2] = ink.R;
            pixels[i + 3] = (byte)(alpha * strength);
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    static string WebIconFile(string host) => Path.Combine(Paths.Icons, host + ".img");

    /// <summary>
    /// Downloads a site's icon once and keeps it. Tries the site's own large
    /// home-screen icon first, then DuckDuckGo's icon service.
    /// </summary>
    static async void Fetch(string host)
    {
        if (!Fetching.Add(host)) return;
        var bare = host.StartsWith("www.") ? host[4..] : host;
        string[] urls = { $"https://{host}/apple-touch-icon.png", $"https://icons.duckduckgo.com/ip3/{bare}.ico" };
        foreach (var url in urls)
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                if (bytes.Length < 64) continue;
                // Some sites answer a missing icon with an HTML page; only keep
                // it if it really decodes as an image.
                using (var check = new MemoryStream(bytes))
                {
                    var decoder = BitmapDecoder.Create(check, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) continue;
                }
                await File.WriteAllBytesAsync(WebIconFile(host), bytes);
                Raw.Remove("web:" + host);
                foreach (var stale in Tinted.Keys.Where(k => k.StartsWith("web:" + host + "|")).ToList()) Tinted.Remove(stale);
                Changed?.Invoke();
                return;
            }
            catch (Exception e)
            {
                Log.Write($"icon download {url}: {e.Message}");
            }
        }
    }

    static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 CouchLauncher");
        return client;
    }
}
