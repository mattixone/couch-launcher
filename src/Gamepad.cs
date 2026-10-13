using System.Runtime.InteropServices;
using System.Windows.Threading;
using CouchLauncher.Ui;

namespace CouchLauncher;

/// <summary>
/// Xbox-style controllers (anything Windows sees through XInput). While the
/// launcher is in front, the d-pad or left stick moves, A selects and B goes
/// back — the same as the Retroid's keys. While another app is in front every
/// button is left to that app, except one held combo (Back+Start by default)
/// that brings you back to the tiles.
///
/// Polled rather than event-driven: XInput has no events, and polling is what
/// lets the combo work while the launcher is in the background.
/// </summary>
sealed class Gamepad
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint user, out XInputState state);

    /// <summary>XInput's button bits, plus the left stick folded in as four
    /// extra "buttons" above them so it can be treated like a d-pad.</summary>
    [Flags]
    public enum Button : uint
    {
        DpadUp = 0x1, DpadDown = 0x2, DpadLeft = 0x4, DpadRight = 0x8,
        Start = 0x10, Back = 0x20, LeftThumb = 0x40, RightThumb = 0x80,
        LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
        StickUp = 0x10000, StickDown = 0x20000, StickLeft = 0x40000, StickRight = 0x80000,
    }

    const int Slots = 4;
    /// <summary>Half way over: a deliberate push, not a resting stick drifting.</summary>
    const short StickThreshold = 16000;
    static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    static readonly TimeSpan RepeatRate = TimeSpan.FromMilliseconds(120);
    /// <summary>Asking XInput about an empty slot is slow, so empty slots are
    /// only re-checked this often.</summary>
    static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(2);

    static readonly (Button Buttons, Nav Nav)[] Directions =
    {
        (Button.DpadUp | Button.StickUp, Nav.Up),
        (Button.DpadDown | Button.StickDown, Nav.Down),
        (Button.DpadLeft | Button.StickLeft, Nav.Left),
        (Button.DpadRight | Button.StickRight, Nav.Right),
    };

    readonly DispatcherTimer timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly bool[] connected = new bool[Slots];
    readonly DateTime[] nextProbe = new DateTime[Slots];
    readonly Func<bool> inFront;
    readonly Action<Nav> onNav;
    readonly Action onHome;

    Button previous;
    Button homeCombo = Button.Back | Button.Start;
    TimeSpan homeHold = TimeSpan.FromMilliseconds(800);
    DateTime? comboSince;
    bool comboFired;
    Nav repeating;
    DateTime repeatAt;
    bool unavailable;

    /// <param name="inFront">Whether the launcher is the window in front, so
    /// buttons should drive it.</param>
    public Gamepad(Func<bool> inFront, Action<Nav> onNav, Action onHome)
    {
        this.inFront = inFront;
        this.onNav = onNav;
        this.onHome = onHome;
        timer.Tick += (_, _) => Poll();
    }

    public int Connected => connected.Count(c => c);
    /// <summary>X pressed, whatever's in front.</summary>
    public event Action? XPressed;

    /// <summary>Turns polling on or off and sets the home combo. Returns a
    /// problem to show, or null.</summary>
    public string? Configure(bool enabled, string combo, int holdMs)
    {
        string? problem = null;
        if (TryParse(combo, out var parsed)) homeCombo = parsed;
        else problem = $"CONTROLLER HOME COMBO \"{combo}\" ISN'T VALID";
        homeHold = TimeSpan.FromMilliseconds(Math.Clamp(holdMs, 0, 5000));

        if (enabled && !unavailable) timer.Start();
        else Stop();
        return problem;
    }

    public void Stop()
    {
        timer.Stop();
        Array.Clear(connected);
        previous = 0;
        comboSince = null;
        repeating = Nav.None;
    }

    /// <summary>"Back+Start" → the buttons. View and Menu are the same two
    /// buttons under their newer Xbox names.</summary>
    public static bool TryParse(string combo, out Button buttons)
    {
        buttons = 0;
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part.ToLowerInvariant() switch
            {
                "view" or "select" => "Back",
                "menu" => "Start",
                _ => part,
            };
            if (!Enum.TryParse<Button>(name, ignoreCase: true, out var button) || char.IsDigit(name[0])) return false;
            buttons |= button;
        }
        return buttons != 0;
    }

    void Poll()
    {
        var now = DateTime.UtcNow;
        Button held;
        try
        {
            held = ReadAll(now);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Write("controller support unavailable: " + e.Message);
            unavailable = true;
            Stop();
            return;
        }
        var pressed = held & ~previous;
        previous = held;

        // The home combo works whatever is in front.
        if ((held & homeCombo) == homeCombo)
        {
            comboSince ??= now;
            if (!comboFired && now - comboSince >= homeHold)
            {
                comboFired = true;
                Log.Write("controller: home combo");
                onHome();
            }
        }
        else
        {
            comboSince = null;
            comboFired = false;
        }

        var direction = Nav.None;
        foreach (var (buttons, nav) in Directions)
        {
            if ((held & buttons) != 0)
            {
                direction = nav;
                break;
            }
        }

        // X goes to whatever's in front (Live Surfer's TV guide), not just the launcher.
        if ((pressed & Button.X) != 0) XPressed?.Invoke();

        if (!inFront())
        {
            // A direction already held when the launcher comes back to the
            // front belongs to the app you were in; don't let it start repeating.
            repeating = direction;
            repeatAt = DateTime.MaxValue;
            return;
        }

        if (direction == Nav.None)
        {
            repeating = Nav.None;
        }
        else if (direction != repeating)
        {
            repeating = direction;
            repeatAt = now + RepeatDelay;
            onNav(direction);
        }
        else if (now >= repeatAt)
        {
            repeatAt = now + RepeatRate;
            onNav(direction);
        }

        if ((pressed & Button.A) != 0) onNav(Nav.Select);
        if ((pressed & Button.B) != 0) onNav(Nav.Back);
    }

    /// <summary>Every connected controller's buttons together — any of them can drive.</summary>
    Button ReadAll(DateTime now)
    {
        Button held = 0;
        for (uint slot = 0; slot < Slots; slot++)
        {
            if (!connected[slot] && now < nextProbe[slot]) continue;
            if (XInputGetState(slot, out var state) == 0)
            {
                if (!connected[slot]) Log.Write($"controller {slot + 1} connected");
                connected[slot] = true;
                held |= Read(state.Gamepad);
            }
            else
            {
                if (connected[slot]) Log.Write($"controller {slot + 1} disconnected");
                connected[slot] = false;
                nextProbe[slot] = now + ProbeEvery;
            }
        }
        return held;
    }

    static Button Read(XInputGamepad pad)
    {
        var held = (Button)pad.Buttons;
        // Only the stronger axis counts, so a diagonal push moves one way.
        if (Math.Abs((int)pad.ThumbLY) >= Math.Abs((int)pad.ThumbLX))
        {
            if (pad.ThumbLY > StickThreshold) held |= Button.StickUp;
            else if (pad.ThumbLY < -StickThreshold) held |= Button.StickDown;
        }
        else
        {
            if (pad.ThumbLX > StickThreshold) held |= Button.StickRight;
            else if (pad.ThumbLX < -StickThreshold) held |= Button.StickLeft;
        }
        return held;
    }
}
