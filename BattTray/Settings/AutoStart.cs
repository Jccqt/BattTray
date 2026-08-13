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

    /// <summary>
    /// Argument the Run entry passes, marking a launch as Windows starting the app rather
    /// than the user double-clicking the exe. Only the latter opens the settings dialog.
    /// </summary>
    public const string StartupSwitch = "--autostart";

    /// <summary>True when the Run entry exists and still points at this executable.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string command || ExecutablePath() is not { } path)
                return false;

            // A stale entry left by a build in another folder is not this app starting up,
            // so report it as off; enabling then rewrites it to the current path. Arguments
            // are ignored here, so an entry written before the switch existed still reads as
            // on rather than presenting the user with a toggle that contradicts their login.
            return string.Equals(ExecutableFrom(command), path, StringComparison.OrdinalIgnoreCase);
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

            key.SetValue(ValueName, CommandFor(path), RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"Autostart write failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Rewrites an existing entry that launches this exe with something other than the
    /// command this build writes.
    /// </summary>
    /// <remarks>
    /// The case that matters is an entry written before <see cref="StartupSwitch"/> existed:
    /// left alone it would look like a manual launch at every login and open the settings
    /// dialog each time. Upgrading it costs the user one unexpected dialog, at the first
    /// login after updating, instead of one at every login afterwards. Entries pointing
    /// elsewhere are left alone — they belong to another copy of the app, and rewriting
    /// them would quietly hijack whichever build the user actually chose to start.
    /// </remarks>
    public static void UpgradeCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string command || ExecutablePath() is not { } path)
                return;

            if (!string.Equals(ExecutableFrom(command), path, StringComparison.OrdinalIgnoreCase)
                || command == CommandFor(path))
                return;

            key.SetValue(ValueName, CommandFor(path), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Nothing to tell the user: the app has started either way, and the only
            // symptom is a dialog they can close.
            Debug.WriteLine($"Autostart upgrade failed: {ex}");
        }
    }

    /// <summary>
    /// The command line the Run key stores. The path is quoted because it routinely
    /// contains spaces, and the shell would otherwise launch the first word and pass the
    /// rest as arguments.
    /// </summary>
    static string CommandFor(string path) => $"\"{path}\" {StartupSwitch}";

    /// <summary>The executable a stored command launches, without quotes or arguments.</summary>
    /// <remarks>
    /// Internal rather than private so the tests can reach it. It is the one piece of this
    /// class that is pure — everything around it is the registry — and it decides whether the
    /// checkbox agrees with what happens at login, so it is worth pinning down without a
    /// machine's Run key in the loop.
    /// </remarks>
    internal static string ExecutableFrom(string command)
    {
        string trimmed = command.Trim();
        if (!trimmed.StartsWith('"'))
        {
            // Entries this app writes are always quoted, so an unquoted one was left by
            // hand; read it the way the shell reads its simplest case, up to the first space.
            int space = trimmed.IndexOf(' ');
            return space < 0 ? trimmed : trimmed[..space];
        }

        int closing = trimmed.IndexOf('"', startIndex: 1);
        return closing < 0 ? trimmed[1..] : trimmed[1..closing];
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
