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

    public Transport Transport => Transport.Bluetooth;

    public IReadOnlyList<Peripheral> GetPeripherals()
    {
        var batteries = ReadBatteryReadings();
        var results = new List<Peripheral>();

        foreach (var device in BluetoothApi.GetPairedDevices())
        {
            batteries.Remove(device.Address, out var reading);

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
            results.Add(new Peripheral
            {
                Id = FormatAddress(address),
                Name = reading.NodeName ?? FormatAddress(address),
                Transport = Transport.Bluetooth,
                BatteryPercent = reading.Percent,
                BatteryUpdatedUtc = reading.UpdatedUtc,
                IsConnected = reading.IsPresent,
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
                        Describe(devInst, "friendly name", "DEVPKEY_Device_FriendlyName", DevPropKeys.FriendlyName),
                    ]));
            }
        }

        return nodes;
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

        foreach (var enumerator in Enumerators)
        {
            foreach (var deviceId in ConfigManager.GetDeviceIds(enumerator))
            {
                uint devInst = ConfigManager.LocateDevNode(deviceId);
                if (devInst == 0)
                    continue;

                if (ConfigManager.GetByte(devInst, DevPropKeys.BluetoothBattery) is not { } percent)
                    continue;

                if (ResolveAddress(devInst, deviceId) is not { } address)
                    continue;

                var reading = new BatteryReading(
                    Percent: Math.Clamp(percent, (byte)0, (byte)100),
                    UpdatedUtc: ConfigManager.GetFileTimeUtc(devInst, DevPropKeys.BluetoothBatteryLastUpdated),
                    IsPresent: ConfigManager.GetBoolean(devInst, DevPropKeys.IsPresent) ?? false,
                    NodeName: CleanNodeName(
                        ConfigManager.GetString(devInst, DevPropKeys.FriendlyName)
                        ?? ConfigManager.GetString(devInst, DevPropKeys.DeviceDesc)));

                // Several profile nodes of one device can each carry a reading; the most
                // recently updated one wins.
                if (!readings.TryGetValue(address, out var existing) || IsNewer(reading, existing))
                    readings[address] = reading;
            }
        }

        return readings;
    }

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

    sealed record BatteryReading(int Percent, DateTime? UpdatedUtc, bool IsPresent, string? NodeName);

    [GeneratedRegex(@"(?:DEV_|&)([0-9A-Fa-f]{12})(?:_|$)")]
    private static partial Regex AddressInInstanceId();

    [GeneratedRegex(@"\s+(Hands-Free(\s+AG)?|Avrcp Transport|AG|Stereo|Audio)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileSuffix();
}
