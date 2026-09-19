using System.Windows.Input;

namespace CouchLauncher.Ui;

enum Nav { None, Up, Down, Left, Right, Select, Back, Close }

static class Input
{
    /// <summary>
    /// What the Retroid actually sends, in Handheld mode: the d-pad is arrow
    /// keys, A is the Play/Pause media key and B is Escape. Enter and Space
    /// also select, for a real keyboard.
    /// </summary>
    public static Nav Map(Key key) => key switch
    {
        Key.Up => Nav.Up,
        Key.Down => Nav.Down,
        Key.Left => Nav.Left,
        Key.Right => Nav.Right,
        Key.Enter or Key.Space or Key.MediaPlayPause or Key.Play => Nav.Select,
        Key.Escape or Key.Back or Key.BrowserBack => Nav.Back,
        Key.Delete => Nav.Close,
        _ => Nav.None,
    };

    /// <summary>Parses "Ctrl+Alt+Shift+H" into RegisterHotKey's modifiers and key.</summary>
    public static bool TryParseHotkey(string combo, out uint modifiers, out uint vk)
    {
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
        modifiers = MOD_NOREPEAT;
        vk = 0;
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default:
                    // "5" would otherwise parse as the enum's numeric value 5.
                    var name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
                    if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    static Native.POINT lastCursor;

    /// <summary>
    /// Whether the mouse has physically moved since the last call. WPF fires
    /// mouse-enter when a screen is rebuilt under a still pointer, which
    /// would yank focus away from where the d-pad left it.
    /// </summary>
    public static bool CursorMoved()
    {
        Native.GetCursorPos(out var now);
        bool moved = now.X != lastCursor.X || now.Y != lastCursor.Y;
        lastCursor = now;
        return moved;
    }
}
