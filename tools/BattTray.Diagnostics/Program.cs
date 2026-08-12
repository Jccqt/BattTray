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
//     percentage should be read as a number or as a band.
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

var monitor = new PeripheralMonitor(new BluetoothPeripheralProvider());
var observations = new Dictionary<string, DeviceLog>(StringComparer.Ordinal);

Write($"BattTray diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Write($"Machine: {Environment.MachineName}   OS: {Environment.OSVersion.VersionString}");
Write(string.Empty);

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

DumpRawEvidence();

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
            $"bat={device.BatteryPercent?.ToString(CultureInfo.InvariantCulture) ?? "-",-4} " +
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
        if (device.BatteryPercent is { } percent)
            entry.Values.Add(percent);

        Write($"[{DateTime.Now:HH:mm:ss}] {Truncate(device.Name, 28),-28} {state}");
    }
}

void DumpRawEvidence()
{
    Write("=== Raw evidence: what each provider actually read");
    Write(string.Empty);

    var nodes = monitor.GetDiagnostics();
    if (nodes.Count == 0)
    {
        Write("  No provider reported any evidence. Either nothing is paired, or every");
        Write("  provider returned the empty default from IPeripheralProvider.GetDiagnostics.");
        return;
    }

    foreach (var node in nodes)
    {
        Write($"  [{node.Transport}] {node.Title}");
        Write($"    instance : {node.InstanceId}");

        foreach (var property in node.Properties)
        {
            Write($"    {property.Name,-16} : {property.Raw}");
            Write($"    {string.Empty,-16}   {property.Key}");

            if (property.Decoded is { } decoded)
                Write($"    {string.Empty,-16}   -> {decoded}");
        }

        Write(string.Empty);
    }

    Write("  Cross-check now: Settings > Bluetooth & devices should show the same percentage.");
    Write("  Both read the same property, so a mismatch means a bug here; a match proves the");
    Write("  plumbing only, not the device's honesty about its own charge.");
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

    public string? LastState { get; set; }
}
