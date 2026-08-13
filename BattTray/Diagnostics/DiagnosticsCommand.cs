namespace BattTray.Diagnostics;

/// <summary>
/// The command-line form of "Save diagnostics…".
/// </summary>
/// <remarks>
/// Secondary to the menu item on purpose. The person this exists for downloaded one exe and
/// double-clicked it, and will find a menu row long before they find a flag — so the flag is
/// kept because it costs a few lines and suits anyone collecting dumps across several machines,
/// not because it is expected to be how this gets used.
///
/// It is handled before <see cref="SingleInstance.TryAcquire"/>, which is the whole reason it
/// is a static class taking <c>args</c> rather than something the tray offers. None of the dump
/// needs the app: the providers are constructed on the spot and the probes talk to Windows. So
/// a diagnostics run must not be refused because a copy is already running — the moment you
/// most want a dump is while the app is up and showing something wrong — and equally must not
/// raise a second tray icon on the way to producing one.
/// </remarks>
internal static class DiagnosticsCommand
{
    /// <summary>Asks for a dump. Optionally followed by where to put it.</summary>
    public const string Switch = "--diagnostics";

    /// <summary>
    /// True when <paramref name="args"/> asks for a dump, with <paramref name="path"/> set to
    /// the destination the caller named, or null for "choose one and show me where it went".
    /// </summary>
    public static bool WasRequested(string[] args, out string? path)
    {
        path = null;

        int index = Array.FindIndex(args, argument =>
            string.Equals(argument, Switch, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return false;

        // The next argument is the destination unless it is another switch, so that
        // `--diagnostics --autostart` is not read as a request to write to a file called
        // "--autostart". A path that genuinely starts with a dash can be given as ".\-odd.txt".
        if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
            path = args[index + 1];

        return true;
    }

    /// <summary>Produces the dump and returns the exit code the process should end with.</summary>
    /// <remarks>
    /// A named path is someone scripting this, and a script wants the file where it asked for
    /// it and no window in its face: the Explorer reveal answers "where did it go", which only
    /// the caller who did not choose has to ask. The exit code is the answer for both, since a
    /// <c>WinExe</c> has no stdout to report on.
    /// </remarks>
    public static int Run(string? path)
    {
        if (path is null)
            return DiagnosticsFile.SaveAndReveal() is null ? 1 : 0;

        try
        {
            DiagnosticsFile.Save(path);
            return 0;
        }
        catch (Exception ex)
        {
            // Deliberately every exception, and deliberately silent. There is nowhere to print
            // it, and a dialog would be the one thing an unattended script cannot answer.
            System.Diagnostics.Debug.WriteLine($"Diagnostics dump to '{path}' failed: {ex}");
            return 1;
        }
    }
}
