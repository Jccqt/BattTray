using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BattTray.Diagnostics;

/// <summary>
/// The hid.dll calls needed to read what a device's report descriptor declares, and the
/// CreateFileW handle they hang off.
/// </summary>
/// <remarks>
/// This lives in the diagnostics tool rather than in BattTray/Interop because nothing the app
/// ships can afford it: enumerating the HID interfaces costs 2 ms, but opening all fifteen of
/// them and parsing their caps costs 105 ms on the development machine, and
/// <see cref="BattTray.Devices.IPeripheralProvider"/> is polled on the UI thread against a
/// single-digit-millisecond budget. Moving any of this into a provider means finding a cheaper
/// shape for it first — caching the handles, or narrowing the sweep to known VID/PIDs — not
/// lifting these bindings as they stand.
///
/// Every hid.dll entry point here returns BOOLEAN, a single byte, rather than the four-byte
/// Win32 BOOL a bare <c>bool</c> would marshal as; each is annotated accordingly. Getting that
/// wrong reads three bytes of adjacent stack as part of the result, which fails intermittently
/// and looks like a device problem.
/// </remarks>
internal static class HidApi
{
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint OPEN_EXISTING = 3;

    /// <summary>The one NTSTATUS from hidpi.h worth naming; everything else is printed as hex.</summary>
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    public const int ReportTypeInput = 0;
    public const int ReportTypeOutput = 1;
    public const int ReportTypeFeature = 2;

    /// <summary>
    /// Opens a HID interface for descriptor queries only, with dwDesiredAccess = 0.
    /// </summary>
    /// <remarks>
    /// Zero access is the whole trick. HID devices are routinely held exclusively by whatever
    /// owns them — a vendor configurator, or Windows itself for the boot keyboard — so asking
    /// for GENERIC_READ is refused with ERROR_SHARING_VIOLATION on exactly the devices worth
    /// asking about. A zero-access handle cannot read input reports, but HidD_GetPreparsedData
    /// and the HidP_* parsing calls work through it, and the descriptor is what says whether a
    /// battery is there at all. HidD_GetFeature is the exception: it may come back
    /// ERROR_ACCESS_DENIED on such a handle, which the caller reports rather than hides.
    /// </remarks>
    public static SafeFileHandle Open(string interfacePath) =>
        CreateFileW(
            interfacePath,
            dwDesiredAccess: 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            dwFlagsAndAttributes: 0,
            IntPtr.Zero);

    public static bool GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData) =>
        HidD_GetPreparsedData(device, out preparsedData);

    public static void FreePreparsedData(IntPtr preparsedData)
    {
        if (preparsedData != IntPtr.Zero)
            HidD_FreePreparsedData(preparsedData);
    }

    public static HIDD_ATTRIBUTES? GetAttributes(SafeFileHandle device)
    {
        var attributes = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        return HidD_GetAttributes(device, ref attributes) ? attributes : null;
    }

    /// <summary>
    /// The device's product string, or null when it publishes none. 126 wide characters is the
    /// USB maximum, so a 128-character buffer cannot truncate a legitimate name.
    /// </summary>
    public static string? GetProductString(SafeFileHandle device)
    {
        var buffer = new char[128];

        return HidD_GetProductString(device, buffer, (uint)buffer.Length * sizeof(char))
            && new string(buffer).TrimEnd('\0') is { Length: > 0 } product
            ? product
            : null;
    }

    public static int GetCaps(IntPtr preparsedData, out HIDP_CAPS caps)
    {
        caps = default;
        return HidP_GetCaps(preparsedData, ref caps);
    }

    /// <summary>
    /// The value caps for one report type. <paramref name="count"/> comes from
    /// <see cref="HIDP_CAPS"/>, which is how the array is sized without a probing call.
    /// </summary>
    public static int GetValueCaps(
        IntPtr preparsedData, int reportType, ushort count, out HIDP_VALUE_CAPS[] valueCaps)
    {
        if (count == 0)
        {
            valueCaps = [];
            return HIDP_STATUS_SUCCESS;
        }

        var buffer = new HIDP_VALUE_CAPS[count];
        ushort length = count;
        int status = HidP_GetValueCaps(reportType, buffer, ref length, preparsedData);

        // The call reports back how many it filled, which can be fewer than the caps claimed.
        valueCaps = status == HIDP_STATUS_SUCCESS ? buffer[..Math.Min(length, count)] : [];
        return status;
    }

    public static int GetButtonCaps(
        IntPtr preparsedData, int reportType, ushort count, out HIDP_BUTTON_CAPS[] buttonCaps)
    {
        if (count == 0)
        {
            buttonCaps = [];
            return HIDP_STATUS_SUCCESS;
        }

        var buffer = new HIDP_BUTTON_CAPS[count];
        ushort length = count;
        int status = HidP_GetButtonCaps(reportType, buffer, ref length, preparsedData);

        buttonCaps = status == HIDP_STATUS_SUCCESS ? buffer[..Math.Min(length, count)] : [];
        return status;
    }

    /// <summary>
    /// Reads a feature report. <paramref name="report"/> must be FeatureReportByteLength long
    /// with its first byte set to the report id, which is how the device is told which one to
    /// hand back.
    /// </summary>
    public static bool GetFeature(SafeFileHandle device, byte[] report) =>
        HidD_GetFeature(device, report, (uint)report.Length);

    public static int GetUsageValue(
        IntPtr preparsedData,
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort usage,
        byte[] report,
        out uint value) =>
        HidP_GetUsageValue(
            reportType, usagePage, linkCollection, usage, out value, preparsedData, report, (uint)report.Length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetProductString(
        SafeFileHandle hidDeviceObject, [Out] char[] buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, [In, Out] byte[] reportBuffer, uint reportBufferLength);

    // The HidP_* calls return an NTSTATUS rather than a BOOLEAN, and set no last error.
    [DllImport("hid.dll", ExactSpelling = true)]
    static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

    [DllImport("hid.dll", ExactSpelling = true)]
    static extern int HidP_GetValueCaps(
        int reportType, [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", ExactSpelling = true)]
    static extern int HidP_GetButtonCaps(
        int reportType, [Out] HIDP_BUTTON_CAPS[] buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", ExactSpelling = true)]
    static extern int HidP_GetUsageValue(
        int reportType,
        ushort usagePage,
        ushort linkCollection,
        ushort usage,
        out uint usageValue,
        IntPtr preparsedData,
        [In] byte[] report,
        uint reportLength);
}

/// <summary>The identifiers hid.dll reports for a device, matching the VID_/PID_/REV_ in its path.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HIDD_ATTRIBUTES
{
    public uint Size;
    public ushort VendorID;
    public ushort ProductID;
    public ushort VersionNumber;
}

/// <summary>
/// The summary of a report descriptor: what the top-level collection calls itself, how long
/// each report is, and how many caps of each kind there are to ask for.
/// </summary>
/// <remarks>
/// Laid out explicitly because most of it is a 17-entry reserved block this tool has no name
/// for, and offsets say what a run of dummy fields would only imply. The named fields total 64
/// bytes; <c>Size</c> is set to the 68 measured on this machine, since trailing slack costs
/// nothing and a struct shorter than what HidP_GetCaps writes would corrupt the stack.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 68)]
internal struct HIDP_CAPS
{
    [FieldOffset(0)] public ushort Usage;
    [FieldOffset(2)] public ushort UsagePage;
    [FieldOffset(4)] public ushort InputReportByteLength;
    [FieldOffset(6)] public ushort OutputReportByteLength;
    [FieldOffset(8)] public ushort FeatureReportByteLength;

    // Reserved[17] spans 10-43.
    [FieldOffset(44)] public ushort NumberLinkCollectionNodes;
    [FieldOffset(46)] public ushort NumberInputButtonCaps;
    [FieldOffset(48)] public ushort NumberInputValueCaps;
    [FieldOffset(52)] public ushort NumberOutputButtonCaps;
    [FieldOffset(54)] public ushort NumberOutputValueCaps;
    [FieldOffset(58)] public ushort NumberFeatureButtonCaps;
    [FieldOffset(60)] public ushort NumberFeatureValueCaps;
}

/// <summary>
/// One value-type control: a usage carrying a number, which is the shape a battery percentage
/// takes. <see cref="Usage"/> and <see cref="UsageMin"/> deliberately share offset 56 — they
/// are the two arms of a union, and <see cref="IsRange"/> says which one is meant.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct HIDP_VALUE_CAPS
{
    [FieldOffset(0)] public ushort UsagePage;
    [FieldOffset(2)] public byte ReportID;
    [FieldOffset(3)] public byte IsAlias;
    [FieldOffset(4)] public ushort BitField;
    [FieldOffset(6)] public ushort LinkCollection;
    [FieldOffset(8)] public ushort LinkUsage;
    [FieldOffset(10)] public ushort LinkUsagePage;
    [FieldOffset(12)] public byte IsRange;
    [FieldOffset(15)] public byte IsAbsolute;
    [FieldOffset(18)] public ushort BitSize;
    [FieldOffset(20)] public ushort ReportCount;
    [FieldOffset(32)] public uint UnitsExp;
    [FieldOffset(36)] public uint Units;
    [FieldOffset(40)] public int LogicalMin;
    [FieldOffset(44)] public int LogicalMax;
    [FieldOffset(48)] public int PhysicalMin;
    [FieldOffset(52)] public int PhysicalMax;
    [FieldOffset(56)] public ushort Usage;
    [FieldOffset(56)] public ushort UsageMin;
    [FieldOffset(58)] public ushort UsageMax;
}

/// <summary>
/// One button-type control: a usage carrying a flag. Charging and Discharging on the Battery
/// System page are declared this way, so a probe that only read value caps would miss them.
/// Same 72-byte shape and same union at offset 56 as <see cref="HIDP_VALUE_CAPS"/>; the fields
/// between are value-specific and unnamed here.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct HIDP_BUTTON_CAPS
{
    [FieldOffset(0)] public ushort UsagePage;
    [FieldOffset(2)] public byte ReportID;
    [FieldOffset(3)] public byte IsAlias;
    [FieldOffset(4)] public ushort BitField;
    [FieldOffset(6)] public ushort LinkCollection;
    [FieldOffset(8)] public ushort LinkUsage;
    [FieldOffset(10)] public ushort LinkUsagePage;
    [FieldOffset(12)] public byte IsRange;
    [FieldOffset(15)] public byte IsAbsolute;
    [FieldOffset(56)] public ushort Usage;
    [FieldOffset(56)] public ushort UsageMin;
    [FieldOffset(58)] public ushort UsageMax;
}
