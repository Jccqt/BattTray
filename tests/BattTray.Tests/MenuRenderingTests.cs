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
    public void ADisconnectedDeviceCarriesTheAgeOfItsCachedReading()
    {
        var device = Device.At(80, connected: false) with { BatteryUpdatedUtc = DateTime.UtcNow.AddHours(-5) };

        Assert.Equal("dev — 80% · disconnected, last seen 5h ago", TrayApplicationContext.DescribeDevice(device));
    }

    [Fact]
    public void DisconnectedBeatsCharging()
    {
        // Order matters: a cached reading cannot be evidence of charging now, whatever the
        // last poll saw.
        var device = Device.At(80, connected: false, charge: ChargeState.Charging);

        Assert.Equal("dev — 80% · disconnected", TrayApplicationContext.DescribeDevice(device));
    }

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
