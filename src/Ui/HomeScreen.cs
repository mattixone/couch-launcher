using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CouchLauncher.Ui;

/// <summary>The grid of big tiles.</summary>
sealed class HomeScreen : Screen
{
    const int Columns = 4;
    const double TileWidth = 408, TileHeight = 280, Gap = 36;

    readonly List<Tile> items = new();
    readonly List<TileView> views = new();
    int focus;

    public HomeScreen(MainWindow host) : base(host) { }

    public override string Hint => "▲▼◄► MOVE     A OPEN     B CLOSE AN OPEN APP";

    public override FrameworkElement Build()
    {
        items.Clear();
        views.Clear();
        items.AddRange(Host.Config.Tiles);
        items.Add(BuiltIn.Power);
        items.Add(BuiltIn.Settings);
        focus = Math.Clamp(focus, 0, items.Count - 1);

        var grid = new WrapPanel
        {
            Width = Columns * (TileWidth + Gap),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var tile = items[i];
            var view = new TileView(tile, Subtitle(tile), Icons.For(tile, Host.Palette), TileWidth, TileHeight);
            view.Root.Margin = new Thickness(Gap / 2);
            view.Root.MouseMove += (_, _) =>
            {
                if (focus != index && Input.CursorMoved()) SetFocus(index, scroll: false);
            };
            view.Root.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SetFocus(index);
                Host.ActivateTile(items[index]);
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

        var scroller = new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(0, 6, 0, 6),
        };
        Tick();
        SetFocus(focus);
        return scroller;
    }

    public void FocusTile(string id)
    {
        int index = items.FindIndex(t => t.Id == id);
        if (index >= 0) SetFocus(index);
    }

    public override void OnNav(Nav nav)
    {
        int row = focus / Columns, lastRow = (items.Count - 1) / Columns;
        switch (nav)
        {
            case Nav.Left: SetFocus(Math.Max(0, focus - 1)); break;
            case Nav.Right: SetFocus(Math.Min(items.Count - 1, focus + 1)); break;
            case Nav.Up: if (row > 0) SetFocus(focus - Columns); break;
            // Down into a shorter last row lands on its last tile.
            case Nav.Down: if (row < lastRow) SetFocus(Math.Min(items.Count - 1, focus + Columns)); break;
            case Nav.Select: Host.ActivateTile(items[focus]); break;
            case Nav.Back:
            case Nav.Close: TryClose(); break;
        }
    }

    public override void Tick()
    {
        for (int i = 0; i < views.Count; i++) views[i].SetRunning(Host.Apps.For(items[i]).Running);
    }

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

    static string Subtitle(Tile tile) => tile.Kind switch
    {
        TileKind.Web => Browsers.Resolve(tile.Browser) is { } browser
            ? browser.Label + (tile.TvMode ? " · TV MODE" : "")
            : "NO BROWSER FOUND",
        TileKind.App => string.IsNullOrWhiteSpace(tile.Target) ? "NOT SET UP YET" : "APP",
        TileKind.Steam => "BIG PICTURE",
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
        if (tile.IsBuiltIn)
        {
            var glyph = Crt.Text(tile.Kind == TileKind.Power ? "" : "", 96);
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
