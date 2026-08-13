using BattTray.Devices;
using BattTray.Settings;
using BattTray.Tests.Support;
using BattTray.Tray;

namespace BattTray.Tests;

/// <summary>
/// The latching rules, which are the whole of what this class is for.
/// </summary>
/// <remarks>
/// Every one of these was previously verified by running the app and waiting for a headset to
/// discharge, which is why several of them — the re-arm point not wandering, a latch surviving
/// a disconnect, a latch being taken while alerts are off — are the kind of thing that could
/// regress for months without anyone noticing. They are cheap here because the alert is
/// injected: no tray icon, no balloon, and no waiting.
/// </remarks>
public class LowBatteryNotifierTests
{
    /// <summary>
    /// A notifier that records what it would have shown, so a test asserts on the alerts
    /// rather than on the private latch set. The latch is only observable through the alerts
    /// it suppresses, which is exactly the property worth testing.
    /// </summary>
    sealed class Recorder
    {
        readonly List<(string Title, string Body)> _alerts = [];

        public LowBatteryNotifier Notifier { get; }

        public Recorder() => Notifier = new LowBatteryNotifier((title, body) => _alerts.Add((title, body)));

        public int Count => _alerts.Count;

        public (string Title, string Body) Last => _alerts[^1];

        public void Feed(AppSettings settings, params Peripheral[] devices) =>
            Notifier.Evaluate(devices, settings);
    }

    static AppSettings Settings(int threshold = 20, bool notifications = true) =>
        new() { LowBatteryThreshold = threshold, LowBatteryNotifications = notifications };

    [Fact]
    public void AlertsOnceWhenADeviceDropsToTheThreshold()
    {
        var recorder = new Recorder();

        recorder.Feed(Settings(), Device.At(15));

        Assert.Equal(1, recorder.Count);
        Assert.Equal("Battery low", recorder.Last.Title);
        Assert.Equal("dev is at 15%.", recorder.Last.Body);
    }

    [Fact]
    public void AlertsAtTheThresholdItself()
    {
        var recorder = new Recorder();

        // "at or below", as the settings dialog labels it.
        recorder.Feed(Settings(threshold: 20), Device.At(20));

        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public void DoesNotAlertAboveTheThreshold()
    {
        var recorder = new Recorder();

        recorder.Feed(Settings(threshold: 20), Device.At(21));

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void DoesNotRepeatWhileTheDeviceStaysLow()
    {
        var recorder = new Recorder();
        var settings = Settings();

        recorder.Feed(settings, Device.At(15));
        recorder.Feed(settings, Device.At(14));
        recorder.Feed(settings, Device.At(9));
        recorder.Feed(settings, Device.At(9));

        // The failure this class exists to prevent: one balloon per poll, which trains the
        // user to dismiss the warning that matters.
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public void ReArmsFromTheThresholdRatherThanFromTheLevelThatFired()
    {
        var recorder = new Recorder();
        var settings = Settings(threshold: 20);

        // Fires well under the threshold, so a margin measured from 5 would re-arm at 20 and
        // a margin measured from the threshold re-arms at 35. The distinction is the whole
        // point: the re-arm point must not wander with each discharge.
        recorder.Feed(settings, Device.At(5));
        Assert.Equal(1, recorder.Count);

        recorder.Feed(settings, Device.At(34));
        recorder.Feed(settings, Device.At(15));
        Assert.Equal(1, recorder.Count);

        recorder.Feed(settings, Device.At(35));
        recorder.Feed(settings, Device.At(15));
        Assert.Equal(2, recorder.Count);
    }

    [Fact]
    public void ReArmsExactlyAtTheMargin()
    {
        var recorder = new Recorder();
        var settings = Settings(threshold: 20);

        recorder.Feed(settings, Device.At(15));
        recorder.Feed(settings, Device.At(20 + LowBatteryNotifier.ReArmMargin));
        recorder.Feed(settings, Device.At(15));

        Assert.Equal(2, recorder.Count);
    }

    [Fact]
    public void ClimbingAboveTheThresholdButNotClearOfItDoesNotReArm()
    {
        var recorder = new Recorder();
        var settings = Settings(threshold: 20);

        recorder.Feed(settings, Device.At(15));
        recorder.Feed(settings, Device.At(20 + LowBatteryNotifier.ReArmMargin - 1));
        recorder.Feed(settings, Device.At(15));

        // A device hovering either side of the threshold — which a coarse source does all the
        // time, since one bucket flip moves it ten points — must not alert on each crossing.
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public void IgnoresStaleReadingsFromDisconnectedDevices()
    {
        var recorder = new Recorder();

        recorder.Feed(Settings(), Device.At(5, connected: false, stale: true));

        // Windows keeps the last known percentage after a disconnect, so a headset that was
        // at 5% last week would otherwise alert on every poll, forever.
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void IgnoresAStaleReadingFromADeviceThatIsStillHere()
    {
        var recorder = new Recorder();

        // The case the old connection test could not catch: a pad on a cable carrying 5% from
        // its last wireless session is not at 5% now, and the cable is the reason.
        recorder.Feed(Settings(), Device.At(5, stale: true));

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void AFreshReadingStillAlertsFromADeviceThatHasDisconnected()
    {
        var recorder = new Recorder();

        // The other half of the split. No source does this today — Bluetooth is why the two
        // used to be one — but a level reported as the link drops is a level about now, and
        // the rule this class applies is about the reading's age and nothing else.
        recorder.Feed(Settings(), Device.At(5, connected: false));

        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public void LatchSurvivesADisconnect()
    {
        var recorder = new Recorder();
        var settings = Settings();

        recorder.Feed(settings, Device.At(15));
        recorder.Feed(settings, Device.At(15, connected: false, stale: true));
        recorder.Feed(settings, Device.At(15));

        // Unplug and replug at the same level is one discharge, not two.
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public void DeviceVanishingEntirelyKeepsItsLatch()
    {
        var recorder = new Recorder();
        var settings = Settings();

        recorder.Feed(settings, Device.At(15));
        recorder.Feed(settings);
        recorder.Feed(settings, Device.At(15));

        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public void LatchesWhileAlertsAreOffSoNothingIsReplayed()
    {
        var recorder = new Recorder();

        recorder.Feed(Settings(notifications: false), Device.At(15));
        Assert.Equal(0, recorder.Count);

        // Turning notifications on is not a request for the backlog. The device is still low,
        // and it has already been latched, so it stays quiet until it recovers and drops again.
        recorder.Feed(Settings(notifications: true), Device.At(15));
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void ChargingClearsTheLatch()
    {
        var recorder = new Recorder();
        var settings = Settings();

        recorder.Feed(settings, Device.At(15));
        recorder.Feed(settings, Device.At(15, charge: ChargeState.Charging));
        recorder.Feed(settings, Device.At(15));

        // No provider sets Charging today. This is the rule waiting for one — a charge signal
        // ends the discharge the warning was about, without needing the 15-point climb.
        Assert.Equal(2, recorder.Count);
    }

    [Fact]
    public void ChargingClearsTheLatchEvenWhenTheReadingIsStale()
    {
        var recorder = new Recorder();
        var settings = Settings();

        recorder.Feed(settings, Device.At(15));
        recorder.Feed(settings, Device.At(15, stale: true, charge: ChargeState.Charging));
        recorder.Feed(settings, Device.At(15));

        // The row this feature exists to make expressible — "15% (stale) · charging" — is a
        // device on a cable whose number came from its last wireless session. The charge is
        // about now even though the number is not, so the discharge is over and the latch
        // has to go: with the age tested first it never reached this rule.
        Assert.Equal(2, recorder.Count);
    }

    [Fact]
    public void ChargingClearsTheLatchWithNoReadingAtAll()
    {
        var recorder = new Recorder();
        var settings = Settings();

        recorder.Feed(settings, Device.At(15));
        recorder.Feed(settings, Device.At(null, charge: ChargeState.Charging));
        recorder.Feed(settings, Device.At(15));

        // The charge-only source: a cable or a dock that can say "charging" and nothing about
        // the level. Nothing obliges a charge signal to arrive with a number — it comes with
        // the link — so the state that ends the discharge is exactly the state in which the
        // reading guard has least to work with. Ordering the two tests the other way loses
        // this device's latch forever: skipped for having no number, it can never re-arm by
        // the 15-point climb either, because it never reports a number to climb.
        Assert.Equal(2, recorder.Count);
    }

    [Fact]
    public void ADisconnectedDeviceClaimingChargeDoesNotClearTheLatch()
    {
        var recorder = new Recorder();
        var settings = Settings();

        recorder.Feed(settings, Device.At(15));
        recorder.Feed(settings, Device.At(15, connected: false, stale: true, charge: ChargeState.Charging));
        recorder.Feed(settings, Device.At(15));

        // The precedence the menu row uses too: charge arrives with the link, so a charge
        // claim outliving the link is not evidence of anything. One discharge, one alert.
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public void ChargingDeviceDoesNotAlert()
    {
        var recorder = new Recorder();

        recorder.Feed(Settings(), Device.At(5, charge: ChargeState.Charging));

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void DevicesLatchIndependently()
    {
        var recorder = new Recorder();
        var settings = Settings();

        recorder.Feed(settings, Device.At(15, id: "headset"));
        recorder.Feed(settings, Device.At(15, id: "headset"), Device.At(15, id: "mouse"));

        Assert.Equal(2, recorder.Count);
        Assert.Equal("mouse is at 15%.", recorder.Last.Body);
    }

    [Fact]
    public void SeveralDevicesGoingLowTogetherProduceOneAlert()
    {
        var recorder = new Recorder();

        recorder.Feed(Settings(), Device.At(15, id: "headset"), Device.At(9, id: "mouse"));

        // Balloons replace rather than queue, so two tips would be a flicker and one of them.
        Assert.Equal(1, recorder.Count);
        Assert.Equal("Batteries low", recorder.Last.Title);
        Assert.Equal($"headset is at 15%.{Environment.NewLine}mouse is at 9%.", recorder.Last.Body);
    }

    [Fact]
    public void ADeviceWithNoReadingIsNotLow()
    {
        var recorder = new Recorder();

        // A connected XInput slot on USB, or a headset with no battery node: present, working,
        // and silent about charge. There is nothing to threshold.
        recorder.Feed(Settings(), Device.At(null));

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void ABandIsNamedRatherThanNumberedInTheAlert()
    {
        var recorder = new Recorder();

        recorder.Feed(Settings(), Device.Band(percent: 5, name: "empty", id: "Gamepad 1 (XInput)"));

        // The stand-in percentage decided that this device is low. It must not appear in the
        // sentence: the device reported one of four levels and never claimed a number.
        Assert.Equal("Gamepad 1 (XInput) is empty.", recorder.Last.Body);
        Assert.DoesNotContain("5", recorder.Last.Body, StringComparison.Ordinal);
    }
}
