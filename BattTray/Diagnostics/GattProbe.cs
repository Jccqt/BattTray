using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BattTray.Devices;
using BattTray.Interop;
using Microsoft.Win32.SafeHandles;

namespace BattTray.Diagnostics;

/// <summary>
/// Asks every bonded LE device what its GATT attribute table holds, and reads back whatever
/// sits under the Battery Service. It exists because neither sweep before it can see a GATT
/// characteristic: <see cref="Probe"/> reads PnP device properties, and <see cref="HidProbe"/>
/// parses HID report descriptors. A characteristic is neither — it is state behind an ATT
/// handle, reachable only by asking the device for it.
/// </summary>
/// <remarks>
/// The point of the sweep is 0x2BED Battery Level Status, not 0x2A19 Battery Level. The
/// percentage is already covered: Windows copies one onto the device node, and
/// <see cref="BattTray.Devices.BluetoothPeripheralProvider"/> reads it there for nothing —
/// measurably staler, at 91% against the 93% a live read returned in the same minute, but the
/// same reading at a different age rather than a second source.
/// Charging state is the open question, and every route named in the roadmap has been closed by
/// measurement — no Bluetooth property carries a charging flag, no HID descriptor on this
/// machine declares one, and XInput's <c>BATTERY_TYPE_WIRED</c> turned out to mean "reachable
/// over USB" rather than "on a cable". 0x2BED is the one route that has never been checked, and
/// it is the one that would answer directly: charging state, charging type and fault reason in
/// a single two-byte field.
///
/// Same discipline as the two probes before it. Bytes go out untransformed next to this file's
/// reading of them, because the decode follows the GATT Specification Supplement and is the
/// half that can be wrong. An interface that would not open is reported with its error rather
/// than dropped, since "unread" and "no battery" are different answers and only keeping them
/// apart makes a negative result worth anything. And where a value came from is printed beside
/// it: a device with a live link is read from the device, one that is bonded but away is read
/// from the stack's cache, and a cached byte is what Windows last saw rather than what is true
/// now.
///
/// Descriptors are deliberately not enumerated. The one worth having would be 0x2904
/// Presentation Format, and every characteristic this probe cares about has its format fixed by
/// the specification instead — while a second round trip per characteristic is real cost paid
/// against a radio link.
/// </remarks>
internal static class GattProbe
{
    /// <summary>
    /// The two interface classes the LE stack publishes, and both are needed.
    /// </summary>
    /// <remarks>
    /// GUID_BLUETOOTHLE_DEVICE_INTERFACE is per bonded device, on the <c>BTHLE\Dev_*</c> parent
    /// node, and a handle on it enumerates every service the device holds.
    /// GUID_BLUETOOTH_GATT_SERVICE_DEVICE_INTERFACE is per service, on the
    /// <c>BTHLEDevice\{uuid}_*</c> children, and enumerates only its own.
    ///
    /// The split matters because reading a value is not the same operation as listing one.
    /// Measured on a connected controller: <c>BluetoothGATTGetCharacteristicValue</c> through
    /// the device-level handle fails with ERROR_INVALID_FUNCTION however live the link is,
    /// while the same characteristic through its own service handle answers immediately. A
    /// sweep on either class alone would therefore be wrong in one direction or the other — the
    /// device class alone reads nothing, and the service class alone cannot show that a device
    /// publishes no Battery Service at all, since a service with no node has no interface.
    /// </remarks>
    static readonly (Guid Class, string Label)[] InterfaceClasses =
    [
        (new("781aee18-7733-4ce4-add0-91f41c67b592"), "device"),
        (new("6e3bb679-4372-40c8-9eaa-4509df260cd8"), "service"),
    ];

    /// <summary>The Bluetooth SIG base UUID, with the 16-bit slot left zero.</summary>
    static readonly Guid SigBaseUuid = new("00000000-0000-1000-8000-00805f9b34fb");

    const ushort BatteryService = 0x180F;
    const ushort BatteryLevel = 0x2A19;
    const ushort BatteryLevelStatus = 0x2BED;

    /// <summary>The BDIF_* connected bits, as <see cref="BluetoothPeripheralProvider"/> reads them.</summary>
    const uint BdifConnected = 0x00000020;
    const uint BdifLeConnected = 0x01000000;

    /// <summary>PnP enumerators that host the nodes carrying a device's BDIF_* flag word.</summary>
    static readonly string[] Enumerators = ["BTHLE", "BTHLEDevice", "BTHENUM"];

    public static void Run(Action<string> write)
    {
        var stopwatch = Stopwatch.StartNew();
        var devices = Enumerate();
        long enumerateMs = stopwatch.ElapsedMilliseconds;

        try
        {
            // Timed apart from the enumeration for the same reason HidProbe splits its two:
            // listing the interfaces is free and opening them is not. Split again at the value
            // reads, because those are the only part that touches the radio — everything above
            // them comes out of the attribute table Windows already holds for a bonded device.
            stopwatch.Restart();
            foreach (var item in devices.SelectMany(device => device.Interfaces))
                Describe(item);
            long describeMs = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            foreach (var device in devices)
                ReadBatteryValues(device);
            long readMs = stopwatch.ElapsedMilliseconds;

            Report(write, devices, enumerateMs, describeMs, readMs);
        }
        finally
        {
            // Every handle is held until here because the read phase needs the one the describe
            // phase opened, and reopening would both double the cost and blur the two timings.
            foreach (var item in devices.SelectMany(device => device.Interfaces))
                item.Handle?.Dispose();
        }
    }

    /// <summary>
    /// Every LE device interface on the machine, grouped by the radio address behind it, with
    /// the identity and link evidence for each device joined in. Nothing is opened here.
    /// </summary>
    static List<LeDevice> Enumerate()
    {
        var paired = new Dictionary<ulong, BluetoothDeviceInfo>();
        foreach (var device in BluetoothApi.GetPairedDevices())
            paired.TryAdd(device.Address, device);

        var links = ReadLinkEvidence();
        var devices = new Dictionary<string, LeDevice>(StringComparer.OrdinalIgnoreCase);

        foreach ((Guid interfaceClass, string label) in InterfaceClasses)
        {
            foreach (string path in ConfigManager.GetDeviceInterfaces(interfaceClass))
            {
                string? instanceId = ReadInstanceId(path);
                uint devInst = instanceId is null ? 0 : ConfigManager.LocateDevNode(instanceId);
                ulong? address = ResolveAddress(devInst, instanceId ?? path);

                // An interface whose address cannot be resolved is still reported, under a key
                // of its own: it belongs to some device, and dropping it would be the one thing
                // this probe promises not to do.
                string key = address is { } value ? value.ToString("X12", CultureInfo.InvariantCulture) : path;

                if (!devices.TryGetValue(key, out var device))
                {
                    devices[key] = device = new LeDevice(address);

                    if (address is { } resolved && paired.TryGetValue(resolved, out var record))
                    {
                        device.PairedName = record.Name;
                        device.PairedConnected = record.IsConnected;
                    }

                    if (address is { } linked && links.TryGetValue(linked, out var evidence))
                        device.Link = evidence;
                }

                // Only the device-level node carries a name worth having; a service node is
                // called after its profile, which is the same mistake the Bluetooth provider
                // guards against on the Classic side.
                if (label == "device")
                    device.NodeName ??= ReadNodeName(devInst);

                device.Interfaces.Add(new GattInterface(path, instanceId, label));
            }
        }

        return [.. devices.Values.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The BDIF_* flag word per radio address, tagged with the node it came from, resolved the
    /// way <see cref="BluetoothPeripheralProvider"/> resolves it: link state belongs to the
    /// device rather than to whichever node happens to publish it, and one node reporting a
    /// live link is enough. It decides which flag each value read is made with, so it is
    /// printed as evidence rather than folded into a verdict.
    /// </summary>
    static Dictionary<ulong, LinkEvidence> ReadLinkEvidence()
    {
        var links = new Dictionary<ulong, LinkEvidence>();

        foreach (string enumerator in Enumerators)
        {
            foreach (string deviceId in ConfigManager.GetDeviceIds(enumerator))
            {
                uint devInst = ConfigManager.LocateDevNode(deviceId);
                if (devInst == 0 || ResolveAddress(devInst, deviceId) is not { } address)
                    continue;

                if (ConfigManager.GetUInt32(devInst, DevPropKeys.BluetoothDeviceFlags) is not { } flags)
                    continue;

                if (!links.TryGetValue(address, out var existing) || (IsLinkUp(flags) && !IsLinkUp(existing.Flags)))
                    links[address] = new LinkEvidence(deviceId, flags);
            }
        }

        return links;
    }

    /// <summary>
    /// Opens one interface and reads its service and characteristic tables. Both come out of
    /// what the stack already holds for a bonded device, so this costs no radio traffic.
    /// </summary>
    static void Describe(GattInterface item)
    {
        var handle = GattApi.Open(item.Path);
        if (handle.IsInvalid)
        {
            // Reported rather than skipped: a device that never opened has not been ruled out.
            item.OpenError = DescribeError(Marshal.GetLastPInvokeError());
            handle.Dispose();
            return;
        }

        item.Handle = handle;

        int hr = GattApi.GetServices(handle, out BTH_LE_GATT_SERVICE[] services);
        if (hr != GattApi.S_OK)
        {
            item.ServicesError = $"BluetoothGATTGetServices failed — {DescribeHResult(hr)}";
            return;
        }

        for (int index = 0; index < services.Length; index++)
        {
            var entry = new GattService(services[index]);
            item.Services.Add(entry);

            // Passed by ref, so the array is walked by index: a foreach variable is readonly
            // and the API takes the service struct as a pointer.
            int status = GattApi.GetCharacteristics(
                handle, ref services[index], out BTH_LE_GATT_CHARACTERISTIC[] characteristics);

            if (status != GattApi.S_OK)
            {
                entry.Error = $"BluetoothGATTGetCharacteristics failed — {DescribeHResult(status)}";
                continue;
            }

            entry.Characteristics.AddRange(characteristics.Select(characteristic => new GattCharacteristic(characteristic)));
        }
    }

    /// <summary>
    /// Reads every readable characteristic under the Battery Service, once per device.
    /// </summary>
    /// <remarks>
    /// Only 0x180F, and only what it declares readable. Sweeping every characteristic on the
    /// device would put a round trip on the radio for each of them and read strings and control
    /// points this probe has no question about; a notify-only characteristic would answer with
    /// an error that says nothing except that it should not have been asked.
    ///
    /// One characteristic can be reached through more than one interface, and the same value
    /// read twice is noise. So a handle is only skipped once a read through some other
    /// interface has actually <em>succeeded</em> — a failure is not allowed to consume the
    /// characteristic's one attempt, which is exactly how the device-level handle's refusal
    /// would otherwise hide a service handle that answers perfectly well.
    /// </remarks>
    static void ReadBatteryValues(LeDevice device)
    {
        // A device on a live link is asked; one that is bonded but away is not woken for a
        // diagnostics sweep, and its cached value is labelled as cached wherever it prints.
        uint flags = device.IsLinkUp ? GattApi.FlagForceReadFromDevice : GattApi.FlagForceReadFromCache;
        var read = new HashSet<ushort>();

        foreach (var item in device.OrderedInterfaces)
        {
            if (item.Handle is not { IsInvalid: false })
                continue;

            foreach (var service in item.Services.Where(service => service.IsBattery))
            {
                foreach (var characteristic in service.Characteristics)
                {
                    if (!characteristic.IsReadable)
                        continue;

                    if (read.Contains(characteristic.ValueHandle))
                    {
                        characteristic.Skipped = "already read through another interface of this device";
                        continue;
                    }

                    if (ReadValue(item, characteristic, flags))
                        read.Add(characteristic.ValueHandle);
                }
            }
        }
    }

    /// <summary>
    /// One characteristic value, asked for at the source the link state chose and then at the
    /// other one if that fails. Every attempt is recorded with the source it named, because
    /// which one answered is half of what the bytes mean.
    /// </summary>
    /// <remarks>
    /// The fallback is the difference between "this device publishes no charging state" and
    /// "nobody managed to ask it", which is the distinction the whole sweep rests on. It is
    /// bounded to one extra attempt per characteristic and picked to be the cheap direction
    /// where there is one: a device read that fails falls back to the cache, which is local and
    /// cannot block, while an empty cache falls back to letting Windows choose — the only
    /// attempt here that can reach for a radio, and reported as such wherever it lands.
    /// </remarks>
    /// <returns>True when either attempt came back with a value.</returns>
    static bool ReadValue(GattInterface item, GattCharacteristic characteristic, uint flags) =>
        Attempt(item, characteristic, flags)
        || Attempt(item, characteristic, flags == GattApi.FlagForceReadFromDevice
            ? GattApi.FlagForceReadFromCache
            : GattApi.FlagNone);

    /// <summary>
    /// One read at one source, with a second try through a GENERIC_READ handle if the
    /// zero-access one is refused outright. The reopened handle is kept, so the rest of the
    /// reads on that interface go through it too.
    /// </summary>
    /// <returns>True when the value was read, whatever it turned out to hold.</returns>
    static bool Attempt(GattInterface item, GattCharacteristic characteristic, uint flags)
    {
        int hr = GattApi.GetCharacteristicValue(item.Handle!, ref characteristic.Raw, flags, out byte[] value);

        if (hr == GattApi.ErrorAccessDenied && !item.ReopenedForRead)
        {
            item.ReopenedForRead = true;
            var reopened = GattApi.OpenForRead(item.Path);

            if (reopened.IsInvalid)
            {
                item.ReopenError = DescribeError(Marshal.GetLastPInvokeError());
                reopened.Dispose();
            }
            else
            {
                item.Handle!.Dispose();
                item.Handle = reopened;
                hr = GattApi.GetCharacteristicValue(item.Handle, ref characteristic.Raw, flags, out value);
            }
        }

        if (hr == GattApi.S_OK)
        {
            characteristic.Attempts.Add(new ValueAttempt(flags, null));
            characteristic.Value = value;
            characteristic.ReadFlags = flags;
            return true;
        }

        // The empty-cache case is named rather than left as "Incorrect function", which reads
        // like a broken call and is the one failure here that means nothing is wrong.
        string error = $"BluetoothGATTGetCharacteristicValue failed — {DescribeHResult(hr)}";
        if (flags == GattApi.FlagForceReadFromCache && hr == GattApi.ErrorInvalidFunction)
            error += ", which is how an empty cache answers: the stack has never held this value";

        characteristic.Attempts.Add(new ValueAttempt(flags, error));
        return false;
    }

    static void Report(
        Action<string> write, List<LeDevice> devices, long enumerateMs, long describeMs, long readMs)
    {
        var interfaces = devices.SelectMany(device => device.Interfaces).ToList();
        var flagged = devices.Where(device => device.HasBatteryService).ToList();
        var refused = interfaces.Where(item => !item.IsUsable).ToList();

        // Whether a refusal actually hides anything. A service interface that will not open is
        // one handle lost, not one service unaccounted for: its device's [device] interface
        // lists every service the device holds, so where that one answered, the service list is
        // complete regardless. Without this the HID service — held exclusively by the HID class
        // driver on every run — would put a caveat on every verdict that the data refutes.
        bool covered = devices.All(device =>
            device.Interfaces.All(item => item.IsUsable)
            || device.Interfaces.Any(item => !item.IsService && item.IsUsable));

        WriteHeader(write, devices, interfaces, refused.Count, enumerateMs, describeMs, readMs);
        WriteBatterySection(write, flagged);
        WriteRefusedSection(write, refused, covered);

        write("--- Every bonded LE device");
        write(string.Empty);

        if (devices.Count == 0)
        {
            write("  (nothing) — no GUID_BLUETOOTHLE_DEVICE_INTERFACE interface is present, so nothing");
            write("  on this machine is bonded over Bluetooth LE right now. That is a statement about");
            write("  what is paired, not about GATT.");
            write(string.Empty);
        }

        foreach (var device in devices)
            WriteDevice(write, device);

        WriteVerdict(write, devices, flagged, refused, covered);
    }

    static void WriteHeader(
        Action<string> write,
        List<LeDevice> devices,
        List<GattInterface> interfaces,
        int refused,
        long enumerateMs,
        long describeMs,
        long readMs)
    {
        write("=== Probe: every bonded LE device, every GATT service it publishes, and what 0x180F holds");
        write(string.Empty);
        write(string.Create(CultureInfo.InvariantCulture,
            $"  {devices.Count} LE device(s) behind {interfaces.Count} interface(s), "
            + $"{interfaces.Count - refused} usable, {refused} not. Enumerated in {enumerateMs} ms, "
            + $"services and characteristics read in {describeMs} ms, values read in {readMs} ms."));
        write("  The last figure is the only part that touches the radio: services and characteristics");
        write("  come out of the attribute table Windows already holds for a bonded device.");
        write(string.Empty);
        write("  Neither sweep before this one can see any of it. --probe reads PnP device properties");
        write("  and --probe-hid parses HID report descriptors; a GATT characteristic is neither, it is");
        write("  state behind an ATT handle that has to be asked for.");
        write(string.Empty);
        write("  Two characteristics are the reason this exists:");
        write("    0x2A19 Battery Level        — one byte, 0-100. Already covered: Windows copies this");
        write("                                  onto the device node, where the app reads it for free.");
        write("    0x2BED Battery Level Status — BAS 1.1, and the point. It carries charge state,");
        write("                                  charging type and fault reason in one 16-bit field,");
        write("                                  and is the only charging route not yet ruled out.");
        write(string.Empty);
        write("  Handles are opened with dwDesiredAccess = 0, which is enough for the service and");
        write("  characteristic tables. A value read refused on such a handle is retried once through");
        write("  a GENERIC_READ handle, and the retry is reported where it happened.");
        write(string.Empty);
        write("  Interfaces are tagged [device] or [service]. Windows publishes one of each class:");
        write("  [device] sits on the BTHLE\\Dev_* node and lists every service the device holds, and");
        write("  [service] sits on a BTHLEDevice\\{uuid}_* child and lists only its own. Both are swept");
        write("  because a value read only works through the second — a [device] handle answers one");
        write("  with ERROR_INVALID_FUNCTION however live the link is — while only the first can show");
        write("  that a device publishes no Battery Service at all, a service with no node having no");
        write("  interface to be found under.");
        write(string.Empty);
        write("  Where a value came from is printed beside it. A device with a live link is read with");
        write("  FORCE_READ_FROM_DEVICE; one that is bonded but away is read with FORCE_READ_FROM_CACHE");
        write("  rather than woken, and a cached byte is what Windows last saw and not what is true now.");
        write(string.Empty);
        write("  Raw bytes print first and untransformed. The decode below them follows the Bluetooth");
        write("  GATT Specification Supplement, and is this tool's reading rather than the device's word.");
        write(string.Empty);
    }

    static void WriteBatterySection(Action<string> write, List<LeDevice> flagged)
    {
        write("--- Devices publishing the Battery Service (0x180F)");
        write(string.Empty);

        if (flagged.Count == 0)
        {
            write("  (nothing) — no bonded LE device publishes 0x180F. This is a real answer and not a");
            write("  gap in the sweep: every device below was asked and none holds a Battery Service,");
            write("  so there is no GATT battery to read on this machine today.");
            write(string.Empty);
            return;
        }

        foreach (var device in flagged)
            WriteDevice(write, device);
    }

    static void WriteRefusedSection(Action<string> write, List<GattInterface> refused, bool covered)
    {
        write("--- Interfaces that could not be opened or enumerated");
        write(string.Empty);

        if (refused.Count == 0)
        {
            write("  (nothing) — every interface opened and gave up its service table, so no device is");
            write("  hiding behind a sharing error.");
            write(string.Empty);
            return;
        }

        write("  An interface that would not answer has not been ruled out: its attribute table was");
        write("  never read, and a battery service could be sitting behind it.");

        if (covered)
        {
            write(string.Empty);
            write("  Every device with one of these did answer through its [device] interface, which");
            write("  lists all of its services — so no service is hidden here. What is unknown is only");
            write("  what these handles would have read, and a service already listed elsewhere is not.");
        }

        write(string.Empty);

        foreach (var item in refused)
        {
            write($"  [{item.Label}] {item.Path}");
            write($"    instance : {item.InstanceId ?? "(the interface publishes no instance id)"}");
            write($"    failed   : {item.OpenError ?? item.ServicesError}");
            write(string.Empty);
        }
    }

    /// <summary>
    /// One device: who it is, whether its link is up, and every service and characteristic
    /// behind each of its interfaces. The same entry is printed in the battery section and in
    /// the full listing, so a flagged device reads identically wherever it is met.
    /// </summary>
    static void WriteDevice(Action<string> write, LeDevice device)
    {
        const int Label = 10;

        write($"  {device.Name}");
        write($"    {"address",-Label} : {device.AddressText}");
        write($"    {"link",-Label} : {DescribeLink(device)}");

        foreach (var item in device.OrderedInterfaces)
        {
            write($"    [{item.Label}] {item.Path}");
            write($"      instance : {item.InstanceId ?? "(the interface publishes no instance id)"}");

            if (item.OpenError is { } openError)
            {
                write($"      open     : failed — {openError}");
                continue;
            }

            if (item.ServicesError is { } servicesError)
            {
                write($"      services : {servicesError}");
                continue;
            }

            if (item.ReopenError is { } reopenError)
                write($"      reopen   : a value read was refused and GENERIC_READ failed too — {reopenError}");
            else if (item.ReopenedForRead)
                write("      reopen   : the zero-access handle was refused a value, reopened with GENERIC_READ");

            if (item.Services.Count == 0)
            {
                write("      services : (nothing) — the interface opened and reports no services at all");
                continue;
            }

            foreach (var service in item.Services)
                WriteService(write, service);
        }

        write(string.Empty);
    }

    static void WriteService(Action<string> write, GattService service)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"      service {DescribeUuid(service.Uuid)} at handle 0x{service.AttributeHandle:X4}");

        write(service.IsBattery ? line + "   <== BATTERY SERVICE" : line);

        if (service.Error is { } error)
        {
            write($"        {error}");
            return;
        }

        if (service.Characteristics.Count == 0)
        {
            write("        (nothing) — the service declares no characteristics");
            return;
        }

        foreach (var characteristic in service.Characteristics)
            WriteCharacteristic(write, characteristic, service.IsBattery);
    }

    static void WriteCharacteristic(Action<string> write, GattCharacteristic characteristic, bool inBatteryService)
    {
        bool flagged = inBatteryService && characteristic.Short is BatteryLevel or BatteryLevelStatus;

        string line = string.Create(CultureInfo.InvariantCulture,
            $"        char {DescribeUuid(characteristic.Uuid)} value handle 0x{characteristic.ValueHandle:X4}"
            + $"  {DescribeProperties(characteristic)}");

        write(flagged ? line + "   <==" : line);

        if (characteristic.Skipped is { } skipped)
        {
            write($"          {skipped}");
            return;
        }

        // Every attempt that failed, in the order they were made. A read that only succeeded
        // on the second source is a different fact from one that answered first time, and both
        // are different from a characteristic nobody could read at all.
        foreach (var attempt in characteristic.Attempts.Where(attempt => attempt.Error is not null))
            write($"          asked {DescribeReadFlags(attempt.Flags)}: {attempt.Error}");

        if (characteristic.Value is not { } value)
        {
            if (inBatteryService && !characteristic.IsReadable)
                write("          not readable, so no value was asked for");

            return;
        }

        write($"          raw    : {(value.Length == 0 ? "(zero bytes)" : Convert.ToHexString(value))}"
            + $"  [from {DescribeReadFlags(characteristic.ReadFlags)}]");

        foreach (string decoded in Decode(characteristic.Short, value))
            write($"          {decoded}");
    }

    static void WriteVerdict(
        Action<string> write,
        List<LeDevice> devices,
        List<LeDevice> flagged,
        List<GattInterface> refused,
        bool covered)
    {
        write("--- Verdict");
        write(string.Empty);

        var withStatus = flagged.Where(device => device.HasBatteryLevelStatus).ToList();
        var withLevel = flagged.Where(device => device.HasBatteryLevel).ToList();

        if (devices.Count == 0)
        {
            write("  Nothing is bonded over LE, so this sweep judged nothing. It is not evidence about");
            write("  GATT either way — run it again with a BLE peripheral bonded.");
            write(string.Empty);
            return;
        }

        if (flagged.Count == 0)
        {
            write(string.Create(CultureInfo.InvariantCulture,
                $"  None of the {devices.Count} bonded LE device(s) publishes the Battery Service. GATT has"));
            write("  nothing to offer this machine today — a fact about what is bonded, not about the");
            write("  approach, which is why this flag exists: the day one is bonded, the answer is one");
            write("  command away.");
        }
        else
        {
            write(string.Create(CultureInfo.InvariantCulture,
                $"  {flagged.Count} of {devices.Count} bonded LE device(s) publish 0x180F, "
                + $"{withLevel.Count} with 0x2A19 Battery Level and {withStatus.Count} with 0x2BED "
                + $"Battery Level Status."));
            write(string.Empty);

            if (withStatus.Count > 0)
            {
                write("  0x2BED is present, and it is the characteristic that carries charging state. That");
                write("  is the roadmap's Phase 3 question answered in the affirmative for this hardware:");
                write("  read the decoded charge state above against what the device is actually doing —");
                write("  plug it in, run the sweep again, and check the field moves before believing it.");
                write("  A provider built on this holds a GATT read rather than a property read, which is");
                write("  a cost the poll budget has never had to carry: compare the values figure in the");
                write("  header against the single-digit milliseconds IPeripheralProvider is polled in.");
            }
            else
            {
                write("  No device publishes 0x2BED, so GATT carries no charging state on this hardware.");
                write("  That closes the last route named for Phase 3 on the devices bonded today, and");
                write("  closes it by measurement rather than by assumption.");
                write(string.Empty);
                write("  0x2A19 is a percentage and nothing more, and Windows already copies one onto the");
                write("  device node, where the Bluetooth provider reads it for free. The two need not be");
                write("  the same number: the node's copy is refreshed on Windows' own schedule and says");
                write("  in a second property when that last happened, while a read here is current. So");
                write("  GATT buys freshness and not a new fact — compare the byte above against the one");
                write("  in the evidence section of a --once run before paying a connection for it.");
            }
        }

        if (refused.Count > 0)
        {
            write(string.Empty);
            write(string.Create(CultureInfo.InvariantCulture,
                $"  {refused.Count} interface(s) never answered — see the section above."));

            if (covered)
            {
                write("  Their devices were listed in full through a [device] interface even so, so the");
                write("  service lists above are complete.");
            }
            else
            {
                write("  Nothing else lists their services, so those devices are judged on a partial view.");
            }
        }

        var away = devices.Where(device => device.HasBatteryService && !device.IsLinkUp).ToList();
        if (away.Count > 0)
        {
            write(string.Empty);
            write(string.Create(CultureInfo.InvariantCulture,
                $"  {away.Count} of the flagged device(s) had no live link, so nothing above was read over"));
            write("  the air from them: the values are the stack's cache where it held one, and an error");
            write("  where it did not. Which services and characteristics exist is settled either way —");
            write("  that comes from the bond — but what one currently holds is not. Connect the device");
            write("  and run this again before drawing anything from a value.");
        }

        write(string.Empty);
    }

    /// <summary>
    /// This file's reading of a characteristic it knows the format of, line by line. Anything
    /// else returns nothing at all rather than a guess: the raw bytes are already printed, and
    /// a wrong decode beside them would be worse than none.
    /// </summary>
    static IEnumerable<string> Decode(ushort? uuid, byte[] value) => uuid switch
    {
        BatteryLevel => DecodeBatteryLevel(value),
        BatteryLevelStatus => DecodeBatteryLevelStatus(value),
        _ => [],
    };

    /// <summary>
    /// 0x2A19: a single byte, 0-100, and no charging state anywhere in it. A device reporting
    /// outside that range is quoted rather than clamped — the clamp belongs to the provider,
    /// and a probe that hid the value would hide the reason the provider needs one.
    /// </summary>
    internal static IEnumerable<string> DecodeBatteryLevel(byte[] value)
    {
        if (value.Length == 0)
            yield break;

        yield return value[0] <= 100
            ? string.Create(CultureInfo.InvariantCulture, $"battery level : {value[0]}%")
            : string.Create(CultureInfo.InvariantCulture,
                $"battery level : {value[0]}, which is outside the 0-100 the characteristic is defined over");

        if (value.Length > 1)
            yield return $"and {value.Length - 1} further byte(s) the characteristic does not define";
    }

    /// <summary>
    /// 0x2BED, per the GATT Specification Supplement: a flags byte, a 16-bit power state, then
    /// three optional fields the flags decide the presence of. The optional fields are what
    /// make a short read dangerous — reading a battery level out of bytes that were never sent
    /// would produce a confident number from nothing — so the length is checked against what
    /// the flags claim and a value that runs out says so and stops.
    /// </summary>
    internal static IEnumerable<string> DecodeBatteryLevelStatus(byte[] value)
    {
        if (value.Length < 3)
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"the characteristic needs at least 3 bytes (flags and power state) and {value.Length} arrived");
            yield break;
        }

        byte flags = value[0];
        bool hasIdentifier = (flags & 0x01) != 0;
        bool hasLevel = (flags & 0x02) != 0;
        bool hasAdditional = (flags & 0x04) != 0;

        yield return string.Create(CultureInfo.InvariantCulture,
            $"flags 0x{flags:X2}   : identifier {Present(hasIdentifier)}, battery level {Present(hasLevel)}, "
            + $"additional status {Present(hasAdditional)}");

        ushort state = BitConverter.ToUInt16(value, 1);

        yield return string.Create(CultureInfo.InvariantCulture, $"power state 0x{state:X4}:");
        yield return $"  battery present   : {((state & 0x0001) != 0 ? "yes" : "no")}";
        yield return $"  wired power       : {DescribeSource((state >> 1) & 0x03)}";
        yield return $"  wireless power    : {DescribeSource((state >> 3) & 0x03)}";
        yield return $"  charge state      : {DescribeChargeState((state >> 5) & 0x03)}   <== the answer";
        yield return $"  charge level      : {DescribeChargeLevel((state >> 7) & 0x03)}";
        yield return $"  charging type     : {DescribeChargingType((state >> 9) & 0x07)}";
        yield return $"  charging fault    : {DescribeFault((state >> 12) & 0x07)}";

        int offset = 3;

        if (hasIdentifier)
        {
            if (value.Length < offset + 2)
            {
                yield return "the flags declare an identifier and the value ends before it";
                yield break;
            }

            yield return string.Create(CultureInfo.InvariantCulture,
                $"identifier    : 0x{BitConverter.ToUInt16(value, offset):X4}");
            offset += 2;
        }

        if (hasLevel)
        {
            if (value.Length < offset + 1)
            {
                yield return "the flags declare a battery level and the value ends before it";
                yield break;
            }

            yield return string.Create(CultureInfo.InvariantCulture, $"battery level : {value[offset]}%");
            offset++;
        }

        if (hasAdditional)
        {
            if (value.Length < offset + 1)
            {
                yield return "the flags declare an additional status and the value ends before it";
                yield break;
            }

            byte additional = value[offset];
            yield return string.Create(CultureInfo.InvariantCulture,
                $"additional 0x{additional:X2}: service required {DescribeSource(additional & 0x03)}, "
                + $"battery fault {((additional & 0x04) != 0 ? "yes" : "no or unknown")}");
            offset++;
        }

        if (offset < value.Length)
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"and {value.Length - offset} byte(s) past what the flags account for");
        }
    }

    static string Present(bool present) => present ? "present" : "absent";

    /// <summary>The 2-bit "no / yes / unknown" enumeration, which several fields share.</summary>
    static string DescribeSource(int value) => value switch
    {
        0 => "no",
        1 => "yes",
        2 => "unknown",
        _ => "reserved",
    };

    static string DescribeChargeState(int value) => value switch
    {
        0 => "unknown",
        1 => "CHARGING",
        2 => "discharging (active)",
        _ => "discharging (inactive)",
    };

    static string DescribeChargeLevel(int value) => value switch
    {
        0 => "unknown",
        1 => "good",
        2 => "low",
        _ => "critical",
    };

    static string DescribeChargingType(int value) => value switch
    {
        0 => "unknown, or not charging",
        1 => "constant current",
        2 => "constant voltage",
        3 => "trickle",
        4 => "float",
        _ => "reserved",
    };

    static string DescribeFault(int value) => value == 0
        ? "none"
        : string.Join(", ", new[]
        {
            (value & 0x01) != 0 ? "battery" : null,
            (value & 0x02) != 0 ? "external power source" : null,
            (value & 0x04) != 0 ? "other" : null,
        }.OfType<string>());

    static string DescribeReadFlags(uint flags) => flags switch
    {
        GattApi.FlagForceReadFromDevice => "the device itself (FORCE_READ_FROM_DEVICE)",
        GattApi.FlagForceReadFromCache => "the stack's cache (FORCE_READ_FROM_CACHE)",
        _ => "whichever source Windows chose (no flags)",
    };

    /// <summary>The eight BOOLEAN properties, listing only the ones that are set.</summary>
    static string DescribeProperties(GattCharacteristic characteristic)
    {
        var raw = characteristic.Raw;

        string?[] properties =
        [
            raw.IsReadable != 0 ? "read" : null,
            raw.IsWritable != 0 ? "write" : null,
            raw.IsWritableWithoutResponse != 0 ? "write-no-response" : null,
            raw.IsSignedWritable != 0 ? "signed-write" : null,
            raw.IsNotifiable != 0 ? "notify" : null,
            raw.IsIndicatable != 0 ? "indicate" : null,
            raw.IsBroadcastable != 0 ? "broadcast" : null,
            raw.HasExtendedProperties != 0 ? "extended" : null,
        ];

        var declared = properties.OfType<string>().ToArray();
        return declared.Length == 0 ? "(no properties declared)" : string.Join(" ", declared);
    }

    static string DescribeLink(LeDevice device)
    {
        string paired = device.PairedConnected is { } connected
            ? $"BLUETOOTH_DEVICE_INFO.fConnected {(connected ? "TRUE" : "FALSE")}"
            : "no pairing record for this address";

        string flags = device.Link is { } evidence
            ? string.Create(CultureInfo.InvariantCulture,
                $"DEVPKEY_Bluetooth_DeviceFlags 0x{evidence.Flags:X8} on {evidence.DeviceId}")
            : "no node at this address publishes DEVPKEY_Bluetooth_DeviceFlags";

        return $"{(device.IsLinkUp ? "connected" : "no live link")} ({paired}; {flags})";
    }

    /// <summary>
    /// A UUID's assigned name where this file is sure of it, and the number alone otherwise —
    /// the same terms <see cref="Probe"/> names property keys on and for the same reason. There
    /// are hundreds of assigned UUIDs and a guessed name would send a reader to a table entry
    /// that does not exist.
    /// </summary>
    static string DescribeUuid(BTH_LE_UUID uuid)
    {
        if (ShortForm(uuid) is not { } assigned)
            return $"{{{uuid.LongUuid}}} (128-bit, vendor-defined)";

        string described = string.Create(CultureInfo.InvariantCulture, $"0x{assigned:X4}");

        if (KnownUuids.TryGetValue(assigned, out string? name))
            described += $" ({name})";

        // A vendor UUID that happens to expand from the SIG base is worth flagging as such, so
        // a reader is not left wondering why a 128-bit value printed as four digits.
        return uuid.IsShortUuid != 0 ? described : $"{described} (as a 128-bit SIG base UUID)";
    }

    /// <summary>
    /// The 16-bit assigned number behind a UUID, whether the stack handed it over as one or as
    /// the 128-bit expansion of it. Both forms turn up, and a probe that only matched the first
    /// would miss the Battery Service on a device that publishes it the long way round.
    /// </summary>
    internal static ushort? ShortForm(BTH_LE_UUID uuid)
    {
        if (uuid.IsShortUuid != 0)
            return uuid.ShortUuid;

        Span<byte> bytes = stackalloc byte[16];
        Span<byte> baseBytes = stackalloc byte[16];

        if (!uuid.LongUuid.TryWriteBytes(bytes) || !SigBaseUuid.TryWriteBytes(baseBytes))
            return null;

        // Everything past the first four bytes has to match the base UUID, and those four are
        // the assigned number in Guid's little-endian first field.
        if (!bytes[4..].SequenceEqual(baseBytes[4..]))
            return null;

        uint assigned = BitConverter.ToUInt32(bytes);
        return assigned <= ushort.MaxValue ? (ushort)assigned : null;
    }

    static bool IsLinkUp(uint flags) => (flags & (BdifConnected | BdifLeConnected)) != 0;

    /// <summary>Reads DEVPKEY_Device_InstanceId off an interface, which is what ties it to a node.</summary>
    static string? ReadInstanceId(string path)
    {
        if (ConfigManager.GetInterfaceRaw(path, DevPropKeys.InstanceId) is not { } property
            || property.Type != DevPropType.String)
        {
            return null;
        }

        return Encoding.Unicode.GetString(property.Bytes).TrimEnd('\0') is { Length: > 0 } instanceId
            ? instanceId
            : null;
    }

    static string? ReadNodeName(uint devInst) =>
        devInst == 0
            ? null
            : BluetoothPeripheralProvider.CleanNodeName(ConfigManager.GetString(devInst, DevPropKeys.FriendlyName))
              ?? BluetoothPeripheralProvider.CleanNodeName(ConfigManager.GetString(devInst, DevPropKeys.DeviceDesc));

    /// <summary>
    /// The node's radio address, falling back to the hex in the instance id exactly as
    /// <see cref="BluetoothPeripheralProvider"/> does — same regex, so a device is grouped here
    /// under the id the provider would know it by.
    /// </summary>
    static ulong? ResolveAddress(uint devInst, string deviceId)
    {
        if (devInst != 0
            && ConfigManager.GetString(devInst, DevPropKeys.BluetoothDeviceAddress) is { Length: > 0 } text
            && ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address))
        {
            return address;
        }

        var match = BluetoothPeripheralProvider.AddressInInstanceId().Match(deviceId);
        return match.Success
            && ulong.TryParse(match.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong fallback)
            ? fallback
            : null;
    }

    static string DescribeError(int error) =>
        $"GetLastError {error} ({new Win32Exception(error).Message})";

    /// <summary>An HRESULT with whatever the system has to say about it.</summary>
    static string DescribeHResult(int hr) =>
        $"HRESULT 0x{hr:X8} ({Marshal.GetExceptionForHR(hr)?.Message ?? "no message"})";

    /// <summary>
    /// The assigned UUIDs worth naming: the services a peripheral publishes, and the
    /// characteristics under the battery and device-information ones. The battery half is the
    /// point — a dump printing "0x2BED" where a reader needs "Battery Level Status" hides the
    /// finding it was run to produce.
    /// </summary>
    static readonly Dictionary<ushort, string> KnownUuids = new()
    {
        // Services.
        [0x1800] = "Generic Access",
        [0x1801] = "Generic Attribute",
        [0x1802] = "Immediate Alert",
        [0x1803] = "Link Loss",
        [0x1804] = "Tx Power",
        [0x180A] = "Device Information",
        [0x180F] = "Battery Service",
        [0x1812] = "Human Interface Device",
        [0x1813] = "Scan Parameters",

        // Generic Access and Generic Attribute.
        [0x2A00] = "Device Name",
        [0x2A01] = "Appearance",
        [0x2A04] = "Peripheral Preferred Connection Parameters",
        [0x2A05] = "Service Changed",

        // Battery Service. 0x2A1A predates BAS 1.1 and is deprecated; it is named so a device
        // that publishes one is recognised, and left undecoded because this file has not been
        // able to check a decode of it against hardware.
        [0x2A19] = "Battery Level",
        [0x2A1A] = "Battery Power State (deprecated)",
        [0x2BED] = "Battery Level Status",

        // Device Information.
        [0x2A23] = "System ID",
        [0x2A24] = "Model Number String",
        [0x2A25] = "Serial Number String",
        [0x2A26] = "Firmware Revision String",
        [0x2A27] = "Hardware Revision String",
        [0x2A28] = "Software Revision String",
        [0x2A29] = "Manufacturer Name String",
        [0x2A2A] = "IEEE 11073-20601 Regulatory Certification Data List",
        [0x2A50] = "PnP ID",

        // HID over GATT, which is how a BLE gamepad or keyboard reports input.
        [0x2A22] = "Boot Keyboard Input Report",
        [0x2A32] = "Boot Keyboard Output Report",
        [0x2A33] = "Boot Mouse Input Report",
        [0x2A4A] = "HID Information",
        [0x2A4B] = "Report Map",
        [0x2A4C] = "HID Control Point",
        [0x2A4D] = "Report",
        [0x2A4E] = "Protocol Mode",

        // Scan Parameters.
        [0x2A31] = "Scan Refresh",
        [0x2A4F] = "Scan Interval Window",
    };

    /// <summary>A BDIF_* flag word and the device instance id it was read from.</summary>
    sealed record LinkEvidence(string DeviceId, uint Flags);

    /// <summary>One read of one characteristic: the source it named, and how it went.</summary>
    sealed record ValueAttempt(uint Flags, string? Error);

    /// <summary>One bonded LE device, and every GATT interface Windows publishes for it.</summary>
    sealed class LeDevice(ulong? address)
    {
        public ulong? Address { get; } = address;

        /// <summary>The name on the pairing record, which is the clean product name where there is one.</summary>
        public string? PairedName { get; set; }

        public bool? PairedConnected { get; set; }

        /// <summary>The name of one of the device's PnP nodes, for a device with no pairing record.</summary>
        public string? NodeName { get; set; }

        public LinkEvidence? Link { get; set; }

        public List<GattInterface> Interfaces { get; } = [];

        /// <summary>
        /// The device's interfaces with the per-service ones first. Both the reads and the
        /// listing go through this, so what is printed is the order things were asked in — and
        /// the service handles are asked first because they are the ones that answer, which
        /// spares every battery characteristic a failed read it is only going to retry.
        /// </summary>
        public IEnumerable<GattInterface> OrderedInterfaces =>
            Interfaces.OrderByDescending(item => item.IsService);

        public string AddressText => Address is { } address
            ? address.ToString("X12", CultureInfo.InvariantCulture)
            : "(none, and none in the instance id either)";

        public string Name => PairedName is { Length: > 0 } paired ? paired : NodeName ?? AddressText;

        /// <summary>
        /// Both halves of the link question, ORed on purpose. A bonded LE device often has no
        /// pairing record at all, and a paired record can report fConnected false while the
        /// node flags say the LE link is up.
        /// </summary>
        public bool IsLinkUp =>
            (PairedConnected ?? false) || (Link is { } evidence && GattProbe.IsLinkUp(evidence.Flags));

        public IEnumerable<GattService> BatteryServices =>
            Interfaces.SelectMany(item => item.Services).Where(service => service.IsBattery);

        public bool HasBatteryService => BatteryServices.Any();

        public bool HasBatteryLevel => HasCharacteristic(BatteryLevel);

        public bool HasBatteryLevelStatus => HasCharacteristic(BatteryLevelStatus);

        bool HasCharacteristic(ushort uuid) =>
            BatteryServices.SelectMany(service => service.Characteristics).Any(item => item.Short == uuid);
    }

    /// <summary>
    /// One GUID_BLUETOOTHLE_DEVICE_INTERFACE path, the handle opened on it, and everything it
    /// gave up — including the ways it refused to.
    /// </summary>
    sealed class GattInterface(string path, string? instanceId, string label)
    {
        public string Path { get; } = path;

        public string? InstanceId { get; } = instanceId;

        /// <summary>Which of <see cref="InterfaceClasses"/> published it: "device" or "service".</summary>
        public string Label { get; } = label;

        /// <summary>
        /// Whether this is a per-service interface, which is the kind a value read needs. See
        /// <see cref="InterfaceClasses"/> for the measurement behind that.
        /// </summary>
        public bool IsService => Label == "service";

        public SafeFileHandle? Handle { get; set; }

        /// <summary>Null when the handle opened; the reason it did not otherwise.</summary>
        public string? OpenError { get; set; }

        /// <summary>Set when the handle opened but the service table behind it would not be read.</summary>
        public string? ServicesError { get; set; }

        public bool ReopenedForRead { get; set; }

        /// <summary>Set when the GENERIC_READ retry could not open a handle either.</summary>
        public string? ReopenError { get; set; }

        public List<GattService> Services { get; } = [];

        public bool IsUsable => OpenError is null && ServicesError is null;
    }

    /// <summary>One GATT service and the characteristics under it.</summary>
    sealed class GattService(BTH_LE_GATT_SERVICE service)
    {
        public BTH_LE_UUID Uuid { get; } = service.ServiceUuid;

        public ushort AttributeHandle { get; } = service.AttributeHandle;

        public string? Error { get; set; }

        public List<GattCharacteristic> Characteristics { get; } = [];

        public bool IsBattery { get; } = ShortForm(service.ServiceUuid) == BatteryService;
    }

    /// <summary>
    /// One characteristic, and the value read back where one was asked for. <see cref="Raw"/>
    /// is a field rather than a property because the API takes it by reference.
    /// </summary>
    sealed class GattCharacteristic(BTH_LE_GATT_CHARACTERISTIC characteristic)
    {
        public BTH_LE_GATT_CHARACTERISTIC Raw = characteristic;

        public BTH_LE_UUID Uuid { get; } = characteristic.CharacteristicUuid;

        /// <summary>The 16-bit assigned number, or null for a genuinely vendor-defined UUID.</summary>
        public ushort? Short { get; } = ShortForm(characteristic.CharacteristicUuid);

        public ushort ValueHandle { get; } = characteristic.CharacteristicValueHandle;

        public bool IsReadable { get; } = characteristic.IsReadable != 0;

        public byte[]? Value { get; set; }

        /// <summary>Every read made for this characteristic, failures included, in order.</summary>
        public List<ValueAttempt> Attempts { get; } = [];

        /// <summary>Why no read was made, where the reason is not "it is not readable".</summary>
        public string? Skipped { get; set; }

        /// <summary>The flags the successful read used, which say where <see cref="Value"/> came from.</summary>
        public uint ReadFlags { get; set; }
    }
}
