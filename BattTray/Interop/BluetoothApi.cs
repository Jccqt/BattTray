using System.Runtime.InteropServices;

namespace BattTray.Interop;

/// <summary>A paired Bluetooth radio device as reported by bluetoothapis.dll.</summary>
internal readonly record struct BluetoothDeviceInfo(
    ulong Address,
    string Name,
    uint ClassOfDevice,
    bool IsConnected);

/// <summary>
/// Enumerates paired Bluetooth devices. Unlike the PnP device tree this reports a clean
/// product name and a live connection flag, so it is the source of truth for identity and
/// connectivity; battery is joined in separately from the device properties.
/// </summary>
internal static class BluetoothApi
{
    const int MaxNameLength = 248;

    public static List<BluetoothDeviceInfo> GetPairedDevices()
    {
        var results = new List<BluetoothDeviceInfo>();

        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnUnknown = false,
            fReturnConnected = true,
            // An inquiry would spin up a radio scan taking seconds and cost battery on
            // both ends. We only want devices Windows already knows about.
            fIssueInquiry = false,
            cTimeoutMultiplier = 0,
            hRadio = IntPtr.Zero,
        };

        var info = NewDeviceInfo();
        IntPtr handle = BluetoothFindFirstDevice(ref search, ref info);
        if (handle == IntPtr.Zero)
            return results;

        try
        {
            do
            {
                results.Add(new BluetoothDeviceInfo(
                    info.Address,
                    info.szName?.Trim() ?? string.Empty,
                    info.ulClassofDevice,
                    info.fConnected));

                info = NewDeviceInfo();
            }
            while (BluetoothFindNextDevice(handle, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(handle);
        }

        return results;
    }

    static BLUETOOTH_DEVICE_INFO NewDeviceInfo() =>
        new() { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };

    [StructLayout(LayoutKind.Sequential)]
    struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxNameLength)] public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstDevice(
        ref BLUETOOTH_DEVICE_SEARCH_PARAMS searchParams, ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool BluetoothFindNextDevice(IntPtr find, ref BLUETOOTH_DEVICE_INFO deviceInfo);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool BluetoothFindDeviceClose(IntPtr find);
}
