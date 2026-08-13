using System.Reflection;

namespace BattTray;

/// <summary>
/// Which build this is, in the two forms the app has a use for: one that identifies the binary
/// exactly, and one short enough to read off a menu row.
/// </summary>
/// <remarks>
/// One reader behind both, because the two places the app states a version — the diagnostics
/// header and the tray's version row — are read against each other. Someone reporting a bug
/// quotes the row and attaches the dump, and whoever picks the issue up has to be able to see
/// that the two came from the same binary. Two lookups would have made that a coincidence.
/// </remarks>
internal static class AppVersion
{
    /// <summary>
    /// The informational version, which carries the source revision the SDK appends after a '+'.
    /// </summary>
    /// <remarks>
    /// That suffix is what separates two builds carrying the same version number, which is the
    /// normal case for anything built between releases. Falling back to the assembly version
    /// rather than to nothing, because a number without a revision still identifies a release
    /// binary, and identifying it is the whole job.
    /// </remarks>
    public static string Full { get; } = Read();

    /// <summary>
    /// The release number alone, e.g. <c>0.1.0</c>: <see cref="Full"/> without the build
    /// metadata.
    /// </summary>
    /// <remarks>
    /// The revision is dropped rather than shortened. It is a forty-character hash, and the
    /// row it goes in sits in a menu whose width is set by device names — one build stamp would
    /// widen every row in the app. Nothing is lost by dropping it here: the version a user says
    /// out loud is the release number, and the exact build is in the dump beside it, which is
    /// where a reader who needs the revision is going anyway.
    /// </remarks>
    /// <remarks>
    /// Derived on each read rather than cached beside <see cref="Full"/>, which would have made
    /// this correct only for as long as the two stayed in this order: a cached initialiser runs
    /// in declaration order, so moving it above <see cref="Full"/> would split a null and fail
    /// the type's initialiser rather than the edit. A string split against a menu that is being
    /// rebuilt row by row costs nothing worth protecting.
    /// </remarks>
    public static string Display => Full.Split('+')[0];

    static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
