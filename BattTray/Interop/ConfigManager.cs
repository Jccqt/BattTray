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
    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;

    /// <summary>Device instance ids beneath a PnP enumerator, e.g. "BTHENUM".</summary>
    public static string[] GetDeviceIds(string enumerator)
    {
        if (CM_Get_Device_ID_List_SizeW(out uint length, enumerator, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS || length == 0)
            return [];

        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(enumerator, buffer, length, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS)
            return [];

        // The list is a double-null-terminated sequence of strings.
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
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

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DevPropKey propertyKey, out uint propertyType,
        [Out] byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);
}
