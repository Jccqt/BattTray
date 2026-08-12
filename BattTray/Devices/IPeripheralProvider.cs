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
    /// Drops whatever this provider is holding about which devices exist, so the next call
    /// enumerates again. Called when Windows reports an arrival or a removal — and on every
    /// poll when nothing is listening for those, so a cache can never outlive a change that
    /// went unreported.
    /// </summary>
    /// <remarks>
    /// Empty by default: a provider cheap enough to enumerate on every call has nothing to
    /// drop, and should not pretend otherwise. Caching is worth the seam for a transport
    /// where enumeration is not the expensive part — opening HID handles and parsing their
    /// report descriptors costs ~105 ms against ~2 ms to list the interfaces — because that
    /// work only has to be redone when the list of devices itself changes. Called on the
    /// same thread as <see cref="GetPeripherals"/>, and must never throw.
    /// </remarks>
    void InvalidateDeviceCache() { }

    /// <summary>
    /// The raw evidence behind this provider's readings, for the diagnostics tool. Empty by
    /// default so a transport can be prototyped without one — but a provider whose numbers
    /// cannot be traced back to bytes is a provider nobody can debug, so fill it in before
    /// trusting it.
    /// </summary>
    IReadOnlyList<DiagnosticNode> GetDiagnostics() => [];
}
