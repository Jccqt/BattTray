using System.Diagnostics;

namespace BattTray.Devices;

/// <summary>
/// Polls every registered provider and exposes the combined, sorted device list.
/// One failing provider must not blank out the others, so faults are swallowed per
/// provider and that transport simply contributes nothing this round.
/// </summary>
internal sealed class PeripheralMonitor(params IPeripheralProvider[] providers)
{
    readonly IPeripheralProvider[] _providers = providers;

    public IReadOnlyList<Peripheral> Peripherals { get; private set; } = [];

    /// <summary>
    /// Whether something is calling <see cref="InvalidateDeviceCache"/> as devices come and
    /// go. False is the safe default and the answer that has to hold when registering for
    /// device-change notifications failed: every poll then drops the caches itself, which is
    /// exactly what the providers did before any of this existed. A registration that does
    /// not take therefore costs speed rather than correctness.
    /// </summary>
    public bool DeviceChangesAreWatched { get; set; }

    /// <summary>
    /// The connected device with the least charge, for the tooltip. The device rather than its
    /// percentage: a device reporting a band has a number only for ordering, so whatever shows
    /// the answer has to reach the device to render it honestly.
    /// </summary>
    public Peripheral? LowestConnected => Peripherals
        .Where(p => p.IsConnected && p.BatteryPercent is not null)
        .MinBy(p => p.BatteryPercent);

    /// <summary>Raw evidence from every provider, for the diagnostics tool.</summary>
    public IReadOnlyList<DiagnosticNode> GetDiagnostics()
    {
        DropCachesUnlessWatched();

        var collected = new List<DiagnosticNode>();

        foreach (var provider in _providers)
        {
            try
            {
                collected.AddRange(provider.GetDiagnostics());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{provider.GetType().Name} diagnostics failed: {ex}");
            }
        }

        return collected;
    }

    /// <summary>
    /// Tells every provider that the set of attached devices has changed, so whatever it
    /// enumerated last time has to be read again. Battery levels are not a device change and
    /// never arrive this way; this is about which devices exist, not what they report.
    /// </summary>
    public void InvalidateDeviceCache()
    {
        foreach (var provider in _providers)
        {
            try
            {
                provider.InvalidateDeviceCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{provider.GetType().Name} cache invalidation failed: {ex}");
            }
        }
    }

    /// <summary>
    /// A provider may only hold an enumeration across calls if something is going to tell it
    /// when that enumeration stopped being true. When nothing will, every way into a provider
    /// starts by saying so — including the dump, which would otherwise be able to describe a
    /// device list a scan had already thrown away.
    /// </summary>
    void DropCachesUnlessWatched()
    {
        if (!DeviceChangesAreWatched)
            InvalidateDeviceCache();
    }

    public void Refresh()
    {
        DropCachesUnlessWatched();

        var collected = new List<Peripheral>();

        foreach (var provider in _providers)
        {
            try
            {
                collected.AddRange(provider.GetPeripherals());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{provider.GetType().Name} failed: {ex}");
            }
        }

        // Live devices first, then the most urgent battery, then a stable alphabetical order.
        collected.Sort(static (a, b) =>
        {
            int byConnection = b.IsConnected.CompareTo(a.IsConnected);
            if (byConnection != 0)
                return byConnection;

            int byBattery = (a.BatteryPercent ?? int.MaxValue).CompareTo(b.BatteryPercent ?? int.MaxValue);
            return byBattery != 0 ? byBattery : string.CompareOrdinal(a.Name, b.Name);
        });

        Peripherals = collected;
    }
}
