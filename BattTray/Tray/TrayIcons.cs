using System.Reflection;
using Microsoft.Win32;

namespace BattTray.Tray;

/// <summary>
/// Supplies the tray icon.
/// </summary>
/// <remarks>
/// The icon is deliberately fixed: it identifies the app and says nothing about charge.
/// Once several peripherals are tracked there is no single level an icon could honestly
/// show — a headset at 100% and a mouse at 20% would make it flip between readings that
/// each look authoritative. Levels belong in the tooltip and the menu, where they can be
/// attributed to a specific device.
///
/// The only thing that varies is black-vs-white, which is contrast against the taskbar
/// rather than state.
/// </remarks>
internal static class TrayIcons
{
    const string BlackIcon = "BattTray.Assets.batttray-black.ico";
    const string WhiteIcon = "BattTray.Assets.batttray-white.ico";

    /// <summary>Loads the variant that will be visible against the current taskbar.</summary>
    public static Icon Load(bool lightTheme)
    {
        // A light taskbar needs the dark glyph, and vice versa.
        string resource = lightTheme ? BlackIcon : WhiteIcon;

        using var stream = typeof(TrayIcons).GetTypeInfo().Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded icon '{resource}' is missing from the assembly.");

        // The .ico carries 16-256px frames; ask for the shell's small-icon size so the
        // right one is chosen at any DPI instead of downscaling the largest.
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    /// <summary>
    /// Reads the system (shell) theme rather than the app theme, since that is what the
    /// taskbar follows.
    /// </summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            // Assume the more common dark taskbar if the setting cannot be read.
            return false;
        }
    }
}
