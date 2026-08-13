using System.Globalization;
using BattTray.Interop;

namespace BattTray.Devices;

/// <summary>
/// Reports battery for wireless controllers Windows exposes through XInput.
/// </summary>
/// <remarks>
/// What this covers is narrower than "gamepads", and the narrowing is the point:
/// <list type="bullet">
/// <item>A slot reporting BATTERY_TYPE_WIRED means "there is no battery to ask about". The
/// BATTERY_LEVEL byte beside it still reads FULL, because the field has to hold something;
/// taking it would put a confident 100% in the tray for a device that never said so. Such a
/// slot therefore contributes a peripheral with no reading at all — the controller is there,
/// and nothing about its battery is known.</item>
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
///
/// WIRED is not a cable, and this was measured rather than reasoned. It reads as one — the
/// name says so, and it is tempting to spend it on <see cref="ChargeState.Charging"/>, which
/// is exactly what an earlier revision of this file did. On the 8BitDo Ultimate 2C with the
/// cable physically out and the pad on its own 2.4 GHz receiver, slot 0 still answered
/// BATTERY_TYPE_WIRED: same VID_2DC8, same PID_310A, same &amp;IG_00 interface the cable
/// produced, because the receiver is itself a bus-powered USB device. Nothing in the answer
/// separates a pad on a cable from a pad on a dongle, so a row saying either would be wrong
/// half the time. The byte supports one statement — XInput has no battery for this device —
/// and that is all the peripheral claims: no reading, and <see cref="ChargeState.Unknown"/>.
///
/// <see cref="ChargeState.Discharging"/> is the one charge state anything here sets. A slot
/// answering ALKALINE, NIMH or UNKNOWN is running off a battery, since a USB-attached device
/// would have come back WIRED as above: that is a reading of the answer rather than an
/// inference from it. It has never been observed. Every occupied slot seen on the development
/// machine — wired, and on the receiver — reported WIRED, so this arm, the bands it carries
/// and the percentages behind them are all supported by XInput.h and by nothing that has
/// actually been plugged in. That is a fact about the hardware to hand rather than about the
/// approach, but it is worth knowing before trusting any of it.
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
/// A pad with a reading is reported as <see cref="Transport.Dongle"/>. XInput does not say
/// which radio it is talking over, and the Bluetooth case above is genuinely filed under the
/// wrong one — but that pad is already in the menu under its real transport from the other
/// provider, and every reading only reachable here arrives over a dongle. The wired row is
/// the exception and needs no guessing: WIRED is the one attachment XInput names outright, so
/// that row is <see cref="Transport.Usb"/>. The provider-level
/// <see cref="IPeripheralProvider.Transport"/> stays Dongle, being the transport this
/// provider is about rather than a claim over each row it produces.
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
    internal static readonly int[] BandPercent = [5, 20, 60, 100];

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

            if (Interpret(reading.Type, reading.Level) is not { } verdict)
                continue;

            results.Add(new Peripheral
            {
                Id = SlotId(slot),
                Name = SlotName(slot),
                Transport = verdict.Transport,
                Category = DeviceCategory.Gamepad,

                // Both null on a USB-attached slot, which is a peripheral with no reading at
                // all — the case every renderer already had to handle for a device that
                // publishes no battery. See Peripheral.BatteryText.
                BatteryPercent = verdict.Band?.Percent,
                BatteryBand = verdict.Band?.Name,
                ChargeState = verdict.Charge,

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
            "BATTERY_TYPE_WIRED — USB-attached, so there is no battery to report and the level "
            + "below means nothing; listed with no reading. Not read as a cable: a 2.4 GHz "
            + "receiver returns this too, measured with the cable out",
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

        return Interpret(type, level) is { Band: { } band }
            ? string.Create(CultureInfo.InvariantCulture,
                $"BATTERY_LEVEL_{name} -> shown as \"{band.Name}\", standing in as {band.Percent}% for sorting and thresholds")
            : $"BATTERY_LEVEL_{name}, ignored: the battery type above rules the reading out";
    }

    /// <summary>
    /// What a type and level pair amount to, or null where they amount to nothing worth
    /// listing: nothing in the slot, or a level outside the four XInput documents.
    /// </summary>
    static Verdict? Interpret(byte type, byte level) => type switch
    {
        XInput.BatteryTypeDisconnected => null,

        // The level byte is thrown away here and only here. It reads FULL because the field
        // has to hold something, and there is no battery it is about. Unknown rather than
        // Charging: this byte is returned for a receiver as readily as for a cable, so it
        // cannot support a claim about charge. Usb because both of those are USB
        // attachments, which is the part it does settle.
        XInput.BatteryTypeWired => new Verdict(null, ChargeState.Unknown, Transport.Usb),

        // A named battery type, or one XInput will not name: either way the pad is running
        // off it, since anything on USB would have been reported as WIRED above.
        _ => level < BandPercent.Length
            ? new Verdict(new Band(BandPercent[level], BandNames[level]), ChargeState.Discharging, Transport.Dongle)
            : null,
    };

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

    /// <summary>
    /// Everything a slot's two bytes settle: the reading if there is one, how the pad is
    /// powered, and how it is attached. A null <paramref name="Band"/> is a cable, which is
    /// the one case here that produces a peripheral with nothing to report.
    /// </summary>
    readonly record struct Verdict(Band? Band, ChargeState Charge, Transport Transport);
}
