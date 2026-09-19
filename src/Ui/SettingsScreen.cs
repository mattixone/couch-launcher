using System.Windows;
using System.Windows.Controls;

namespace CouchLauncher.Ui;

/// <summary>Theme, start-with-Windows, tile editing, updates. D-pad friendly.</summary>
sealed class SettingsScreen : Screen
{
    sealed record Row(Func<string> Label, Action Select, Action<int>? Adjust = null);

    readonly List<Row> rows;
    readonly List<Border> views = new();
    int selected;
    string note = "";

    public SettingsScreen(MainWindow host) : base(host)
    {
        rows = new List<Row>
        {
            new(() => $"THEME   ◄ {Host.Palette.Label} ►",
                () => Host.SetTheme(Host.Palette.Next),
                step => Host.SetTheme(step > 0 ? Host.Palette.Next : Host.Palette.Previous)),
            new(() => $"START WITH WINDOWS:  {(Host.Config.StartAtLogin ? "ON" : "OFF")}", ToggleStartup),
            new(() => "EDIT TILES", () => Host.ShowScreen(new EditorScreen(Host))),
            new(() => "CHECK FOR UPDATES", CheckForUpdates),
            new(() => "EXIT THE LAUNCHER", ConfirmExit),
        };
    }

    public override string Hint => "▲▼ MOVE     ◄► CHANGE     A SELECT     B BACK";

    public override FrameworkElement Build()
    {
        views.Clear();
        var layout = new Grid { Margin = new Thickness(90, 10, 90, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(900) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var list = new StackPanel();
        list.Children.Add(Heading("SETTINGS"));
        for (int i = 0; i < rows.Count; i++)
        {
            int index = i;
            var view = new Border
            {
                Padding = new Thickness(26, 14, 26, 14),
                Margin = new Thickness(0, 0, 0, 14),
                BorderThickness = new Thickness(2),
                Child = Crt.Text(rows[i].Label(), 32, bold: true),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            view.MouseMove += (_, _) =>
            {
                if (selected != index && Input.CursorMoved())
                {
                    selected = index;
                    Paint();
                }
            };
            view.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                selected = index;
                rows[index].Select();
            };
            views.Add(view);
            list.Children.Add(view);
        }
        if (note.Length > 0)
        {
            var noteText = Crt.Text(note, 24, 0.9);
            noteText.Margin = new Thickness(4, 10, 0, 0);
            list.Children.Add(noteText);
        }
        layout.Children.Add(list);

        var info = new StackPanel { Margin = new Thickness(80, 0, 0, 0) };
        info.Children.Add(Heading("SHORTCUTS"));
        info.Children.Add(Line($"HOME     {Host.Config.HomeHotkey.ToUpperInvariant()}"));
        info.Children.Add(Line($"SWITCH   {Host.Config.SwitchHotkey.ToUpperInvariant()}"));
        info.Children.Add(Line("Set these up as shortcuts in the Retroid", 0.6, 20));
        info.Children.Add(Line("app (Actions › Hotkeys) and they work", 0.6, 20));
        info.Children.Add(Line("from inside any app.", 0.6, 20));
        if (Host.HotkeyProblems.Length > 0) info.Children.Add(Line(Host.HotkeyProblems, 1, 20));
        info.Children.Add(Spacer());
        info.Children.Add(Heading("ABOUT"));
        info.Children.Add(Line($"VERSION  {Updater.Version}"));
        info.Children.Add(Line("Log and settings:", 0.6, 20));
        info.Children.Add(Line(Paths.Data, 0.6, 18));
        Grid.SetColumn(info, 1);
        layout.Children.Add(info);

        Paint();
        return layout;
    }

    public override void OnNav(Nav nav)
    {
        switch (nav)
        {
            case Nav.Up: selected = Math.Max(0, selected - 1); Paint(); break;
            case Nav.Down: selected = Math.Min(rows.Count - 1, selected + 1); Paint(); break;
            case Nav.Left: rows[selected].Adjust?.Invoke(-1); break;
            case Nav.Right: rows[selected].Adjust?.Invoke(1); break;
            case Nav.Select: rows[selected].Select(); break;
            case Nav.Back: Host.ShowHome(); break;
        }
    }

    void Paint()
    {
        for (int i = 0; i < views.Count; i++)
        {
            bool on = i == selected;
            views[i].BorderBrush = Crt.Fg(on ? 1 : 0.25);
            views[i].Background = Crt.Fg(on ? 0.16 : 0.03);
            views[i].Effect = on ? Crt.Glow(24, 0.7) : null;
            ((TextBlock)views[i].Child).Text = rows[i].Label();
        }
    }

    void ToggleStartup()
    {
        Host.Config.StartAtLogin = !Host.Config.StartAtLogin;
        Host.Config.Save();
        LoginStartup.Apply(Host.Config.StartAtLogin);
        note = Updater.IsInstalled ? "" : "(START WITH WINDOWS ONLY APPLIES TO THE INSTALLED APP)";
        Host.RebuildScreen();
    }

    async void CheckForUpdates()
    {
        note = "CHECKING…";
        Host.RebuildScreen();
        note = await Host.RunUpdate(automatic: false);
        Host.RebuildScreen();
    }

    void ConfirmExit() =>
        Host.ShowModal(new ChoiceModal(Host, "EXIT THE LAUNCHER?", new[] { "YES, EXIT", "NO" }, choice =>
        {
            if (choice == 0) Host.ExitLauncher();
        }));

    static TextBlock Heading(string text)
    {
        var heading = Crt.Text(text, 26, 0.6, bold: true);
        heading.Margin = new Thickness(4, 0, 0, 18);
        return heading;
    }

    static TextBlock Line(string text, double alpha = 1, double size = 26)
    {
        var line = Crt.Text(text, size, alpha);
        line.Margin = new Thickness(4, 0, 0, 10);
        line.TextWrapping = TextWrapping.Wrap;
        return line;
    }

    static FrameworkElement Spacer() => new Border { Height = 30 };
}
