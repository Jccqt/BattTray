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

    /// <summary>Lowest battery level among connected devices, for the tray icon.</summary>
    public int? LowestConnectedBattery => Peripherals
        .Where(p => p.IsConnected && p.BatteryPercent is not null)
        .Min(p => p.BatteryPercent);

    public void Refresh()
    {
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
