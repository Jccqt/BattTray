using System.Text.RegularExpressions;
using BattTray.Devices;
using BattTray.Tests.Support;
using BattTray.Tray;

namespace BattTray.Tests;

/// <summary>
/// The text of a menu row, and the reading age inside it.
/// </summary>
/// <remarks>
/// Static methods on the tray context, so nothing here constructs a window or a tray icon —
/// which is the reason the rendering was pulled out into them in the first place.
/// </remarks>
public class MenuRenderingTests
{
    [Fact]
    public void AConnectedDeviceShowsItsPercentage() =>
        Assert.Equal("dev — 80% · connected", TrayApplicationContext.DescribeDevice(Device.At(80)));

    [Fact]
    public void ABandIsRenderedByName() =>
        Assert.Equal(
            "pad — medium · connected",
            TrayApplicationContext.DescribeDevice(Device.Band(percent: 60, name: "medium")));

    [Fact]
    public void ADeviceWithNoReadingSaysSoRatherThanShowingABlank()
    {
        // "No battery reported" rather than an empty gap: a connected XInput slot on USB is
        // present, working and silent about charge, and an omitted clause reads as though the
        // reading had been forgotten.
        string row = TrayApplicationContext.DescribeDevice(Device.At(null));

        Assert.Equal("dev — no battery reported · connected", row);
    }

    [Fact]
    public void AChargingDeviceSaysCharging() =>
        // The arm no provider has ever reached. Kept as the rendering a charge source would
        // land on, and pinned so it is still correct when one arrives.
        Assert.Equal(
            "dev — 80% · charging",
            TrayApplicationContext.DescribeDevice(Device.At(80, charge: ChargeState.Charging)));

    [Fact]
    public void AStaleReadingCarriesItsAge()
    {
        var device = Device.At(80, connected: false, stale: true)
            with { BatteryUpdatedUtc = DateTime.UtcNow.AddHours(-5) };

        // The age sits with the number rather than with the link, because it is the number's
        // age. The clause used to hang off "disconnected", which is why a present device with
        // an old reading had nowhere to put it.
        Assert.Equal(
            "dev — 80% (stale, last seen 5h ago) · disconnected",
            TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void AStaleReadingWithNoTimestampSaysOnlyThatItIsStale()
    {
        // Windows has no timestamp for some nodes. Better "(stale)" alone than an age the app
        // does not have.
        var device = Device.At(80, connected: false, stale: true);

        Assert.Equal("dev — 80% (stale) · disconnected", TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void ALiveDeviceCanCarryAnOldReading()
    {
        // The row the derivation ruled out, and the one any charge-correlation work has to be
        // able to render: present and charging over a cable, with a percentage from the last
        // wireless session. Both facts, neither dropped.
        var device = Device.At(87, stale: true, charge: ChargeState.Charging);

        Assert.Equal("dev — 87% (stale) · charging", TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void AFreshReadingFromADisconnectedDeviceIsNotMarkedStale()
    {
        // Disconnection is not what makes a number old. A source that reports a level as the
        // link drops is quoted plainly; only the age clause is missing, because there is none.
        var device = Device.At(80, connected: false);

        Assert.Equal("dev — 80% · disconnected", TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void ABandIsStillNamedWhenItIsStale() =>
        // The stand-in number must not surface just because the row grew a clause.
        Assert.Equal(
            "pad — medium (stale) · connected",
            TrayApplicationContext.DescribeDevice(Device.Band(percent: 60, name: "medium") with { IsStale = true }));

    [Fact]
    public void ADeviceWithNoReadingIsNeverCalledStale() =>
        // Not "no battery reported (stale)": there is no number to have aged.
        Assert.Equal(
            "dev — no battery reported · disconnected",
            TrayApplicationContext.DescribeDevice(Device.At(null, connected: false, stale: true)));

    [Fact]
    public void DisconnectedBeatsCharging()
    {
        // Order matters, though for a different reason than the reading's: charging is a claim
        // about a link, and the link is gone. The number keeps its own clause either way.
        var device = Device.At(80, connected: false, stale: true, charge: ChargeState.Charging);

        Assert.Equal("dev — 80% (stale) · disconnected", TrayApplicationContext.DescribeDevice(device));
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

    [Theory]
    [InlineData(0, ", last seen just now")]
    [InlineData(30, ", last seen just now")]
    [InlineData(55, ", last seen just now")]
    [InlineData(60, ", last seen 1m ago")]
    [InlineData(90, ", last seen 1m ago")]
    [InlineData(3_570, ", last seen 59m ago")]
    [InlineData(3_600, ", last seen 1h ago")]
    [InlineData(86_340, ", last seen 23h ago")]
    [InlineData(86_400, ", last seen 1d ago")]
    [InlineData(4 * 86_400, ", last seen 4d ago")]
    public void AgeIsRenderedInTheLargestUnitThatFits(int secondsAgo, string expected)
    {
        // Each boundary is here because the switch is a chain of exclusive upper bounds, and
        // an off-by-one at any of them produces "60m ago" or "24h ago" — legible, but the
        // kind of thing that makes a reader wonder whether the app can count.
        //
        // The ages are read against the wall clock, so every case is the stated age plus
        // however long the test took to reach the assertion. That only ever rounds upward,
        // which is why the boundaries themselves are safe and the cases just below one are
        // held back by half a unit rather than by a second.
        var updated = DateTime.UtcNow.AddSeconds(-secondsAgo);

        Assert.Equal(expected, TrayApplicationContext.FormatAge(updated));
    }

    [Fact]
    public void NoTimestampMeansNoAgeClause() =>
        // A device Windows has no timestamp for. Better a row that says only "disconnected"
        // than one claiming a reading age it does not have.
        Assert.Equal(string.Empty, TrayApplicationContext.FormatAge(null));

    [Fact]
    public void AFutureTimestampIsNotRenderedAsANegativeAge() =>
        // Clock changes and time-zone slips do produce these. "-3h ago" would look like a bug
        // in the app rather than in the clock, so the clause is dropped instead.
        Assert.Equal(string.Empty, TrayApplicationContext.FormatAge(DateTime.UtcNow.AddHours(1)));
}
