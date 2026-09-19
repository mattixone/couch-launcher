using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CouchLauncher.Ui;

/// <summary>
/// The switch overlay: a row of the open apps with one highlighted. Each
/// press of the switch shortcut moves along; stop pressing and it goes there.
/// </summary>
sealed class SwitcherScreen : Screen
{
    readonly List<Tile> tiles;
    readonly string? returnTo;
    readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(1300) };
    readonly List<Border> cards = new();
    int selected;

    public SwitcherScreen(MainWindow host, List<Tile> tiles, int start, string? returnTo) : base(host)
    {
        this.tiles = tiles;
        this.returnTo = returnTo;
        selected = start;
        settle.Tick += (_, _) => Go();
    }

    public override string Hint => "SWITCH AGAIN FOR THE NEXT ONE     A GO NOW     B CANCEL";

    public override FrameworkElement Build()
    {
        cards.Clear();
        double width = Math.Min(300, 1740.0 / tiles.Count - 28);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < tiles.Count; i++)
        {
            int index = i;
            var tile = tiles[i];
            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var icon = Icons.For(tile, Host.Palette);
            FrameworkElement art = icon != null
                ? new Image { Source = icon, Width = 96, Height = 96 }
                : Crt.Text(tile.Name.Length > 0 ? tile.Name[..1].ToUpperInvariant() : "?", 80, 0.8, bold: true);
            art.HorizontalAlignment = HorizontalAlignment.Center;
            art.Margin = new Thickness(0, 0, 0, 18);
            body.Children.Add(art);
            var name = Crt.Text(tile.Name.ToUpperInvariant(), 28, bold: true);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = width - 20;
            body.Children.Add(name);

            var card = new Border
            {
                Width = width,
                Height = 240,
                Margin = new Thickness(14),
                BorderThickness = new Thickness(2),
                Child = body,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            card.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                selected = index;
                Go();
            };
            cards.Add(card);
            row.Children.Add(card);
        }

        var layout = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 80) };
        var heading = Crt.Text("SWITCH TO", 30, 0.7, bold: true);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.Margin = new Thickness(0, 0, 0, 20);
        layout.Children.Add(heading);
        layout.Children.Add(row);

        Paint();
        settle.Stop();
        settle.Start();
        return layout;
    }

    public void Advance() => Move(1);

    public override void OnNav(Nav nav)
    {
        switch (nav)
        {
            case Nav.Left: Move(-1); break;
            case Nav.Right: Move(1); break;
            case Nav.Select: Go(); break;
            case Nav.Back: Cancel(); break;
        }
    }

    public override void OnLeave() => settle.Stop();

    void Move(int step)
    {
        selected = (selected + step + tiles.Count) % tiles.Count;
        Paint();
        settle.Stop();
        settle.Start();
    }

    void Paint()
    {
        for (int i = 0; i < cards.Count; i++)
        {
            bool on = i == selected;
            cards[i].BorderBrush = Crt.Fg(on ? 1 : 0.3);
            cards[i].Background = Crt.Fg(on ? 0.18 : 0.03);
            cards[i].Effect = on ? Crt.Glow(34, 0.9) : null;
            cards[i].RenderTransform = new ScaleTransform(on ? 1.06 : 1, on ? 1.06 : 1);
        }
    }

    void Go()
    {
        settle.Stop();
        Host.ActivateTile(tiles[selected]);
    }

    void Cancel()
    {
        settle.Stop();
        var back = returnTo == null ? null : tiles.FirstOrDefault(t => t.Id == returnTo);
        if (back != null) Host.ActivateTile(back);
        else Host.ShowHome();
    }
}
