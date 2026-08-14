using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BattTray.Diagnostics;

/// <summary>
/// The BluetoothAPIs.dll calls needed to ask a bonded LE device what its GATT attribute table
/// holds, and the CreateFileW handle they hang off.
/// </summary>
/// <remarks>
/// Hand-rolled interop rather than <c>Windows.Devices.Bluetooth.GenericAttributeProfile</c>,
/// which is the obvious way to read GATT and was measured against on this project already. Two
/// reasons, in order of weight:
/// <list type="bullet">
/// <item>The WinRT projection needs a Windows SDK version pinned into the target framework
/// (<c>net10.0-windows10.0.19041.0</c>), which every project in the solution would inherit for
/// the sake of one diagnostics sweep. <see cref="BattTray.Interop.ConfigManager"/> already
/// records why that trade was refused for enumeration; nothing here changes it.</item>
/// <item>These are ordinary exports of a system DLL, so a single-file publish carries them for
/// free — there is no projection assembly to fail to embed.</item>
/// </list>
/// The earlier rejection of <c>Windows.Gaming.Input</c> was about accuracy rather than
/// dependencies and says nothing either way about this; the argument above is its own.
///
/// Every entry point returns an HRESULT rather than the BOOLEAN
/// <see cref="HidApi"/>'s hid.dll calls return, and sets no last error — the code is the whole
/// answer, which is why the probe prints it rather than a <c>GetLastError</c> beside it.
/// </remarks>
internal static class GattApi
{
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint OPEN_EXISTING = 3;
    const uint GENERIC_READ = 0x80000000;

    public const int S_OK = 0;

    /// <summary>
    /// HRESULT_FROM_WIN32(ERROR_MORE_DATA). Every one of these calls answers a sizing request
    /// with it, so it is the expected result of the first call rather than a failure.
    /// </summary>
    public const int ErrorMoreData = unchecked((int)0x800700EA);

    /// <summary>HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED), which is what a zero-access handle
    /// would fail a value read with if the driver refused one.</summary>
    public const int ErrorAccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// HRESULT_FROM_WIN32(ERROR_INVALID_FUNCTION), which is how a cache read answers when the
    /// stack holds no value for the characteristic. Measured on a bonded, switched-off
    /// controller, and worth naming: "Incorrect function" reads like a bug in the call rather
    /// than the empty cache it actually reports.
    /// </summary>
    public const int ErrorInvalidFunction = unchecked((int)0x80070001);

    /// <summary>Let Windows decide where the value comes from: the cache, or the device.</summary>
    public const uint FlagNone = 0x00000000;

    /// <summary>BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE — a real ATT read over a live link.</summary>
    public const uint FlagForceReadFromDevice = 0x00000004;

    /// <summary>BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_CACHE — whatever the stack last saw.</summary>
    public const uint FlagForceReadFromCache = 0x00000008;

    /// <summary>
    /// Opens one GUID_BLUETOOTHLE_DEVICE_INTERFACE path with dwDesiredAccess = 0, on the same
    /// reasoning as <see cref="HidApi.Open"/>: the service and characteristic tables are
    /// readable through a handle that asks for nothing, and asking for more is what gets
    /// refused on a device something else already holds. The caller reports whatever a value
    /// read makes of that handle rather than assuming it will be enough.
    /// </summary>
    public static SafeFileHandle Open(string interfacePath) =>
        CreateFileW(
            interfacePath,
            dwDesiredAccess: 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            dwFlagsAndAttributes: 0,
            IntPtr.Zero);

    /// <summary>
    /// Opens the same path asking for GENERIC_READ, which is what the documented samples do.
    /// Kept as a second attempt only, so a device that answers through the cheap handle is
    /// never asked for more than it needs to give.
    /// </summary>
    public static SafeFileHandle OpenForRead(string interfacePath) =>
        CreateFileW(
            interfacePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            dwFlagsAndAttributes: 0,
            IntPtr.Zero);

    /// <summary>
    /// Every GATT service the stack holds for the device behind this handle. Sized by a first
    /// call that is expected to come back <see cref="ErrorMoreData"/>; a device with no
    /// services answers S_OK with a count of zero, which is a real answer and not an error.
    /// </summary>
    public static int GetServices(SafeFileHandle device, out BTH_LE_GATT_SERVICE[] services)
    {
        services = [];

        int hr = BluetoothGATTGetServices(device, 0, null, out ushort required, FlagNone);
        if (hr != ErrorMoreData)
            return hr;

        if (required == 0)
            return S_OK;

        var buffer = new BTH_LE_GATT_SERVICE[required];
        hr = BluetoothGATTGetServices(device, required, buffer, out ushort actual, FlagNone);

        // The call reports back how many it filled, which can be fewer than the sizing claimed.
        services = hr == S_OK ? buffer[..Math.Min(actual, required)] : [];
        return hr;
    }

    /// <summary>The characteristics under one service, on the same two-call pattern.</summary>
    public static int GetCharacteristics(
        SafeFileHandle device, ref BTH_LE_GATT_SERVICE service, out BTH_LE_GATT_CHARACTERISTIC[] characteristics)
    {
        characteristics = [];

        int hr = BluetoothGATTGetCharacteristics(device, ref service, 0, null, out ushort required, FlagNone);
        if (hr != ErrorMoreData)
            return hr;

        if (required == 0)
            return S_OK;

        var buffer = new BTH_LE_GATT_CHARACTERISTIC[required];
        hr = BluetoothGATTGetCharacteristics(device, ref service, required, buffer, out ushort actual, FlagNone);

        characteristics = hr == S_OK ? buffer[..Math.Min(actual, required)] : [];
        return hr;
    }

    /// <summary>
    /// One characteristic's value, as bytes, with no interpretation applied.
    /// </summary>
    /// <remarks>
    /// The value struct is a ULONG length followed by a flexible array, so it cannot be
    /// declared as a managed struct and is carried in unmanaged memory that this method owns
    /// end to end. The buffer is zeroed before the read: the length field is what the copy back
    /// is trusted to, and reading it out of whatever was on the heap would be this file's own
    /// bug in the one column the probe exists to be believed on.
    ///
    /// <paramref name="flags"/> decides where the answer comes from, and the caller passes it
    /// deliberately rather than defaulting: a cached value from a device that is switched off
    /// and a live read over an open link are both worth having and must never be confused.
    /// </remarks>
    public static int GetCharacteristicValue(
        SafeFileHandle device, ref BTH_LE_GATT_CHARACTERISTIC characteristic, uint flags, out byte[] value)
    {
        const int HeaderSize = sizeof(uint);

        value = [];

        int hr = BluetoothGATTGetCharacteristicValue(device, ref characteristic, 0, IntPtr.Zero, out ushort required, flags);
        if (hr != ErrorMoreData)
            return hr;

        if (required <= HeaderSize)
            return S_OK;

        IntPtr buffer = Marshal.AllocHGlobal(required);

        try
        {
            Marshal.Copy(new byte[required], 0, buffer, required);

            hr = BluetoothGATTGetCharacteristicValue(device, ref characteristic, required, buffer, out _, flags);
            if (hr != S_OK)
                return hr;

            // Bounded by what was actually allocated as well as by what the header claims: a
            // length longer than the buffer would otherwise copy from past the end of it.
            int length = Math.Min(Marshal.ReadInt32(buffer), required - HeaderSize);
            if (length <= 0)
                return S_OK;

            var bytes = new byte[length];
            Marshal.Copy(buffer + HeaderSize, bytes, 0, length);
            value = bytes;
            return S_OK;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("BluetoothApis.dll", ExactSpelling = true)]
    static extern int BluetoothGATTGetServices(
        SafeFileHandle hDevice,
        ushort servicesBufferCount,
        [Out] BTH_LE_GATT_SERVICE[]? servicesBuffer,
        out ushort servicesBufferActual,
        uint flags);

    [DllImport("BluetoothApis.dll", ExactSpelling = true)]
    static extern int BluetoothGATTGetCharacteristics(
        SafeFileHandle hDevice,
        ref BTH_LE_GATT_SERVICE service,
        ushort characteristicsBufferCount,
        [Out] BTH_LE_GATT_CHARACTERISTIC[]? characteristicsBuffer,
        out ushort characteristicsBufferActual,
        uint flags);

    [DllImport("BluetoothApis.dll", ExactSpelling = true)]
    static extern int BluetoothGATTGetCharacteristicValue(
        SafeFileHandle hDevice,
        ref BTH_LE_GATT_CHARACTERISTIC characteristic,
        uint characteristicValueDataSize,
        IntPtr characteristicValue,
        out ushort characteristicValueSizeRequired,
        uint flags);
}

/// <summary>
/// A GATT UUID: either one of the 16-bit values the Bluetooth SIG assigns, or a full 128-bit
/// vendor one. <see cref="ShortUuid"/> and <see cref="LongUuid"/> deliberately share offset 4 —
/// they are the two arms of a union, and <see cref="IsShortUuid"/> says which is meant. The
/// union sits at 4 rather than 1 because a GUID aligns to 4, which is also why the whole struct
/// is 20 bytes rather than 17.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct BTH_LE_UUID
{
    /// <summary>BOOLEAN, a single byte, not the four-byte Win32 BOOL.</summary>
    [FieldOffset(0)] public byte IsShortUuid;

    [FieldOffset(4)] public ushort ShortUuid;

    [FieldOffset(4)] public Guid LongUuid;
}

/// <summary>One GATT service: what it calls itself, and the attribute handle it sits at.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct BTH_LE_GATT_SERVICE
{
    [FieldOffset(0)] public BTH_LE_UUID ServiceUuid;

    [FieldOffset(20)] public ushort AttributeHandle;
}

/// <summary>
/// One characteristic under a service: its UUID, the handle its value lives at, and the eight
/// BOOLEAN properties saying what may be done with it. <see cref="IsReadable"/> is the one that
/// decides whether the probe asks for a value at all — a notify-only characteristic answers a
/// read with an error, which would be reported as a failure of a question never worth asking.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 36)]
internal struct BTH_LE_GATT_CHARACTERISTIC
{
    [FieldOffset(0)] public ushort ServiceHandle;
    [FieldOffset(4)] public BTH_LE_UUID CharacteristicUuid;
    [FieldOffset(24)] public ushort AttributeHandle;
    [FieldOffset(26)] public ushort CharacteristicValueHandle;
    [FieldOffset(28)] public byte IsBroadcastable;
    [FieldOffset(29)] public byte IsReadable;
    [FieldOffset(30)] public byte IsWritable;
    [FieldOffset(31)] public byte IsWritableWithoutResponse;
    [FieldOffset(32)] public byte IsSignedWritable;
    [FieldOffset(33)] public byte IsNotifiable;
    [FieldOffset(34)] public byte IsIndicatable;
    [FieldOffset(35)] public byte HasExtendedProperties;
}
