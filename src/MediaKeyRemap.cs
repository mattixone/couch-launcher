using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace CouchLauncher;

/// <summary>
/// Turns the Retroid's A button — the Play/Pause media key — into Enter, but
/// only while an app that asked for it is in front.
///
/// A can't just be Enter everywhere: on the desktop it's play/pause. But
/// TV-style interfaces like YouTube TV ignore Play/Pause for their menus and
/// only respond to Enter, so there it has to change. (Enter also toggles the
/// video when nothing is highlighted, so nothing is lost.)
///
/// A keyboard hook rather than RegisterHotKey: Chromium browsers already
/// claim the media keys with RegisterHotKey, and only one program can, so
/// ours would silently lose. A hook sees the key first.
/// </summary>
static class MediaKeyRemap
{
    // Held in a field: Windows keeps only a raw pointer to the callback, so a
    // local would be garbage-collected and the next keypress would crash.
    static Native.LowLevelKeyboardProc? callback;
    static IntPtr hook;
    static Func<bool> when = () => false;
    static bool logged;

    public static void Install(Func<bool> shouldRemap)
    {
        if (hook != IntPtr.Zero) return;
        when = shouldRemap;
        callback = Callback;
        hook = Native.InstallKeyboardHook(callback);
        if (hook == IntPtr.Zero) Log.Write("couldn't install the A-button hook, error " + Marshal.GetLastWin32Error());
    }

    public static void Remove()
    {
        if (hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var key = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                // Not one we sent ourselves, and only the media key.
                if (key.vkCode == Native.VK_MEDIA_PLAY_PAUSE && (key.flags & Native.LLKHF_INJECTED) == 0 && when())
                {
                    int message = wParam.ToInt32();
                    if (message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
                    {
                        // Sent after this callback returns: Windows gives hooks
                        // well under a second, and sending a key from inside one is asking for trouble.
                        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Native.TapKey(Native.VK_RETURN)));
                        if (!logged)
                        {
                            logged = true;
                            Log.Write("A button pressed in a select-mode app: sent Enter instead of Play/Pause");
                        }
                    }
                    return (IntPtr)1;       // swallow the press and its release
                }
            }
            catch (Exception e)
            {
                Log.Write("A-button hook: " + e.Message);
            }
        }
        return Native.CallNextHookEx(hook, code, wParam, lParam);
    }
}
