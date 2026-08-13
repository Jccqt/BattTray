using BattTray.Devices;
using BattTray.Diagnostics;

namespace BattTray.Tests;

/// <summary>
/// The dump the shipped exe writes: the header that says which binary produced it, where the
/// file goes, how the flag is read, and the evidence section's rendering.
/// </summary>
/// <remarks>
/// The two sweeps themselves are not tested and cannot usefully be: they report what is
/// plugged into the machine running them, which is the whole reason they exist. What is
/// testable is everything around them, and the header most of all — a dump that cannot name
/// its own build is a bug report about an unknown binary.
/// </remarks>
public class DiagnosticsDumpTests
{
    /// <summary>A fixed moment with an explicit offset, so no test depends on a time zone.</summary>
    static readonly DateTimeOffset Taken = new(2026, 8, 13, 21, 4, 11, TimeSpan.FromHours(8));

    /// <summary>A provider with evidence to report and nothing else.</summary>
    sealed class Evidenced(params DiagnosticNode[] nodes) : IPeripheralProvider
    {
        public Transport Transport => Transport.Bluetooth;

        public IReadOnlyList<Peripheral> GetPeripherals() => [];

        public void InvalidateDeviceCache() { }

        public IReadOnlyList<DiagnosticNode> GetDiagnostics() => nodes;
    }

    static List<string> Capture(Action<Action<string>> section)
    {
        var lines = new List<string>();
        section(lines.Add);
        return lines;
    }

    [Fact]
    public void TheHeaderNamesTheBuildTheOsAndTheMoment()
    {
        var lines = Capture(write => DiagnosticsDump.WriteHeader(write, Taken));

        Assert.Equal("=== BattTray diagnostics", lines[0]);
        Assert.Equal($"  version : {DiagnosticsDump.AppVersion}", lines[1]);
        Assert.Contains(Environment.OSVersion.VersionString, lines[2]);
        Assert.Equal("  taken   : 2026-08-13 21:04:11 +08:00", lines[3]);
    }

    [Fact]
    public void TheVersionIsARealVersionRatherThanTheFallback()
    {
        // "unknown" is the last resort for an assembly carrying no version attribute at all.
        // Reaching it in a shipped build would leave every dump unattributable, silently.
        Assert.NotEqual("unknown", DiagnosticsDump.AppVersion);
        Assert.StartsWith("0.", DiagnosticsDump.AppVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeaderCarriesNoMachineName() =>
        // The file is meant to be attached to a public issue, and which machine it came from
        // answers no question here — the subject is the hardware attached to it.
        Assert.DoesNotContain(
            Capture(write => DiagnosticsDump.WriteHeader(write, Taken)),
            line => line.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EvidenceIsRenderedAsRawBytesBesideWhatTheAppMadeOfThem()
    {
        // The pair is the point of the section: a reading that disagrees with the vendor app is
        // either a decoding bug or the device lying, and only the raw form separates those.
        var node = new DiagnosticNode(Transport.Bluetooth, "node: pad", "BTHENUM\\dev", [
            new DiagnosticProperty("battery level", "{104ea319} PID 2", "BYTE [57]", "87"),
        ]);

        var lines = Capture(write =>
            DiagnosticsDump.WriteProviderEvidence(write, new PeripheralMonitor(new Evidenced(node))));

        Assert.Contains("  [Bluetooth] node: pad", lines);
        Assert.Contains("    instance : BTHENUM\\dev", lines);
        Assert.Contains(lines, line => line.Contains("battery level") && line.EndsWith("BYTE [57]", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("{104ea319} PID 2"));
        Assert.Contains(lines, line => line.EndsWith("-> 87", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceFromNoProviderAtAllSaysSoRatherThanEndingSilently() =>
        // An empty section reads as "nothing is wrong here"; the sentence distinguishes a
        // machine with nothing paired from a provider that never implemented GetDiagnostics.
        Assert.Contains(
            Capture(write => DiagnosticsDump.WriteProviderEvidence(write, new PeripheralMonitor())),
            line => line.Contains("No provider reported any evidence"));

    [Fact]
    public void TheDefaultFileIsNamedForTheMomentItWasTaken()
    {
        string path = DiagnosticsFile.DefaultPath(Taken);

        Assert.Equal("BattTray-diagnostics-20260813-210411.txt", Path.GetFileName(path));
        Assert.Equal(
            Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(path));
    }

    [Fact]
    public void ASecondDumpDoesNotOverwriteTheFirst() =>
        // Two dumps are usually being compared against each other — before and after plugging
        // something in — rather than one replacing the other.
        Assert.NotEqual(DiagnosticsFile.DefaultPath(Taken), DiagnosticsFile.DefaultPath(Taken.AddSeconds(1)));

    [Fact]
    public void NoFlagIsNoDump() =>
        Assert.False(DiagnosticsCommand.WasRequested(["--autostart"], out _));

    [Fact]
    public void TheFlagAloneAsksForADumpSomewhereItCanChoose()
    {
        Assert.True(DiagnosticsCommand.WasRequested(["--diagnostics"], out string? path));
        Assert.Null(path);
    }

    [Fact]
    public void TheFlagIsReadWhateverCaseItIsTypedIn() =>
        Assert.True(DiagnosticsCommand.WasRequested(["--Diagnostics"], out _));

    [Fact]
    public void AnArgumentAfterTheFlagIsWhereTheDumpGoes()
    {
        Assert.True(DiagnosticsCommand.WasRequested(["--diagnostics", @"C:\logs\dump.txt"], out string? path));
        Assert.Equal(@"C:\logs\dump.txt", path);
    }

    [Fact]
    public void AnotherSwitchAfterTheFlagIsNotAFileName()
    {
        // Otherwise `BattTray.exe --diagnostics --autostart` writes a file called "--autostart"
        // into the working directory, silently, and reveals nothing.
        Assert.True(DiagnosticsCommand.WasRequested(["--diagnostics", "--autostart"], out string? path));
        Assert.Null(path);
    }
}
