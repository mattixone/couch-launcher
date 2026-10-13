using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CouchLauncher.Ui;

/// <summary>
/// The grid of big tiles. Tiles given a folder name are gathered behind one
/// folder tile; opening it shows just those, with BACK first.
/// </summary>
sealed class HomeScreen : Screen
{
    /// <summary>Tiles are laid out at LARGE size and scaled down to fit more
    /// to a row, so smaller sizes look the same, just smaller.</summary>
    const int LargeColumns = 4;
    const double TileWidth = 408, TileHeight = 280, Gap = 36;

    readonly List<Tile> items = new();
    readonly List<TileView> views = new();
    int focus;
    int columns = LargeColumns;
    /// <summary>The open folder's <see cref="Tile.FolderKey"/>, or "" for the top level.</summary>
    string folder = "";
    /// <summary>Tile to focus when the grid is next built.</summary>
    string? focusAfterBuild;

    public HomeScreen(MainWindow host) : base(host) { }

    bool InFolder => folder.Length > 0;

    public override string Hint => InFolder
        ? "▲▼◄► MOVE     A OPEN     B BACK (OR CLOSE AN OPEN APP)"
        : "▲▼◄► MOVE     A OPEN     B CLOSE AN OPEN APP";

    public override FrameworkElement Build()
    {
        items.Clear();
        views.Clear();
        var visible = Host.Config.Tiles.Where(t => !t.Hidden).ToList();
        // The folder was emptied or renamed while it was open.
        if (InFolder && !visible.Any(t => t.FolderKey == folder)) folder = "";
        if (InFolder)
        {
            items.Add(BuiltIn.Back);
            items.AddRange(visible.Where(t => t.FolderKey == folder));
        }
        else
        {
            // Each folder sits where its first tile would have been.
            var folders = new HashSet<string>();
            foreach (var tile in visible)
            {
                if (tile.FolderKey.Length == 0) items.Add(tile);
                else if (folders.Add(tile.FolderKey)) items.Add(BuiltIn.Folder(tile.Folder));
            }
            items.Add(BuiltIn.Power);
            items.Add(BuiltIn.Settings);
        }
        if (focusAfterBuild != null)
        {
            int wanted = items.FindIndex(t => t.Id == focusAfterBuild);
            if (wanted >= 0) focus = wanted;
            focusAfterBuild = null;
        }
        focus = Math.Clamp(focus, 0, items.Count - 1);

        columns = TileSizes.Columns(Host.Config.TileSize);
        double scale = (double)LargeColumns / columns;
        var grid = new WrapPanel
        {
            Width = columns * (TileWidth + Gap) * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var tile = items[i];
            var view = new TileView(tile, Subtitle(tile), Icons.For(tile, Host.Palette), TileWidth, TileHeight);
            view.Root.LayoutTransform = new ScaleTransform(scale, scale);
            view.Root.Margin = new Thickness(Gap * scale / 2);
            view.Root.MouseMove += (_, _) =>
            {
                if (focus != index && Input.CursorMoved()) SetFocus(index, scroll: false);
            };
            view.Root.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SetFocus(index);
                Open(items[index]);
            };
            view.Root.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                SetFocus(index);
                TryClose();
            };
            grid.Children.Add(view.Root);
            views.Add(view);
        }

        var content = new StackPanel();
        if (InFolder)
        {
            var heading = Crt.Text("FOLDER  ›  " + folder, 26, 0.6, bold: true);
            heading.Margin = new Thickness(0, 0, 0, 6);
            heading.Width = grid.Width;
            heading.Padding = new Thickness(Gap * scale / 2, 0, 0, 0);
            heading.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(heading);
        }
        content.Children.Add(grid);

        var scroller = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(0, 6, 0, 6),
        };
        Tick();
        SetFocus(focus);
        return scroller;
    }

    /// <summary>Focuses a tile, opening its folder (or leaving the open one) as needed.</summary>
    public void FocusTile(string id)
    {
        var tile = Host.Config.Tiles.FirstOrDefault(t => t.Id == id);
        if (tile != null && !tile.Hidden && tile.FolderKey != folder)
        {
            folder = tile.FolderKey;
            focusAfterBuild = id;
            Host.RebuildScreen();
            return;
        }
        int index = items.FindIndex(t => t.Id == id);
        if (index >= 0) SetFocus(index);
    }

    public override void OnNav(Nav nav)
    {
        int row = focus / columns, lastRow = (items.Count - 1) / columns;
        switch (nav)
        {
            case Nav.Left: SetFocus(Math.Max(0, focus - 1)); break;
            case Nav.Right: SetFocus(Math.Min(items.Count - 1, focus + 1)); break;
            case Nav.Up: if (row > 0) SetFocus(focus - columns); break;
            // Down into a shorter last row lands on its last tile.
            case Nav.Down: if (row < lastRow) SetFocus(Math.Min(items.Count - 1, focus + columns)); break;
            case Nav.Select: Open(items[focus]); break;
            // In a folder B still closes the app it's on, if that's open;
            // otherwise it leaves the folder.
            case Nav.Back when InFolder && !IsOpen(items[focus]): LeaveFolder(); break;
            case Nav.Back:
            case Nav.Close: TryClose(); break;
        }
    }

    public override void Tick()
    {
        for (int i = 0; i < views.Count; i++) views[i].SetRunning(IsOpen(items[i]));
    }

    void Open(Tile tile)
    {
        switch (tile.Kind)
        {
            case TileKind.Folder:
                folder = tile.FolderKey;
                focus = 1;      // the first tile, after BACK
                Host.RebuildScreen();
                break;
            case TileKind.Back:
                LeaveFolder();
                break;
            default:
                Host.ActivateTile(tile);
                break;
        }
    }

    void LeaveFolder()
    {
        focusAfterBuild = BuiltIn.Folder(folder).Id;
        folder = "";
        Host.RebuildScreen();
    }

    /// <summary>A folder counts as open when any of its tiles is.</summary>
    bool IsOpen(Tile tile) => tile.Kind switch
    {
        TileKind.Folder => FolderTiles(tile.FolderKey).Any(t => Host.Apps.For(t).Running),
        TileKind.Back => false,
        _ => Host.Apps.For(tile).Running,
    };

    IEnumerable<Tile> FolderTiles(string key) => Host.Config.Tiles.Where(t => !t.Hidden && t.FolderKey == key);

    void TryClose()
    {
        var tile = items[focus];
        if (!tile.IsBuiltIn && Host.Apps.For(tile).Running) Host.ConfirmClose(tile);
    }

    void SetFocus(int index, bool scroll = true)
    {
        if (views.Count == 0) return;
        focus = Math.Clamp(index, 0, views.Count - 1);
        for (int i = 0; i < views.Count; i++) views[i].SetFocused(i == focus);
        if (scroll) views[focus].Root.BringIntoView();
    }

    string Subtitle(Tile tile) => tile.Kind switch
    {
        TileKind.Folder => FolderTiles(tile.FolderKey).Count() switch
        {
            1 => "FOLDER · 1 TILE",
            var n => $"FOLDER · {n} TILES",
        },
        TileKind.Back => "TO ALL TILES",
        _ => Describe(tile),
    };

    static string Describe(Tile tile) => tile.Kind switch
    {
        TileKind.Web => Browsers.Resolve(tile.Browser) is { } browser
            ? browser.Label + (tile.TvMode ? " · TV MODE" : "")
            : "NO BROWSER FOUND",
        TileKind.App => string.IsNullOrWhiteSpace(tile.Target) ? "NOT SET UP YET" : "APP",
        TileKind.Steam => "BIG PICTURE",
        TileKind.LiveSurfer => tile.Channels.Count switch
        {
            0 => "LIVE SURFER · NO CHANNELS YET",
            1 => "LIVE SURFER · 1 CHANNEL",
            var n => $"LIVE SURFER · {n} CHANNELS",
        },
        TileKind.Power => "SLEEP · RESTART · OFF",
        _ => "THEME · TILES · UPDATES",
    };
}

/// <summary>One tile: icon, name, what opens it, and an OPEN badge.</summary>
sealed class TileView
{
    public Border Root { get; }
    readonly TextBlock badge;
    readonly ScaleTransform scale = new(1, 1);

    public TileView(Tile tile, string subtitle, ImageSource? icon, double width, double height)
    {
        var content = new Grid();

        FrameworkElement art;
        if (tile.IsBuiltIn || (tile.Kind == TileKind.LiveSurfer && icon == null))
        {
            // Power, settings, folders, back, and a TV for Live Surfer.
            var glyph = Crt.Text(tile.Kind switch { TileKind.Power => "", TileKind.Settings => "", TileKind.Folder => "\uE8B7", TileKind.Back => "\uE72B", _ => "" }, 96);
            glyph.FontFamily = Crt.Symbols;
            art = glyph;
        }
        else if (icon != null)
        {
            var image = new Image { Source = icon, Width = 124, Height = 124, Stretch = Stretch.Uniform };
            // Tiny favicons stay crisp and blocky rather than blurring —
            // which suits the CRT look anyway.
            bool small = icon is BitmapSource bitmap && bitmap.PixelWidth < 64;
            RenderOptions.SetBitmapScalingMode(image, small ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            art = image;
        }
        else
        {
            art = Crt.Text(tile.Name.Length > 0 ? tile.Name[..1].ToUpperInvariant() : "?", 100, 0.8, bold: true);
        }
        art.HorizontalAlignment = HorizontalAlignment.Center;
        art.VerticalAlignment = VerticalAlignment.Top;
        art.Margin = new Thickness(0, 30, 0, 0);
        content.Children.Add(art);

        var name = Crt.Text(tile.Name.ToUpperInvariant(), 38, bold: true);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.VerticalAlignment = VerticalAlignment.Bottom;
        name.Margin = new Thickness(0, 0, 0, 58);
        name.MaxWidth = width - 32;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(name);

        var detail = Crt.Text(subtitle, 20, 0.6);
        detail.HorizontalAlignment = HorizontalAlignment.Center;
        detail.VerticalAlignment = VerticalAlignment.Bottom;
        detail.Margin = new Thickness(0, 0, 0, 24);
        content.Children.Add(detail);

        badge = Crt.Text("● OPEN", 18, bold: true);
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        badge.VerticalAlignment = VerticalAlignment.Top;
        badge.Margin = new Thickness(0, 12, 14, 0);
        badge.Visibility = Visibility.Hidden;
        content.Children.Add(badge);

        Root = new Border
        {
            Width = width,
            Height = height,
            Child = content,
            BorderThickness = new Thickness(2),
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Cursor = Cursors.Hand,
        };
        SetFocused(false);
    }

    public void SetFocused(bool on)
    {
        Root.BorderBrush = Crt.Fg(on ? 1 : 0.35);
        Root.Background = Crt.Fg(on ? 0.16 : 0.04);
        Root.Effect = on ? Crt.Glow(34, 0.85) : null;
        scale.ScaleX = scale.ScaleY = on ? 1.04 : 1;
    }

    public void SetRunning(bool on) => badge.Visibility = on ? Visibility.Visible : Visibility.Hidden;
}

/// <summary>How big the home screen's tiles are: how many to a row.</summary>
static class TileSizes
{
    public static readonly (string Id, int Columns)[] All = { ("LARGE", 4), ("MEDIUM", 5), ("SMALL", 6) };

    public static int IndexOf(string id) =>
        Math.Max(0, Array.FindIndex(All, size => size.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

    public static int Columns(string id) => All[IndexOf(id)].Columns;

    /// <summary>The next size along, wrapping round.</summary>
    public static string Step(string id, int step) => All[(IndexOf(id) + step + All.Length) % All.Length].Id;
}
