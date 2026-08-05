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

    /// <summary>
    /// The raw evidence behind this provider's readings, for the diagnostics tool. Empty by
    /// default so a transport can be prototyped without one — but a provider whose numbers
    /// cannot be traced back to bytes is a provider nobody can debug, so fill it in before
    /// trusting it.
    /// </summary>
    IReadOnlyList<DiagnosticNode> GetDiagnostics() => [];
}
