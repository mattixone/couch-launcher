using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CouchLauncher.Ui;

/// <summary>
/// Adding and changing tiles. The one screen meant for a mouse and keyboard
/// (or the Retroid's cursor and typing) — it's a one-time setup job.
/// Changes apply as you make them and are saved on the way out.
/// </summary>
sealed class EditorScreen : Screen
{
    StackPanel list = new();
    Border form = new();
    int selected;

    public EditorScreen(MainWindow host) : base(host) { }

    public override bool CapturesTyping => true;
    public override string Hint => "MOUSE AND KEYBOARD HERE     CHANGES SAVE AUTOMATICALLY     ESC GOES BACK";

    List<Tile> Tiles => Host.Config.Tiles;
    Tile? Current => selected >= 0 && selected < Tiles.Count ? Tiles[selected] : null;

    public override FrameworkElement Build()
    {
        Browsers.Forget();
        list = new StackPanel();
        form = new Border();

        var layout = new Grid { Margin = new Thickness(90, 0, 90, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(480) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel();
        var heading = Crt.Text("YOUR TILES", 26, 0.6, bold: true);
        heading.Margin = new Thickness(0, 0, 0, 16);
        DockPanel.SetDock(heading, Dock.Top);
        left.Children.Add(heading);
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(Crt.Button("+ ADD", Add));
        buttons.Children.Add(Crt.Button("▲", () => Move(-1)));
        buttons.Children.Add(Crt.Button("▼", () => Move(1)));
        buttons.Children.Add(Crt.Button("DELETE", Delete));
        buttons.Children.Add(Crt.Button("DONE", Done, selected: true));
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(buttons);
        left.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        layout.Children.Add(left);

        var right = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        Grid.SetColumn(right, 2);
        layout.Children.Add(right);

        RebuildList();
        RebuildForm();
        return layout;
    }

    public override void OnNav(Nav nav)
    {
        if (nav == Nav.Back) Done();
    }

    public override void OnLeave()
    {
        Host.Config.Save();
        Host.Apps.Sync();
    }

    void Done() => Host.ShowScreen(new SettingsScreen(Host));

    void RebuildList()
    {
        list.Children.Clear();
        for (int i = 0; i < Tiles.Count; i++)
        {
            int index = i;
            bool on = i == selected;
            var row = new Border
            {
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 0, 0, 8),
                BorderThickness = new Thickness(2),
                BorderBrush = Crt.Fg(on ? 1 : 0.2),
                Background = Crt.Fg(on ? 0.16 : 0.02),
                Child = Crt.Text($"{i + 1}. {Tiles[i].Name.ToUpperInvariant()}", 26, bold: on),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                selected = index;
                RebuildList();
                RebuildForm();
            };
            list.Children.Add(row);
        }
    }

    void RebuildForm()
    {
        var tile = Current;
        var fields = new StackPanel();
        form.Child = fields;
        if (tile == null)
        {
            fields.Children.Add(Crt.Text("PRESS + ADD TO MAKE A TILE", 26, 0.7));
            return;
        }

        fields.Children.Add(Field("NAME", Crt.Input(tile.Name, value =>
        {
            tile.Name = value;
            RebuildList();
        })));

        fields.Children.Add(Field("TYPE", Chips(
            ("WEB PAGE", tile.Kind == TileKind.Web, () => SetKind(tile, TileKind.Web)),
            ("APP", tile.Kind == TileKind.App, () => SetKind(tile, TileKind.App)),
            ("STEAM BIG PICTURE", tile.Kind == TileKind.Steam, () => SetKind(tile, TileKind.Steam)))));

        switch (tile.Kind)
        {
            case TileKind.Web:
                fields.Children.Add(Field("WEB ADDRESS", Crt.Input(tile.Url, value => tile.Url = value.Trim())));
                fields.Children.Add(Field("BROWSER", Chips(Browsers.All.Select(b =>
                    (b.Label + (Browsers.Installed(b) ? "" : " (NOT INSTALLED)"), tile.Browser == b.Id,
                     (Action)(() => { tile.Browser = b.Id; RebuildForm(); }))).ToArray())));
                fields.Children.Add(Field("TV MODE", Chips(
                    (tile.TvMode ? "ON" : "OFF", tile.TvMode, () => { tile.TvMode = !tile.TvMode; RebuildForm(); })),
                    "Pretends to be a smart TV. Needed for youtube.com/tv, which otherwise redirects."));
                break;

            case TileKind.App:
                var program = new StackPanel();
                program.Children.Add(Crt.Input(tile.Target, value => tile.Target = value.Trim()));
                program.Children.Add(Chips(
                    ("PICK FROM START MENU", false, () => PickFromStartMenu(tile)),
                    ("BROWSE…", false, () => Browse(tile))));
                fields.Children.Add(Field("PROGRAM", program));
                fields.Children.Add(Field("ARGUMENTS", Crt.Input(tile.Args, value => tile.Args = value), "Usually empty."));

                var watch = new StackPanel();
                watch.Children.Add(Crt.Input(tile.MatchProcess, value => tile.MatchProcess = value.Trim()));
                watch.Children.Add(Chips(("PICK FROM OPEN WINDOWS", false, () => PickFromOpenWindows(tile))));
                fields.Children.Add(Field("ITS WINDOWS", watch,
                    "Program name the launcher looks for to know the app is open. Filled in for you; if it's wrong, open the app and pick it from the open windows."));
                fields.Children.Add(Field("FULLSCREEN", Chips(
                    (tile.ForceFullscreen ? "FORCE IT" : "LEAVE ALONE", tile.ForceFullscreen,
                     () => { tile.ForceFullscreen = !tile.ForceFullscreen; RebuildForm(); }))));
                break;

            case TileKind.Steam:
                fields.Children.Add(Field("", Crt.Text("Opens Steam straight into Big Picture mode. Nothing to set up.", 22, 0.7)));
                break;
        }

        fields.Children.Add(Field("POP-UP WINDOWS", Chips(
            (tile.BlockPopups ? "BLOCKED" : "ALLOWED", tile.BlockPopups, () => { tile.BlockPopups = !tile.BlockPopups; RebuildForm(); })),
            "Allow them while you sign in for the first time, then block them again."));

        fields.Children.Add(Field("ICON", Chips(
            ("AUTOMATIC", tile.IconPath.Length == 0, () => { tile.IconPath = ""; RebuildForm(); }),
            ("CHOOSE IMAGE…", tile.IconPath.Length > 0, () => ChooseIcon(tile))),
            tile.IconPath.Length > 0 ? tile.IconPath : null));
    }

    void SetKind(Tile tile, TileKind kind)
    {
        tile.Kind = kind;
        // Browsers love opening stray windows; desktop apps' extra windows
        // are usually real (settings, dialogs).
        tile.BlockPopups = kind == TileKind.Web;
        if (kind == TileKind.Steam && tile.Name == "NEW TILE") tile.Name = "STEAM";
        RebuildList();
        RebuildForm();
    }

    void Add()
    {
        Tiles.Add(new Tile());
        selected = Tiles.Count - 1;
        Host.Config.Save();
        RebuildList();
        RebuildForm();
    }

    void Move(int step)
    {
        int to = selected + step;
        if (Current == null || to < 0 || to >= Tiles.Count) return;
        (Tiles[selected], Tiles[to]) = (Tiles[to], Tiles[selected]);
        selected = to;
        RebuildList();
    }

    void Delete()
    {
        var tile = Current;
        if (tile == null) return;
        Host.ShowModal(new ChoiceModal(Host, $"DELETE {tile.Name.ToUpperInvariant()}?", new[] { "YES, DELETE IT", "NO" }, choice =>
        {
            if (choice != 0) return;
            Tiles.Remove(tile);
            selected = Math.Min(selected, Tiles.Count - 1);
            Host.Config.Save();
            RebuildList();
            RebuildForm();
        }));
    }

    void PickFromStartMenu(Tile tile)
    {
        var apps = AppCatalog.StartMenuApps();
        Host.ShowModal(new PickerModal(Host, "PICK AN APP", apps.Select(a => a.Name.ToUpperInvariant()).ToList(),
            index => UseProgram(tile, apps[index].ShortcutPath, apps[index].Name)));
    }

    void Browse(Tile tile)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick the program",
            Filter = "Programs and shortcuts|*.exe;*.lnk;*.url|All files|*.*",
        };
        if (dialog.ShowDialog(Host) == true)
            UseProgram(tile, dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
    }

    void UseProgram(Tile tile, string path, string suggestedName)
    {
        var (target, args) = AppCatalog.Resolve(path);
        tile.Target = target;
        tile.Args = args;
        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            tile.MatchProcess = Path.GetFileNameWithoutExtension(target);
        if (tile.Name is "NEW TILE" or "") tile.Name = suggestedName.ToUpperInvariant();
        RebuildList();
        RebuildForm();
    }

    void PickFromOpenWindows(Tile tile)
    {
        var windows = WindowScanner.Scan();
        var labels = windows
            .Select(w => $"{w.Process.ToUpperInvariant()}  —  {(w.Title.Length > 60 ? w.Title[..60] + "…" : w.Title)}")
            .ToList();
        Host.ShowModal(new PickerModal(Host, "WHICH WINDOW IS THIS APP?", labels, index =>
        {
            var window = windows[index];
            tile.MatchProcess = window.Process;
            if (string.IsNullOrWhiteSpace(tile.Target)) tile.Target = Native.ProcessPath(window.Pid) ?? "";
            if (tile.Name is "NEW TILE" or "") tile.Name = window.Process.ToUpperInvariant();
            RebuildList();
            RebuildForm();
        }));
    }

    void ChooseIcon(Tile tile)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick an icon",
            Filter = "Images and programs|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.exe|All files|*.*",
        };
        if (dialog.ShowDialog(Host) != true) return;
        tile.IconPath = dialog.FileName;
        RebuildForm();
    }

    /// <summary>A labelled form row: label on the left, control and an optional note on the right.</summary>
    static FrameworkElement Field(string label, UIElement control, string? note = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = Crt.Text(label, 22, 0.65, bold: true);
        name.Margin = new Thickness(0, 12, 0, 0);
        row.Children.Add(name);

        var right = new StackPanel();
        right.Children.Add(control);
        if (note != null)
        {
            var text = Crt.Text(note, 18, 0.5);
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(2, 6, 0, 0);
            right.Children.Add(text);
        }
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    static WrapPanel Chips(params (string Label, bool Selected, Action OnClick)[] chips)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 10, 0, -14) };
        foreach (var chip in chips) panel.Children.Add(Crt.Button(chip.Label, chip.OnClick, chip.Selected, size: 22));
        return panel;
    }
}
