using BattTray.Devices;

namespace BattTray.Tests.Support;

/// <summary>
/// Builders for the two shapes of peripheral the rules under test care about.
/// </summary>
/// <remarks>
/// <see cref="Peripheral"/> is a record with required members, so every test would otherwise
/// open with six lines of object initializer in which the one value that matters — the
/// percentage — is indistinguishable from the five that do not. These name the interesting
/// argument and default the rest to the ordinary case: connected, discharging, a real
/// percentage rather than a band.
/// </remarks>
static class Device
{
    /// <summary>A device reporting a true percentage, connected unless told otherwise.</summary>
    public static Peripheral At(
        int? percent,
        string id = "dev",
        bool connected = true,
        ChargeState charge = ChargeState.Unknown) =>
        new()
        {
            Id = id,
            Name = id,
            Transport = Transport.Bluetooth,
            BatteryPercent = percent,
            IsConnected = connected,
            ChargeState = charge,
        };

    /// <summary>
    /// A device whose provider reports steps rather than percentages, so the number is a
    /// stand-in and nothing may render it. See <see cref="Peripheral.BatteryBand"/>.
    /// </summary>
    public static Peripheral Band(int percent, string name, string id = "pad") =>
        At(percent, id) with { BatteryBand = name };
}
