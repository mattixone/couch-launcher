using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CouchLauncher.Ui;

/// <summary>
/// The launcher: one borderless full-screen window that sits behind every app
/// it opens, so closing an app always lands you back on the tiles.
///
/// Everything is laid out on a fixed 1920×1080 stage inside a Viewbox, so it
/// looks the same on a 1080p TV, a 4K TV, and at any Windows scaling setting.
/// </summary>
sealed class MainWindow : Window
{
    const int HotkeyHome = 1, HotkeySwitch = 2;

    public LauncherConfig Config { get; }
    public AppManager Apps { get; }
    public Theme Palette => Theme.Get(Config.Theme);
    public string HotkeyProblems { get; private set; } = "";

    readonly Grid stage = new() { Width = 1920, Height = 1080 };
    readonly Border screenHost = new();
    readonly Grid modalHost = new();
    readonly System.Windows.Shapes.Rectangle scanlines = new() { IsHitTestVisible = false };
    readonly System.Windows.Shapes.Rectangle grain = new() { IsHitTestVisible = false };
    readonly TextBlock title = new(), subtitle = new(), clock = new(), date = new(), hint = new(), status = new();
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer statusTimer = new();
    readonly HomeScreen home;

    Screen screen;
    Modal? modal;
    IntPtr hwnd;
    /// <summary>The tile whose app is in front right now (null while the launcher is).</summary>
    string? currentTileId;
    /// <summary>The tile used most recently — where focus returns on the home screen.</summary>
    string? lastTileId;
    int desktopTicks;
    bool allowClose;

    public MainWindow()
    {
        Title = "Couch Launcher";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;

        Config = LauncherConfig.Load();
        Apps = new AppManager(Config);
        BuildChrome();
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = stage };

        home = new HomeScreen(this);
        screen = home;
        ApplyTheme();

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            RegisterHotkeys();
        };
        Loaded += OnLoaded;
        PreviewKeyDown += OnKey;
        // Alt+F4 in the launcher would strand you on the desktop, so the only
        // ways out are the menu and Windows itself shutting down.
        Closing += (_, e) => e.Cancel = !allowClose;
        Closed += (_, _) =>
        {
            Native.UnregisterHotKey(hwnd, HotkeyHome);
            Native.UnregisterHotKey(hwnd, HotkeySwitch);
        };
        Application.Current.SessionEnding += (_, _) => allowClose = true;
        ticker.Tick += (_, _) => Tick();
        statusTimer.Tick += (_, _) =>
        {
            statusTimer.Stop();
            status.Text = "";
        };
        Icons.Changed += () =>
        {
            if (screen is HomeScreen or SwitcherScreen) RebuildScreen();
        };
    }

    async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Log.Write($"launcher v{Updater.Version} started");
        LoginStartup.Apply(Config.StartAtLogin);
        BringToFront();
        await Apps.DiscoverAsync();
        Apps.Refresh();
        home.Tick();
        ticker.Start();
        await RunUpdate(automatic: true);
    }

    void BuildChrome()
    {
        stage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(160) });
        stage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });

        var header = new Grid { Margin = new Thickness(90, 44, 90, 0) };
        var titles = new StackPanel();
        titles.Children.Add(title);
        titles.Children.Add(subtitle);
        var clocks = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        clock.HorizontalAlignment = HorizontalAlignment.Right;
        date.HorizontalAlignment = HorizontalAlignment.Right;
        clocks.Children.Add(clock);
        clocks.Children.Add(date);
        header.Children.Add(titles);
        header.Children.Add(clocks);
        stage.Children.Add(header);

        Grid.SetRow(screenHost, 1);
        stage.Children.Add(screenHost);

        var footer = new Grid { Margin = new Thickness(90, 0, 90, 26) };
        hint.VerticalAlignment = VerticalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(hint);
        footer.Children.Add(status);
        Grid.SetRow(footer, 2);
        stage.Children.Add(footer);

        foreach (var layer in new UIElement[] { modalHost, scanlines, grain })
        {
            Grid.SetRowSpan(layer, 3);
            stage.Children.Add(layer);
        }
    }

    public void ApplyTheme()
    {
        Crt.Use(Palette);
        Background = Crt.Bg();
        stage.Background = Crt.Bg();

        title.Text = "COUCH COMMANDER";
        Crt.Style(title, 56, bold: true);
        title.Effect = Crt.Glow(20, 0.9);
        subtitle.Text = "tv launcher";
        Crt.Style(subtitle, 22, 0.6);
        Crt.Style(clock, 50, bold: true);
        clock.Effect = Crt.Glow(16, 0.7);
        Crt.Style(date, 22, 0.6);
        Crt.Style(hint, 22, 0.55);
        Crt.Style(status, 22, 0.95);

        scanlines.Fill = Palette.Scanlines > 0 ? Crt.Scanlines(Palette.Scanlines) : null;
        grain.Fill = Palette.Noise ? Crt.Noise() : null;
        UpdateClock();
        RebuildScreen();
        if (modal != null) ShowModal(modal);
    }

    public void SetTheme(Theme theme)
    {
        Config.Theme = theme.Id;
        Config.Save();
        ApplyTheme();
    }

    // ---------------------------------------------------------------- screens

    public void ShowScreen(Screen next)
    {
        if (next != screen) screen.OnLeave();
        screen = next;
        CloseModal();
        RebuildScreen();
    }

    public void RebuildScreen()
    {
        screenHost.Child = screen.Build();
        hint.Text = screen.Hint;
    }

    public void ShowModal(Modal next)
    {
        modal = next;
        modalHost.Children.Clear();
        // Dims the screen behind and swallows clicks meant for it.
        modalHost.Children.Add(new Border { Background = Crt.Bg(0.86) });
        modalHost.Children.Add(next.Build());
    }

    public void CloseModal()
    {
        modal = null;
        modalHost.Children.Clear();
    }

    public void SetStatus(string text, int seconds = 5)
    {
        status.Text = text;
        statusTimer.Stop();
        if (seconds > 0 && text.Length > 0)
        {
            statusTimer.Interval = TimeSpan.FromSeconds(seconds);
            statusTimer.Start();
        }
    }

    /// <summary>Back to the tiles, focused on whatever you were just using.</summary>
    public void ShowHome()
    {
        CloseModal();
        if (screen != home) ShowScreen(home);
        if (lastTileId != null) home.FocusTile(lastTileId);
        BringToFront();
    }

    void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        // Topmost on-and-off lifts the window over a fullscreen app even when
        // Windows refuses to hand over keyboard focus.
        Topmost = true;
        Topmost = false;
        Activate();
        if (hwnd != IntPtr.Zero && !Native.ForceForeground(hwnd)) Log.Write("couldn't bring the launcher to the front");
    }

    public void ExitLauncher()
    {
        Log.Write("exit from settings");
        allowClose = true;
        Application.Current.Shutdown();
    }

    // ---------------------------------------------------------------- tiles

    public async void ActivateTile(Tile tile)
    {
        switch (tile.Kind)
        {
            case TileKind.Power:
                ShowPowerMenu();
                return;
            case TileKind.Settings:
                ShowScreen(new SettingsScreen(this));
                return;
        }

        if (screen != home) ShowScreen(home);
        home.FocusTile(tile.Id);
        lastTileId = tile.Id;
        SetStatus($"OPENING {tile.Name.ToUpperInvariant()}…");
        try
        {
            var problem = await Apps.ActivateAsync(tile);
            if (problem != null) SetStatus(problem, 10);
        }
        catch (Exception e)
        {
            Log.Write($"activate {tile.Name}: {e}");
            SetStatus($"COULDN'T OPEN {tile.Name.ToUpperInvariant()}", 10);
        }
    }

    public void ConfirmClose(Tile tile)
    {
        var name = tile.Name.ToUpperInvariant();
        ShowModal(new ChoiceModal(this, $"CLOSE {name}?", new[] { "YES, CLOSE IT", "NO" }, choice =>
        {
            if (choice != 0) return;
            Apps.Close(tile);
            SetStatus($"CLOSING {name}…");
        }));
    }

    void ShowPowerMenu()
    {
        string[] options = { "SLEEP", "RESTART", "SHUT DOWN", "CANCEL" };
        ShowModal(new ChoiceModal(this, "POWER", options, choice =>
        {
            if (choice == 3) return;
            var verb = options[choice];
            ShowModal(new ChoiceModal(this, $"{verb} THE PC?", new[] { $"YES, {verb}", "NO" }, confirm =>
            {
                if (confirm != 0) return;
                Log.Write("power: " + verb);
                switch (choice)
                {
                    case 0: Power.Sleep(); break;
                    case 1: Power.Restart(); break;
                    case 2: Power.ShutDown(); break;
                }
            }));
        }));
    }

    void OnSwitchHotkey()
    {
        if (screen is SwitcherScreen open)
        {
            open.Advance();
            return;
        }
        Apps.Refresh();
        var running = Config.Tiles.Where(t => Apps.For(t).Running).ToList();
        if (running.Count == 0)
        {
            ShowHome();
            SetStatus("NOTHING IS OPEN YET");
            return;
        }

        // Read what's in front NOW — the tick-based value can be half a second stale.
        var inFront = Apps.ByWindow(Native.GetForegroundWindow())?.Tile.Id;
        int at = inFront == null ? -1 : running.FindIndex(t => t.Id == inFront);
        // From an app: the next one along. From the tiles: back to the last one used.
        int start = at >= 0 ? (at + 1) % running.Count : Math.Max(0, running.FindIndex(t => t.Id == lastTileId));
        ShowScreen(new SwitcherScreen(this, running, start, returnTo: inFront));
        BringToFront();
    }

    // ---------------------------------------------------------------- input

    void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var nav = Input.Map(key);
        // Open question going in: does Windows hand the Retroid's A button
        // (a media key) to the launcher, or only to whatever's playing?
        if (key is Key.MediaPlayPause or Key.Play) Log.Write("play/pause key reached the launcher");
        if (modal != null)
        {
            if (nav != Nav.None)
            {
                modal.OnNav(nav);
                e.Handled = true;
            }
            return;
        }
        if (screen.CapturesTyping)
        {
            if (key == Key.Escape)
            {
                screen.OnNav(Nav.Back);
                e.Handled = true;
            }
            return;
        }
        if (nav != Nav.None)
        {
            screen.OnNav(nav);
            e.Handled = true;
        }
    }

    void RegisterHotkeys()
    {
        var problems = new List<string>();
        Register(HotkeyHome, Config.HomeHotkey, "HOME");
        Register(HotkeySwitch, Config.SwitchHotkey, "SWITCH");
        HotkeyProblems = string.Join("   ", problems);
        if (problems.Count > 0)
        {
            Log.Write("hotkeys: " + HotkeyProblems);
            SetStatus(HotkeyProblems, 20);
        }

        void Register(int id, string combo, string label)
        {
            if (!Input.TryParseHotkey(combo, out var modifiers, out var vk))
                problems.Add($"{label} SHORTCUT \"{combo}\" ISN'T VALID");
            else if (!Native.RegisterHotKey(hwnd, id, modifiers, vk))
                problems.Add($"{label} SHORTCUT {combo.ToUpperInvariant()} IS TAKEN BY ANOTHER APP");
        }
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case HotkeyHome:
                ShowHome();
                break;
            case HotkeySwitch:
                OnSwitchHotkey();
                break;
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- upkeep

    /// <summary>
    /// Twice a second: sort windows into tiles, notice when the app in front
    /// has closed (or the desktop has surfaced) and come back to the tiles.
    /// </summary>
    void Tick()
    {
        Apps.Refresh();
        var foreground = Native.GetForegroundWindow();
        var previous = currentTileId;
        var owner = foreground == hwnd ? null : Apps.ByWindow(foreground);
        currentTileId = owner?.Tile.Id;
        if (owner != null) lastTileId = owner.Tile.Id;

        if (previous != null && Apps.For(previous) is { Running: false })
        {
            Log.Write($"{Apps.For(previous)!.Tile.Name} closed — back to the tiles");
            desktopTicks = 0;
            ShowHome();
        }
        else if (owner == null && foreground != hwnd && Native.IsDesktopish(foreground))
        {
            // Two ticks in a row, so a momentary gap while windows swap
            // focus doesn't count.
            if (++desktopTicks >= 2)
            {
                desktopTicks = 0;
                ShowHome();
            }
        }
        else
        {
            desktopTicks = 0;
        }

        screen.Tick();
        UpdateClock();
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        clock.Text = now.ToString("HH:mm");
        date.Text = now.ToString("ddd d MMM").ToUpperInvariant();
    }

    public async Task<string> RunUpdate(bool automatic)
    {
        var result = await Updater.UpdateAsync(progress => Dispatcher.InvokeAsync(() => SetStatus(progress, 0)));
        // Automatic checks stay quiet unless something needs saying.
        if (automatic && !result.StartsWith("UPDATE FAILED")) SetStatus("", 0);
        else SetStatus(result, 10);
        return result;
    }
}
