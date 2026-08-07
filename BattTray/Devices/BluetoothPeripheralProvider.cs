using System.Globalization;
using System.Text.RegularExpressions;
using BattTray.Interop;

namespace BattTray.Devices;

/// <summary>
/// Reports battery for paired Bluetooth peripherals.
/// </summary>
/// <remarks>
/// Windows splits the information we need across two places, so this joins them by radio
/// address:
/// <list type="bullet">
/// <item>bluetoothapis.dll knows the product name and whether the device is connected,
/// but nothing about battery.</item>
/// <item>The PnP device tree carries the battery percentage, but only on a profile child
/// node whose name is an implementation detail ("... Hands-Free AG").</item>
/// </list>
/// Charging state is deliberately left <see cref="ChargeState.Unknown"/>: Bluetooth
/// Classic exposes no charging property at all, so claiming "discharging" would be a
/// guess. BLE devices implementing the newer Battery Level Status characteristic could
/// report it, but that requires holding a GATT connection open.
/// </remarks>
internal sealed partial class BluetoothPeripheralProvider : IPeripheralProvider
{
    /// <summary>PnP enumerators that host Bluetooth device nodes.</summary>
    static readonly string[] Enumerators = ["BTHENUM", "BTHLE", "BTHLEDevice"];

    /// <summary>
    /// Service classes only a phone hosts, as they appear at the head of a device instance
    /// id. Class-of-device would be the obvious way to spot a phone, but Windows reports
    /// 0x000000 for it on iPhone pairings and on anything bonded through the LE path, so the
    /// services the device publishes are the dependable signal. The opening brace is part of
    /// each prefix so it can only ever match the service GUID, never a hex run further along
    /// the id.
    /// </summary>
    static readonly string[] PhoneServiceClasses =
    [
        "{0000111F", // Hands-Free Audio Gateway: the phone half of HFP
        "{00001112", // Headset Audio Gateway: the phone half of HSP
        "{0000112F", // Phonebook Access, server side
        "{00001132", // Message Access Server
        "{7905F431", // Apple Notification Center Service
        "{89D3502B", // Apple Media Service
    ];

    /// <summary>
    /// The two BDIF_* bits from bthdef.h that mean "the radio link is up right now", one per
    /// transport. Everything else a device node offers describes the bond instead: a bonded LE
    /// device keeps <c>DEVPKEY_Device_IsPresent</c> true while switched off, and Windows even
    /// marks those containers <c>AlwaysShowDeviceAsConnected</c>. Both bits were watched
    /// flipping in real time as a gamepad connected and dropped.
    /// </summary>
    const uint BdifConnected = 0x00000020;
    const uint BdifLeConnected = 0x01000000;

    public Transport Transport => Transport.Bluetooth;

    public IReadOnlyList<Peripheral> GetPeripherals()
    {
        var batteries = ReadBatteryReadings();
        var results = new List<Peripheral>();
        var pairedDevices = BluetoothApi.GetPairedDevices();
        var phoneAddresses = ReadPhoneAddresses();

        // Phones often expose a battery profile, but they are not PC peripherals. Keep
        // their names too: iOS may rotate the LE address, leaving an old PnP node that
        // cannot be joined to the current pairing record by address alone.
        var phoneNames = pairedDevices
            .Where(device => IsPhone(device, phoneAddresses))
            .Select(device => CleanNodeName(device.Name))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var device in pairedDevices)
        {
            batteries.Remove(device.Address, out var reading);

            // BattTray deliberately tracks PC peripherals only. Remove the associated
            // battery entry before continuing so a phone cannot reappear in the
            // unmatched-node fallback below.
            if (IsPhone(device, phoneAddresses))
                continue;

            // A paired device with neither a battery reading nor a live connection is
            // almost always a leftover pairing; keep it out of the menu.
            if (reading is null && !device.IsConnected)
                continue;

            results.Add(new Peripheral
            {
                Id = FormatAddress(device.Address),
                Name = string.IsNullOrWhiteSpace(device.Name) ? FormatAddress(device.Address) : device.Name,
                Transport = Transport.Bluetooth,
                Category = Categorize(device.ClassOfDevice),
                BatteryPercent = reading?.Percent,
                BatteryUpdatedUtc = reading?.UpdatedUtc,
                IsConnected = device.IsConnected,
            });
        }

        // Battery-reporting nodes with no matching pairing record: typically BLE devices
        // bonded through the LE-only path. Fall back to the node's own name.
        foreach (var (address, reading) in batteries)
        {
            // A phone can use a different, private LE address after reconnecting, so its
            // battery node has no pairing record to be matched against. Identify it by the
            // services that node publishes, and failing that by the friendly name, which
            // survives the address change — either way, no duplicate phone entry.
            if (phoneAddresses.Contains(address)
                || (reading.NodeName is not null && phoneNames.Contains(reading.NodeName)))
            {
                continue;
            }

            results.Add(new Peripheral
            {
                Id = FormatAddress(address),
                Name = reading.NodeName ?? FormatAddress(address),
                Transport = Transport.Bluetooth,
                BatteryPercent = reading.Percent,
                BatteryUpdatedUtc = reading.UpdatedUtc,
                IsConnected = reading.IsConnected,
            });
        }

        return results;
    }

    /// <summary>
    /// Dumps both halves of the join this provider performs: the pairing records that supply
    /// identity and connection, and the PnP nodes that supply battery. A device missing from
    /// the menu is nearly always present in exactly one of these two lists.
    /// </summary>
    public IReadOnlyList<DiagnosticNode> GetDiagnostics()
    {
        var nodes = new List<DiagnosticNode>();
        var phoneAddresses = ReadPhoneAddresses();

        foreach (var device in BluetoothApi.GetPairedDevices())
        {
            nodes.Add(new DiagnosticNode(
                Transport.Bluetooth,
                $"paired: {device.Name}",
                FormatAddress(device.Address),
                [
                    new DiagnosticProperty(
                        "class of device", "BLUETOOTH_DEVICE_INFO.ulClassofDevice",
                        $"0x{device.ClassOfDevice:X6}", Categorize(device.ClassOfDevice).ToString()),
                    new DiagnosticProperty(
                        "connected", "BLUETOOTH_DEVICE_INFO.fConnected",
                        device.IsConnected ? "TRUE" : "FALSE", null),
                    // Without this line a phone-filtered device would simply be absent from
                    // the menu with nothing here explaining why.
                    new DiagnosticProperty(
                        "phone services", "phone-only service GUIDs on this device's PnP nodes",
                        phoneAddresses.Contains(device.Address) ? "present" : "absent",
                        IsPhone(device, phoneAddresses) ? "treated as a phone, excluded" : null),
                ]));
        }

        foreach (var enumerator in Enumerators)
        {
            foreach (var deviceId in ConfigManager.GetDeviceIds(enumerator))
            {
                uint devInst = ConfigManager.LocateDevNode(deviceId);

                // Only nodes carrying a battery byte are worth printing; the rest of the
                // Bluetooth tree is profile plumbing this provider never looks at.
                if (devInst == 0 || ConfigManager.GetByte(devInst, DevPropKeys.BluetoothBattery) is null)
                    continue;

                nodes.Add(new DiagnosticNode(
                    Transport.Bluetooth,
                    $"node: {ConfigManager.GetString(devInst, DevPropKeys.FriendlyName) ?? "(unnamed)"}",
                    deviceId,
                    [
                        Describe(devInst, "battery level", "{104ea319-...bbe5} PID 2", DevPropKeys.BluetoothBattery),
                        Describe(devInst, "battery updated", "{104ea319-...bbe5} PID 7", DevPropKeys.BluetoothBatteryLastUpdated),
                        Describe(devInst, "radio address", "DEVPKEY_Bluetooth_DeviceAddress", DevPropKeys.BluetoothDeviceAddress),
                        Describe(devInst, "is present", "DEVPKEY_Device_IsPresent", DevPropKeys.IsPresent),
                        DescribeLink(devInst),
                        Describe(devInst, "friendly name", "DEVPKEY_Device_FriendlyName", DevPropKeys.FriendlyName),
                    ]));
            }
        }

        return nodes;
    }

    /// <summary>
    /// The BDIF_* flag word with the connection verdict drawn from it. Printed next to
    /// "is present" on purpose: when those two disagree, the flags are the truthful one.
    /// </summary>
    static DiagnosticProperty DescribeLink(uint devInst)
    {
        if (ConfigManager.GetUInt32(devInst, DevPropKeys.BluetoothDeviceFlags) is not { } flags)
            return new DiagnosticProperty("device flags", "DEVPKEY_Bluetooth_DeviceFlags", "(absent)", null);

        return new DiagnosticProperty(
            "device flags", "DEVPKEY_Bluetooth_DeviceFlags", $"UINT32 [0x{flags:X8}]",
            IsLinkUp(flags)
                ? "BDIF_CONNECTED or BDIF_LE_CONNECTED set -> connected"
                : "connected bits clear -> disconnected, so any battery value here is cached");
    }

    /// <summary>Reads one property both ways: the bytes on the wire and this app's reading of them.</summary>
    static DiagnosticProperty Describe(uint devInst, string name, string key, DevPropKey propertyKey)
    {
        if (ConfigManager.GetRaw(devInst, propertyKey) is not { } property)
            return new DiagnosticProperty(name, key, "(absent)", null);

        string raw = $"{DevPropType.Describe(property.Type)} [{Convert.ToHexString(property.Bytes)}]";

        string? decoded = property.Type switch
        {
            DevPropType.Byte => ConfigManager.GetByte(devInst, propertyKey)?.ToString(CultureInfo.InvariantCulture),
            DevPropType.Boolean => ConfigManager.GetBoolean(devInst, propertyKey)?.ToString(),
            DevPropType.String => ConfigManager.GetString(devInst, propertyKey),
            DevPropType.FileTime => ConfigManager.GetFileTimeUtc(devInst, propertyKey)?.ToString("u", CultureInfo.InvariantCulture),
            _ => null,
        };

        return new DiagnosticProperty(name, key, raw, decoded);
    }

    /// <summary>Newest battery reading per radio address, across all Bluetooth device nodes.</summary>
    static Dictionary<ulong, BatteryReading> ReadBatteryReadings()
    {
        var readings = new Dictionary<ulong, BatteryReading>();
        var links = new Dictionary<ulong, bool>();

        foreach (var enumerator in Enumerators)
        {
            foreach (var deviceId in ConfigManager.GetDeviceIds(enumerator))
            {
                uint devInst = ConfigManager.LocateDevNode(deviceId);
                if (devInst == 0)
                    continue;

                if (ResolveAddress(devInst, deviceId) is not { } address)
                    continue;

                // Link state is collected per address, not per node, because the two live on
                // different nodes of the same device: an LE device carries the flag word on its
                // parent node while the battery sits on a child that has no flags at all. One
                // node reporting a live link is enough.
                if (ConfigManager.GetUInt32(devInst, DevPropKeys.BluetoothDeviceFlags) is { } flags)
                    links[address] = links.GetValueOrDefault(address) || IsLinkUp(flags);

                if (ConfigManager.GetByte(devInst, DevPropKeys.BluetoothBattery) is not { } percent)
                    continue;

                var reading = new BatteryReading(
                    Percent: Math.Clamp(percent, (byte)0, (byte)100),
                    UpdatedUtc: ConfigManager.GetFileTimeUtc(devInst, DevPropKeys.BluetoothBatteryLastUpdated),
                    // Presence is a poor stand-in — it stays true for a bonded LE device that is
                    // switched off — but it is all there is when no node published flags.
                    IsConnected: ConfigManager.GetBoolean(devInst, DevPropKeys.IsPresent) ?? false,
                    NodeName: CleanNodeName(
                        ConfigManager.GetString(devInst, DevPropKeys.FriendlyName)
                        ?? ConfigManager.GetString(devInst, DevPropKeys.DeviceDesc)));

                // Several profile nodes of one device can each carry a reading; the most
                // recently updated one wins.
                if (!readings.TryGetValue(address, out var existing) || IsNewer(reading, existing))
                    readings[address] = reading;
            }
        }

        foreach (ulong address in readings.Keys.ToArray())
        {
            if (links.TryGetValue(address, out bool isConnected))
                readings[address] = readings[address] with { IsConnected = isConnected };
        }

        return readings;
    }

    /// <summary>Whether either transport's connected bit is set in a BDIF_* flag word.</summary>
    static bool IsLinkUp(uint flags) => (flags & (BdifConnected | BdifLeConnected)) != 0;

    static bool IsNewer(BatteryReading candidate, BatteryReading existing) =>
        (candidate.UpdatedUtc ?? DateTime.MinValue) > (existing.UpdatedUtc ?? DateTime.MinValue);

    /// <summary>
    /// Reads the node's Bluetooth address, falling back to the 12 hex digits embedded in
    /// the device instance id when the property is missing (common on LE nodes).
    /// </summary>
    static ulong? ResolveAddress(uint devInst, string deviceId)
    {
        if (ConfigManager.GetString(devInst, DevPropKeys.BluetoothDeviceAddress) is { Length: > 0 } text
            && ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address))
        {
            return address;
        }

        var match = AddressInInstanceId().Match(deviceId);
        return match.Success
            && ulong.TryParse(match.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong fallback)
            ? fallback
            : null;
    }

    /// <summary>Strips the Bluetooth profile suffixes Windows appends to child node names.</summary>
    static string? CleanNodeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string cleaned = ProfileSuffix().Replace(name, string.Empty).Trim();
        return cleaned.Length > 0 ? cleaned : name.Trim();
    }

    /// <summary>
    /// True when either half of the join says "phone": the pairing record's class-of-device,
    /// or a phone-only service published by one of the device's PnP nodes.
    /// </summary>
    static bool IsPhone(BluetoothDeviceInfo device, HashSet<ulong> phoneAddresses) =>
        Categorize(device.ClassOfDevice) == DeviceCategory.Phone || phoneAddresses.Contains(device.Address);

    /// <summary>
    /// Radio addresses of every device publishing a service from <see cref="PhoneServiceClasses"/>.
    /// All the Bluetooth enumerators are swept, because a phone's Classic profiles and its GATT
    /// services live in separate subtrees and a given phone may only appear in one of them.
    /// </summary>
    static HashSet<ulong> ReadPhoneAddresses()
    {
        var addresses = new HashSet<ulong>();

        foreach (var enumerator in Enumerators)
        {
            foreach (var deviceId in ConfigManager.GetDeviceIds(enumerator))
            {
                if (!PhoneServiceClasses.Any(prefix => deviceId.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
                    continue;

                uint devInst = ConfigManager.LocateDevNode(deviceId);
                if (devInst != 0 && ResolveAddress(devInst, deviceId) is { } address)
                    addresses.Add(address);
            }
        }

        return addresses;
    }

    /// <summary>Maps a Bluetooth class-of-device word to a display category.</summary>
    static DeviceCategory Categorize(uint classOfDevice)
    {
        uint major = (classOfDevice >> 8) & 0x1F;
        uint minor = (classOfDevice >> 2) & 0x3F;

        switch (major)
        {
            case 0x02: // Phone
                return DeviceCategory.Phone;

            case 0x04: // Audio / video
                return DeviceCategory.Headset;

            case 0x05: // Peripheral
                // Bits 4-5 flag keyboard/pointing; bits 0-3 name a specific device type.
                switch (minor & 0x0F)
                {
                    case 0x02 or 0x03:
                        return DeviceCategory.Gamepad;
                    case 0x05:
                        return DeviceCategory.Pen;
                }

                return ((minor >> 4) & 0x03) switch
                {
                    0x01 or 0x03 => DeviceCategory.Keyboard,
                    0x02 => DeviceCategory.Mouse,
                    _ => DeviceCategory.Unknown,
                };

            default:
                return DeviceCategory.Unknown;
        }
    }

    static string FormatAddress(ulong address) => address.ToString("X12", CultureInfo.InvariantCulture);

    sealed record BatteryReading(int Percent, DateTime? UpdatedUtc, bool IsConnected, string? NodeName);

    [GeneratedRegex(@"(?:DEV_|&)([0-9A-Fa-f]{12})(?:_|$)")]
    private static partial Regex AddressInInstanceId();

    [GeneratedRegex(@"\s+(Hands-Free(\s+AG)?|Avrcp Transport|AG|Stereo|Audio)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileSuffix();
}
