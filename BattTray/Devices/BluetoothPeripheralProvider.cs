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
/// guess. The newer Battery Level Status characteristic (0x2BED) would report it, and
/// <see cref="BattTray.Diagnostics.GattProbe"/> was written to find out whether anything here
/// publishes one. Nothing does — the only bonded device with a GATT Battery Service holds a
/// bare 0x2A19 Battery Level — so the question is not what it would cost to hold a GATT
/// connection open, but that there is nothing on the other end of one to read.
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

    /// <summary>
    /// The device ids under each swept enumerator, held between polls. Null until the first
    /// sweep and after every invalidation.
    /// </summary>
    /// <remarks>
    /// Enumerating is not expensive on its own, but one refresh walks the Bluetooth subtree
    /// three times over — once for phone services, once for battery readings, and once more
    /// when the diagnostics dump asks — and the list it walks changes only when a node
    /// arrives or goes away. Which is precisely when this is thrown out. Nothing here needs
    /// locking: invalidation is marshalled onto the same thread the provider is polled from.
    /// </remarks>
    Dictionary<string, string[]>? _deviceIds;

    public Transport Transport => Transport.Bluetooth;

    public void InvalidateDeviceCache() => _deviceIds = null;

    /// <summary>
    /// Device instance ids beneath one enumerator, from <see cref="_deviceIds"/> when it is
    /// warm. A stale id is survivable and already handled — every caller locates the node and
    /// steps over one that has gone — but a missing id is not, which is why the cache is only
    /// allowed to live as long as nothing has arrived.
    /// </summary>
    string[] DeviceIds(string enumerator)
    {
        _deviceIds ??= [];

        if (!_deviceIds.TryGetValue(enumerator, out string[]? ids))
            _deviceIds[enumerator] = ids = ConfigManager.GetDeviceIds(enumerator);

        return ids;
    }

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

                // Windows keeps the last percentage it saw after a device goes away and says
                // nothing about when the link dropped, so on this transport a reading from a
                // disconnected device is the leftover and a reading from a connected one is
                // about now. That is this source's behaviour and not a rule of the model,
                // which is why it is claimed here rather than derived in Peripheral.IsStale.
                IsStale = !device.IsConnected,
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

                // Same claim as above, from the same source: the node kept the number, the
                // link flag says whether it is about now.
                IsStale = !reading.IsConnected,
            });
        }

        return results;
    }

    /// <summary>
    /// Dumps both halves of the join this provider performs: the pairing records that supply
    /// identity and connection, and the PnP nodes that supply battery and link state. A device
    /// missing from the menu is nearly always present in exactly one of these two lists.
    /// </summary>
    public IReadOnlyList<DiagnosticNode> GetDiagnostics()
    {
        var nodes = new List<DiagnosticNode>();
        var phoneAddresses = ReadPhoneAddresses();
        var links = ReadLinkEvidence();

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
                    DescribePhoneFilter(device, phoneAddresses),
                ]));
        }

        foreach (var enumerator in Enumerators)
        {
            foreach (var deviceId in DeviceIds(enumerator))
            {
                uint devInst = ConfigManager.LocateDevNode(deviceId);

                // Only nodes carrying a battery byte are worth printing; the rest of the
                // Bluetooth tree is profile plumbing this provider never looks at. The flag
                // word lives out there on a node with no battery, but DescribeLink quotes it
                // here rather than printing those nodes: every BTHENUM service node repeats
                // its parent's flags, which on this machine turned four nodes into twenty-eight.
                if (devInst == 0 || ConfigManager.GetByte(devInst, DevPropKeys.BluetoothBattery) is null)
                    continue;

                nodes.Add(new DiagnosticNode(
                    Transport.Bluetooth,
                    $"node: {ReadNodeName(devInst) ?? "(unnamed)"}",
                    deviceId,
                    [
                        DescribeBattery(devInst),
                        Describe(devInst, "battery updated", "{104ea319-...bbe5} PID 7", DevPropKeys.BluetoothBatteryLastUpdated),
                        DescribeAddress(devInst, deviceId),
                        Describe(devInst, "is present", "DEVPKEY_Device_IsPresent", DevPropKeys.IsPresent),
                        DescribeLink(devInst, deviceId, links),
                        DescribeName(devInst),
                    ]));
            }
        }

        return nodes;
    }

    /// <summary>
    /// The verdict <see cref="IsPhone"/> reached for a pairing record, naming the signal that
    /// reached it. The value column can only speak for the service GUIDs this property is
    /// sourced from, so a phone recognised by its class-of-device alone reads "absent" there;
    /// without the deciding signal spelled out beside it, that pair reads as a contradiction
    /// and invites the conclusion that the phone filter was not what dropped the device.
    /// </summary>
    static DiagnosticProperty DescribePhoneFilter(BluetoothDeviceInfo device, HashSet<ulong> phoneAddresses)
    {
        bool byClass = Categorize(device.ClassOfDevice) == DeviceCategory.Phone;
        bool byService = phoneAddresses.Contains(device.Address);

        string? verdict = (byClass, byService) switch
        {
            (true, true) => "treated as a phone, excluded (class of device and service GUID)",
            (true, false) => "treated as a phone, excluded (class of device)",
            (false, true) => "treated as a phone, excluded (service GUID)",
            _ => null,
        };

        return new DiagnosticProperty(
            "phone services", "phone-only service GUIDs on this device's PnP nodes",
            byService ? "present" : "absent", verdict);
    }

    /// <summary>
    /// The node's own BDIF_* flag word, with the verdict this provider draws from the flags
    /// held for that radio address. Printed next to "is present" on purpose: when those two
    /// disagree, the flags are the truthful one. That verdict is what the menu shows for a
    /// device with no pairing record; a paired device takes its state from fConnected on the
    /// "paired:" node above instead, so compare the two before believing either.
    /// </summary>
    /// <remarks>
    /// Raw and verdict can come from different nodes, and have to: an LE device carries the
    /// flag word on its parent while the battery sits on a child that has none, so a dump of
    /// battery-bearing nodes alone would answer "why is this shown as disconnected?" with
    /// "(absent)" on every line. Whenever the verdict was not drawn from this node's own
    /// bytes, the node it did come from is named, so the join stays checkable.
    /// </remarks>
    static DiagnosticProperty DescribeLink(
        uint devInst, string deviceId, IReadOnlyDictionary<ulong, LinkEvidence> links)
    {
        const string Key = "DEVPKEY_Bluetooth_DeviceFlags";

        uint? own = ConfigManager.GetUInt32(devInst, DevPropKeys.BluetoothDeviceFlags);
        string raw = own is { } flags ? $"UINT32 [0x{flags:X8}]" : "(absent)";

        if (ResolveAddress(devInst, deviceId) is not { } address || !links.TryGetValue(address, out var evidence))
        {
            return new DiagnosticProperty("device flags", Key, raw,
                "no node at this radio address publishes flags, so the node-side reading falls back to DEVPKEY_Device_IsPresent");
        }

        string verdict = IsLinkUp(evidence.Flags)
            ? "BDIF_CONNECTED or BDIF_LE_CONNECTED set -> connected"
            : "connected bits clear -> disconnected, so any battery value here is cached";

        // Naming the source node is only worth the line when this node's own bytes do not
        // already say the same thing: either it published none, or it disagrees and was
        // outvoted. Siblings usually just repeat their parent's flag word verbatim.
        bool ownSaysTheSame = own is { } value && IsLinkUp(value) == IsLinkUp(evidence.Flags);

        return new DiagnosticProperty("device flags", Key, raw,
            ownSaysTheSame ? verdict : $"{verdict} (0x{evidence.Flags:X8} on {evidence.DeviceId})");
    }

    /// <summary>
    /// The flag word each radio address is judged by, tagged with the node it was read from.
    /// Resolved exactly as <see cref="ReadBatteryReadings"/> resolves it — link state belongs
    /// to the device rather than to whichever node happens to publish it, and one node
    /// reporting a live link is enough — so the dump cannot contradict the menu it exists to
    /// explain.
    /// </summary>
    Dictionary<ulong, LinkEvidence> ReadLinkEvidence()
    {
        var links = new Dictionary<ulong, LinkEvidence>();

        foreach (var enumerator in Enumerators)
        {
            foreach (var deviceId in DeviceIds(enumerator))
            {
                uint devInst = ConfigManager.LocateDevNode(deviceId);
                if (devInst == 0 || ResolveAddress(devInst, deviceId) is not { } address)
                    continue;

                if (ConfigManager.GetUInt32(devInst, DevPropKeys.BluetoothDeviceFlags) is not { } flags)
                    continue;

                // A node claiming the link is up outranks one that does not, matching the
                // OR in ReadBatteryReadings; otherwise the first node found stands.
                if (!links.TryGetValue(address, out var existing) || (IsLinkUp(flags) && !IsLinkUp(existing.Flags)))
                    links[address] = new LinkEvidence(deviceId, flags);
            }
        }

        return links;
    }

    /// <summary>
    /// The battery byte with the percentage the menu shows for it. The two only part company
    /// when a node reports outside 0-100, which is the case the clamp in
    /// <see cref="ReadBatteryReadings"/> exists for, and the one where seeing both numbers
    /// matters: the reading is not the byte, and neither is wrong.
    /// </summary>
    static DiagnosticProperty DescribeBattery(uint devInst)
    {
        const string Key = "{104ea319-...bbe5} PID 2";
        string raw = ReadRaw(devInst, DevPropKeys.BluetoothBattery);

        if (ConfigManager.GetByte(devInst, DevPropKeys.BluetoothBattery) is not { } percent)
            return new DiagnosticProperty("battery level", Key, raw, null);

        int clamped = Math.Clamp(percent, (byte)0, (byte)100);

        return new DiagnosticProperty("battery level", Key, raw,
            clamped == percent
                ? clamped.ToString(CultureInfo.InvariantCulture)
                : $"{clamped} (clamped from {percent})");
    }

    /// <summary>
    /// The address property as published, with the address this provider actually joined the
    /// node by. Those differ on the LE nodes that carry no address property at all:
    /// <see cref="ResolveAddress"/> falls back to the hex in the instance id, so printing the
    /// property alone would answer "what did this node join as?" with "(absent)" for a join
    /// that plainly succeeded. Every other half of that join is keyed by this value, which
    /// makes it worth spelling out even when no property backs it.
    /// </summary>
    static DiagnosticProperty DescribeAddress(uint devInst, string deviceId)
    {
        const string Key = "DEVPKEY_Bluetooth_DeviceAddress";
        string raw = ReadRaw(devInst, DevPropKeys.BluetoothDeviceAddress);

        // Neither source yielded an address. Worth stating outright: the pairing join and the
        // unmatched-node fallback are both keyed by address, so this node reaches neither.
        if (ResolveAddressWithSource(devInst, deviceId) is not { } resolved)
        {
            return new DiagnosticProperty("radio address", Key, raw,
                "no address on this node and none in the instance id, so it joins to nothing");
        }

        return new DiagnosticProperty("radio address", Key, raw,
            resolved.FromInstanceId
                ? $"{FormatAddress(resolved.Address)} (recovered from the instance id)"
                : FormatAddress(resolved.Address));
    }

    /// <summary>
    /// The friendly name as published, with the name this provider derives from it: profile
    /// suffix stripped, and DEVPKEY_Device_DeviceDesc standing in when the node publishes no
    /// friendly name. The derived name is what an unmatched node is listed under, and it also
    /// decides an exclusion — a node whose cleaned name matches a paired phone's is dropped —
    /// so a node can be renamed or vanish entirely on a string the property line never shows.
    /// </summary>
    static DiagnosticProperty DescribeName(uint devInst)
    {
        const string Key = "DEVPKEY_Device_FriendlyName";
        string raw = ReadRaw(devInst, DevPropKeys.FriendlyName);

        string? friendly = ConfigManager.GetString(devInst, DevPropKeys.FriendlyName);
        if (ReadNodeName(devInst) is not { } name)
            return new DiagnosticProperty("node name", Key, raw, null);

        // A name that survived CleanNodeName unchanged needs no explanation; the other two
        // cases are precisely where the menu shows something this line otherwise would not.
        // The fallback is judged the way ReadNodeName judges it — on whether this property
        // amounts to a name, not on whether it is present — so an empty friendly name reads
        // as the handover it is rather than as a suffix nobody stripped.
        string? note = CleanNodeName(friendly) is null
            ? " (no usable friendly name here, so DEVPKEY_Device_DeviceDesc supplied it)"
            : name == friendly ? null : " (profile suffix stripped)";

        return new DiagnosticProperty("node name", Key, raw, name + note);
    }

    /// <summary>Reads one property both ways: the bytes on the wire and this app's reading of them.</summary>
    static DiagnosticProperty Describe(uint devInst, string name, string key, DevPropKey propertyKey)
    {
        if (ConfigManager.GetRaw(devInst, propertyKey) is not { } property)
            return new DiagnosticProperty(name, key, Absent, null);

        string raw = FormatRaw(property);

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

    /// <summary>What the Raw column reads when the node does not publish the property at all.</summary>
    const string Absent = "(absent)";

    static string FormatRaw((uint Type, byte[] Bytes) property) =>
        $"{DevPropType.Describe(property.Type)} [{Convert.ToHexString(property.Bytes)}]";

    /// <summary>
    /// The Raw column for a property whose decoded half is derived rather than read straight
    /// back, so those lines quote the same bytes <see cref="Describe"/> would have.
    /// </summary>
    static string ReadRaw(uint devInst, DevPropKey propertyKey) =>
        ConfigManager.GetRaw(devInst, propertyKey) is { } property ? FormatRaw(property) : Absent;

    /// <summary>Newest battery reading per radio address, across all Bluetooth device nodes.</summary>
    Dictionary<ulong, BatteryReading> ReadBatteryReadings()
    {
        var readings = new Dictionary<ulong, BatteryReading>();
        var links = new Dictionary<ulong, bool>();

        foreach (var enumerator in Enumerators)
        {
            foreach (var deviceId in DeviceIds(enumerator))
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
                    NodeName: ReadNodeName(devInst));

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
    /// the device instance id when the property is missing (common on LE nodes). Accepts
    /// <paramref name="devInst"/> of 0 for a node that could not be located, in which case
    /// the id is the only source available.
    /// </summary>
    static ulong? ResolveAddress(uint devInst, string deviceId) =>
        ResolveAddressWithSource(devInst, deviceId)?.Address;

    /// <summary>
    /// <see cref="ResolveAddress"/> with the source it settled on, which only the dump cares
    /// about. Kept as the single implementation of the rule so a line explaining the join
    /// cannot drift from the join itself.
    /// </summary>
    static (ulong Address, bool FromInstanceId)? ResolveAddressWithSource(uint devInst, string deviceId)
    {
        if (ConfigManager.GetString(devInst, DevPropKeys.BluetoothDeviceAddress) is { Length: > 0 } text
            && ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address))
        {
            return (address, false);
        }

        var match = AddressInInstanceId().Match(deviceId);
        return match.Success
            && ulong.TryParse(match.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong fallback)
            ? (fallback, true)
            : null;
    }

    /// <summary>
    /// The name this provider knows a node by: its friendly name, or its device description
    /// when that yields nothing, with the profile suffix stripped. Shared with the dump so the
    /// name printed there is the name the menu uses.
    /// </summary>
    /// <remarks>
    /// Each candidate is cleaned before the fallback is judged, rather than the raw strings
    /// being chained with <c>??</c>. A node can publish DEVPKEY_Device_FriendlyName as an empty
    /// string, which is not the same as not publishing it: the property read hands back "", the
    /// null test passes it, and the device description behind it is never reached — so a node
    /// with a perfectly good description was listed under its bare radio address. Whether a
    /// candidate amounts to a name is <see cref="CleanNodeName"/>'s question and is asked of
    /// both, which also settles the whitespace-only case without a second rule for it.
    /// </remarks>
    static string? ReadNodeName(uint devInst) =>
        CleanNodeName(ConfigManager.GetString(devInst, DevPropKeys.FriendlyName))
        ?? CleanNodeName(ConfigManager.GetString(devInst, DevPropKeys.DeviceDesc));

    /// <summary>Strips the Bluetooth profile suffixes Windows appends to child node names.</summary>
    internal static string? CleanNodeName(string? name)
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
    HashSet<ulong> ReadPhoneAddresses()
    {
        var addresses = new HashSet<ulong>();

        foreach (var enumerator in Enumerators)
        {
            foreach (var deviceId in DeviceIds(enumerator))
            {
                if (!PhoneServiceClasses.Any(prefix => deviceId.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // A failed lookup is not a reason to skip the node: Windows tears down and
                // recreates a phone's GATT nodes constantly, so the id can go stale between
                // being listed and being located. The id itself still names the address, and
                // dropping a phone here is what lets it back into the menu as a peripheral.
                uint devInst = ConfigManager.LocateDevNode(deviceId);
                if (ResolveAddress(devInst, deviceId) is { } address)
                    addresses.Add(address);
            }
        }

        return addresses;
    }

    /// <summary>Maps a Bluetooth class-of-device word to a display category.</summary>
    internal static DeviceCategory Categorize(uint classOfDevice)
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
                // Bits 4-5 flag keyboard/pointing; bits 0-3 name a specific device type, and
                // the values are the assigned-numbers ones: 1 joystick, 2 gamepad, 3 remote
                // control, 4 sensing, 5 digitizer tablet, 6 card reader, 7 digital pen. A
                // remote control gets no arm of its own — there is no category here to file it
                // under, and Unknown is the honest answer rather than the nearest-looking one.
                switch (minor & 0x0F)
                {
                    case 0x01 or 0x02:
                        return DeviceCategory.Gamepad;
                    case 0x05 or 0x07:
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

    /// <summary>A BDIF_* flag word and the device instance id it was read from.</summary>
    sealed record LinkEvidence(string DeviceId, uint Flags);

    /// <summary>
    /// The radio address as it is spelled in a device instance id. Rather than enumerate the
    /// prefixes Windows puts in front of it — which differ per enumerator and per device, and
    /// which a swept-from-this-machine sample kept producing new variants of — this matches any
    /// run of exactly 12 hex digits fenced by id separators:
    /// <list type="bullet">
    /// <item><c>BTHLE\DEV_5093524E6499\8&amp;259B6687&amp;0&amp;5093524E6499</c></item>
    /// <item><c>BTHENUM\{0000111E-...}_LOCALMFG&amp;0000\7&amp;2A1A2FB2&amp;0&amp;5093524E6499_C00000000</c></item>
    /// <item><c>BTHLEDevice\{7905F431-...}_5093524E6499\9&amp;3B7951A&amp;0&amp;0019</c> (GATT)</item>
    /// <item><c>BTHLEDEVICE\{0000180F-...}_DEV_VID&amp;022DC8_PID&amp;301B_REV&amp;0001_E417D8248EB3\9&amp;...</c></item>
    /// </list>
    /// Requiring a separator on both sides is what keeps it off the hex inside a service GUID:
    /// the final group of <c>{00010203-0405-0607-0809-0A0B0C0D1912}</c> is also 12 hex digits,
    /// but it is fenced by <c>-</c> and <c>}</c>. Verified against all 55 Bluetooth instance
    /// ids present on the development machine.
    /// </summary>
    [GeneratedRegex(@"[_&\\]([0-9A-Fa-f]{12})(?=[_&\\]|$)", RegexOptions.IgnoreCase)]
    internal static partial Regex AddressInInstanceId();

    [GeneratedRegex(@"\s+(Hands-Free(\s+AG)?|Avrcp Transport|AG|Stereo|Audio)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileSuffix();
}
