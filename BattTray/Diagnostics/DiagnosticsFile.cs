using System.Diagnostics;
using System.Globalization;

namespace BattTray.Diagnostics;

/// <summary>
/// Puts a <see cref="DiagnosticsDump"/> on disk and shows the user where it went.
/// </summary>
/// <remarks>
/// A file rather than the console the harness writes to, because the app is a
/// <c>WinExe</c>: it has no stdout, so a flag run from a terminal would print nothing at all
/// and a dump that only exists in a console cannot be attached to an issue anyway. Explorer
/// with the file selected is the ending — the folder is where the user has to be to drag the
/// file into a browser, and it proves the dump happened, which nothing else in a window-less
/// app would.
/// </remarks>
internal static class DiagnosticsFile
{
    /// <summary>The caption on anything this shows the user.</summary>
    const string DialogTitle = "BattTray diagnostics";

    /// <summary>
    /// Where a dump goes when the caller did not name a destination: the temp directory, with
    /// the moment it was taken in the name.
    /// </summary>
    /// <remarks>
    /// Not beside the exe. That folder is writable for a download sitting in Downloads and is
    /// not for one that has been put in Program Files, so it fails exactly for the user who
    /// installed the app properly. The timestamp is in the name because a second dump is
    /// usually being compared against the first rather than replacing it.
    /// </remarks>
    public static string DefaultPath(DateTimeOffset taken) => Path.Combine(
        Path.GetTempPath(),
        $"BattTray-diagnostics-{taken.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");

    /// <summary>Writes the dump to <paramref name="path"/>, replacing anything already there.</summary>
    /// <remarks>
    /// No <c>AutoFlush</c>, unlike the harness's <c>--log</c>: this is one burst of a few
    /// thousand lines that nobody is watching arrive, rather than a file written across hours
    /// that has to survive whatever ends the run.
    /// </remarks>
    public static void Save(string path)
    {
        using var writer = new StreamWriter(path, append: false);
        DiagnosticsDump.WriteAll(writer.WriteLine);
    }

    /// <summary>
    /// Opens Explorer with the file selected. False if Explorer could not be started, which
    /// leaves the caller to say where the file went in words.
    /// </summary>
    /// <remarks>
    /// Selected in its folder rather than opened: the app cannot know what <c>.txt</c> is
    /// associated with on this machine, and the folder is where the user is going anyway.
    /// </remarks>
    public static bool Reveal(string path)
    {
        try
        {
            // The path is quoted because /select, takes the rest of the line as one argument
            // and the temp directory sits under a user name that may well contain a space.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""))?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Revealing the diagnostics file failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// The whole gesture behind "Save diagnostics…": collect the dump, write it somewhere the
    /// user can find it, and put them in front of it. Returns the path written, or null if the
    /// dump could not be produced — in which case the user has already been told why.
    /// </summary>
    /// <remarks>
    /// This reports through message boxes rather than return values because both of its callers
    /// are window-less: the tray has no surface to show a status on, and the command-line form
    /// has no stdout to fail on. It is also why nothing here is allowed to throw. A tray app
    /// that dies on the way to explaining itself is a worse bug than whatever was being
    /// diagnosed.
    ///
    /// The collection takes a second or two — most of it opening HID handles — with no
    /// progress shown, because there is no window to show it in. Explorer opening is the
    /// signal that it finished.
    /// </remarks>
    public static string? SaveAndReveal()
    {
        string path = DefaultPath(DateTimeOffset.Now);

        try
        {
            Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Warn($"BattTray could not write its diagnostics file.\n\n{path}\n\n{ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            // A sweep that fell over part-way. The partial file is still worth having — where
            // it stops is itself the finding — so the user is pointed at it rather than left
            // with nothing.
            Warn($"BattTray hit an error while collecting diagnostics.\n\n{ex.Message}\n\n"
                + $"Whatever was written before it stopped is still at:\n\n{path}");
            return null;
        }

        if (!Reveal(path))
            Inform($"Diagnostics saved to:\n\n{path}");

        return path;
    }

    static void Warn(string message) =>
        MessageBox.Show(message, DialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    static void Inform(string message) =>
        MessageBox.Show(message, DialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
}
