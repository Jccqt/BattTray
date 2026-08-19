using System.Text.RegularExpressions;
using BattTray.Devices;
using BattTray.Tests.Support;
using BattTray.Tray;

namespace BattTray.Tests;

/// <summary>
/// The text of a menu row: what it states about the reading, the link, and the build.
/// </summary>
/// <remarks>
/// Static methods on the tray context, so nothing here constructs a window or a tray icon —
/// which is the reason the rendering was pulled out into them in the first place.
/// </remarks>
public class MenuRenderingTests
{
    [Fact]
    public void AConnectedDeviceShowsItsPercentage() =>
        Assert.Equal("dev — 80% · connected (Bluetooth)", TrayApplicationContext.DescribeDevice(Device.At(80)));

    [Fact]
    public void ABandIsRenderedByName() =>
        Assert.Equal(
            "pad — medium · connected (Bluetooth)",
            TrayApplicationContext.DescribeDevice(Device.Band(percent: 60, name: "medium")));

    [Fact]
    public void ADeviceWithNoReadingSaysSoRatherThanShowingABlank()
    {
        // "No battery reported" rather than an empty gap: a connected XInput slot answering
        // WIRED is present, working and silent about charge, and an omitted clause reads as
        // though the reading had been forgotten.
        string row = TrayApplicationContext.DescribeDevice(Device.At(null));

        Assert.Equal("dev — no battery reported · connected (Bluetooth)", row);
    }

    [Fact]
    public void AChargingDeviceSaysCharging() =>
        // The arm no provider has ever reached. Kept as the rendering a charge source would
        // land on, and pinned so it is still correct when one arrives.
        Assert.Equal(
            "dev — 80% · charging (Bluetooth)",
            TrayApplicationContext.DescribeDevice(Device.At(80, charge: ChargeState.Charging)));

    [Fact]
    public void ADongleIsNamedByItsBand() =>
        // Which of the two headsets this is, and why the mouse on the dongle is silent: the
        // question the row could not answer before. Last on the row, being the least urgent of
        // the three things there.
        //
        // Not a [Theory] over the enum, here or below: Transport is internal, and an
        // InlineData of it would make this class's methods less accessible than they are.
        Assert.Equal(
            "dev — 80% · connected (2.4 GHz)",
            TrayApplicationContext.DescribeDevice(Device.At(80, transport: Transport.Dongle)));

    [Fact]
    public void AWiredDeviceSaysUsb() =>
        // No provider reports this today. It is the rendering a wired source would land on,
        // pinned for the same reason the charging arm above is.
        Assert.Equal(
            "dev — 80% · connected (USB)",
            TrayApplicationContext.DescribeDevice(Device.At(80, transport: Transport.Usb)));

    [Fact]
    public void ARadioNobodyWillNameIsStillARadio() =>
        // An XInput pad with a reading. The battery type settles that it is running off its
        // own cells and nothing more, so the row says that much and stops: "(2.4 GHz)" would
        // be wrong for the Xbox pad that reaches XInput over Bluetooth.
        Assert.Equal(
            "pad — medium · connected (wireless)",
            TrayApplicationContext.DescribeDevice(
                Device.Band(percent: 60, name: "medium") with { Transport = Transport.Wireless }));

    [Fact]
    public void ASourceThatWillNotNameTheLinkLeavesItOff()
    {
        // An XInput slot answering WIRED, which is measured to come back for a bus-powered
        // receiver as readily as for a cable. Not even "wireless" is available here, so the
        // row keeps the two facts it has and mentions no link at all.
        string row = TrayApplicationContext.DescribeDevice(Device.At(80, transport: Transport.Unknown));

        Assert.Equal("dev — 80% · connected", row);
    }

    [Fact]
    public void AStaleReadingSaysThatAndNothingElseAboutItsAge()
    {
        // "(stale)" is the whole of what a row says about how old a number is, and it sits
        // with the number rather than with the link, because it is the number that is old: a
        // present device carrying a leftover has nowhere else to put it. The row used to
        // spell the age out beside it — "80% (stale, last seen 5h ago)" — which bought a
        // qualification on a menu that is read at a glance.
        var device = Device.At(80, connected: false, stale: true)
            with { BatteryUpdatedUtc = DateTime.UtcNow.AddHours(-5) };

        string row = TrayApplicationContext.DescribeDevice(device);

        Assert.Equal("dev — 80% (stale) · disconnected (Bluetooth)", row);
        Assert.DoesNotContain("last seen", row);
    }

    [Fact]
    public void AReadingWindowsHasATimestampForIsStillQuotedPlainly()
    {
        // The device the age clause existed for: a connected headset whose level was written
        // when HFP connected and not since. The timestamp is still read and still reaches
        // BatteryUpdatedUtc for the diagnostics dump; the menu spends no words on it.
        var device = Device.At(100) with { BatteryUpdatedUtc = DateTime.UtcNow.AddMinutes(-17) };

        Assert.Equal("dev — 100% · connected (Bluetooth)", TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void ALiveBandWithATimestampSurrendersNeitherNumber()
    {
        // Two numbers the row must not show: the stand-in percentage behind the band, and any
        // rendering of the timestamp.
        var device = Device.Band(percent: 60, name: "medium")
            with { BatteryUpdatedUtc = DateTime.UtcNow.AddHours(-2) };

        string row = TrayApplicationContext.DescribeDevice(device);

        Assert.Equal("pad — medium · connected (Bluetooth)", row);
        Assert.DoesNotContain("60", row);
    }

    [Fact]
    public void ADeviceWithNoReadingIsUnmovedByATimestamp()
    {
        // A timestamp without a level is a node Windows once had a number for. Nothing was
        // read, so the row says only that.
        var device = Device.At(null) with { BatteryUpdatedUtc = DateTime.UtcNow.AddHours(-3) };

        Assert.Equal(
            "dev — no battery reported · connected (Bluetooth)",
            TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void ALiveDeviceCanCarryAnOldReading()
    {
        // The row the derivation ruled out, and the one any charge-correlation work has to be
        // able to render: present and charging over a cable, with a percentage from the last
        // wireless session. Both facts, neither dropped.
        var device = Device.At(87, stale: true, charge: ChargeState.Charging);

        Assert.Equal("dev — 87% (stale) · charging (Bluetooth)", TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void AFreshReadingFromADisconnectedDeviceIsNotMarkedStale()
    {
        // Disconnection is not what makes a number old. A source that reports a level as the
        // link drops is quoted plainly, with no "(stale)" to cast doubt on it.
        var device = Device.At(80, connected: false);

        Assert.Equal("dev — 80% · disconnected (Bluetooth)", TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void ABandIsStillNamedWhenItIsStale() =>
        // The stand-in number must not surface just because the row grew a clause.
        Assert.Equal(
            "pad — medium (stale) · connected (Bluetooth)",
            TrayApplicationContext.DescribeDevice(Device.Band(percent: 60, name: "medium") with { IsStale = true }));

    [Fact]
    public void ADeviceWithNoReadingIsNeverCalledStale() =>
        // Not "no battery reported (stale)": there is no number to have aged.
        Assert.Equal(
            "dev — no battery reported · disconnected (Bluetooth)",
            TrayApplicationContext.DescribeDevice(Device.At(null, connected: false, stale: true)));

    [Fact]
    public void DisconnectedBeatsCharging()
    {
        // Order matters, though for a different reason than the reading's: charging is a claim
        // about a link, and the link is gone. The number keeps its own clause either way.
        var device = Device.At(80, connected: false, stale: true, charge: ChargeState.Charging);

        Assert.Equal("dev — 80% (stale) · disconnected (Bluetooth)", TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void TheFooterRowNamesTheAppAndTheBuildRunning()
    {
        // The row is there so a bug report can name the binary without its author hunting down
        // the exe and opening its properties, and both halves earn their place: a bare "0.1.0"
        // is attached to nothing in a menu whose other rows are device names, and the name
        // alone answers nothing at all.
        string row = TrayApplicationContext.DescribeVersion();

        // Matched by shape rather than against "BattTray 0.", which would have been a test that
        // failed on the day the app reached 1.0 and told whoever fixed it nothing. What it is
        // really guarding is the "unknown" fallback reaching a user's eyes: an app that cannot
        // name its own build states that in the one row whose entire job is naming it.
        Assert.Matches(new Regex(@"^BattTray \d+\.\d+\.\d+"), row);
    }

    [Fact]
    public void TheFooterRowLeavesTheBuildRevisionToTheDump() =>
        // The revision the SDK appends after a '+' is a forty-character hash, and a menu is as
        // wide as its widest row. The exact build is in the dump, which is where anyone who
        // needs the revision is already going.
        Assert.DoesNotContain('+', TrayApplicationContext.DescribeVersion());
}
