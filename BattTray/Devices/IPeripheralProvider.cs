namespace BattTray.Devices;

/// <summary>
/// A source of battery-reporting peripherals for one transport. Implementations are
/// polled from the UI thread and must stay fast (single-digit milliseconds) and never
/// throw; a provider that cannot answer returns an empty list.
/// </summary>
internal interface IPeripheralProvider
{
    /// <summary>Transport this provider covers, shown when grouping the tray menu.</summary>
    Transport Transport { get; }

    IReadOnlyList<Peripheral> GetPeripherals();
}
