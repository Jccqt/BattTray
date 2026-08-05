using System.Runtime.InteropServices;

namespace BattTray.Interop;

/// <summary>Win32 DEVPROPKEY: a property format GUID plus a property id.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct DevPropKey(Guid formatId, uint propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly uint PropertyId = propertyId;
}

/// <summary>The DEVPROP_TYPE_* values we actually read.</summary>
internal static class DevPropType
{
    public const uint Byte = 0x03;
    public const uint Guid = 0x0D;
    public const uint FileTime = 0x10;
    public const uint Boolean = 0x11;
    public const uint String = 0x12;
}

/// <summary>Known DEVPROPKEYs. Names match the SDK headers so they can be looked up.</summary>
internal static class DevPropKeys
{
    static readonly Guid DeviceGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly Guid DeviceExGuid = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2");
    static readonly Guid ContainerGuid = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");
    static readonly Guid BluetoothGuid = new("2bd67d8b-8beb-48d5-87e0-6cda3428040a");

    /// <summary>
    /// The undocumented-but-stable key Windows Settings itself uses to show Bluetooth
    /// battery. Populated by the HFP/GATT stack; survives disconnects as a cached value,
    /// which is why <see cref="BluetoothBatteryLastUpdated"/> matters.
    /// </summary>
    static readonly Guid BluetoothBatteryGuid = new("104ea319-6ee2-4701-bd47-8ddbf425bbe5");

    public static readonly DevPropKey DeviceDesc = new(DeviceGuid, 2);
    public static readonly DevPropKey FriendlyName = new(DeviceGuid, 14);
    public static readonly DevPropKey ContainerId = new(ContainerGuid, 2);
    public static readonly DevPropKey IsPresent = new(DeviceExGuid, 5);

    public static readonly DevPropKey BluetoothDeviceAddress = new(BluetoothGuid, 1);
    public static readonly DevPropKey BluetoothBattery = new(BluetoothBatteryGuid, 2);
    public static readonly DevPropKey BluetoothBatteryLastUpdated = new(BluetoothBatteryGuid, 7);
}
