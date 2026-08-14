using BattTray.Diagnostics;

namespace BattTray.Tests;

/// <summary>
/// The two pieces of <c>--probe-gatt</c> that are rules rather than reports: reading a 16-bit
/// assigned number out of a UUID, and reading the Battery Level Status characteristic.
/// </summary>
/// <remarks>
/// The sweep itself is untestable for the reason the other two probes are — it reports what is
/// bonded to the machine running it — but these two are not, and one of them cannot be checked
/// any other way. No device bonded here publishes 0x2BED, so its decode has never met the
/// hardware it was written for; a synthetic value built from the GATT Specification Supplement
/// is the only thing standing between a bit shifted one place wrong and a dump that says
/// "CHARGING" about a controller that is flat.
///
/// The values below are hand-built from that layout: flags byte, then a 16-bit power state
/// whose fields are bit 0 battery present, bits 1-2 wired power, bits 3-4 wireless power, bits
/// 5-6 charge state, bits 7-8 charge level, bits 9-11 charging type, bits 12-14 fault.
/// </remarks>
public class GattDecodingTests
{
    static string[] Decode(params byte[] value) => [.. GattProbe.DecodeBatteryLevelStatus(value)];

    static string Field(string[] lines, string label) =>
        lines.Single(line => line.TrimStart().StartsWith(label, StringComparison.Ordinal));

    [Fact]
    public void ChargingIsReadOutOfBitsFiveAndSix()
    {
        // Power state 0x00A1: battery present (bit 0), charge state 1 = charging (bits 5-6, so
        // 0x20), charge level 1 = good (bits 7-8, so 0x80). No optional fields. The state is
        // little-endian, which is why the low byte comes first.
        var lines = Decode(0x00, 0xA1, 0x00);

        Assert.Contains("CHARGING", Field(lines, "charge state"), StringComparison.Ordinal);
        Assert.Contains("good", Field(lines, "charge level"), StringComparison.Ordinal);
        Assert.Contains("yes", Field(lines, "battery present"), StringComparison.Ordinal);
    }

    [Fact]
    public void DischargingActiveIsNotCharging()
    {
        // Charge state 2 in bits 5-6, which is 0x40 — the value a device on battery reports,
        // and the one a shifted decode would most easily turn into "charging".
        var lines = Decode(0x00, 0x41, 0x00);

        string state = Field(lines, "charge state");
        Assert.Contains("discharging (active)", state, StringComparison.Ordinal);
        Assert.DoesNotContain("CHARGING", state, StringComparison.Ordinal);
    }

    [Fact]
    public void AWiredSourceIsReadApartFromAWirelessOne()
    {
        // Bits 1-2 = 1 (wired connected), bits 3-4 = 0 (no wireless source).
        var lines = Decode(0x00, 0x02, 0x00);

        Assert.Contains("yes", Field(lines, "wired power"), StringComparison.Ordinal);
        Assert.Contains("no", Field(lines, "wireless power"), StringComparison.Ordinal);
    }

    [Fact]
    public void ChargingTypeAndFaultComeOutOfTheTopOfTheWord()
    {
        // Bits 9-11 = 1 (constant current), bits 12-14 = 2 (external power source fault).
        var lines = Decode(0x00, 0x00, 0x22);

        Assert.Contains("constant current", Field(lines, "charging type"), StringComparison.Ordinal);
        Assert.Contains("external power source", Field(lines, "charging fault"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionalFieldsAreReadInTheOrderTheFlagsDeclareThem()
    {
        // Flags 0x07: identifier, battery level and additional status all present. The
        // identifier is two bytes and the other two are one each, so an off-by-one here would
        // report the additional status byte as the percentage.
        var lines = Decode(0x07, 0x00, 0x00, 0x34, 0x12, 62, 0x01);

        Assert.Contains("0x1234", Field(lines, "identifier"), StringComparison.Ordinal);
        Assert.Contains("62%", Field(lines, "battery level"), StringComparison.Ordinal);
        Assert.Contains("service required yes", Field(lines, "additional 0x01"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentBatteryLevelIsNotReadOutOfTheNextFieldAlong()
    {
        // Flags 0x00: no optional field is present at all, so nothing may be reported as a
        // percentage however many bytes follow.
        var lines = Decode(0x00, 0x00, 0x00, 55);

        // The flags line names the field to say it is absent, so the check is for a line that
        // reports one rather than for the words anywhere.
        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith("battery level", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("1 byte(s) past what the flags account for", StringComparison.Ordinal));
    }

    [Fact]
    public void AValueThatEndsBeforeAFieldTheFlagsPromisedSaysSo()
    {
        // Flags claim a battery level; the value stops after the power state. Reporting a
        // number here would be inventing one, which is the failure this probe exists to avoid.
        var lines = Decode(0x02, 0x00, 0x00);

        Assert.Contains(lines, line => line.Contains("ends before it", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith("battery level", StringComparison.Ordinal));
    }

    [Fact]
    public void AValueTooShortToHoldTheMandatoryFieldsIsReportedRatherThanDecoded()
    {
        var lines = Decode(0x00, 0x00);

        Assert.Single(lines);
        Assert.Contains("at least 3 bytes", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void BatteryLevelOutsideItsDefinedRangeIsQuotedRatherThanClamped() =>
        // The clamp belongs to the provider. A probe that hid the byte would hide the reason
        // the provider needs one.
        Assert.Contains(
            "outside the 0-100",
            string.Join("\n", GattProbe.DecodeBatteryLevel([200])),
            StringComparison.Ordinal);

    [Fact]
    public void AShortUuidIsItsAssignedNumber() =>
        Assert.Equal<ushort?>(0x180F, GattProbe.ShortForm(new BTH_LE_UUID { IsShortUuid = 1, ShortUuid = 0x180F }));

    [Fact]
    public void AnAssignedNumberSpelledOutAsA128BitUuidIsRecognisedToo() =>
        // Windows hands both forms back, and the Battery Service arriving the long way round
        // must not read as a vendor service nobody has heard of.
        Assert.Equal<ushort?>(0x180F, GattProbe.ShortForm(new BTH_LE_UUID
        {
            IsShortUuid = 0,
            LongUuid = new Guid("0000180f-0000-1000-8000-00805f9b34fb"),
        }));

    [Fact]
    public void AGenuinelyVendorDefinedUuidHasNoAssignedNumber() =>
        // The 8BitDo pad publishes this one. It must stay a 128-bit UUID: pretending it has a
        // short form would put four digits in the dump that mean nothing.
        Assert.Null(GattProbe.ShortForm(new BTH_LE_UUID
        {
            IsShortUuid = 0,
            LongUuid = new Guid("00010203-0405-0607-0809-0a0b0c0d1912"),
        }));
}
