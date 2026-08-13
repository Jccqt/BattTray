namespace BattTray.Devices;

/// <summary>How the peripheral is attached to the PC.</summary>
/// <remarks>
/// Named on the menu row, which makes this a claim about one device rather than a filing
/// label for the list it sits in. A source that cannot tell a cable from a receiver says
/// <see cref="Unknown"/> and its rows stay quiet about the link, rather than picking whichever
/// of the two is wrong less often. <see cref="IPeripheralProvider.Transport"/> is the other
/// thing and keeps its answer: the transport a provider is about.
///
/// The values are therefore graded by how much a source can support, not by how much a reader
/// would like: <see cref="Wireless"/> sits between the two named radios and <see cref="Unknown"/>
/// for a source that has established there is no cable and cannot say which radio replaced it.
/// </remarks>
internal enum Transport
{
    /// <summary>The source knows the device is there and not how it got here.</summary>
    Unknown,
    Bluetooth,
    Usb,
    Dongle,

    /// <summary>
    /// On a radio, and which radio is not known. Weaker than <see cref="Dongle"/> and
    /// <see cref="Bluetooth"/> on purpose: XInput settles that a pad is running off its own
    /// battery without ever naming what it is talking over. See XInputGamepadProvider.
    /// </summary>
    Wireless,
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

    readonly bool _isStale;

    /// <summary>
    /// True when <see cref="BatteryPercent"/> is a leftover from an earlier session rather
    /// than a reading about now. Stated by the provider, which is the only code that knows
    /// whether its source remembers readings across a disconnect.
    /// </summary>
    /// <remarks>
    /// This was derived as <c>!IsConnected &amp;&amp; BatteryPercent is not null</c>, and on
    /// every source written so far the two do coincide — but they coincide because of a
    /// hardware limitation rather than because of a rule, and folding them together made the
    /// interesting case inexpressible. A pad present over USB carrying a percentage from its
    /// last Bluetooth session is connected and stale at once; its row wants to read
    /// "87% (stale) · charging", which the derivation ruled out before any of the correlation
    /// work could start. Connectedness is about the link, staleness is about the number, and
    /// nothing but a provider is in a position to say the second.
    ///
    /// A device with no reading is never stale, whatever a provider sets: there is nothing
    /// cached to have aged, and "no battery reported (stale)" would be claiming an age for a
    /// number that was never taken.
    /// </remarks>
    public bool IsStale
    {
        get => _isStale && BatteryPercent is not null;
        init => _isStale = value;
    }

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
