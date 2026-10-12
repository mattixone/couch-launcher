using System.Runtime.InteropServices;

namespace CouchLauncher;

/// <summary>
/// The PC's main volume, through Windows' Core Audio — the same level the
/// phone remote's and the Retroid's volume keys change, so they all agree.
/// Live Surfer uses it for ▲▼ and draws its own volume bar from the level.
/// If Core Audio can't be reached, it presses the volume keys instead.
/// </summary>
static class SystemVolume
{
    /// <summary>Raises or lowers the volume one step (5%). Returns the new level (0–100) and mute, or null if unknown.</summary>
    public static (int Percent, bool Muted)? Step(int direction)
    {
        try
        {
            var endpoint = Endpoint();
            try
            {
                Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out float level));
                Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
                var guid = Guid.Empty;
                if (muted && direction > 0)
                {
                    // Turning it up after a mute starts from where it was.
                    Marshal.ThrowExceptionForHR(endpoint.SetMute(false, ref guid));
                    muted = false;
                }
                else
                {
                    level = Math.Clamp(MathF.Round((level + direction * 0.05f) * 20) / 20, 0, 1);
                    Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(level, ref guid));
                }
                return ((int)Math.Round(level * 100), muted);
            }
            finally
            {
                Marshal.ReleaseComObject(endpoint);
            }
        }
        catch (Exception e)
        {
            Log.Write("volume: Core Audio failed, pressing the volume key instead: " + e.Message);
            Native.TapKey(direction > 0 ? VK_VOLUME_UP : VK_VOLUME_DOWN);
            return null;
        }
    }

    const byte VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

    static IAudioEndpointVolume Endpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device));
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var endpoint));
                return (IAudioEndpointVolume)endpoint;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    // Only the methods used are named; the ones before them hold their places
    // in the interface's order, which is what COM goes by.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
