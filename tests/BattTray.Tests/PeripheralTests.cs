using BattTray.Devices;
using BattTray.Tests.Support;

namespace BattTray.Tests;

/// <summary>
/// What the record answers about a reading: whether it is a leftover, and how it may be
/// spelled.
/// </summary>
public class PeripheralTests
{
    [Fact]
    public void AStaleReadingSurvivesOnAConnectedDevice()
    {
        // The state the derivation could not express, and the reason it went: a device
        // present over one transport carrying a number from its last session on another.
        var device = Device.At(87, connected: true, stale: true);

        Assert.True(device.IsStale);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void DisconnectionAloneDoesNotMakeAReadingStale()
    {
        // Connectedness is about the link and staleness is about the number. That the
        // Bluetooth provider sets both together is that source's behaviour, not the record's
        // rule — a source that hands over a fresh reading as it goes says so and is believed.
        Assert.False(Device.At(80, connected: false).IsStale);
    }

    [Fact]
    public void AConnectedDeviceIsFreshUnlessAProviderSaysOtherwise()
    {
        // The safe default of the two: a provider that forgets to mention staleness claims
        // its readings are current, which is true of every source that keeps no memory.
        Assert.False(Device.At(80).IsStale);
    }

    [Fact]
    public void ADeviceWithNoReadingIsNotStaleEvenWhenItsProviderSaysSo()
    {
        // Nothing cached, so nothing to have aged. The row says "no battery reported" with no
        // age beside it rather than claiming one for a number that was never taken.
        Assert.False(Device.At(null, connected: false, stale: true).IsStale);
        Assert.False(Device.At(null, stale: true).IsStale);
    }

    [Fact]
    public void StalenessSurvivesACopy()
    {
        // IsStale is stored in a field behind the property so the reading-less case can be
        // guarded, and `with` has to carry that field the way it carries every other value.
        var device = Device.At(87, stale: true) with { Name = "renamed" };

        Assert.True(device.IsStale);
    }

    [Fact]
    public void APercentageIsShownAsAPercentage()
    {
        Assert.Equal("80%", Device.At(80).BatteryText);
    }

    [Fact]
    public void ABandIsShownAsItsNameAndNeverAsItsStandInNumber()
    {
        var pad = Device.Band(percent: 60, name: "medium");

        // This is the guarantee the whole BatteryBand mechanism exists for: XInput reports
        // four levels, and rendering one of them as "60%" invents two digits the device never
        // claimed.
        Assert.Equal("medium", pad.BatteryText);
        Assert.DoesNotContain("60", pad.BatteryText!, StringComparison.Ordinal);
        Assert.DoesNotContain("%", pad.BatteryText!, StringComparison.Ordinal);
    }

    [Fact]
    public void NoReadingRendersAsNothingRatherThanZero()
    {
        // Null, not "0%": a device that publishes no level is silent, not empty, and the two
        // read very differently to someone deciding whether to go and find a cable.
        Assert.Null(Device.At(null).BatteryText);
    }

    [Fact]
    public void ANumericReadingStillSortsWhenItIsABand()
    {
        // The band names the reading and the percentage orders it. Both have to be there, or
        // a coarse device cannot take part in sorting or in the low-battery threshold.
        var pad = Device.Band(percent: 5, name: "empty");

        Assert.Equal(5, pad.BatteryPercent);
        Assert.Equal("empty", pad.BatteryText);
    }
}
