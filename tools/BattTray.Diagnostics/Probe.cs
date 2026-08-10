using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BattTray.Interop;

namespace BattTray.Diagnostics;

/// <summary>
/// Asks every present device node and device interface what properties it publishes, rather
/// than asking known nodes for known properties. It exists to answer one question before a
/// second provider is written: does Windows already hold a battery percentage for USB and
/// wireless-dongle peripherals the way it does for Bluetooth ones? If it does, the next
/// provider is a near copy of <see cref="BattTray.Devices.BluetoothPeripheralProvider"/>; if
/// it does not, that is worth knowing before a day is spent assuming otherwise.
/// </summary>
/// <remarks>
/// Nothing here decodes a candidate into a percentage. It prints the key coordinates and the
/// bytes, and leaves the judgement to whoever reads the dump against what the vendor app
/// shows at the same moment — a byte that reads 64 next to a vendor app showing 100 is a
/// scale, not a percentage, and only the comparison can tell.
///
/// Nodes and interfaces are swept separately and reported separately, because they are
/// separate property stores rather than two views of one: a node carries what the PnP tree
/// knows about the device, an interface what a driver chose to publish alongside the handle
/// it hands out. The same tiers run over both, so a candidate is judged the same way
/// wherever it turns up.
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

    /// <summary>
    /// The format GUID holding DEVPKEY_Device_InstanceId, which is what ties an interface back
    /// to the node that owns it.
    /// </summary>
    static readonly Guid InstanceFormatGuid = new("78c34fc8-104a-4aca-9ea4-524d52996e57");

    /// <summary>The format GUID the DEVPKEY_DeviceInterface_* block lives under.</summary>
    static readonly Guid InterfaceFormatGuid = new("026e516e-b814-414b-83cd-856d6fef4822");

    /// <summary>Setup class names worth dumping in full. Anything hosting a peripheral.</summary>
    static readonly HashSet<string> PeripheralClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "HIDClass", "Mouse", "Keyboard", "Bluetooth", "USB", "USBDevice", "MEDIA",
        "AudioEndpoint", "XnaComposite", "XboxComposite", "Camera", "Image", "WPD", "Battery",
    };

    public static void Run(Action<string> write, bool dumpEveryNode)
    {
        var stopwatch = Stopwatch.StartNew();
        var nodes = SweepNodes();
        long nodeElapsed = stopwatch.ElapsedMilliseconds;

        // Interfaces are swept second because each is filed under the node that owns it, and
        // that owner has to be in hand before the link can be made.
        stopwatch.Restart();
        var interfaces = SweepInterfaces(nodes);
        long interfaceElapsed = stopwatch.ElapsedMilliseconds;

        ReportSweep(write, "device node", nodes, nodeElapsed, dumpEveryNode);
        ReportSweep(write, "device interface", interfaces, interfaceElapsed, dumpEveryNode);
    }

    /// <summary>Reads every property of every present node, keeping the bytes untransformed.</summary>
    static List<ProbeNode> SweepNodes()
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
    /// The same sweep across every present device interface, resolved back to the node that
    /// owns it. That link is most of the value here: an interface path names a driver and a
    /// class GUID and nothing a human recognises, so without the owner a candidate would be
    /// found on "\\?\HID#VID_2DC8..." with no way to tell whose battery it is.
    /// </summary>
    static List<ProbeInterface> SweepInterfaces(List<ProbeNode> nodes)
    {
        var owners = new Dictionary<string, ProbeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
            owners[node.DeviceId] = node;

        var interfaces = new List<ProbeInterface>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var interfaceClass in ConfigManager.GetInterfaceClasses())
        {
            foreach (string path in ConfigManager.GetDeviceInterfaces(interfaceClass))
            {
                // One interface can be listed under more than one class, and counting it
                // twice would inflate every figure in the frequency table it feeds.
                if (!seen.Add(path))
                    continue;

                var properties = new List<ProbeProperty>();

                foreach (var key in ConfigManager.GetInterfacePropertyKeys(path))
                {
                    if (ConfigManager.GetInterfaceRaw(path, key) is { } property)
                        properties.Add(new ProbeProperty(key, property.Type, property.Bytes));
                }

                var owner = Text(properties, InstanceFormatGuid, 256) is { } instanceId
                    ? owners.GetValueOrDefault(instanceId)
                    : null;

                interfaces.Add(new ProbeInterface(path, interfaceClass, properties, owner));
            }
        }

        return interfaces;
    }

    /// <summary>
    /// One sweep's worth of output: the counts, the three tiers, then the full dump.
    /// <paramref name="noun"/> names what was swept and is pluralised by suffix, so the two
    /// sections read as themselves rather than as one section run twice.
    /// </summary>
    static void ReportSweep(
        Action<string> write,
        string noun,
        IReadOnlyList<ProbeSubject> subjects,
        long elapsedMs,
        bool dumpEveryNode)
    {
        int properties = subjects.Sum(subject => subject.Properties.Count);

        // How many subjects publish each key. A battery property is per-device by nature, so a
        // key on a third of the machine is furniture however percentage-shaped its value looks —
        // and that is not obvious from one line, which is where a candidate is judged.
        var frequency = subjects
            .SelectMany(subject => subject.Properties.Select(property => property.Key))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());

        write($"=== Probe: every present {noun}, every property key it publishes");
        write(string.Empty);
        write(string.Create(CultureInfo.InvariantCulture,
            $"  {subjects.Count} {noun}s, {properties} properties, {frequency.Count} distinct keys, "
            + $"swept in {elapsedMs} ms."));
        write($"  In the tiers below, xN after a key means N of those {noun}s publish it.");
        write(string.Empty);

        var candidates = ReportBatteryShaped(write, noun, subjects, frequency);
        ReportFullDump(write, noun, subjects, dumpEveryNode, candidates);
    }

    /// <summary>
    /// The three shapes a battery reading could plausibly take, loosest last. Split into tiers
    /// because the last one is noisy by construction — plenty of unrelated counters sit in
    /// 0-100 — and a tier that has to be waded through is worth less than one that does not.
    /// </summary>
    /// <returns>Every subject that hit a tier, so the full dump can guarantee they appear there too.</returns>
    static HashSet<ProbeSubject> ReportBatteryShaped(
        Action<string> write,
        string noun,
        IReadOnlyList<ProbeSubject> subjects,
        Dictionary<DevPropKey, int> frequency)
    {
        write("--- Tier 1: keys under the battery format GUID (any property id)");
        var tier1 = Report(write, subjects, frequency,
            subject => subject.Properties.Where(property => property.Key.FormatId == BatteryFormatGuid));

        // The Bluetooth provider already reads these, so the count that matters is the one
        // outside the Bluetooth enumerators: that is the part of this probe that would make
        // the next provider a near copy of the one already written.
        int elsewhere = tier1.Count(subject => !subject.IsBluetooth);
        if (tier1.Count > 0)
        {
            write(elsewhere > 0
                ? $"  {elsewhere} of these are NOT Bluetooth {noun}s — read those first."
                : $"  All of these are Bluetooth {noun}s, already covered by the existing provider.");
        }

        write(string.Empty);

        write("--- Tier 2: DEVPROP_TYPE_BYTE properties holding 0-100");
        write("  A single byte is a rare property type, which makes a percentage-shaped one a strong signal.");
        var tier2 = Report(write, subjects, frequency, subject => subject.Properties.Where(IsPercentageByte));
        write(string.Empty);

        write($"--- Tier 3: 16/32-bit integers holding 1-100, on peripheral-looking {noun}s,");
        write("            excluding keys documented in devpkey.h as something else.");
        write("  Noisy even so — unrelated counters live here. Corroborate before believing one.");
        var tier3 = Report(write, subjects, frequency,
            subject => subject.IsPeripheral ? subject.Properties.Where(IsPercentageInteger).Where(IsUndocumented) : []);
        write(string.Empty);

        return [.. tier1, .. tier2, .. tier3];
    }

    /// <summary>
    /// Prints every subject with at least one matching property, rarest key first, and returns
    /// those subjects so the caller can say something about the shape of the result.
    /// </summary>
    static List<ProbeSubject> Report(
        Action<string> write,
        IReadOnlyList<ProbeSubject> subjects,
        Dictionary<DevPropKey, int> frequency,
        Func<ProbeSubject, IEnumerable<ProbeProperty>> select)
    {
        var matched = subjects
            .Select(subject => (Subject: subject, Hits: select(subject).ToList()))
            .Where(match => match.Hits.Count > 0)
            // A key on one subject is the interesting kind; ordering by that spares a reader
            // scrolling past forty repetitions of the same piece of Bluetooth furniture.
            .OrderBy(match => match.Hits.Min(hit => frequency.GetValueOrDefault(hit.Key)))
            .ToList();

        foreach (var (subject, hits) in matched)
            WriteSubject(write, subject, hits, frequency);

        if (matched.Count == 0)
            write("  (nothing)");

        return matched.Select(match => match.Subject).ToList();
    }

    /// <summary>
    /// The full key list for subjects that look like peripherals — the section to read when the
    /// tiers above come back empty, since a battery property Windows exposes under a name
    /// nobody has published would show up here and nowhere else.
    /// </summary>
    /// <remarks>
    /// Every subject from a tier is included whether or not it looks like a peripheral. The node
    /// that carries a headset's battery is filed under setup class "System" with a name no
    /// keyword matches, so the heuristic alone would answer "what else is on the node this
    /// candidate came from?" with silence, for exactly the nodes a reader is here to judge.
    /// </remarks>
    static void ReportFullDump(
        Action<string> write,
        string noun,
        IReadOnlyList<ProbeSubject> subjects,
        bool dumpEveryNode,
        HashSet<ProbeSubject> candidates)
    {
        write(dumpEveryNode
            ? $"--- Full dump: every {noun}"
            : $"--- Full dump: {noun}s that hit a tier above, plus anything that looks like a"
            + " peripheral (--all for the rest)");
        write(string.Empty);

        foreach (var subject in subjects.Where(s => dumpEveryNode || s.IsPeripheral || candidates.Contains(s)))
            WriteSubject(write, subject, subject.Properties, frequency: null);
    }

    /// <summary>
    /// One subject and the properties of it worth printing. <paramref name="frequency"/> is null
    /// for the full dump, where every key would carry a count and none of them would stand out.
    /// </summary>
    static void WriteSubject(
        Action<string> write,
        ProbeSubject subject,
        IReadOnlyList<ProbeProperty> properties,
        Dictionary<DevPropKey, int>? frequency)
    {
        const int Column = 48;

        write($"  {subject.Name}");

        foreach (var (label, value) in subject.Header)
            write($"    {label,-9}: {value}");

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

    /// <summary>A DEVPKEY read as a string, or null if the subject does not publish it as one.</summary>
    static string? Text(IReadOnlyList<ProbeProperty> properties, Guid formatId, uint propertyId)
    {
        var match = properties.FirstOrDefault(property =>
            property.Key.FormatId == formatId
            && property.Key.PropertyId == propertyId
            && property.Type == DevPropTypes.String);

        return match is null ? null : ReadString(match.Bytes) is { Length: > 0 } text ? text : null;
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

    /// <summary>
    /// An interface class GUID's SDK name, on the same terms as <see cref="Describe"/>: only
    /// the ones this file is certain of are named. There are hundreds of classes and no
    /// enumerable table of their names, so the rest print as GUIDs and are looked up by hand.
    /// </summary>
    static string DescribeInterfaceClass(Guid interfaceClass) =>
        KnownInterfaceClasses.TryGetValue(interfaceClass, out string? name)
            ? $"{{{interfaceClass}}} ({name})"
            : $"{{{interfaceClass}}}";

    static readonly Dictionary<Guid, string> KnownInterfaceClasses = new()
    {
        [new("4d1e55b2-f16f-11cf-88cb-001111000030")] = "GUID_DEVINTERFACE_HID",
        [new("a5dcbf10-6530-11d2-901f-00c04fb951ed")] = "GUID_DEVINTERFACE_USB_DEVICE",
        [new("f18a0e88-c30c-11d0-8815-00a0c906bed8")] = "GUID_DEVINTERFACE_USB_HUB",
        [new("72631e54-78a4-11d0-bcf7-00aa00b7b32a")] = "GUID_DEVINTERFACE_BATTERY",
        [new("884b96c3-56ef-11d1-bc8c-00a0c91405dd")] = "GUID_DEVINTERFACE_KEYBOARD",
        [new("378de44c-56ef-11d1-bc8c-00a0c91405dd")] = "GUID_DEVINTERFACE_MOUSE",
        [new("0850302a-b344-4fda-9be9-90576b8d46f0")] = "GUID_BTHPORT_DEVICE_INTERFACE",
    };

    static readonly Dictionary<(Guid, uint), string> KnownKeys = BuildKnownKeys();

    static Dictionary<(Guid, uint), string> BuildKnownKeys()
    {
        var status = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7");
        var deviceEx = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2");
        var container = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");
        var origin = new Guid("80497100-8c73-48b9-aad9-ce387e19c56e");
        var bluetooth = new Guid("2bd67d8b-8beb-48d5-87e0-6cda3428040a");

        var keys = new Dictionary<(Guid, uint), string>
        {
            [(InstanceFormatGuid, 256)] = "DEVPKEY_Device_InstanceId",
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
        Add(DeviceFormatGuid, "DEVPKEY_Device_", deviceNames);
        Add(status, "DEVPKEY_Device_", statusNames);

        // The interface block is the same shape, and worth naming for the same reason: without
        // it every interface in the dump leads with five lines of unresolved GUID.
        Add(InterfaceFormatGuid, "DEVPKEY_DeviceInterface_",
        [
            "FriendlyName", "Enabled", "ClassGuid", "ReferenceString", "Restricted", "",
            "UnrestrictedAppCapabilities", "SchematicName",
        ]);

        // The HID block, checked against this machine rather than taken on trust: VendorId and
        // ProductId matched the VID_/PID_ in every interface path, VersionNumber matched the
        // REV_ in the hardware id, and UsagePage/UsageId matched what each node calls itself
        // (mouse 01/02, keyboard 01/06, consumer control 0C/01, vendor-defined FFA0/01).
        // Naming them is what makes a HID battery findable: the Power Device page is 0x84 and
        // the Battery System page 0x85, and those two numbers are the thing to grep this dump
        // for once a device that reports charge over HID is plugged in.
        Add(new Guid("cbf38310-4a17-4310-a1eb-247f0b67593b"), "DEVPKEY_DeviceInterface_HID_",
            ["UsagePage", "UsageId", "IsReadOnly", "VendorId", "ProductId", "VersionNumber"]);

        return keys;

        void Add(Guid formatId, string prefix, string[] names)
        {
            for (uint index = 0; index < names.Length; index++)
            {
                if (names[index].Length > 0)
                    keys[(formatId, index + 2)] = prefix + names[index];
            }
        }
    }

    /// <summary>One property exactly as the node reported it.</summary>
    sealed record ProbeProperty(DevPropKey Key, uint Type, byte[] Bytes);

    /// <summary>
    /// Something that publishes properties. The tiers, the frequency table and the dump all
    /// work through this, so a node and an interface are judged by exactly the same rules and
    /// only the identity lines at the top of an entry differ.
    /// </summary>
    abstract class ProbeSubject
    {
        public abstract string Name { get; }

        public abstract IReadOnlyList<ProbeProperty> Properties { get; }

        public abstract bool IsBluetooth { get; }

        /// <summary>Whether the subject is worth a full dump.</summary>
        public abstract bool IsPeripheral { get; }

        /// <summary>The identity lines printed under the name, label and value per line.</summary>
        public abstract (string Label, string Value)[] Header { get; }
    }

    /// <summary>One device node and everything it publishes.</summary>
    sealed class ProbeNode(string deviceId, List<ProbeProperty> properties) : ProbeSubject
    {
        public string DeviceId { get; } = deviceId;

        public override IReadOnlyList<ProbeProperty> Properties { get; } = properties;

        /// <summary>The enumerator, which is the leading segment of every device instance id.</summary>
        public string Enumerator { get; } = deviceId.Split('\\')[0];

        public override string Name { get; } =
            Text(properties, DeviceFormatGuid, 14) ?? Text(properties, DeviceFormatGuid, 2) ?? "(unnamed)";

        /// <summary>DEVPKEY_Device_Class, e.g. "HIDClass".</summary>
        public string? Class { get; } = Text(properties, DeviceFormatGuid, 9);

        public override bool IsBluetooth =>
            Enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Setup class first, since it is Windows' own classification; the name pattern then
        /// catches what the class misses — a headset sitting under a vendor class, say — at the
        /// price of the odd false positive, which in a dump costs nothing but a few lines.
        /// </summary>
        public override bool IsPeripheral =>
            (Class is not null && PeripheralClasses.Contains(Class)) || PeripheralName().IsMatch(Name);

        public override (string Label, string Value)[] Header =>
            [("instance", DeviceId), ("class", Class ?? "(none)")];
    }

    /// <summary>One device interface, everything it publishes, and the node that owns it.</summary>
    sealed class ProbeInterface(
        string path, Guid interfaceClass, List<ProbeProperty> properties, ProbeNode? owner) : ProbeSubject
    {
        public override IReadOnlyList<ProbeProperty> Properties { get; } = properties;

        /// <summary>
        /// The owning node, or null when the interface names an instance id no present node
        /// answers to — which happens as devices come and go between the two sweeps.
        /// </summary>
        public ProbeNode? Owner { get; } = owner;

        /// <summary>
        /// The interface's own friendly name, falling back to the owner's. Interfaces mostly
        /// publish no name of their own, and "(unnamed)" repeated four hundred times would make
        /// the dump unreadable for the sake of a distinction nobody reading it needs.
        /// </summary>
        public override string Name { get; } =
            Text(properties, InterfaceFormatGuid, 2) ?? owner?.Name ?? "(unnamed)";

        /// <summary>
        /// Taken from the owner rather than judged again. An interface path carries a class
        /// GUID and a driver's idea of a name, neither of which the peripheral heuristic can
        /// read; the node behind it is where that question was already answered.
        /// </summary>
        public override bool IsPeripheral => Owner?.IsPeripheral ?? false;

        public override bool IsBluetooth =>
            Owner?.IsBluetooth ?? path.Contains(@"\BTH", StringComparison.OrdinalIgnoreCase);

        public override (string Label, string Value)[] Header =>
        [
            ("interface", path),
            ("owner", Owner is null ? "(no present node claims it)" : $"{Owner.DeviceId} [{Owner.Class ?? "no class"}]"),
            ("class", DescribeInterfaceClass(interfaceClass)),
        ];
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
