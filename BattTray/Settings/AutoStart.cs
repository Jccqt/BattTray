using System.Diagnostics;
using Microsoft.Win32;

namespace BattTray.Settings;

/// <summary>
/// The "start with Windows" toggle, backed by the per-user Run key.
/// </summary>
/// <remarks>
/// HKCU rather than HKLM, and the Run key rather than a scheduled task or a service: all
/// three alternatives want elevation, and a battery indicator is not worth a UAC prompt.
/// The registry is treated as the single source of truth and re-read whenever the value
/// is shown, so removing the entry by hand or with another startup manager is reflected
/// rather than fought.
/// </remarks>
internal static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "BattTray";

    /// <summary>True when the Run entry exists and still points at this executable.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string command || ExecutablePath() is not { } path)
                return false;

            // A stale entry left by a build in another folder is not this app starting up,
            // so report it as off; enabling then rewrites it to the current path.
            return string.Equals(command.Trim('"'), path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Autostart read failed: {ex}");
            return false;
        }
    }

    /// <summary>Writes or removes the Run entry. Returns false if the registry refused.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
                return false;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            if (ExecutablePath() is not { } path)
                return false;

            // Quoted because the path routinely contains spaces, and the shell would
            // otherwise launch the first word and pass the rest as arguments.
            key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Autostart write failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// The exe the shell should launch. Under <c>dotnet run</c> this is the dotnet host,
    /// which would not start the app on its own, so autostart is refused there rather than
    /// silently writing an entry that does nothing at the next login.
    /// </summary>
    static string? ExecutablePath()
    {
        string? path = Environment.ProcessPath;
        return path is not null && Path.GetFileName(path).Equals("BattTray.exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }
}
