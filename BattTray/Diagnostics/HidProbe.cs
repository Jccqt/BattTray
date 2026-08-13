using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using BattTray.Interop;
using Microsoft.Win32.SafeHandles;

namespace BattTray.Diagnostics;

/// <summary>
/// Asks every attached HID interface what its report descriptor declares, and reports anything
/// on a battery-carrying usage page. It exists because <see cref="Probe"/> cannot answer this
/// question: that sweep reads device properties, and a HID battery is not a property — it is a
/// usage inside a report descriptor, invisible to the PnP tree that publishes everything else
/// about the device.
/// </summary>
/// <remarks>
/// The property sweep found no battery outside the Bluetooth enumerators, which ruled out a
/// second provider shaped like the first. That leaves reading HID reports as the only route
/// left for USB and dongle peripherals, and this is the tool that says whether the hardware in
/// front of you would give one anything to read.
///
/// It is expected to come back empty on most machines, and empty is a real answer. The value
/// is in being able to get it again in one command the day a mouse, headset or controller that
/// does report over HID is plugged in, rather than rediscovering the whole question then.
///
/// The same discipline as <see cref="Probe"/> applies to what is printed: raw report bytes go
/// out untransformed next to the decoded value, and every logical and physical range is shown,
/// because a battery usage is not necessarily a 0-100 percentage. Devices report the same
/// usage in 0-255 steps or in mWh, and only the declared range separates those from a
/// percentage that happens to be under 100 at the moment it was read.
/// </remarks>
internal static class HidProbe
{
    /// <summary>GUID_DEVINTERFACE_HID. Every HID collection Windows exposes is listed under it.</summary>
    static readonly Guid HidInterfaceClass = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    /// <summary>
    /// Generic Device Controls. Only usage 0x20 (Battery Strength) on this page counts as a
    /// battery — the rest of it is wireless-link and security-code plumbing.
    /// </summary>
    const ushort GenericDeviceControlsPage = 0x06;

    /// <summary>Power Device. UPS-derived, and the page a wired device with a charger uses.</summary>
    const ushort PowerDevicePage = 0x84;

    /// <summary>Battery System. The page the roadmap named, and the richest of the three.</summary>
    const ushort BatterySystemPage = 0x85;

    const ushort BatteryStrengthUsage = 0x20;

    /// <summary>Caps are asked for on these two only: output reports are what a host sends.</summary>
    static readonly int[] ReadableReportTypes = [HidApi.ReportTypeInput, HidApi.ReportTypeFeature];

    public static void Run(Action<string> write)
    {
        var stopwatch = Stopwatch.StartNew();
        string[] paths = ConfigManager.GetDeviceInterfaces(HidInterfaceClass);
        long enumerateMs = stopwatch.ElapsedMilliseconds;

        // Timed apart from the enumeration because the gap between the two is the finding that
        // keeps this in a diagnostics tool: listing the interfaces is free, and opening them is
        // not. A provider polled on the UI thread can afford the first and not the second.
        stopwatch.Restart();
        var interfaces = paths.Select(Inspect).ToList();
        long inspectMs = stopwatch.ElapsedMilliseconds;

        WriteHeader(write, interfaces, enumerateMs, inspectMs);

        var flagged = interfaces.Where(item => item.HasBatteryUsage).ToList();
        var refused = interfaces.Where(item => !item.IsOpen).ToList();

        WriteBatterySection(write, flagged);
        WriteRefusedSection(write, refused);

        write("--- Every HID interface");
        write(string.Empty);

        foreach (var item in interfaces)
            WriteInterface(write, item);

        WriteVerdict(write, interfaces, flagged, refused);
    }

    /// <summary>
    /// Opens one interface and reads everything its descriptor will give up. The handle and the
    /// preparsed data are both released on every path out, including the failures: a probe that
    /// leaks a handle per device is a probe that changes what the next tool to run can see.
    /// </summary>
    static HidInterface Inspect(string path)
    {
        var item = new HidInterface(path);

        using SafeFileHandle device = HidApi.Open(path);
        if (device.IsInvalid)
        {
            // Reported rather than skipped. "Could not open" and "opened, no battery" are
            // different findings, and silently dropping the first would let a device that does
            // report a battery disappear from the answer entirely.
            item.OpenError = DescribeError(Marshal.GetLastPInvokeError());
            return item;
        }

        item.Product = HidApi.GetProductString(device);
        item.Attributes = HidApi.GetAttributes(device);

        if (!HidApi.GetPreparsedData(device, out IntPtr preparsed))
        {
            item.Failure = $"HidD_GetPreparsedData failed — {DescribeError(Marshal.GetLastPInvokeError())}";
            return item;
        }

        try
        {
            int status = HidApi.GetCaps(preparsed, out HIDP_CAPS caps);
            if (status != HidApi.HIDP_STATUS_SUCCESS)
            {
                item.Failure = $"HidP_GetCaps failed — NTSTATUS 0x{status:X8}";
                return item;
            }

            item.Caps = caps;
            ReadCaps(item, preparsed, caps);
            ReadBatteryFeatureReports(item, device, preparsed, caps.FeatureReportByteLength);
        }
        finally
        {
            HidApi.FreePreparsedData(preparsed);
        }

        return item;
    }

    /// <summary>Every value and button cap the descriptor declares for input and feature reports.</summary>
    static void ReadCaps(HidInterface item, IntPtr preparsed, HIDP_CAPS caps)
    {
        foreach (int reportType in ReadableReportTypes)
        {
            ushort values = reportType == HidApi.ReportTypeInput
                ? caps.NumberInputValueCaps
                : caps.NumberFeatureValueCaps;
            ushort buttons = reportType == HidApi.ReportTypeInput
                ? caps.NumberInputButtonCaps
                : caps.NumberFeatureButtonCaps;

            int status = HidApi.GetValueCaps(preparsed, reportType, values, out HIDP_VALUE_CAPS[] valueCaps);
            if (status == HidApi.HIDP_STATUS_SUCCESS)
                item.Values.AddRange(valueCaps.Select(cap => new ValueCap(reportType, cap)));
            else
                item.CapErrors.Add($"HidP_GetValueCaps({DescribeReportType(reportType)}) failed — NTSTATUS 0x{status:X8}");

            status = HidApi.GetButtonCaps(preparsed, reportType, buttons, out HIDP_BUTTON_CAPS[] buttonCaps);
            if (status == HidApi.HIDP_STATUS_SUCCESS)
                item.Buttons.AddRange(buttonCaps.Select(cap => new ButtonCap(reportType, cap)));
            else
                item.CapErrors.Add($"HidP_GetButtonCaps({DescribeReportType(reportType)}) failed — NTSTATUS 0x{status:X8}");
        }
    }

    /// <summary>
    /// Reads, once per report id, every feature report carrying a flagged usage. Feature reports
    /// are the ones a host can ask for on demand; an input report only arrives when the device
    /// decides to send one, so a flagged input usage is noted and left unread rather than
    /// blocking the probe on a device that may have nothing to say.
    /// </summary>
    static void ReadBatteryFeatureReports(
        HidInterface item, SafeFileHandle device, IntPtr preparsed, ushort featureLength)
    {
        var reportIds = item.BatteryFeatureValues.Select(cap => cap.Caps.ReportID)
            .Concat(item.BatteryFeatureButtons.Select(cap => cap.Caps.ReportID))
            .Distinct()
            .Order();

        foreach (byte reportId in reportIds)
        {
            if (featureLength == 0)
            {
                item.FeatureReads.Add(new FeatureRead(
                    reportId, [], "the descriptor declares a feature report length of 0", []));
                continue;
            }

            // The first byte is the report id, which is how the device is told which report is
            // wanted. Descriptors that use no report ids leave it 0, and that works too.
            var report = new byte[featureLength];
            report[0] = reportId;

            if (!HidApi.GetFeature(device, report))
            {
                item.FeatureReads.Add(new FeatureRead(
                    reportId, [], $"HidD_GetFeature failed — {DescribeError(Marshal.GetLastPInvokeError())}", []));
                continue;
            }

            var decoded = new List<DecodedUsage>();

            foreach (var cap in item.BatteryFeatureValues.Where(cap => cap.Caps.ReportID == reportId))
            {
                foreach (ushort usage in BatteryUsages(cap.Caps.UsagePage, cap.IsRange, cap.Usage, cap.UsageMax))
                {
                    int status = HidApi.GetUsageValue(
                        preparsed, HidApi.ReportTypeFeature, cap.Caps.UsagePage, cap.Caps.LinkCollection,
                        usage, report, out uint value);

                    decoded.Add(new DecodedUsage(
                        cap.Caps.UsagePage, usage, status == HidApi.HIDP_STATUS_SUCCESS ? value : null,
                        status, cap.Caps.LogicalMin, cap.Caps.LogicalMax));
                }
            }

            item.FeatureReads.Add(new FeatureRead(reportId, report, null, decoded));
        }
    }

    static void WriteHeader(
        Action<string> write, List<HidInterface> interfaces, long enumerateMs, long inspectMs)
    {
        int opened = interfaces.Count(item => item.IsOpen);

        write("=== Probe: every HID interface, every usage its report descriptor declares");
        write(string.Empty);
        write(string.Create(CultureInfo.InvariantCulture,
            $"  {interfaces.Count} HID interfaces, {opened} opened, {interfaces.Count - opened} refused. "
            + $"Enumerated in {enumerateMs} ms, opened and parsed in {inspectMs} ms."));
        write("  Handles are opened with dwDesiredAccess = 0, which is why devices held exclusively");
        write("  by their own software still answer: no reports can be read through such a handle,");
        write("  but the descriptor can, and the descriptor is what says a battery is there.");
        write(string.Empty);
        write("  Three pages count as a battery, and all three are checked:");
        write("    0x85 Battery System        — 0x66 RemainingCapacity, 0x44 Charging, 0x45 Discharging");
        write("    0x84 Power Device          — the UPS-derived page, used by wired devices with a charger");
        write("    0x06 Generic Device Ctrls  — usage 0x20 Battery Strength, common on gamepads and BLE HID");
        write("  The roadmap named only 0x85; a probe that looked there alone would miss the third,");
        write("  which is the one a modern controller is most likely to use.");
        write(string.Empty);
        write("  Logical ranges print as declared, and HID declares those items signed: a maximum");
        write("  of 255 or 65535 written into a one- or two-byte item comes back as -1, so read");
        write("  \"logical 0..-1\" as \"up to the full width of the field\". Read the range rather");
        write("  than assuming a percentage — it separates 0-100 from 0-255 and from mWh.");
        write(string.Empty);
    }

    static void WriteBatterySection(Action<string> write, List<HidInterface> flagged)
    {
        write("--- Interfaces declaring a battery usage");
        write(string.Empty);

        if (flagged.Count == 0)
        {
            write("  (nothing) — no attached HID device declares a usage on any of the three pages.");
            write("  This is the expected answer on hardware whose peripherals report over Bluetooth");
            write("  or not at all. It is a statement about what is plugged in now, not about HID.");
            write(string.Empty);
            return;
        }

        foreach (var item in flagged)
            WriteInterface(write, item);
    }

    static void WriteRefusedSection(Action<string> write, List<HidInterface> refused)
    {
        write("--- Interfaces that could not be opened");
        write(string.Empty);

        if (refused.Count == 0)
        {
            write("  (nothing) — every interface opened, so no device is hiding behind a sharing error.");
            write(string.Empty);
            return;
        }

        write("  A device that will not open has not been ruled out: its descriptor was never read.");
        write(string.Empty);

        foreach (var item in refused)
            WriteInterface(write, item);
    }

    /// <summary>
    /// One interface: who it is, what its reports look like, and every cap it declares. The
    /// same entry is printed in the battery section and in the full dump, so a flagged device
    /// reads identically wherever it is met.
    /// </summary>
    static void WriteInterface(Action<string> write, HidInterface item)
    {
        const int Label = 10;

        write($"  {item.Name}");
        write($"    {"interface",-Label} : {item.Path}");

        if (item.OpenError is { } openError)
        {
            write($"    {"open",-Label} : failed — {openError}");
            write(string.Empty);
            return;
        }

        write($"    {"product",-Label} : {item.Product ?? "(none published)"}");

        write(item.Attributes is { } attributes
            ? string.Create(CultureInfo.InvariantCulture,
                $"    {"ids",-Label} : VID_{attributes.VendorID:X4} PID_{attributes.ProductID:X4} "
                + $"version 0x{attributes.VersionNumber:X4}")
            : $"    {"ids",-Label} : (HidD_GetAttributes declined)");

        if (item.Failure is { } failure)
        {
            write($"    {"descriptor",-Label} : {failure}");
            write(string.Empty);
            return;
        }

        if (item.Caps is { } caps)
        {
            write($"    {"top level",-Label} : {DescribeUsage(caps.UsagePage, caps.Usage)}");
            write(string.Create(CultureInfo.InvariantCulture,
                $"    {"reports",-Label} : input {caps.InputReportByteLength} bytes, "
                + $"output {caps.OutputReportByteLength} bytes, feature {caps.FeatureReportByteLength} bytes"));
            write(string.Create(CultureInfo.InvariantCulture,
                $"    {"caps",-Label} : input {caps.NumberInputValueCaps} values / {caps.NumberInputButtonCaps} buttons, "
                + $"feature {caps.NumberFeatureValueCaps} values / {caps.NumberFeatureButtonCaps} buttons, "
                + $"{caps.NumberLinkCollectionNodes} link collections"));
        }

        foreach (string error in item.CapErrors)
            write($"    {"caps",-Label} : {error}");

        foreach (var cap in item.Values)
            write($"      {DescribeValueCap(cap)}");

        foreach (var cap in item.Buttons)
            write($"      {DescribeButtonCap(cap)}");

        if (item.Values.Count + item.Buttons.Count == 0)
            write("      (no value or button caps on input or feature reports)");

        foreach (var read in item.FeatureReads)
            WriteFeatureRead(write, read);

        if (item.BatteryInputCount > 0)
        {
            write(string.Create(CultureInfo.InvariantCulture,
                $"      {item.BatteryInputCount} battery usage(s) are on input reports, which arrive when the"));
            write("      device sends one rather than on request, and are left unread here.");
        }

        write(string.Empty);
    }

    /// <summary>
    /// A feature report as it read back, bytes first. The bytes are printed whatever the decode
    /// did, because they are the part that can be checked against a vendor app; the decoded
    /// value is this tool's interpretation of them and is the part that can be wrong.
    /// </summary>
    static void WriteFeatureRead(Action<string> write, FeatureRead read)
    {
        write(string.Create(CultureInfo.InvariantCulture, $"      feature report 0x{read.ReportId:X2}, read just now"));

        if (read.Error is { } error)
        {
            write($"        {error}");
            return;
        }

        write($"        raw    : {Convert.ToHexString(read.Bytes)}");

        foreach (var decoded in read.Decoded)
        {
            string value = decoded.Value is { } number
                ? number.ToString(CultureInfo.InvariantCulture)
                : $"HidP_GetUsageValue failed — NTSTATUS 0x{decoded.Status:X8}";

            write(string.Create(CultureInfo.InvariantCulture,
                $"        {DescribeUsage(decoded.UsagePage, decoded.Usage)} -> {value} "
                + $"(logical {decoded.LogicalMin}..{decoded.LogicalMax})"));
        }

        write("        A device may only refresh a feature report when polled, so a first read can be");
        write("        stale or empty. Read it twice, minutes apart, before believing a fixed value.");
    }

    static void WriteVerdict(
        Action<string> write, List<HidInterface> interfaces, List<HidInterface> flagged, List<HidInterface> refused)
    {
        write("--- Verdict");
        write(string.Empty);

        if (flagged.Count > 0)
        {
            write(string.Create(CultureInfo.InvariantCulture,
                $"  {flagged.Count} of {interfaces.Count} interfaces declare a battery usage. A HID provider"));
            write("  has something to read on this hardware. Check the logical range before assuming a");
            write("  percentage, and compare a feature read against the vendor app at the same moment.");
        }
        else
        {
            write(string.Create(CultureInfo.InvariantCulture,
                $"  None of the {interfaces.Count} interfaces declares a battery usage on page 0x85, 0x84"));
            write("  or 0x06/0x20. A HID battery provider would have nothing to read on this hardware");
            write("  today, which is a fact about what is attached rather than about the approach.");
        }

        if (refused.Count > 0)
        {
            write(string.Create(CultureInfo.InvariantCulture,
                $"  {refused.Count} interface(s) never opened and are unjudged — see the section above."));
        }

        write(string.Empty);
    }

    /// <summary>
    /// Whether a cap sits on a page this probe treats as a battery. Whole pages count for 0x85
    /// and 0x84, since everything on them is battery or charger business; 0x06 is a general
    /// page where only Battery Strength is, so it is matched by usage — including when the cap
    /// declares a range that happens to span 0x20.
    /// </summary>
    static bool IsBatteryUsage(ushort page, bool isRange, ushort usage, ushort usageMax) => page switch
    {
        BatterySystemPage or PowerDevicePage => true,
        GenericDeviceControlsPage => isRange
            ? usage <= BatteryStrengthUsage && BatteryStrengthUsage <= usageMax
            : usage == BatteryStrengthUsage,
        _ => false,
    };

    /// <summary>
    /// The usages within a flagged cap that are worth reading back. A range is walked, bounded
    /// because a malformed descriptor can declare one spanning the whole page and there is no
    /// reading worth 65,000 decode calls.
    /// </summary>
    static IEnumerable<ushort> BatteryUsages(ushort page, bool isRange, ushort usage, ushort usageMax)
    {
        const int Limit = 64;

        if (page == GenericDeviceControlsPage)
        {
            yield return BatteryStrengthUsage;
            yield break;
        }

        if (!isRange)
        {
            yield return usage;
            yield break;
        }

        for (int current = usage; current <= usageMax && current - usage < Limit; current++)
            yield return (ushort)current;
    }

    static string DescribeValueCap(ValueCap cap)
    {
        var caps = cap.Caps;

        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DescribeReportType(cap.ReportType),-7} value  report 0x{caps.ReportID:X2}  "
            + $"{DescribeUsage(caps.UsagePage, cap.IsRange, cap.Usage, cap.UsageMax)}  "
            + $"bits {caps.BitSize} x{caps.ReportCount}  "
            + $"logical {caps.LogicalMin}..{caps.LogicalMax}  "
            + $"physical {caps.PhysicalMin}..{caps.PhysicalMax}  "
            + $"collection {caps.LinkCollection}");

        // Units are only printed when the device declares them, which is rare — but when a
        // battery usage carries one it is the fastest way to tell mWh from a percentage.
        if (caps.Units != 0)
            line += string.Create(CultureInfo.InvariantCulture, $"  units 0x{caps.Units:X8} exp {caps.UnitsExp}");

        if (caps.IsAbsolute == 0)
            line += "  relative";

        return cap.IsBattery ? line + "   <== BATTERY USAGE" : line;
    }

    static string DescribeButtonCap(ButtonCap cap)
    {
        var caps = cap.Caps;

        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DescribeReportType(cap.ReportType),-7} button report 0x{caps.ReportID:X2}  "
            + $"{DescribeUsage(caps.UsagePage, cap.IsRange, cap.Usage, cap.UsageMax)}  "
            + $"collection {caps.LinkCollection}");

        return cap.IsBattery ? line + "   <== BATTERY USAGE" : line;
    }

    static string DescribeReportType(int reportType) => reportType switch
    {
        HidApi.ReportTypeInput => "input",
        HidApi.ReportTypeOutput => "output",
        HidApi.ReportTypeFeature => "feature",
        _ => reportType.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>A Win32 error code alongside the message the system has for it.</summary>
    static string DescribeError(int error) =>
        $"GetLastError {error} ({new Win32Exception(error).Message})";

    static string DescribeUsage(ushort page, bool isRange, ushort usage, ushort usageMax) =>
        isRange
            ? string.Create(CultureInfo.InvariantCulture,
                $"page 0x{page:X4} ({DescribePage(page)}) usage 0x{usage:X4}..0x{usageMax:X4}")
            : DescribeUsage(page, usage);

    static string DescribeUsage(ushort page, ushort usage)
    {
        string described = string.Create(CultureInfo.InvariantCulture,
            $"page 0x{page:X4} ({DescribePage(page)}) usage 0x{usage:X4}");

        return KnownUsages.TryGetValue((page, usage), out string? name) ? $"{described} ({name})" : described;
    }

    /// <summary>
    /// A usage page's name where this file is sure of it. Vendor-defined pages are named as a
    /// class rather than guessed at, and anything else prints bare — on the same terms as the
    /// property-key naming in <see cref="Probe"/>, and for the same reason: a wrong name here
    /// would send a reader to a table entry that does not exist.
    /// </summary>
    static string DescribePage(ushort page) => page switch
    {
        0x01 => "Generic Desktop",
        0x02 => "Simulation",
        0x03 => "VR",
        0x04 => "Sport",
        0x05 => "Game",
        GenericDeviceControlsPage => "Generic Device Controls",
        0x07 => "Keyboard/Keypad",
        0x08 => "LEDs",
        0x09 => "Button",
        0x0A => "Ordinal",
        0x0B => "Telephony",
        0x0C => "Consumer",
        0x0D => "Digitizers",
        0x0F => "PID",
        0x10 => "Unicode",
        0x14 => "Auxiliary Display",
        0x20 => "Sensors",
        PowerDevicePage => "Power Device",
        BatterySystemPage => "Battery System",
        0x8C => "Bar Code Scanner",
        0x90 => "Camera Control",
        >= 0xFF00 => "vendor-defined",
        _ => "unnamed here",
    };

    /// <summary>
    /// Usages worth naming: the top-level ones that say what a collection is, and every battery
    /// usage on the three pages this probe flags. The battery half is the point — a dump that
    /// prints "usage 0x0066" where a reader needs "RemainingCapacity" hides the finding it was
    /// run to produce.
    /// </summary>
    static readonly Dictionary<(ushort Page, ushort Usage), string> KnownUsages = new()
    {
        // Generic Desktop, top-level collections only.
        [(0x01, 0x01)] = "Pointer",
        [(0x01, 0x02)] = "Mouse",
        [(0x01, 0x04)] = "Joystick",
        [(0x01, 0x05)] = "Game Pad",
        [(0x01, 0x06)] = "Keyboard",
        [(0x01, 0x07)] = "Keypad",
        [(0x01, 0x08)] = "Multi-axis Controller",
        [(0x01, 0x80)] = "System Control",
        [(0x0C, 0x01)] = "Consumer Control",
        [(0x0D, 0x01)] = "Digitizer",
        [(0x0D, 0x04)] = "Touch Screen",
        [(0x0D, 0x05)] = "Touch Pad",

        // Generic Device Controls: the wireless-and-battery block.
        [(GenericDeviceControlsPage, BatteryStrengthUsage)] = "Battery Strength",
        [(GenericDeviceControlsPage, 0x21)] = "Wireless Channel",
        [(GenericDeviceControlsPage, 0x22)] = "Wireless ID",

        // Power Device: the collections and measurements a charger-carrying device declares.
        [(PowerDevicePage, 0x02)] = "PresentStatus",
        [(PowerDevicePage, 0x03)] = "ChangedStatus",
        [(PowerDevicePage, 0x10)] = "BatterySystem",
        [(PowerDevicePage, 0x12)] = "Battery",
        [(PowerDevicePage, 0x14)] = "Charger",
        [(PowerDevicePage, 0x24)] = "PowerSummary",
        [(PowerDevicePage, 0x30)] = "Voltage",
        [(PowerDevicePage, 0x31)] = "Current",
        [(PowerDevicePage, 0x35)] = "PercentLoad",
        [(PowerDevicePage, 0x36)] = "Temperature",
        [(PowerDevicePage, 0x60)] = "Present",
        [(PowerDevicePage, 0x61)] = "Good",
        [(PowerDevicePage, 0x65)] = "Overload",
        [(PowerDevicePage, 0x66)] = "OverCharged",
        [(PowerDevicePage, 0x68)] = "ShutdownRequested",
        [(PowerDevicePage, 0x69)] = "ShutdownImminent",

        // Battery System, the flags and then the values. RemainingCapacity is the one a
        // percentage would come from, and RelativeStateOfCharge the one it is confused with:
        // the first is scaled by CapacityMode, the second is always a percentage.
        [(BatterySystemPage, 0x2C)] = "CapacityMode",
        [(BatterySystemPage, 0x42)] = "BelowRemainingCapacityLimit",
        [(BatterySystemPage, 0x44)] = "Charging",
        [(BatterySystemPage, 0x45)] = "Discharging",
        [(BatterySystemPage, 0x46)] = "FullyCharged",
        [(BatterySystemPage, 0x47)] = "FullyDischarged",
        [(BatterySystemPage, 0x4B)] = "NeedReplacement",
        [(BatterySystemPage, 0x64)] = "RelativeStateOfCharge",
        [(BatterySystemPage, 0x65)] = "AbsoluteStateOfCharge",
        [(BatterySystemPage, 0x66)] = "RemainingCapacity",
        [(BatterySystemPage, 0x67)] = "FullChargeCapacity",
        [(BatterySystemPage, 0x68)] = "RunTimeToEmpty",
        [(BatterySystemPage, 0x6B)] = "CycleCount",
        [(BatterySystemPage, 0x83)] = "DesignCapacity",
        [(BatterySystemPage, 0x8B)] = "Rechargable",
        [(BatterySystemPage, 0x8C)] = "WarningCapacityLimit",
        [(BatterySystemPage, 0x8D)] = "CapacityGranularity1",
        [(BatterySystemPage, 0x8E)] = "CapacityGranularity2",
        [(BatterySystemPage, 0xD0)] = "ACPresent",
        [(BatterySystemPage, 0xD1)] = "BatteryPresent",
    };

    /// <summary>One value-type cap, with the report type it belongs to and the battery verdict.</summary>
    sealed record ValueCap(int ReportType, HIDP_VALUE_CAPS Caps)
    {
        public bool IsRange { get; } = Caps.IsRange != 0;

        /// <summary>UsageMin under the union when this is a range, and Usage when it is not.</summary>
        public ushort Usage { get; } = Caps.Usage;

        public ushort UsageMax { get; } = Caps.IsRange != 0 ? Caps.UsageMax : Caps.Usage;

        public bool IsBattery { get; } =
            IsBatteryUsage(Caps.UsagePage, Caps.IsRange != 0, Caps.Usage, Caps.UsageMax);
    }

    /// <summary>One button-type cap, on the same terms as <see cref="ValueCap"/>.</summary>
    sealed record ButtonCap(int ReportType, HIDP_BUTTON_CAPS Caps)
    {
        public bool IsRange { get; } = Caps.IsRange != 0;

        public ushort Usage { get; } = Caps.Usage;

        public ushort UsageMax { get; } = Caps.IsRange != 0 ? Caps.UsageMax : Caps.Usage;

        public bool IsBattery { get; } =
            IsBatteryUsage(Caps.UsagePage, Caps.IsRange != 0, Caps.Usage, Caps.UsageMax);
    }

    /// <summary>
    /// A usage decoded out of a feature report. The logical range travels with it because the
    /// number alone does not say what it means: 64 against 0..100 is a percentage, and 64
    /// against 0..255 is a quarter charge.
    /// </summary>
    sealed record DecodedUsage(
        ushort UsagePage, ushort Usage, uint? Value, int Status, int LogicalMin, int LogicalMax);

    /// <summary>
    /// One feature report as read. <c>Error</c> is set instead of the bytes when the read never
    /// happened — a zero-access handle can be refused here even though the same handle parsed
    /// the descriptor happily.
    /// </summary>
    sealed record FeatureRead(byte ReportId, byte[] Bytes, string? Error, List<DecodedUsage> Decoded);

    /// <summary>Everything one HID interface gave up, including the ways it refused to.</summary>
    sealed class HidInterface(string path)
    {
        public string Path { get; } = path;

        /// <summary>Null when the handle opened; the reason it did not otherwise.</summary>
        public string? OpenError { get; set; }

        /// <summary>Set when the handle opened but the descriptor behind it could not be read.</summary>
        public string? Failure { get; set; }

        public string? Product { get; set; }

        public HIDD_ATTRIBUTES? Attributes { get; set; }

        public HIDP_CAPS? Caps { get; set; }

        public List<ValueCap> Values { get; } = [];

        public List<ButtonCap> Buttons { get; } = [];

        /// <summary>Cap calls that failed, kept per interface rather than aborting the sweep.</summary>
        public List<string> CapErrors { get; } = [];

        public List<FeatureRead> FeatureReads { get; } = [];

        public bool IsOpen => OpenError is null;

        /// <summary>
        /// The product string, or the VID/PID segment of the path where none is published —
        /// which is most of them. A device with no name is still identifiable by its ids, and
        /// "(unnamed)" repeated fifteen times would not be.
        /// </summary>
        public string Name => Product is { Length: > 0 } product
            ? product
            : Path.Split('#') is [_, { Length: > 0 } identity, ..] ? identity : Path;

        public IEnumerable<ValueCap> BatteryFeatureValues =>
            Values.Where(cap => cap.IsBattery && cap.ReportType == HidApi.ReportTypeFeature);

        public IEnumerable<ButtonCap> BatteryFeatureButtons =>
            Buttons.Where(cap => cap.IsBattery && cap.ReportType == HidApi.ReportTypeFeature);

        public int BatteryInputCount =>
            Values.Count(cap => cap.IsBattery && cap.ReportType == HidApi.ReportTypeInput)
            + Buttons.Count(cap => cap.IsBattery && cap.ReportType == HidApi.ReportTypeInput);

        public bool HasBatteryUsage =>
            Values.Any(cap => cap.IsBattery) || Buttons.Any(cap => cap.IsBattery);
    }
}
