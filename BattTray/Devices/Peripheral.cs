namespace BattTray.Devices;

/// <summary>How the peripheral is attached to the PC.</summary>
internal enum Transport
{
    Bluetooth,
    Usb,
    Dongle,
}

/// <summary>Broad device category, used to pick a glyph and to sort the list.</summary>
internal enum DeviceCategory
{
    Unknown,
    Headset,
    Mouse,
    Keyboard,
    Gamepad,
    Pen,
    Phone,
}

/// <summary>
/// Charge state is tri-state on purpose: most Windows battery sources report a level
/// without saying whether the device is charging, and "not charging" is a different
/// claim from "we don't know".
/// </summary>
/// <remarks>
/// <see cref="Charging"/> is set by nothing. Bluetooth cannot: a full property dump of a
/// battery-reporting node showed no charging flag anywhere, and "not charging" would be a
/// guess. XInput looked as though it could, through BATTERY_TYPE_WIRED — but that byte comes
/// back for a 2.4 GHz receiver as readily as for a cable, measured with the cable out, so it
/// carries no charge claim either. <see cref="Discharging"/> is the only value a provider
/// sets, and only on a slot that names a battery type, which no hardware to hand has done.
/// See XInputGamepadProvider for both measurements.
/// </remarks>
internal enum ChargeState
{
    Unknown,
    Discharging,
    Charging,
}

/// <summary>A single battery-reporting peripheral, as shown in the tray menu.</summary>
internal sealed record Peripheral
{
    /// <summary>Stable identity across refreshes, unique per transport.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required Transport Transport { get; init; }

    public DeviceCategory Category { get; init; } = DeviceCategory.Unknown;

    /// <summary>Battery percentage 0-100, or null when the device never reported one.</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>
    /// The name of the band <see cref="BatteryPercent"/> stands in for, when the source
    /// reports a handful of steps rather than a percentage — "low", "medium" — or null when
    /// the percentage means what it says.
    /// </summary>
    /// <remarks>
    /// Two things need the reading and they need it in different forms. Sorting and the
    /// low-battery threshold are numeric and always will be, so a coarse source still has to
    /// supply a number. Anything the user reads must not show that number: XInput reports
    /// four levels, and rendering one of them as "20%" invents two digits of precision the
    /// device never claimed. Setting this is a provider promising the percentage is a
    /// stand-in, and every renderer prefers <see cref="BatteryText"/> because of it.
    ///
    /// The 10-step scale some Bluetooth headsets report is deliberately not marked this way.
    /// It is coarse too — which is why the alert thresholds are restricted to multiples of
    /// ten — but its buckets are already spelled as percentages by the device, so showing the
    /// number is repeating what was said rather than inventing anything.
    /// </remarks>
    public string? BatteryBand { get; init; }

    public ChargeState ChargeState { get; init; } = ChargeState.Unknown;

    public bool IsConnected { get; init; }

    /// <summary>When the battery reading was last refreshed by Windows, if known.</summary>
    public DateTime? BatteryUpdatedUtc { get; init; }

    /// <summary>
    /// True when the reading is a leftover from an earlier session rather than live data:
    /// Windows keeps the last known percentage after a device disconnects.
    /// </summary>
    public bool IsStale => !IsConnected && BatteryPercent is not null;

    /// <summary>
    /// The charge as it may be shown to someone: the band name where the reading is one, the
    /// percentage where it is a percentage, null where there is no reading at all. Every
    /// display goes through this rather than reading <see cref="BatteryPercent"/> directly,
    /// so a stand-in number cannot leak into the menu, the tooltip or a balloon by being
    /// formatted somewhere that had not heard of bands.
    /// </summary>
    public string? BatteryText =>
        BatteryBand ?? (BatteryPercent is { } percent ? $"{percent}%" : null);
}
