using System.Runtime.InteropServices;

namespace BattTray.Interop;

/// <summary>
/// Asking Windows to bring one of this app's windows to the front.
/// </summary>
/// <remarks>
/// Needed because the tray menu is shown without activation, which leaves the app running
/// behind whatever the user last used. A window opened from the menu inherits that: the
/// framework can put it on screen but cannot pull it in front, so it surfaces underneath
/// the window in focus and reads as never having opened at all.
/// </remarks>
internal static class ForegroundWindow
{
    /// <summary>
    /// Brings a window to the front. Windows refuses this to processes that have had no
    /// recent hand in what the user is doing, so it is only worth calling on the back of a
    /// click the user just made — and a refusal is silent, hence no return value to check.
    /// </summary>
    public static void Claim(nint window)
    {
        if (window != 0)
            SetForegroundWindow(window);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(nint hWnd);
}
