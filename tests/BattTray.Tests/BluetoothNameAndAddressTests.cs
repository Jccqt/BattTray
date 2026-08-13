using BattTray.Devices;

namespace BattTray.Tests;

/// <summary>
/// The two string rules the Bluetooth provider runs on: what a node is called, and which radio
/// address a device instance id belongs to.
/// </summary>
public class BluetoothNameAndAddressTests
{
    [Theory]
    [InlineData("soundcore R60i NC Hands-Free AG", "soundcore R60i NC")]
    [InlineData("iPhone Hands-Free HF", "iPhone Hands-Free HF")] // "HF" is not a suffix it strips
    [InlineData("WH-1000XM4 Hands-Free", "WH-1000XM4")]
    [InlineData("WH-1000XM4 Avrcp Transport", "WH-1000XM4")]
    [InlineData("WH-1000XM4 Stereo", "WH-1000XM4")]
    [InlineData("WH-1000XM4 Audio", "WH-1000XM4")]
    [InlineData("WH-1000XM4 AG", "WH-1000XM4")]
    public void ProfileSuffixesAreStripped(string raw, string expected) =>
        Assert.Equal(expected, BluetoothPeripheralProvider.CleanNodeName(raw));

    [Fact]
    public void OnlyATrailingSuffixIsStripped() =>
        // The pattern is anchored, so a device whose actual name contains one of these words
        // keeps it. "Audio Pro" is a real speaker brand.
        Assert.Equal("Audio Pro A10", BluetoothPeripheralProvider.CleanNodeName("Audio Pro A10"));

    [Fact]
    public void OnlyOneSuffixIsStripped() =>
        // Deliberate: the regex is not applied repeatedly. Windows appends one profile suffix,
        // and stripping greedily would eat real names ending in a profile word.
        Assert.Equal("Pad Stereo", BluetoothPeripheralProvider.CleanNodeName("Pad Stereo Audio"));

    [Fact]
    public void ADeviceNamedOnlyAfterAProfileKeepsItsName() =>
        // Stripping would leave an empty string, and a nameless row is worse than an oddly
        // named one.
        Assert.Equal("Stereo", BluetoothPeripheralProvider.CleanNodeName("Stereo"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameWithNothingInItIsNull(string? raw) =>
        Assert.Null(BluetoothPeripheralProvider.CleanNodeName(raw));

    [Fact]
    public void NamesAreTrimmed() =>
        Assert.Equal("Jc&Nics", BluetoothPeripheralProvider.CleanNodeName("  Jc&Nics  "));

    /// <summary>The address the instance-id regex finds, or null where it finds none.</summary>
    static string? AddressIn(string instanceId)
    {
        var match = BluetoothPeripheralProvider.AddressInInstanceId().Match(instanceId);
        return match.Success ? match.Groups[1].Value : null;
    }

    [Theory]
    // Every shape swept from the development machine, spelled out so a regex change has to
    // face the real ids rather than a summary of them.
    [InlineData(@"BTHLE\Dev_e417d8248eb3\8&259b6687&0&e417d8248eb3", "e417d8248eb3")]
    [InlineData(@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&000102b0_PID&0000\8&1c2cf347&0&3409C9FD7C1E_C00000000", "3409C9FD7C1E")]
    [InlineData(@"BTHLEDevice\{7905f431-b5ce-4e99-a40f-c11f19dfd3f2}_5093524e6499\9&3b7951a&0&0019", "5093524e6499")]
    [InlineData(@"BTHLEDEVICE\{0000180f-0000-1000-8000-00805f9b34fb}_DEV_VID&022DC8_PID&301B_REV&0001_E417D8248EB3\9&1a1b0d0f&0&0000", "E417D8248EB3")]
    public void RealInstanceIdsYieldTheirAddress(string instanceId, string expected) =>
        Assert.Equal(expected, AddressIn(instanceId));

    [Fact]
    public void HexInsideAServiceGuidIsNotMistakenForAnAddress() =>
        // The final group of a GUID is also 12 hex digits. Requiring an id separator on both
        // sides is the whole reason the pattern is written the way it is, and this is the case
        // that breaks if someone loosens it.
        Assert.Null(AddressIn(@"BTHLEDevice\{00010203-0405-0607-0809-0A0B0C0D1912}"));

    [Fact]
    public void ARunOfTheWrongLengthIsNotAnAddress()
    {
        Assert.Null(AddressIn(@"BTHLE\Dev_e417d8248eb\8&259b6687"));   // 11
        Assert.Null(AddressIn(@"BTHLE\Dev_e417d8248eb33\8&259b6687")); // 13
    }

    [Fact]
    public void AnIdWithNoAddressYieldsNothing() =>
        Assert.Null(AddressIn(@"USB\VID_2DC8&PID_310A\5&2f4c4a70&0&3"));

    [Fact]
    public void TheAddressMayEndTheId() =>
        // Anchored with $ as well as a separator, because the id often stops at the address.
        Assert.Equal("e417d8248eb3", AddressIn(@"BTHLE\Dev_e417d8248eb3"));
}
