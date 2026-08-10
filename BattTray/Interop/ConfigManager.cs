using System.Runtime.InteropServices;
using System.Text;

namespace BattTray.Interop;

/// <summary>
/// Thin wrapper over cfgmgr32.dll for walking PnP device nodes and reading their
/// device properties. Chosen over the WinRT enumeration APIs because it needs no
/// projection assemblies and never triggers a Bluetooth radio scan: a full sweep of
/// the Bluetooth enumerators costs single-digit milliseconds.
/// </summary>
internal static class ConfigManager
{
    const uint CR_SUCCESS = 0;
    const uint CR_INVALID_DATA = 0x0000001F;
    const uint CM_GETIDLIST_FILTER_NONE = 0x00000000;
    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    const uint CM_ENUMERATE_CLASSES_INTERFACE = 0x00000001;
    const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000000;

    /// <summary>
    /// Device instance ids beneath a PnP enumerator, e.g. "BTHENUM", or of every present
    /// device when <paramref name="enumerator"/> is null.
    /// </summary>
    public static string[] GetDeviceIds(string? enumerator = null)
    {
        uint filter = enumerator is null ? CM_GETIDLIST_FILTER_NONE : CM_GETIDLIST_FILTER_ENUMERATOR;

        if (CM_Get_Device_ID_List_SizeW(out uint length, enumerator, filter) != CR_SUCCESS || length == 0)
            return [];

        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(enumerator, buffer, length, filter) != CR_SUCCESS)
            return [];

        // The list is a double-null-terminated sequence of strings.
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Every property key a device node publishes. The reading side of this class asks for
    /// keys it already knows; this asks the node what it has, which is the only way to find a
    /// battery property nobody has documented a name for.
    /// </summary>
    public static DevPropKey[] GetPropertyKeys(uint devInst)
    {
        if (devInst == 0)
            return [];

        uint count = 0;
        // First call sizes the array; it is expected to fail with CR_BUFFER_SMALL.
        CM_Get_DevNode_Property_Keys(devInst, null, ref count, 0);
        if (count == 0)
            return [];

        var keys = new DevPropKey[count];
        if (CM_Get_DevNode_Property_Keys(devInst, keys, ref count, 0) != CR_SUCCESS)
            return [];

        // A node can shed a property between the two calls, leaving the tail unwritten.
        return count == keys.Length ? keys : keys[..(int)count];
    }

    /// <summary>
    /// Every device interface class registered on the machine. Interfaces cannot be listed
    /// directly — one is only reachable through the class it belongs to — so a sweep that
    /// wants all of them has to walk the classes first.
    /// </summary>
    public static Guid[] GetInterfaceClasses()
    {
        var classes = new List<Guid>();

        // The list is indexed rather than counted, so the only way to learn its length is to
        // walk off the end of it.
        for (uint index = 0; ; index++)
        {
            uint result = CM_Enumerate_Classes(index, out Guid classGuid, CM_ENUMERATE_CLASSES_INTERFACE);

            // A malformed entry reports CR_INVALID_DATA and is stepped over rather than
            // stopped at: the indexes past it are still good, and treating it as the end
            // would truncate the sweep silently.
            if (result == CR_INVALID_DATA)
                continue;

            // CR_NO_SUCH_VALUE is how the walk ends; anything else is a failure, and both
            // mean the same thing here — no more classes are coming.
            if (result != CR_SUCCESS)
                return [.. classes];

            classes.Add(classGuid);
        }
    }

    /// <summary>
    /// Paths of every present interface in a class, in the
    /// <c>\\?\HID#VID_2DC8&amp;PID_301C#...#{4d1e55b2-...}</c> form the property calls below
    /// take.
    /// </summary>
    public static string[] GetDeviceInterfaces(Guid interfaceClass)
    {
        const uint Flags = CM_GET_DEVICE_INTERFACE_LIST_PRESENT;

        if (CM_Get_Device_Interface_List_SizeW(out uint length, ref interfaceClass, null, Flags) != CR_SUCCESS
            || length == 0)
        {
            return [];
        }

        var buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(ref interfaceClass, null, buffer, length, Flags) != CR_SUCCESS)
            return [];

        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Every property key a device interface publishes. The interface half of
    /// <see cref="GetPropertyKeys"/>, and a separate store from it: a node and its interfaces
    /// each carry their own properties, so a value absent from one can still be on the other.
    /// </summary>
    public static DevPropKey[] GetInterfacePropertyKeys(string interfacePath)
    {
        uint count = 0;
        // First call sizes the array; it is expected to fail with CR_BUFFER_SMALL.
        CM_Get_Device_Interface_Property_KeysW(interfacePath, null, ref count, 0);
        if (count == 0)
            return [];

        var keys = new DevPropKey[count];
        if (CM_Get_Device_Interface_Property_KeysW(interfacePath, keys, ref count, 0) != CR_SUCCESS)
            return [];

        // An interface can shed a property between the two calls, leaving the tail unwritten.
        return count == keys.Length ? keys : keys[..(int)count];
    }

    /// <summary>Resolves a device instance id to a devnode handle, or 0 if it is gone.</summary>
    public static uint LocateDevNode(string deviceId) =>
        CM_Locate_DevNodeW(out uint devInst, deviceId, 0) == CR_SUCCESS ? devInst : 0;

    public static string? GetString(uint devInst, DevPropKey key) =>
        Read(devInst, key, DevPropType.String) is { } bytes
            ? Encoding.Unicode.GetString(bytes).TrimEnd('\0')
            : null;

    public static byte? GetByte(uint devInst, DevPropKey key) =>
        Read(devInst, key, DevPropType.Byte) is { Length: > 0 } bytes ? bytes[0] : null;

    public static uint? GetUInt32(uint devInst, DevPropKey key) =>
        Read(devInst, key, DevPropType.UInt32) is { Length: >= 4 } bytes ? BitConverter.ToUInt32(bytes) : null;

    public static bool? GetBoolean(uint devInst, DevPropKey key) =>
        Read(devInst, key, DevPropType.Boolean) is { Length: > 0 } bytes ? bytes[0] != 0 : null;

    public static Guid? GetGuid(uint devInst, DevPropKey key) =>
        Read(devInst, key, DevPropType.Guid) is { Length: >= 16 } bytes ? new Guid(bytes.AsSpan(0, 16)) : null;

    /// <summary>Reads a FILETIME property as UTC.</summary>
    public static DateTime? GetFileTimeUtc(uint devInst, DevPropKey key)
    {
        if (Read(devInst, key, DevPropType.FileTime) is not { Length: >= 8 } bytes)
            return null;

        long ticks = BitConverter.ToInt64(bytes);
        if (ticks <= 0)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// A property exactly as the device node reported it. The diagnostics tool prints these
    /// untransformed, so a wrong percentage can be traced to a byte rather than to a guess
    /// about which conversion this code applied.
    /// </summary>
    public static (uint Type, byte[] Bytes)? GetRaw(uint devInst, DevPropKey key) =>
        ReadCore(devInst, key);

    /// <summary>The interface half of <see cref="GetRaw"/>, keyed by path rather than devnode.</summary>
    public static (uint Type, byte[] Bytes)? GetInterfaceRaw(string interfacePath, DevPropKey key)
    {
        uint size = 0;
        // First call sizes the buffer; it is expected to fail with CR_BUFFER_SMALL.
        CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, null, ref size, 0);
        if (size == 0)
            return null;

        var buffer = new byte[size];
        return CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out uint type, buffer, ref size, 0) == CR_SUCCESS
            ? (type, buffer)
            : null;
    }

    /// <summary>Fetches a property's raw bytes, or null if absent or not of the expected type.</summary>
    static byte[]? Read(uint devInst, DevPropKey key, uint expectedType) =>
        ReadCore(devInst, key) is { } property && property.Type == expectedType ? property.Bytes : null;

    static (uint Type, byte[] Bytes)? ReadCore(uint devInst, DevPropKey key)
    {
        if (devInst == 0)
            return null;

        uint size = 0;
        // First call sizes the buffer; it is expected to fail with CR_BUFFER_SMALL.
        CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);
        if (size == 0)
            return null;

        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buffer, ref size, 0) != CR_SUCCESS)
            return null;

        return (type, buffer);
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    // Exported without the W suffix the other property call carries, and it takes no strings.
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    static extern uint CM_Get_DevNode_Property_Keys(
        uint dnDevInst, [Out] DevPropKey[]? propertyKeyArray, ref uint propertyKeyCount, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DevPropKey propertyKey, out uint propertyType,
        [Out] byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    // Exported without a W suffix: it takes no strings.
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    static extern uint CM_Enumerate_Classes(uint ulClassIndex, out Guid classGuid, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_List_SizeW(
        out uint pulLen, ref Guid interfaceClassGuid, string? pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_ListW(
        ref Guid interfaceClassGuid, string? pDeviceID, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_Property_KeysW(
        string pszDeviceInterface, [Out] DevPropKey[]? propertyKeyArray,
        ref uint propertyKeyCount, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_PropertyW(
        string pszDeviceInterface, ref DevPropKey propertyKey, out uint propertyType,
        [Out] byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);
}
