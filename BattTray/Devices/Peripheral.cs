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

    public ChargeState ChargeState { get; init; } = ChargeState.Unknown;

    public bool IsConnected { get; init; }

    /// <summary>When the battery reading was last refreshed by Windows, if known.</summary>
    public DateTime? BatteryUpdatedUtc { get; init; }

    /// <summary>
    /// True when the reading is a leftover from an earlier session rather than live data:
    /// Windows keeps the last known percentage after a device disconnects.
    /// </summary>
    public bool IsStale => !IsConnected && BatteryPercent is not null;
}
