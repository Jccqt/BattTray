using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BattTray.Interop;

namespace BattTray.Diagnostics;

/// <summary>
/// Asks every present device node what properties it publishes, rather than asking known
/// nodes for known properties. It exists to answer one question before a second provider is
/// written: does Windows already hold a battery percentage for USB and wireless-dongle
/// peripherals the way it does for Bluetooth ones? If it does, the next provider is a near
/// copy of <see cref="BattTray.Devices.BluetoothPeripheralProvider"/>; if it does not, that
/// is worth knowing before a day is spent assuming otherwise.
/// </summary>
/// <remarks>
/// Nothing here decodes a candidate into a percentage. It prints the key coordinates and the
/// bytes, and leaves the judgement to whoever reads the dump against what the vendor app
/// shows at the same moment — a byte that reads 64 next to a vendor app showing 100 is a
/// scale, not a percentage, and only the comparison can tell.
///
/// Device *interface* properties are not swept. Those need SetupAPI and a class GUID per
/// interface, which is a different sweep; if the node dump comes back empty this is the
/// obvious next place to look.
/// </remarks>
internal static partial class Probe
{
    /// <summary>
    /// The format GUID Windows keeps Bluetooth battery under. Any key under it is reported
    /// wherever it turns up, whatever the property id: finding it on a USB node is the single
    /// most valuable outcome this probe has, and the id it uses there need not be the id 2
    /// that Bluetooth nodes use.
    /// </summary>
    static readonly Guid BatteryFormatGuid = new("104ea319-6ee2-4701-bd47-8ddbf425bbe5");

    /// <summary>The format GUID the DEVPKEY_Device_* block of devpkey.h lives under.</summary>
    static readonly Guid DeviceFormatGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    /// <summary>Setup class names worth dumping in full. Anything hosting a peripheral.</summary>
    static readonly HashSet<string> PeripheralClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "HIDClass", "Mouse", "Keyboard", "Bluetooth", "USB", "USBDevice", "MEDIA",
        "AudioEndpoint", "XnaComposite", "XboxComposite", "Camera", "Image", "WPD", "Battery",
    };

    public static void Run(Action<string> write, bool dumpEveryNode)
    {
        var stopwatch = Stopwatch.StartNew();
        var nodes = Sweep();
        stopwatch.Stop();

        int properties = nodes.Sum(node => node.Properties.Count);

        // How many nodes publish each key. A battery property is per-device by nature, so a key
        // on a third of the machine is furniture however percentage-shaped its value looks —
        // and that is not obvious from one node's line, which is where a candidate is judged.
        var frequency = nodes
            .SelectMany(node => node.Properties.Select(property => property.Key))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());

        write("=== Probe: every present device node, every property key it publishes");
        write(string.Empty);
        write(string.Create(CultureInfo.InvariantCulture,
            $"  {nodes.Count} nodes, {properties} properties, {frequency.Count} distinct keys, "
            + $"swept in {stopwatch.ElapsedMilliseconds} ms."));
        write("  In the tiers below, xN after a key means N of those nodes publish it.");
        write(string.Empty);

        var candidates = ReportBatteryShaped(write, nodes, frequency);
        ReportFullDump(write, nodes, dumpEveryNode, candidates);
    }

    /// <summary>Reads every property of every present node, keeping the bytes untransformed.</summary>
    static List<ProbeNode> Sweep()
    {
        var nodes = new List<ProbeNode>();

        foreach (string deviceId in ConfigManager.GetDeviceIds())
        {
            uint devInst = ConfigManager.LocateDevNode(deviceId);
            if (devInst == 0)
                continue;

            var properties = new List<ProbeProperty>();

            foreach (var key in ConfigManager.GetPropertyKeys(devInst))
            {
                // A key the node advertises can still fail to read — a security descriptor
                // this process may not see, or a property torn down mid-sweep. Skipping it
                // silently is right: the key list is the claim, this is the evidence.
                if (ConfigManager.GetRaw(devInst, key) is { } property)
                    properties.Add(new ProbeProperty(key, property.Type, property.Bytes));
            }

            nodes.Add(new ProbeNode(deviceId, properties));
        }

        return nodes;
    }

    /// <summary>
    /// The three shapes a battery reading could plausibly take, loosest last. Split into tiers
    /// because the last one is noisy by construction — plenty of unrelated counters sit in
    /// 0-100 — and a tier that has to be waded through is worth less than one that does not.
    /// </summary>
    /// <returns>Every node that hit a tier, so the full dump can guarantee they appear there too.</returns>
    static HashSet<ProbeNode> ReportBatteryShaped(
        Action<string> write, List<ProbeNode> nodes, Dictionary<DevPropKey, int> frequency)
    {
        write("--- Tier 1: keys under the battery format GUID (any property id)");
        var tier1 = Report(write, nodes, frequency,
            node => node.Properties.Where(property => property.Key.FormatId == BatteryFormatGuid));

        // The Bluetooth provider already reads these, so the count that matters is the one
        // outside the Bluetooth enumerators: that is the part of this probe that would make
        // the next provider a near copy of the one already written.
        int elsewhere = tier1.Count(node => !node.IsBluetooth);
        if (tier1.Count > 0)
        {
            write(elsewhere > 0
                ? $"  {elsewhere} of these are NOT Bluetooth nodes — read those first."
                : "  All of these are Bluetooth nodes, already covered by the existing provider.");
        }

        write(string.Empty);

        write("--- Tier 2: DEVPROP_TYPE_BYTE properties holding 0-100");
        write("  A single byte is a rare property type, which makes a percentage-shaped one a strong signal.");
        var tier2 = Report(write, nodes, frequency, node => node.Properties.Where(IsPercentageByte));
        write(string.Empty);

        write("--- Tier 3: 16/32-bit integers holding 1-100, on peripheral-looking nodes,");
        write("            excluding keys documented in devpkey.h as something else.");
        write("  Noisy even so — unrelated counters live here. Corroborate before believing one.");
        var tier3 = Report(write, nodes, frequency,
            node => node.IsPeripheral ? node.Properties.Where(IsPercentageInteger).Where(IsUndocumented) : []);
        write(string.Empty);

        return [.. tier1, .. tier2, .. tier3];
    }

    /// <summary>
    /// Prints every node with at least one matching property, rarest key first, and returns
    /// those nodes so the caller can say something about the shape of the result.
    /// </summary>
    static List<ProbeNode> Report(
        Action<string> write,
        List<ProbeNode> nodes,
        Dictionary<DevPropKey, int> frequency,
        Func<ProbeNode, IEnumerable<ProbeProperty>> select)
    {
        var matched = nodes
            .Select(node => (Node: node, Hits: select(node).ToList()))
            .Where(match => match.Hits.Count > 0)
            // A key on one node is the interesting kind; ordering by that spares a reader
            // scrolling past forty nodes repeating the same piece of Bluetooth furniture.
            .OrderBy(match => match.Hits.Min(hit => frequency.GetValueOrDefault(hit.Key)))
            .ToList();

        foreach (var (node, hits) in matched)
            WriteNode(write, node, hits, frequency);

        if (matched.Count == 0)
            write("  (nothing)");

        return matched.Select(match => match.Node).ToList();
    }

    /// <summary>
    /// The full key list for nodes that look like peripherals — the section to read when the
    /// tiers above come back empty, since a battery property Windows exposes under a name
    /// nobody has published would show up here and nowhere else.
    /// </summary>
    /// <remarks>
    /// Every node from a tier is included whether or not it looks like a peripheral. The node
    /// that carries a headset's battery is filed under setup class "System" with a name no
    /// keyword matches, so the heuristic alone would answer "what else is on the node this
    /// candidate came from?" with silence, for exactly the nodes a reader is here to judge.
    /// </remarks>
    static void ReportFullDump(
        Action<string> write, List<ProbeNode> nodes, bool dumpEveryNode, HashSet<ProbeNode> candidates)
    {
        write(dumpEveryNode
            ? "--- Full dump: every node"
            : "--- Full dump: nodes that hit a tier above, plus anything that looks like a"
            + " peripheral (--all for the rest)");
        write(string.Empty);

        foreach (var node in nodes.Where(node => dumpEveryNode || node.IsPeripheral || candidates.Contains(node)))
            WriteNode(write, node, node.Properties, frequency: null);
    }

    /// <summary>
    /// One node and the properties of it worth printing. <paramref name="frequency"/> is null
    /// for the full dump, where every key would carry a count and none of them would stand out.
    /// </summary>
    static void WriteNode(
        Action<string> write,
        ProbeNode node,
        IReadOnlyList<ProbeProperty> properties,
        Dictionary<DevPropKey, int>? frequency)
    {
        const int Column = 48;

        write($"  {node.Name}");
        write($"    instance : {node.DeviceId}");
        write($"    class    : {node.Class ?? "(none)"}");

        foreach (var property in properties)
        {
            string label = Describe(property.Key);

            if (frequency?.GetValueOrDefault(property.Key) is > 1 and var count)
                label += $" x{count}";

            write($"    {label,-Column} : {FormatRaw(property)}");

            if (Decode(property) is { } decoded)
                write($"    {string.Empty,-Column}   -> {decoded}");
        }

        write(string.Empty);
    }

    static bool IsPercentageByte(ProbeProperty property) =>
        property.Type is DevPropTypes.Byte or DevPropTypes.SByte
        && property.Bytes is [<= 100];

    /// <summary>
    /// Whether the key is absent from <see cref="KnownKeys"/>. Used to keep tier 3 readable:
    /// a key devpkey.h documents as a removal policy or a bus number is not a battery, and on
    /// the first run those two alone accounted for most of what that tier printed.
    /// </summary>
    static bool IsUndocumented(ProbeProperty property) =>
        !KnownKeys.ContainsKey((property.Key.FormatId, property.Key.PropertyId));

    static bool IsPercentageInteger(ProbeProperty property) =>
        ToInteger(property) is > 0 and <= 100
        && property.Type is DevPropTypes.UInt16 or DevPropTypes.Int16 or DevPropTypes.UInt32 or DevPropTypes.Int32;

    /// <summary>
    /// The integer a property holds, read with the signedness its type claims — an INT32 of -1
    /// printed as 4294967295 would be this tool's own bug in the one column it exists to be
    /// trusted on. A negative value fails the 1-100 test either way, so the candidate filter
    /// reads the same.
    /// </summary>
    static long? ToInteger(ProbeProperty property) => property.Type switch
    {
        DevPropTypes.UInt16 when property.Bytes.Length >= 2 => BitConverter.ToUInt16(property.Bytes),
        DevPropTypes.Int16 when property.Bytes.Length >= 2 => BitConverter.ToInt16(property.Bytes),
        DevPropTypes.UInt32 when property.Bytes.Length >= 4 => BitConverter.ToUInt32(property.Bytes),
        DevPropTypes.Int32 when property.Bytes.Length >= 4 => BitConverter.ToInt32(property.Bytes),
        _ => null,
    };

    /// <summary>
    /// Bytes as reported, with the type the node claimed. Long values are cut short: a
    /// security descriptor runs to hundreds of bytes and buries the neighbouring line that a
    /// reader is actually here for, and nothing battery-shaped is that long.
    /// </summary>
    static string FormatRaw(ProbeProperty property)
    {
        const int Limit = 32;

        string type = DevPropTypes.Describe(property.Type);

        return property.Bytes.Length <= Limit
            ? $"{type} [{Convert.ToHexString(property.Bytes)}]"
            : $"{type} [{Convert.ToHexString(property.Bytes.AsSpan(0, Limit))}… {property.Bytes.Length} bytes]";
    }

    /// <summary>The value a type this probe recognises carries, or null to leave the hex to speak.</summary>
    static string? Decode(ProbeProperty property) => property.Type switch
    {
        DevPropTypes.String => ReadString(property.Bytes),
        DevPropTypes.StringList => string.Join(" | ", ReadStringList(property.Bytes)),
        DevPropTypes.Boolean => (property.Bytes is [not 0]).ToString(),
        DevPropTypes.Byte when property.Bytes.Length >= 1 =>
            property.Bytes[0].ToString(CultureInfo.InvariantCulture),
        DevPropTypes.SByte when property.Bytes.Length >= 1 =>
            ((sbyte)property.Bytes[0]).ToString(CultureInfo.InvariantCulture),
        DevPropTypes.Guid when property.Bytes.Length >= 16 => new Guid(property.Bytes.AsSpan(0, 16)).ToString(),
        DevPropTypes.FileTime when property.Bytes.Length >= 8 => ReadFileTime(property.Bytes),
        _ => ToInteger(property)?.ToString(CultureInfo.InvariantCulture),
    };

    static string ReadString(byte[] bytes) => Encoding.Unicode.GetString(bytes).TrimEnd('\0');

    static string[] ReadStringList(byte[] bytes) =>
        Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    static string? ReadFileTime(byte[] bytes)
    {
        long ticks = BitConverter.ToInt64(bytes);
        if (ticks <= 0)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(ticks).ToString("u", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// A key's SDK name where this file is sure of it, and its raw coordinates otherwise. The
    /// unnamed ones are the interesting ones here — an undocumented vendor key is exactly what
    /// this probe is hunting — so a guessed name would be worse than none: it would send a
    /// reader looking up a header entry that does not exist.
    /// </summary>
    static string Describe(DevPropKey key) =>
        KnownKeys.TryGetValue((key.FormatId, key.PropertyId), out string? name)
            ? name
            : $"{{{key.FormatId}}} PID {key.PropertyId}";

    static readonly Dictionary<(Guid, uint), string> KnownKeys = BuildKnownKeys();

    static Dictionary<(Guid, uint), string> BuildKnownKeys()
    {
        var status = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7");
        var deviceEx = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2");
        var container = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");
        var origin = new Guid("80497100-8c73-48b9-aad9-ce387e19c56e");
        var instance = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57");
        var bluetooth = new Guid("2bd67d8b-8beb-48d5-87e0-6cda3428040a");

        var keys = new Dictionary<(Guid, uint), string>
        {
            [(instance, 256)] = "DEVPKEY_Device_InstanceId",
            [(deviceEx, 5)] = "DEVPKEY_Device_IsPresent",
            [(deviceEx, 6)] = "DEVPKEY_Device_HasProblem",
            [(container, 2)] = "DEVPKEY_Device_ContainerId",
            [(container, 4)] = "DEVPKEY_Device_InLocalMachineContainer",
            [(origin, 2)] = "DEVPKEY_Device_Reported",
            [(origin, 3)] = "DEVPKEY_Device_Legacy",
            [(bluetooth, 1)] = "DEVPKEY_Bluetooth_DeviceAddress",
            [(bluetooth, 2)] = "DEVPKEY_Bluetooth_ServiceGUID",
            [(bluetooth, 3)] = "DEVPKEY_Bluetooth_DeviceFlags",
            [(BatteryFormatGuid, 2)] = "Bluetooth battery level (undocumented)",
            [(BatteryFormatGuid, 7)] = "Bluetooth battery last updated (undocumented)",
        };

        string[] deviceNames =
        [
            "DeviceDesc", "HardwareIds", "CompatibleIds", "", "Service", "", "", "Class", "ClassGuid",
            "Driver", "ConfigFlags", "Manufacturer", "FriendlyName", "LocationInfo", "PDOName",
            "Capabilities", "UINumber", "UpperFilters", "LowerFilters", "BusTypeGuid", "LegacyBusType",
            "BusNumber", "EnumeratorName", "Security", "SecuritySDS", "DevType", "Exclusive",
            "Characteristics", "Address", "UINumberDescFormat", "PowerData", "RemovalPolicy",
            "RemovalPolicyDefault", "RemovalPolicyOverride", "InstallState", "LocationPaths",
            "BaseContainerId",
        ];

        string[] statusNames =
        [
            "DevNodeStatus", "ProblemCode", "EjectionRelations", "RemovalRelations", "PowerRelations",
            "BusRelations", "Parent", "Children", "Siblings", "TransportRelations", "ProblemStatus",
        ];

        // Both blocks are contiguous runs starting at property id 2 in devpkey.h; the gaps in
        // the first are the Unused* entries, which are left out rather than mislabelled.
        Add(DeviceFormatGuid, deviceNames);
        Add(status, statusNames);

        return keys;

        void Add(Guid formatId, string[] names)
        {
            for (uint index = 0; index < names.Length; index++)
            {
                if (names[index].Length > 0)
                    keys[(formatId, index + 2)] = $"DEVPKEY_Device_{names[index]}";
            }
        }
    }

    /// <summary>One property exactly as the node reported it.</summary>
    sealed record ProbeProperty(DevPropKey Key, uint Type, byte[] Bytes);

    /// <summary>One device node and everything it publishes.</summary>
    sealed class ProbeNode(string deviceId, List<ProbeProperty> properties)
    {
        public string DeviceId { get; } = deviceId;

        public IReadOnlyList<ProbeProperty> Properties { get; } = properties;

        /// <summary>The enumerator, which is the leading segment of every device instance id.</summary>
        public string Enumerator { get; } = deviceId.Split('\\')[0];

        public string Name { get; } =
            Text(properties, 14) ?? Text(properties, 2) ?? "(unnamed)";

        /// <summary>DEVPKEY_Device_Class, e.g. "HIDClass".</summary>
        public string? Class { get; } = Text(properties, 9);

        public bool IsBluetooth =>
            Enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether the node is worth a full dump. Setup class first, since it is Windows' own
        /// classification; the name pattern then catches what the class misses — a headset
        /// sitting under a vendor class, say — at the price of the odd false positive, which
        /// in a dump costs nothing but a few lines.
        /// </summary>
        public bool IsPeripheral =>
            (Class is not null && PeripheralClasses.Contains(Class)) || PeripheralName().IsMatch(Name);

        /// <summary>A DEVPKEY under the standard device format GUID, read as a string.</summary>
        static string? Text(List<ProbeProperty> properties, uint propertyId)
        {
            var match = properties.FirstOrDefault(property =>
                property.Key.FormatId == DeviceFormatGuid
                && property.Key.PropertyId == propertyId
                && property.Type == DevPropTypes.String);

            return match is null ? null : ReadString(match.Bytes) is { Length: > 0 } text ? text : null;
        }
    }

    [GeneratedRegex(
        @"mouse|keyboard|headset|headphone|earbud|earphone|gamepad|controller|joystick|stylus|"
        + @"trackpad|touchpad|touch pen|digitizer|speaker|webcam|remote|dial|receiver|dongle",
        RegexOptions.IgnoreCase)]
    private static partial Regex PeripheralName();

    /// <summary>
    /// The full DEVPROP_TYPE_* table from devpropdef.h. The app's own <see cref="DevPropType"/>
    /// names only the handful it reads, which is right for it and wrong here: a probe that
    /// prints "0x2012" where a reader needs "STRING_LIST" hides the thing it was run to find.
    /// </summary>
    static class DevPropTypes
    {
        public const uint SByte = 0x02;
        public const uint Byte = 0x03;
        public const uint Int16 = 0x04;
        public const uint UInt16 = 0x05;
        public const uint Int32 = 0x06;
        public const uint UInt32 = 0x07;
        public const uint Guid = 0x0D;
        public const uint FileTime = 0x10;
        public const uint Boolean = 0x11;
        public const uint String = 0x12;
        public const uint StringList = 0x2012;

        public static string Describe(uint type) => type switch
        {
            0x00 => "EMPTY",
            0x01 => "NULL",
            SByte => "SBYTE",
            Byte => "BYTE",
            Int16 => "INT16",
            UInt16 => "UINT16",
            Int32 => "INT32",
            UInt32 => "UINT32",
            0x08 => "INT64",
            0x09 => "UINT64",
            0x0A => "FLOAT",
            0x0B => "DOUBLE",
            0x0C => "DECIMAL",
            Guid => "GUID",
            0x0E => "CURRENCY",
            0x0F => "DATE",
            FileTime => "FILETIME",
            Boolean => "BOOLEAN",
            String => "STRING",
            0x13 => "SECURITY_DESCRIPTOR",
            0x14 => "SECURITY_DESCRIPTOR_STRING",
            0x15 => "DEVPROPKEY",
            0x16 => "DEVPROPTYPE",
            0x17 => "ERROR",
            0x18 => "NTSTATUS",
            0x19 => "STRING_INDIRECT",
            0x1003 => "BINARY",
            StringList => "STRING_LIST",
            _ => $"0x{type:X4}",
        };
    }
}
