using BattTray.Devices;

namespace BattTray.Tests;

/// <summary>
/// Reading a Bluetooth class-of-device word, which is bit-twiddling against a spec and so is
/// exactly the sort of thing that is right until someone tidies it.
/// </summary>
/// <remarks>
/// The words here are real ones observed on the development machine where noted; the rest are
/// built from the Bluetooth assigned-numbers layout — bits 12..8 major class, bits 7..2 minor.
/// </remarks>
public class BluetoothCategorizationTests
{
    [Theory]
    [InlineData(0x240404u)] // Wearable headset, as an HFP earpiece reports
    [InlineData(0x240418u)] // Headphones
    [InlineData(0x200404u)]
    public void AudioDevicesAreHeadsets(uint classOfDevice) =>
        Assert.Equal(DeviceCategory.Headset, BluetoothPeripheralProvider.Categorize(classOfDevice));

    [Theory]
    [InlineData(0x200204u)] // Smartphone
    [InlineData(0x5A020Cu)]
    public void PhoneMajorClassIsAPhone(uint classOfDevice) =>
        Assert.Equal(DeviceCategory.Phone, BluetoothPeripheralProvider.Categorize(classOfDevice));

    [Fact]
    public void KeyboardBitIsAKeyboard() =>
        // Peripheral major (0x05), pointing/keyboard bits = keyboard.
        Assert.Equal(DeviceCategory.Keyboard, BluetoothPeripheralProvider.Categorize(0x002540u));

    [Fact]
    public void MouseBitIsAMouse() =>
        Assert.Equal(DeviceCategory.Mouse, BluetoothPeripheralProvider.Categorize(0x002580u));

    [Fact]
    public void CombinedKeyboardAndPointingIsAKeyboard() =>
        // Both bits set — a keyboard with a trackpad. Named for the part with the keys, which
        // is what the user calls it.
        Assert.Equal(DeviceCategory.Keyboard, BluetoothPeripheralProvider.Categorize(0x0025C0u));

    [Theory]
    [InlineData(0x002504u)] // Joystick, device type 1
    [InlineData(0x002508u)] // Gamepad, device type 2
    public void GamepadMinorTypesAreGamepads(uint classOfDevice) =>
        // The device type is bits 5..2 of the low byte, so each value here is its number
        // shifted up by two. Written out because these were off by one slot — 0x00250C was
        // read as a joystick and matched, while the joystick above matched nothing and came
        // back Unknown.
        Assert.Equal(DeviceCategory.Gamepad, BluetoothPeripheralProvider.Categorize(classOfDevice));

    [Fact]
    public void ARemoteControlIsNotAGamepad() =>
        // Device type 3, which sits next to the gamepad and is not one. There is no category
        // for it, and Unknown says that rather than filing a TV remote under gamepads.
        Assert.Equal(DeviceCategory.Unknown, BluetoothPeripheralProvider.Categorize(0x00250Cu));

    [Theory]
    [InlineData(0x002514u)] // Digitizer tablet, device type 5
    [InlineData(0x00251Cu)] // Digital pen, device type 7
    public void DigitizerAndPenMinorTypesArePens(uint classOfDevice) =>
        Assert.Equal(DeviceCategory.Pen, BluetoothPeripheralProvider.Categorize(classOfDevice));

    [Fact]
    public void TheSpecificMinorTypeBeatsThePointingBits() =>
        // A gamepad that also flags itself as a pointing device is a gamepad. The order these
        // are checked in is load-bearing, and nothing else records it.
        Assert.Equal(DeviceCategory.Gamepad, BluetoothPeripheralProvider.Categorize(0x002588u));

    [Fact]
    public void AZeroClassIsUnknownRatherThanAnythingElse() =>
        // The hole worth pinning: iPhones and anything bonded through the LE path report
        // 0x000000, which is why phone detection cannot rest on this function alone. If this
        // ever starts returning Phone, the service-class fallback has been made redundant —
        // and if it starts returning something else, phones start appearing in the menu.
        Assert.Equal(DeviceCategory.Unknown, BluetoothPeripheralProvider.Categorize(0x000000u));

    [Fact]
    public void AnUnrecognisedMajorClassIsUnknown() =>
        // Imaging (0x06) — a printer is not a peripheral this app has an opinion about.
        Assert.Equal(DeviceCategory.Unknown, BluetoothPeripheralProvider.Categorize(0x000640u));

    [Fact]
    public void ServiceClassBitsAboveTheMajorFieldAreIgnored()
    {
        // The top 11 bits are service classes and vary per device; two headsets advertising
        // different services must categorise the same.
        var bare = BluetoothPeripheralProvider.Categorize(0x000404u);
        var decorated = BluetoothPeripheralProvider.Categorize(0xFFE00000u | 0x000404u);

        Assert.Equal(bare, decorated);
        Assert.Equal(DeviceCategory.Headset, bare);
    }
}
