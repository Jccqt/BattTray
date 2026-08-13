using BattTray.Devices;
using BattTray.Tests.Support;

namespace BattTray.Tests;

/// <summary>
/// Combining providers: the order the menu is built in, the tooltip's choice of device, and
/// the rule that one broken provider must not blank out the others.
/// </summary>
public class PeripheralMonitorTests
{
    /// <summary>A provider that answers with whatever the test handed it.</summary>
    sealed class Fake(Transport transport, params Peripheral[] peripherals) : IPeripheralProvider
    {
        public Transport Transport => transport;

        public int Calls { get; private set; }

        public int Invalidations { get; private set; }

        public IReadOnlyList<Peripheral> GetPeripherals()
        {
            Calls++;
            return peripherals;
        }

        public void InvalidateDeviceCache() => Invalidations++;
    }

    /// <summary>A provider that throws, as one whose transport has gone away may.</summary>
    sealed class Broken : IPeripheralProvider
    {
        public Transport Transport => Transport.Usb;

        public IReadOnlyList<Peripheral> GetPeripherals() => throw new InvalidOperationException("radio gone");

        public void InvalidateDeviceCache() => throw new InvalidOperationException("radio gone");
    }

    static PeripheralMonitor Monitor(params IPeripheralProvider[] providers)
    {
        var monitor = new PeripheralMonitor(providers);
        monitor.Refresh();
        return monitor;
    }

    [Fact]
    public void ConnectedDevicesComeBeforeDisconnectedOnesWhateverTheirLevels()
    {
        var monitor = Monitor(new Fake(
            Transport.Bluetooth,
            Device.At(90, id: "cached", connected: false),
            Device.At(95, id: "live")));

        // A live 95% outranks a cached 90%: the cached one is history, and the list is about
        // what needs attention now.
        Assert.Equal(["live", "cached"], monitor.Peripherals.Select(p => p.Id));
    }

    [Fact]
    public void TheMostUrgentBatteryComesFirst()
    {
        var monitor = Monitor(new Fake(
            Transport.Bluetooth,
            Device.At(80, id: "full"),
            Device.At(9, id: "dying"),
            Device.At(40, id: "middling")));

        Assert.Equal(["dying", "middling", "full"], monitor.Peripherals.Select(p => p.Id));
    }

    [Fact]
    public void ADeviceWithNoReadingSortsAfterEveryDeviceWithOne()
    {
        var monitor = Monitor(new Fake(
            Transport.Bluetooth,
            Device.At(null, id: "silent"),
            Device.At(100, id: "full")));

        // Null sorts as int.MaxValue, so "nothing to report" ranks below a device at 100%
        // rather than above one at 0%.
        Assert.Equal(["full", "silent"], monitor.Peripherals.Select(p => p.Id));
    }

    [Fact]
    public void DevicesAtTheSameLevelAreOrderedByNameSoTheListDoesNotShuffle()
    {
        var monitor = Monitor(new Fake(
            Transport.Bluetooth,
            Device.At(50, id: "zeta"),
            Device.At(50, id: "alpha")));

        // Without this the two would swap places whenever the providers happened to answer in
        // a different order, and a menu that reorders itself under the cursor is unusable.
        Assert.Equal(["alpha", "zeta"], monitor.Peripherals.Select(p => p.Id));
    }

    [Fact]
    public void EveryProviderContributes()
    {
        var monitor = Monitor(
            new Fake(Transport.Bluetooth, Device.At(50, id: "headset")),
            new Fake(Transport.Dongle, Device.At(50, id: "gamepad")));

        Assert.Equal(2, monitor.Peripherals.Count);
    }

    [Fact]
    public void AThrowingProviderCostsOnlyItsOwnTransport()
    {
        var monitor = Monitor(new Broken(), new Fake(Transport.Bluetooth, Device.At(50, id: "headset")));

        // The contract says providers must not throw. This is what happens when one does
        // anyway, which is the case that matters: the tray keeps working.
        Assert.Equal(["headset"], monitor.Peripherals.Select(p => p.Id));
    }

    [Fact]
    public void AThrowingProviderDoesNotStopCacheInvalidation()
    {
        var healthy = new Fake(Transport.Bluetooth, Device.At(50));
        var monitor = new PeripheralMonitor(new Broken(), healthy);

        monitor.InvalidateDeviceCache();

        Assert.Equal(1, healthy.Invalidations);
    }

    [Fact]
    public void TheTooltipTakesTheLowestConnectedDevice()
    {
        var monitor = Monitor(new Fake(
            Transport.Bluetooth,
            Device.At(20, id: "mouse"),
            Device.At(5, id: "stale-and-lower", connected: false),
            Device.At(45, id: "headset")));

        // The disconnected 5% is lower and must not win: it is a cached number about a device
        // that is not here.
        Assert.Equal("mouse", monitor.LowestConnected?.Id);
    }

    [Fact]
    public void TheTooltipIgnoresALeftoverReadingFromADeviceThatIsStillHere()
    {
        var monitor = Monitor(new Fake(
            Transport.Bluetooth,
            Device.At(5, id: "pad", stale: true),
            Device.At(40, id: "headset")));

        // Connectedness was doing this job while staleness meant it, which held only for as
        // long as no source could be present and out of date at once. The pad is here and its
        // number is not about now, so it is not the most urgent reading on the machine.
        Assert.Equal("headset", monitor.LowestConnected?.Id);
    }

    [Fact]
    public void TheTooltipIgnoresConnectedDevicesWithNoReading()
    {
        var monitor = Monitor(new Fake(
            Transport.Bluetooth,
            Device.At(null, id: "silent"),
            Device.At(70, id: "headset")));

        Assert.Equal("headset", monitor.LowestConnected?.Id);
    }

    [Fact]
    public void TheTooltipHasNoAnswerWhenNothingIsConnected()
    {
        var monitor = Monitor(new Fake(Transport.Bluetooth, Device.At(50, connected: false)));

        Assert.Null(monitor.LowestConnected);
    }

    [Fact]
    public void CachesAreDroppedOnEveryPollWhenNothingWatchesForDeviceChanges()
    {
        var provider = new Fake(Transport.Bluetooth, Device.At(50));
        var monitor = new PeripheralMonitor(provider) { DeviceChangesAreWatched = false };

        monitor.Refresh();
        monitor.Refresh();

        // The safe default, and the answer that has to hold when registering for notifications
        // failed: a registration that does not take costs speed, never correctness.
        Assert.Equal(2, provider.Invalidations);
    }

    [Fact]
    public void CachesSurvivePollsOnceDeviceChangesAreWatched()
    {
        var provider = new Fake(Transport.Bluetooth, Device.At(50));
        var monitor = new PeripheralMonitor(provider) { DeviceChangesAreWatched = true };

        monitor.Refresh();
        monitor.Refresh();

        Assert.Equal(0, provider.Invalidations);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void NoProvidersIsAnEmptyListRatherThanAFailure()
    {
        var monitor = Monitor();

        Assert.Empty(monitor.Peripherals);
        Assert.Null(monitor.LowestConnected);
    }
}
