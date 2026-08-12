using System.Globalization;
using BattTray.Interop;

namespace BattTray.Devices;

/// <summary>
/// Reports battery for wireless controllers Windows exposes through XInput.
/// </summary>
/// <remarks>
/// What this covers is narrower than "gamepads", and the narrowing is the point:
/// <list type="bullet">
/// <item>A pad on a cable reports BATTERY_TYPE_WIRED, which means "there is no battery to
/// ask about". The BATTERY_LEVEL byte beside it still reads FULL, because the field has to
/// hold something; taking it would put a confident 100% in the tray for a device that never
/// said so. A wired pad therefore contributes no peripheral at all.</item>
/// <item>What is left is a pad on a radio — this controller in 2.4 GHz dongle mode, an Xbox
/// pad on its own dongle — reporting one of four levels. Four. Not a percentage, and the
/// menu never shows it as one; see <see cref="Peripheral.BatteryBand"/>.</item>
/// </list>
///
/// Windows.Gaming.Input's <c>TryGetBatteryReport</c> looks like the richer source and is
/// not: measured against this controller it reported 1000 of 1000 mWh with a status of
/// Discharging, while the pad was plugged in and charging. Those milliwatt-hours are the
/// same four-step byte scaled up, so the WinRT dependency would buy a number that is no more
/// accurate, reads as though it were, and is wrong about the charge direction as well.
/// <see cref="ChargeState"/> is accordingly left Unknown here, exactly as Bluetooth leaves
/// it: the one thing XInput will say about a cable is said by refusing to report a battery.
///
/// Identity is the awkward part. A slot is an index from 0 to 3 with no name, no VID/PID and
/// no serial behind it, and a pad that reconnects can land in a different one. Two
/// consequences are lived with rather than papered over:
/// <list type="bullet">
/// <item>The rows are named for the slot, not the hardware, and say so. An Xbox pad paired
/// over Bluetooth reaches XInput <em>and</em> publishes a battery to the PnP tree, so it can
/// appear twice — once under its product name from the Bluetooth provider, once as a slot
/// here. Nothing XInput exposes could correlate the two, so the duplicate is made legible
/// instead: one row carries a real name and one is plainly a slot.</item>
/// <item><see cref="Peripheral.Id"/> is the slot, so LowBatteryNotifier's latch belongs to
/// the slot rather than to the controller. Swap a low pad for a second low pad in the same
/// slot and the second one inherits the first one's warning. The alternative is a latch that
/// re-fires whenever a pad reconnects, which is the failure that class exists to avoid.</item>
/// </list>
///
/// The transport is reported as <see cref="Transport.Dongle"/>. XInput does not say which
/// radio it is talking over, and the Bluetooth case above is genuinely filed under the wrong
/// one — but that pad is already in the menu under its real transport from the other
/// provider, and every reading only reachable here arrives over a dongle.
///
/// No cache, and no <see cref="IPeripheralProvider.InvalidateDeviceCache"/> override: there
/// is nothing to enumerate and nothing to open, only four calls into a loaded DLL. Measured
/// on the development machine, a whole sweep is ~0.45 ms against the single-digit-millisecond
/// budget in <see cref="IPeripheralProvider"/> — but not in the shape anyone expects, which is
/// why the figures are here rather than in a commit message. An occupied slot answers in
/// ~0.005 ms and an *empty* one takes ~0.155 ms, thirty times as long, so three empty slots are
/// some 97% of the cost and the provider gets cheaper as controllers are plugged in. That is
/// the old "never poll disconnected controllers" problem, still visible and no longer
/// expensive; should a later XInput call be added, it is worth re-measuring rather than
/// assuming this one's verdict carries.
///
/// The first call costs ~13 ms, being the load of xinput1_4.dll. It lands in the constructor's
/// first refresh, before the message loop starts, so it is paid once as startup rather than as
/// a stutter in the tray.
/// </remarks>
internal sealed class XInputGamepadProvider : IPeripheralProvider
{
    /// <summary>
    /// The percentage standing in for each of XInput's four levels, indexed by the level byte.
    /// </summary>
    /// <remarks>
    /// Nobody sees these numbers — <see cref="Peripheral.BatteryText"/> renders the band name
    /// instead — but sorting and the low-battery threshold are numeric, so a band still needs
    /// one. They are placed so that each of the three thresholds the settings dialog offers
    /// cuts between bands rather than through the middle of one: 10 separates EMPTY from LOW,
    /// and 20 and 30 both leave LOW below and MEDIUM above. Spacing them widely is what keeps
    /// LowBatteryNotifier's re-arm margin workable, since a band is the smallest step this
    /// device can take: at the 20% and 30% thresholds, climbing to MEDIUM clears the margin
    /// and re-arms. The exception is a pad that alerted at EMPTY against a 10% threshold,
    /// which needs 25 to re-arm and so stays latched through LOW until it reaches MEDIUM.
    /// </remarks>
    static readonly int[] BandPercent = [5, 20, 60, 100];

    /// <summary>
    /// What each level is called, in level order. Rendered verbatim and mid-sentence — "is
    /// low", "— medium · connected" — so they are lower case and read as adjectives.
    /// </summary>
    static readonly string[] BandNames = ["empty", "low", "medium", "full"];

    public Transport Transport => Transport.Dongle;

    public IReadOnlyList<Peripheral> GetPeripherals()
    {
        var results = new List<Peripheral>();

        for (uint slot = 0; slot < XInput.SlotCount; slot++)
        {
            if (XInput.Read(slot) is not { Result: XInput.ErrorSuccess } reading)
                continue;

            if (Interpret(reading.Type, reading.Level) is not { } band)
                continue;

            results.Add(new Peripheral
            {
                Id = SlotId(slot),
                Name = SlotName(slot),
                Transport = Transport.Dongle,
                Category = DeviceCategory.Gamepad,
                BatteryPercent = band.Percent,
                BatteryBand = band.Name,

                // Nothing here is ever stale, unlike Bluetooth: XInput keeps no memory of a
                // pad that has gone, so a slot that answered at all answered about now. There
                // is no BatteryUpdatedUtc for the same reason — the reading carries no
                // timestamp, and inventing one would only make the menu's "last seen" lie.
                IsConnected = true,
            });
        }

        return results;
    }

    /// <summary>
    /// Dumps all four slots, including the empty ones, with the bytes behind each verdict.
    /// Printing the empty slots is deliberate: the question this dump usually has to answer is
    /// "why is my controller not listed?", and the answers — nothing in the slot, a cable, a
    /// battery type nobody documented — are told apart by these two bytes and the return code,
    /// which is precisely what a slot omitted for reporting nothing would take with it.
    /// </summary>
    public IReadOnlyList<DiagnosticNode> GetDiagnostics()
    {
        var nodes = new List<DiagnosticNode>();

        for (uint slot = 0; slot < XInput.SlotCount; slot++)
            nodes.Add(new DiagnosticNode(Transport.Dongle, $"slot {slot}: {SlotName(slot)}", SlotId(slot), Describe(slot)));

        return nodes;
    }

    static IReadOnlyList<DiagnosticProperty> Describe(uint slot)
    {
        if (XInput.Read(slot) is not { } reading)
        {
            return
            [
                new DiagnosticProperty(
                    "xinput1_4.dll", "LoadLibrary", "(unavailable)",
                    "the DLL could not be called, so this provider reports nothing on this machine"),
            ];
        }

        var result = new DiagnosticProperty(
            "call result", "XInputGetBatteryInformation",
            $"DWORD [0x{reading.Result:X8}]", DescribeResult(reading.Result));

        // The struct is only written on success, so on failure there are no bytes to print and
        // anything in those fields would be this wrapper's zeroes rather than XInput's answer.
        return reading.Result != XInput.ErrorSuccess
            ? [result]
            :
            [
                result,
                new DiagnosticProperty(
                    "battery type", "XINPUT_BATTERY_INFORMATION.BatteryType",
                    $"BYTE [{reading.Type:X2}]", DescribeType(reading.Type)),
                new DiagnosticProperty(
                    "battery level", "XINPUT_BATTERY_INFORMATION.BatteryLevel",
                    $"BYTE [{reading.Level:X2}]", DescribeLevel(reading.Type, reading.Level)),
            ];
    }

    static string DescribeResult(uint result) => result switch
    {
        XInput.ErrorSuccess => "ERROR_SUCCESS",
        XInput.ErrorDeviceNotConnected => "ERROR_DEVICE_NOT_CONNECTED — no controller in this slot",
        _ => "the call failed, so this slot contributes nothing",
    };

    static string DescribeType(byte type) => type switch
    {
        XInput.BatteryTypeDisconnected =>
            "BATTERY_TYPE_DISCONNECTED — no battery, so no peripheral",
        XInput.BatteryTypeWired =>
            "BATTERY_TYPE_WIRED — a cable, so there is no battery to report and the level below "
            + "means nothing; no peripheral",
        XInput.BatteryTypeAlkaline => "BATTERY_TYPE_ALKALINE",
        XInput.BatteryTypeNimh => "BATTERY_TYPE_NIMH",
        XInput.BatteryTypeUnknown =>
            "BATTERY_TYPE_UNKNOWN — a battery XInput will not name; the level is still taken",
        _ => "battery type not in XInput.h; the level is still taken",
    };

    /// <summary>
    /// The level byte, the band it names, and the stand-in percentage that band sorts and
    /// alerts by — or the reason it was thrown away. All three are worth a line: the number is
    /// the one thing here nothing on screen ever shows, so this is the only place a threshold
    /// that fired, or did not, can be traced back to the byte that decided it.
    /// </summary>
    static string DescribeLevel(byte type, byte level)
    {
        if (level >= BandNames.Length)
            return "level outside BATTERY_LEVEL_EMPTY..FULL, ignored";

        string name = BandNames[level].ToUpperInvariant();

        return Interpret(type, level) is { } band
            ? string.Create(CultureInfo.InvariantCulture,
                $"BATTERY_LEVEL_{name} -> shown as \"{band.Name}\", standing in as {band.Percent}% for sorting and thresholds")
            : $"BATTERY_LEVEL_{name}, ignored: the battery type above rules the reading out";
    }

    /// <summary>
    /// The band a type and level pair amount to, or null where they amount to no reading:
    /// nothing in the slot, a cable, or a level outside the four XInput documents.
    /// </summary>
    static Band? Interpret(byte type, byte level) =>
        type is XInput.BatteryTypeDisconnected or XInput.BatteryTypeWired || level >= BandPercent.Length
            ? null
            : new Band(BandPercent[level], BandNames[level]);

    /// <summary>
    /// Slot 0 is "Gamepad 1" because the pad itself counts from one: the player light on the
    /// controller shows the slot index plus one, which makes the row identifiable by looking at
    /// the hardware — the only identification available when the API supplies no name. The
    /// qualifier is what stops it reading as a product name, and what tells it apart from the
    /// same controller listed under its real name by the Bluetooth provider.
    /// </summary>
    static string SlotName(uint slot) =>
        string.Create(CultureInfo.InvariantCulture, $"Gamepad {slot + 1} (XInput)");

    /// <summary>
    /// Not shaped like a PnP instance id, because it is not one and should not be searched for
    /// as one. The slot is the whole of what XInput knows.
    /// </summary>
    static string SlotId(uint slot) => string.Create(CultureInfo.InvariantCulture, $"XINPUT:{slot}");

    /// <summary>One of XInput's four levels: what it is called, and what it sorts as.</summary>
    readonly record struct Band(int Percent, string Name);
}
