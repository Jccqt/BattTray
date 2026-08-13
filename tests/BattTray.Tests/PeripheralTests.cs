using BattTray.Devices;
using BattTray.Tests.Support;

namespace BattTray.Tests;

/// <summary>
/// The two derived properties every renderer goes through: whether a reading is a leftover,
/// and how a reading may be spelled.
/// </summary>
public class PeripheralTests
{
    [Fact]
    public void ADisconnectedDeviceWithAReadingIsStale()
    {
        // Windows keeps the last known percentage after a disconnect. The value is real; what
        // it is about is over.
        Assert.True(Device.At(80, connected: false).IsStale);
    }

    [Fact]
    public void AConnectedDeviceIsNeverStale()
    {
        Assert.False(Device.At(80).IsStale);
    }

    [Fact]
    public void ADisconnectedDeviceWithNoReadingIsNotStale()
    {
        // Nothing cached, so nothing to be stale. The row says "disconnected" with no age.
        Assert.False(Device.At(null, connected: false).IsStale);
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
