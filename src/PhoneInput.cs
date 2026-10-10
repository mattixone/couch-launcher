using System.Runtime.InteropServices;
using System.Text.Json;

namespace CouchLauncher;

/// <summary>
/// Turns the phone remote's commands into keyboard and mouse input on this
/// PC, for when the Pico isn't running the web remote. Same messages the Pico
/// takes (see CouchControllerWeb's README), so the page doesn't care which
/// one it's talking to.
///
/// Windows ignores input from programs on the lock screen and on admin
/// prompts, which is why UNLOCK still needs the Pico (a real USB keyboard).
/// </summary>
static class PhoneInput
{
    /// <summary>Whether Play/Pause should be Enter right now (TV-style apps),
    /// as for the Retroid's A button. Set by the main window.</summary>
    public static Func<bool> ASelects { get; set; } = () => false;
    public static bool AllowPower { get; set; } = true;

    static readonly object Gate = new();
    static uint heldButtons;

    /// <summary>Runs one message. Returns an error to send back, or null.</summary>
    public static string? Handle(JsonElement msg)
    {
        string kind = Str(msg, "t");
        switch (kind)
        {
            case "move":
                Mouse(MOUSEEVENTF_MOVE, Clamp(Int(msg, "x"), 400), Clamp(Int(msg, "y"), 400));
                return null;
            case "scroll":
                Mouse(MOUSEEVENTF_WHEEL, 0, 0, Clamp(Int(msg, "v"), 20) * 120);
                return null;
            case "click":
            {
                var (down, up) = Button(Str(msg, "b"));
                Mouse(down, 0, 0);
                Mouse(up, 0, 0);
                return null;
            }
            case "press":
            {
                var (down, up) = Button(Str(msg, "b"));
                lock (Gate) heldButtons |= up;
                Mouse(down, 0, 0);
                return null;
            }
            case "release":
            {
                var name = Str(msg, "b");
                if (name.Length == 0) ReleaseAll();
                else
                {
                    var (_, up) = Button(name);
                    lock (Gate) heldButtons &= ~up;
                    Mouse(up, 0, 0);
                }
                return null;
            }
            case "key":
                return Keys(msg);
            case "media":
                return Media(Str(msg, "k"));
            case "text":
                Type(Str(msg, "s"));
                return null;
            case "unlock":
                return "UNLOCK NEEDS THE PICO";
            case "power":
                return PowerAction(Str(msg, "a"));
            default:
                return null; // ping, or something newer than this launcher
        }
    }

    /// <summary>Let go of any mouse button a phone was holding when it vanished.</summary>
    public static void ReleaseAll()
    {
        uint held;
        lock (Gate)
        {
            held = heldButtons;
            heldButtons = 0;
        }
        foreach (uint up in new[] { MOUSEEVENTF_LEFTUP, MOUSEEVENTF_RIGHTUP, MOUSEEVENTF_MIDDLEUP })
            if ((held & up) != 0) Mouse(up, 0, 0);
    }

    static string? Keys(JsonElement msg)
    {
        if (!msg.TryGetProperty("k", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var vks = new List<ushort>();
        foreach (var item in list.EnumerateArray().Take(6))
        {
            if (item.ValueKind != JsonValueKind.String || !KeyCodes.TryGetValue(item.GetString()!, out ushort vk))
                return "UNKNOWN KEY";
            vks.Add(vk);
        }
        if (vks.Count == 0) return null;
        // Pressed in order, let go in reverse: Ctrl down, Alt down, H down, H up, Alt up, Ctrl up.
        var inputs = vks.Select(vk => KeyInput(vk, false))
            .Concat(Enumerable.Reverse(vks).Select(vk => KeyInput(vk, true)))
            .ToArray();
        Send(inputs);
        return null;
    }

    static string? Media(string name)
    {
        if (!MediaCodes.TryGetValue(name, out ushort vk)) return "UNKNOWN MEDIA KEY";
        // Same as the Retroid's A button: TV-style apps only answer to Enter.
        if (vk == VK_MEDIA_PLAY_PAUSE && ASelects()) vk = VK_RETURN;
        Send(KeyInput(vk, false), KeyInput(vk, true));
        return null;
    }

    static void Type(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text.Length > 300 ? text[..300] : text)
        {
            if (c == '\n' || c == '\r')
            {
                inputs.Add(KeyInput(VK_RETURN, false));
                inputs.Add(KeyInput(VK_RETURN, true));
                continue;
            }
            // Unicode packets type the character itself, whatever the keyboard
            // layout, accents and emoji included.
            inputs.Add(UnicodeInput(c, false));
            inputs.Add(UnicodeInput(c, true));
        }
        if (inputs.Count > 0) Send(inputs.ToArray());
    }

    static string? PowerAction(string action)
    {
        if (!AllowPower) return "POWER IS SWITCHED OFF";
        Log.Write("phone remote: power " + action);
        switch (action)
        {
            case "lock": LockWorkStation(); break;
            case "sleep": Power.Sleep(); break;
            case "restart": Power.Restart(); break;
            case "shutdown": Power.ShutDown(); break;
        }
        return null;
    }

    static (uint down, uint up) Button(string name) => name switch
    {
        "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    static string Str(JsonElement msg, string name) =>
        msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    static int Int(JsonElement msg, string name) =>
        msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    static int Clamp(int value, int limit) => Math.Clamp(value, -limit, limit);

    // ------------------------------------------------------------- key names
    // adafruit_hid Keycode names, as the page sends them, to Windows virtual keys.

    static readonly Dictionary<string, ushort> KeyCodes = BuildKeyCodes();

    static Dictionary<string, ushort> BuildKeyCodes()
    {
        var map = new Dictionary<string, ushort>
        {
            ["ENTER"] = 0x0D, ["RETURN"] = 0x0D, ["ESCAPE"] = 0x1B, ["BACKSPACE"] = 0x08, ["TAB"] = 0x09,
            ["SPACEBAR"] = 0x20, ["SPACE"] = 0x20, ["MINUS"] = 0xBD, ["EQUALS"] = 0xBB,
            ["LEFT_BRACKET"] = 0xDB, ["RIGHT_BRACKET"] = 0xDD, ["BACKSLASH"] = 0xDC, ["SEMICOLON"] = 0xBA,
            ["QUOTE"] = 0xDE, ["GRAVE_ACCENT"] = 0xC0, ["COMMA"] = 0xBC, ["PERIOD"] = 0xBE, ["FORWARD_SLASH"] = 0xBF,
            ["CAPS_LOCK"] = 0x14, ["PRINT_SCREEN"] = 0x2C, ["SCROLL_LOCK"] = 0x91, ["PAUSE"] = 0x13,
            ["INSERT"] = 0x2D, ["HOME"] = 0x24, ["PAGE_UP"] = 0x21, ["DELETE"] = 0x2E, ["END"] = 0x23, ["PAGE_DOWN"] = 0x22,
            ["RIGHT_ARROW"] = 0x27, ["LEFT_ARROW"] = 0x25, ["DOWN_ARROW"] = 0x28, ["UP_ARROW"] = 0x26,
            ["APPLICATION"] = 0x5D,
            ["CONTROL"] = 0xA2, ["LEFT_CONTROL"] = 0xA2, ["RIGHT_CONTROL"] = 0xA3,
            ["SHIFT"] = 0xA0, ["LEFT_SHIFT"] = 0xA0, ["RIGHT_SHIFT"] = 0xA1,
            ["ALT"] = 0xA4, ["LEFT_ALT"] = 0xA4, ["OPTION"] = 0xA4, ["RIGHT_ALT"] = 0xA5,
            ["GUI"] = 0x5B, ["LEFT_GUI"] = 0x5B, ["WINDOWS"] = 0x5B, ["COMMAND"] = 0x5B, ["RIGHT_GUI"] = 0x5C,
            ["ZERO"] = 0x30, ["ONE"] = 0x31, ["TWO"] = 0x32, ["THREE"] = 0x33, ["FOUR"] = 0x34,
            ["FIVE"] = 0x35, ["SIX"] = 0x36, ["SEVEN"] = 0x37, ["EIGHT"] = 0x38, ["NINE"] = 0x39,
        };
        for (char c = 'A'; c <= 'Z'; c++) map[c.ToString()] = c;
        for (int f = 1; f <= 24; f++) map["F" + f] = (ushort)(0x70 + f - 1);
        return map;
    }

    const ushort VK_RETURN = 0x0D, VK_MEDIA_PLAY_PAUSE = 0xB3;

    static readonly Dictionary<string, ushort> MediaCodes = new()
    {
        ["PLAY_PAUSE"] = VK_MEDIA_PLAY_PAUSE, ["SCAN_NEXT_TRACK"] = 0xB0, ["SCAN_PREVIOUS_TRACK"] = 0xB1,
        ["STOP"] = 0xB2, ["MUTE"] = 0xAD, ["VOLUME_DECREMENT"] = 0xAE, ["VOLUME_INCREMENT"] = 0xAF,
    };

    // Keys that live on the "extended" part of a keyboard; Windows wants a
    // flag on these or some apps read them as the number pad.
    static readonly HashSet<ushort> Extended = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x2C, 0x5B, 0x5C, 0x5D, 0xA3, 0xA5,
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,
    };

    // ----------------------------------------------------------------- Win32

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
               MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10,
               MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800;
    const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")] static extern bool LockWorkStation();

    static INPUT KeyInput(ushort vk, bool up)
    {
        uint flags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        // The scan code too, for apps (games, some browsers) that read that instead.
        var scan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        return new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
    }

    static INPUT UnicodeInput(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    static void Mouse(uint flags, int dx, int dy, int data = 0)
    {
        if (flags == MOUSEEVENTF_MOVE && dx == 0 && dy == 0) return;
        Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags } } });
    }

    static void Send(params INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == 0)
        {
            // Usually the lock screen or an admin prompt being in front. Once a
            // minute is plenty; a finger on the touchpad would fill the log.
            int error = Marshal.GetLastWin32Error();
            if (DateTime.UtcNow - lastBlockedLog > TimeSpan.FromMinutes(1))
            {
                lastBlockedLog = DateTime.UtcNow;
                Log.Write("phone remote: input blocked, error " + error);
            }
        }
    }

    static DateTime lastBlockedLog;
}
