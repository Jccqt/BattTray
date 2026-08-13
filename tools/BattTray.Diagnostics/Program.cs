using System.Globalization;
using BattTray.Devices;
// Top-level statements compile into the global namespace, so the probe needs naming.
using BattTray.Diagnostics;

// Accuracy harness for BattTray's battery providers.
//
//  1. Dumps the raw evidence behind every reading, so a number the app shows can be traced
//     back to a specific node and property byte rather than to a guess.
//  2. Then watches, logging every change with a wall-clock time. Leave it running across a
//     real discharge and compare each logged value against the vendor app.
//  3. On exit, reports per device whether the values observed look like true 0-100
//     granularity or the coarse 10-step scale HFP headsets use — which decides whether a
//     percentage should be read as a number or as a band. A provider that already knows its
//     source is coarse says so per reading, and that answer is reported rather than inferred.
//
// It drives the real providers through the real IPeripheralProvider seam, so every
// transport added later is covered the moment its provider implements GetDiagnostics.

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        Usage: BattTray.Diagnostics [options]

          --once             Dump the raw evidence and exit, without watching.
          --probe            Sweep every device node and interface for battery-shaped
                             properties, and exit.
          --all              With --probe, dump everything rather than peripheral-looking ones.
          --probe-hid        Sweep every HID interface for battery usages in its report
                             descriptor, which no device property exposes, and exit.
          --interval <sec>   Seconds between scans while watching (default 5).
          --log <path>       Also append everything to this file.
          --help             This message.
        """);
    return 0;
}

int intervalSeconds = ReadInterval(args) ?? 5;
var log = OpenLog(args);

var monitor = new PeripheralMonitor(new BluetoothPeripheralProvider(), new XInputGamepadProvider());
var observations = new Dictionary<string, DeviceLog>(StringComparer.Ordinal);

// The same header the app's own dump carries, from the same code: which build produced this,
// on which Windows, when. The version it names is the app's rather than the harness's, which
// is the right one — every line below comes from code in that assembly.
DiagnosticsDump.WriteHeader(Write, DateTimeOffset.Now);

// The probes answer a different question from the rest of this tool — what Windows knows
// about devices no provider covers yet — so they run alone rather than ahead of the watch.
if (args.Contains("--probe"))
{
    Probe.Run(Write, dumpEveryNode: args.Contains("--all"));
    log?.Dispose();
    return 0;
}

// Separate from --probe rather than folded into it: that sweep reads device properties, this
// one opens handles and parses report descriptors, and the second costs fifty times the first.
// They also answer in different terms, and running both would bury the shorter answer.
if (args.Contains("--probe-hid"))
{
    HidProbe.Run(Write);
    log?.Dispose();
    return 0;
}

// The app's own, so that the evidence section of a harness run and of a "Save diagnostics…"
// dump are the same text produced by the same code rather than two renderings that drift.
DiagnosticsDump.WriteProviderEvidence(Write, monitor);

if (args.Contains("--once"))
{
    log?.Dispose();
    return 0;
}

Write(string.Empty);
Write($"=== Watching every {intervalSeconds}s — Ctrl+C to stop and print the summary.");
Write("Connect the device, use it, let it drain. Compare each logged value against what");
Write("Windows Settings and the vendor app show at the same moment.");
Write(string.Empty);

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

try
{
    while (!stopping.IsCancellationRequested)
    {
        Scan();
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stopping.Token);
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C during the wait: the expected way out.
}

Summarize();
log?.Dispose();
return 0;

void Scan()
{
    monitor.Refresh();

    foreach (var device in monitor.Peripherals)
    {
        string state = string.Create(CultureInfo.InvariantCulture,
            // The band where there is one, so the log records what the app displayed rather
            // than the stand-in number behind it.
            $"bat={device.BatteryText ?? "-",-6} " +
            $"connected={device.IsConnected,-5} " +
            $"charge={device.ChargeState,-11} " +
            $"stale={device.IsStale,-5} " +
            $"propUpdated={device.BatteryUpdatedUtc?.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "-"}");

        if (!observations.TryGetValue(device.Id, out var entry))
            observations[device.Id] = entry = new DeviceLog(device.Name, device.Transport);

        // Only changes are logged: an unchanged line every few seconds would bury the
        // moments that actually matter under thousands of identical rows.
        if (entry.LastState == state)
            continue;

        entry.LastState = state;
        if (device.BatteryBand is { } band)
            entry.Bands.Add(band);
        else if (device.BatteryPercent is { } percent)
            entry.Values.Add(percent);

        Write($"[{DateTime.Now:HH:mm:ss}] {Truncate(device.Name, 28),-28} {state}");
    }
}

void Summarize()
{
    Write(string.Empty);
    Write("=== Summary");

    if (observations.Count == 0)
    {
        Write("No devices observed.");
        return;
    }

    foreach (var (_, entry) in observations.OrderBy(pair => pair.Value.Name, StringComparer.Ordinal))
    {
        Write(string.Empty);
        Write($"  {entry.Name} [{entry.Transport}]");

        // Asked before the granularity test rather than after it, because the test cannot
        // reach the right answer here: XInput's four levels stand in as 5/20/60/100, and
        // "not every value is a multiple of 10" would read that as true 0-100 granularity.
        // The provider already knows the source is coarse, so it is quoted, not guessed at.
        if (entry.Bands.Count > 0)
        {
            Write($"    Bands reported: {string.Join(", ", entry.Bands)}");
            Write("    The provider declared this reading coarse, so the percentage behind it is a");
            Write("    stand-in for sorting and thresholds only. Read the band, and expect an alert");
            Write("    threshold to behave as though it sat on the boundary between two of them.");
            continue;
        }

        if (entry.Values.Count == 0)
        {
            Write("    No battery value ever reported.");
            continue;
        }

        Write($"    Distinct values: {string.Join(", ", entry.Values)}");

        if (entry.Values.Count < 2)
        {
            Write("    Only one value seen — run across a real discharge to judge granularity.");
            continue;
        }

        Write(entry.Values.All(value => value % 10 == 0)
            ? "    Every value was a multiple of 10 — consistent with the coarse 10-bucket scale.\n"
            + "    Read the number as a band, not a precise reading, and keep alert thresholds\n"
            + "    on bucket boundaries."
            : "    Values landed off multiples of 10 — this device reports true 0-100 granularity.");
    }
}

void Write(string line)
{
    Console.WriteLine(line);
    log?.WriteLine(line);
}

static int? ReadInterval(string[] arguments)
{
    int index = Array.IndexOf(arguments, "--interval");
    return index >= 0 && index + 1 < arguments.Length
        && int.TryParse(arguments[index + 1], CultureInfo.InvariantCulture, out int seconds)
        && seconds > 0
        ? seconds
        : null;
}

static StreamWriter? OpenLog(string[] arguments)
{
    int index = Array.IndexOf(arguments, "--log");
    if (index < 0 || index + 1 >= arguments.Length)
        return null;

    try
    {
        // Appending and flushing per line, because the interesting runs are the long ones
        // that end in a Ctrl+C, a sleep or a flat battery.
        return new StreamWriter(arguments[index + 1], append: true) { AutoFlush = true };
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"Could not open log file: {ex.Message}");
        return null;
    }
}

static string Truncate(string text, int length) =>
    text.Length <= length ? text : text[..(length - 1)] + "…";

/// <summary>Everything observed about one device across the run.</summary>
sealed class DeviceLog(string name, Transport transport)
{
    public string Name { get; } = name;

    public Transport Transport { get; } = transport;

    /// <summary>Distinct percentages, ordered, for the granularity verdict.</summary>
    public SortedSet<int> Values { get; } = [];

    /// <summary>
    /// Distinct band names, where the provider reported bands rather than percentages. Kept
    /// apart from <see cref="Values"/> so the verdict cannot be drawn from a stand-in number.
    /// </summary>
    public SortedSet<string> Bands { get; } = new(StringComparer.Ordinal);

    public string? LastState { get; set; }
}
