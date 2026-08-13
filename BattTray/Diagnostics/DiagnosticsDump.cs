using System.Globalization;
using System.Runtime.InteropServices;
using BattTray.Devices;

namespace BattTray.Diagnostics;

/// <summary>
/// Everything the app can find out about the peripherals attached to this machine, in the
/// order someone reading a bug report wants it: what BattTray's own providers read, then what
/// Windows publishes as device properties, then what the HID report descriptors declare.
/// </summary>
/// <remarks>
/// The reason this lives in the shipped exe rather than only in the harness is that the people
/// who own the hardware this project cannot buy are, definitionally, the people who downloaded
/// a single exe. Asking them to install the .NET SDK and clone a repo to answer a question
/// about their mouse is asking them not to answer it.
///
/// Nothing here needs the tray, a message loop, or a running instance: the providers are
/// constructed on the spot and the probes talk to Windows rather than to the app. That is what
/// lets a dump be produced while another copy is already running.
/// </remarks>
internal static class DiagnosticsDump
{
    /// <summary>Where the reader is asked to send the file.</summary>
    const string IssuesUrl = "https://github.com/Jccqt/BattTray/issues";

    /// <summary>
    /// The build that produced the dump: the exact one, revision and all — see
    /// <see cref="BattTray.AppVersion.Full"/>. The tray's version row quotes the same reader in
    /// its shorter form, so a dump and the row the user read cannot name different builds.
    /// </summary>
    public static string Version => BattTray.AppVersion.Full;

    /// <summary>Writes the whole dump, header first, to <paramref name="write"/>.</summary>
    public static void WriteAll(Action<string> write)
    {
        WriteHeader(write, DateTimeOffset.Now);
        WriteWhatThisIsFor(write);

        // Its own monitor rather than the tray's, so this path is identical whether it was
        // reached from the menu, from the flag with the app already running, or from the flag
        // with nothing running at all. A fresh monitor also has DeviceChangesAreWatched false,
        // which makes it re-enumerate rather than describe a device list it cached earlier.
        var monitor = new PeripheralMonitor(new BluetoothPeripheralProvider(), new XInputGamepadProvider());
        WriteProviderEvidence(write, monitor);

        write(string.Empty);

        // Not the --all form the harness offers: that is 12,701 properties on this machine and
        // the tiers are what answer the question. Someone who needs the rest has the harness.
        Probe.Run(write, dumpEveryNode: false);

        write(string.Empty);
        HidProbe.Run(write);
    }

    /// <summary>
    /// Says which binary produced the dump, and when.
    /// </summary>
    /// <remarks>
    /// A pasted dump with no build number is a bug report about an unknown binary — the reader
    /// cannot tell a fixed bug from a live one, and every line below it is evidence about code
    /// nobody can identify. It is the one part of the file that is worth nothing on its own and
    /// makes the rest worth something.
    ///
    /// No machine name. The dump is meant to be attached to a public issue, and which machine
    /// it came from answers no question here: the subject is the hardware attached to it.
    /// </remarks>
    public static void WriteHeader(Action<string> write, DateTimeOffset taken)
    {
        write("=== BattTray diagnostics");
        write($"  version : {Version}");
        write($"  os      : {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture})");
        write($"  taken   : {taken.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        write(string.Empty);
    }

    /// <summary>
    /// What the person who clicked "Save diagnostics…" is supposed to do with the file.
    /// </summary>
    /// <remarks>
    /// Attach rather than paste, because the two sweeps below run to well over a megabyte on an
    /// ordinary machine and a GitHub issue body holds 64 KB. Saying so here is cheaper than
    /// letting someone find out by pasting.
    ///
    /// Not part of <see cref="WriteHeader"/>, which the harness shares: its output goes to a
    /// console, where "attach this file" is advice about a file that does not exist.
    /// </remarks>
    static void WriteWhatThisIsFor(Action<string> write)
    {
        write($"  Attach this file to an issue at {IssuesUrl}.");
        write("  It is too large to paste. Say which device you expected to see and what its");
        write("  own app shows for it; read the file first if you like — it is device names,");
        write("  hardware ids and property bytes, and nothing else.");
        write(string.Empty);
    }

    /// <summary>
    /// The raw evidence behind every reading the app would show: the property key, the bytes as
    /// reported, and what this app decoded them into.
    /// </summary>
    /// <remarks>
    /// The pair is the point. A percentage that disagrees with the vendor app is either a
    /// decoding bug (bytes fine, value wrong) or the device lying (both agree), and only the
    /// raw form separates those.
    /// </remarks>
    public static void WriteProviderEvidence(Action<string> write, PeripheralMonitor monitor)
    {
        write("=== Raw evidence: what each provider actually read");
        write(string.Empty);

        var nodes = monitor.GetDiagnostics();
        if (nodes.Count == 0)
        {
            write("  No provider reported any evidence. Either nothing is paired, or every");
            write("  provider returned the empty default from IPeripheralProvider.GetDiagnostics.");
            return;
        }

        foreach (var node in nodes)
        {
            write($"  [{node.Transport}] {node.Title}");
            write($"    instance : {node.InstanceId}");

            foreach (var property in node.Properties)
            {
                write($"    {property.Name,-16} : {property.Raw}");
                write($"    {string.Empty,-16}   {property.Key}");

                if (property.Decoded is { } decoded)
                    write($"    {string.Empty,-16}   -> {decoded}");
            }

            write(string.Empty);
        }

        write("  Cross-check now: Settings > Bluetooth & devices should show the same percentage.");
        write("  Both read the same property, so a mismatch means a bug here; a match proves the");
        write("  plumbing only, not the device's honesty about its own charge.");
    }
}
