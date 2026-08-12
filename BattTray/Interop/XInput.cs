using System.Runtime.InteropServices;

namespace BattTray.Interop;

/// <summary>
/// One answer from <c>XInputGetBatteryInformation</c>: the call's return code, and the two
/// bytes it wrote when that code was <see cref="XInput.ErrorSuccess"/>.
/// </summary>
/// <remarks>
/// The return code is carried rather than collapsed into a null because the two failures
/// mean different things — an empty slot is the ordinary answer three times out of four,
/// and anything else is a fault worth seeing in the dump.
/// </remarks>
internal readonly record struct XInputBattery(uint Result, byte Type, byte Level);

/// <summary>
/// Thin wrapper over the one xinput1_4.dll call that says anything about charge.
/// </summary>
/// <remarks>
/// XInput addresses controllers by slot rather than by identity: there are always exactly
/// four, a slot carries no name, no VID/PID and no serial, and which slot a pad lands in is
/// decided when it connects. What follows from that is the provider's problem; this reads
/// bytes and nothing else.
///
/// xinput1_4.dll ships with Windows 8 and later, so a missing one means something is badly
/// wrong rather than something to design around — but a provider is contractually not
/// allowed to throw, and a load failure would otherwise surface on the first call, on the UI
/// thread. It is latched so that costs one exception rather than four on every poll.
/// </remarks>
internal static class XInput
{
    /// <summary>XInput has a fixed four slots; there is no count to ask it for.</summary>
    public const uint SlotCount = 4;

    public const uint ErrorSuccess = 0x00000000;

    /// <summary>ERROR_DEVICE_NOT_CONNECTED: nothing in that slot, and the usual answer.</summary>
    public const uint ErrorDeviceNotConnected = 0x0000048F;

    // BATTERY_TYPE_* from XInput.h.
    public const byte BatteryTypeDisconnected = 0x00;
    public const byte BatteryTypeWired = 0x01;
    public const byte BatteryTypeAlkaline = 0x02;
    public const byte BatteryTypeNimh = 0x03;
    public const byte BatteryTypeUnknown = 0xFF;

    // BATTERY_LEVEL_* from XInput.h — the entire scale, all four steps of it.
    public const byte BatteryLevelEmpty = 0x00;
    public const byte BatteryLevelLow = 0x01;
    public const byte BatteryLevelMedium = 0x02;
    public const byte BatteryLevelFull = 0x03;

    /// <summary>
    /// BATTERY_DEVTYPE_GAMEPAD. A headset plugged into a pad is a separate subject under
    /// BATTERY_DEVTYPE_HEADSET, and is not asked about: it has its own battery, its own
    /// levels, and no way to be told apart from the pad it hangs off in a list.
    /// </summary>
    const byte BatteryDevTypeGamepad = 0x00;

    /// <summary>Set once the DLL has proved unloadable, so it is only proved once.</summary>
    static bool _unavailable;

    /// <summary>
    /// Reads one slot, or null when xinput1_4.dll could not be called at all — a different
    /// answer from "nothing in that slot", and reported separately so the dump can say which.
    /// </summary>
    public static XInputBattery? Read(uint slot)
    {
        if (_unavailable)
            return null;

        try
        {
            uint result = XInputGetBatteryInformation(slot, BatteryDevTypeGamepad, out var information);

            // A failed call does not write the struct, so the bytes are only passed on when
            // the call succeeded; zeroes here read as DISCONNECTED, which is what a failure
            // means anyway and what every caller does with it.
            return result == ErrorSuccess
                ? new XInputBattery(result, information.BatteryType, information.BatteryLevel)
                : new XInputBattery(result, BatteryTypeDisconnected, BatteryLevelEmpty);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetBatteryInformation(
        uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);
}
