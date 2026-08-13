using BattTray.Devices;
using BattTray.Tests.Support;
using BattTray.Tray;

namespace BattTray.Tests;

/// <summary>
/// The tray icon's tooltip: which of the five sentences it picks, and how it is cut down
/// when the answer is longer than the shell will take.
/// </summary>
/// <remarks>
/// Worth its own file rather than a corner of the menu-row tests, because the tooltip is not
/// a smaller menu. It is the only content of this app a keyboard can reach — the menu opens
/// on the Menu key but is shown without activation, so keystrokes go on driving the shell's
/// flyout behind it — which makes every character it drops a character nobody navigating by
/// keyboard can get back. The neighbouring tests in PeripheralMonitorTests cover which device
/// the tooltip names; these cover what it then says about it.
/// </remarks>
public class TooltipRenderingTests
{
    /// <summary>The cap in TrayApplicationContext, restated so the boundary cases can do sums.</summary>
    const int MaxTooltipLength = 127;

    /// <summary>
    /// The tooltip for a set of devices, with the lowest picked exactly as
    /// PeripheralMonitor.LowestConnected picks it — stale readings included in the list and
    /// excluded from the contest, since the two have to agree about what counts as a reading.
    /// </summary>
    static string Tooltip(params Peripheral[] devices) =>
        TrayApplicationContext.BuildTooltip(
            devices,
            devices
                .Where(d => d.IsConnected && !d.IsStale && d.BatteryPercent is not null)
                .MinBy(d => d.BatteryPercent));

    [Fact]
    public void NothingPairedSaysSo() =>
        Assert.Equal("BattTray — no devices", TrayApplicationContext.BuildTooltip([], null));

    [Fact]
    public void EverythingOfflineIsCountedRatherThanNamed()
    {
        // Every reading on show is a leftover. Said once here because it is the one thing all
        // the menu rows underneath have in common, and naming one of three would imply the
        // other two were in a different state.
        string tooltip = Tooltip(
            Device.At(80, id: "mouse", connected: false, stale: true),
            Device.At(40, id: "headset", connected: false, stale: true));

        Assert.Equal("BattTray — 2 device(s), none connected", tooltip);
    }

    [Fact]
    public void OneConnectedAndSilentDeviceIsNamed()
    {
        // The complaint this answers is "my controller is right there", so the answer has to
        // be that it is seen and will not say — not a count, and not "none reporting", which
        // reads as nothing being attached at all.
        string tooltip = Tooltip(Device.At(null, id: "Gamepad 1 (XInput)"));

        Assert.Equal("Gamepad 1 (XInput): no battery reported", tooltip);
    }

    [Fact]
    public void SeveralConnectedAndSilentDevicesAreCounted() =>
        // Past one there is no name to give, so the count carries it.
        Assert.Equal(
            "2 devices connected, none reporting a level",
            Tooltip(Device.At(null, id: "pad"), Device.At(null, id: "headset")));

    [Fact]
    public void ASingleReadingIsQuotedAgainstItsDevice() =>
        Assert.Equal("headset: 45%", Tooltip(Device.At(45, id: "headset")));

    [Fact]
    public void TheLowestOfSeveralReadingsIsNamed()
    {
        // Named, not just numbered. An unattributed reading is the same ambiguity that keeps a
        // level off the tray icon in the first place.
        string tooltip = Tooltip(
            Device.At(80, id: "mouse"),
            Device.At(20, id: "headset"),
            Device.At(55, id: "keyboard"));

        Assert.Equal("3 devices — lowest headset: 20%", tooltip);
    }

    [Fact]
    public void ASilentDeviceIsNotCountedAmongTheReadings()
    {
        // Two counts in one sentence and they are different counts: three devices are
        // connected, two of them report. The sentence is about the readings.
        string tooltip = Tooltip(
            Device.At(80, id: "mouse"),
            Device.At(20, id: "headset"),
            Device.At(null, id: "pad"));

        Assert.Equal("2 devices — lowest headset: 20%", tooltip);
    }

    [Fact]
    public void ADisconnectedDeviceCannotBeTheLowest()
    {
        // A cached 5% about a device that is not here must not become the headline, and the
        // one connected reading must still be quoted as a single reading rather than as the
        // lowest of a crowd.
        string tooltip = Tooltip(
            Device.At(60, id: "mouse"),
            Device.At(5, id: "gone", connected: false, stale: true));

        Assert.Equal("mouse: 60%", tooltip);
    }

    [Fact]
    public void APresentDeviceCarryingOnlyALeftoverNumberCannotBeTheLowest()
    {
        // The case connectedness alone could not catch, and the reason the filter here is about
        // the number rather than the link: a pad on a cable holding 5% from its last wireless
        // session is not the most urgent thing on this machine, and quoting it would hide the
        // headset that genuinely is — behind a figure the menu row marks "(stale)" and this
        // line has nowhere to.
        string tooltip = Tooltip(
            Device.At(5, id: "pad", stale: true),
            Device.At(40, id: "headset"));

        Assert.Equal("headset: 40%", tooltip);
    }

    [Fact]
    public void APresentDeviceWithOnlyALeftoverNumberIsNotCalledSilent()
    {
        // Its row a click below reads "87% (stale)", so "no battery reported" here would have
        // the two surfaces contradicting each other about whether a number exists. What the
        // tooltip is short of is a reading about now, and that is what it says.
        string tooltip = Tooltip(Device.At(87, id: "pad", stale: true));

        Assert.Equal("pad: no reading about now", tooltip);
    }

    [Fact]
    public void SeveralPresentDevicesWithNothingCurrentAreCountedLikeAnyOtherSilence() =>
        // Past one device there is no name to hang the distinction on, so the leftover and the
        // device that never reports are counted together. Which is which is on their rows.
        Assert.Equal(
            "2 devices connected, none reporting a level",
            Tooltip(Device.At(87, id: "pad", stale: true), Device.At(null, id: "headset")));

    [Fact]
    public void ABandIsNamedRatherThanShowingItsStandInNumber() =>
        // The number behind a band exists for sorting and thresholding only. It reaches the
        // tooltip through BatteryText or not at all.
        Assert.Equal("pad: medium", Tooltip(Device.Band(percent: 60, name: "medium")));

    [Fact]
    public void AnAnswerThatFitsIsLeftAlone()
    {
        // Exactly at the cap: the boundary the truncation must not take a bite out of.
        string name = new('x', MaxTooltipLength - ": no battery reported".Length);
        string tooltip = Tooltip(Device.At(null, id: name));

        Assert.Equal(MaxTooltipLength, tooltip.Length);
        Assert.DoesNotContain('…', tooltip);
    }

    [Fact]
    public void AnAnswerOneCharacterTooLongIsCutToTheCap()
    {
        string name = new('x', MaxTooltipLength - ": no battery reported".Length + 1);
        string tooltip = Tooltip(Device.At(null, id: name));

        // The ellipsis replaces a character rather than being appended past the limit, which
        // is what keeps the result at the cap instead of one over it — and one over it is the
        // length NotifyIcon.Text raises on.
        Assert.Equal(MaxTooltipLength, tooltip.Length);
        Assert.EndsWith("…", tooltip);
    }

    [Fact]
    public void TheRoomTheOldLimitGaveAwayIsUsed()
    {
        // The regression guard for the fix itself. 63 was the .NET Framework limit rather than
        // the shell's, and this hundred-character answer was arriving cut in half — halving
        // the only thing a keyboard user can read. Anything that quietly restores a smaller
        // cap fails here rather than in a bug report nobody can reproduce.
        string name = new('x', 100 - ": no battery reported".Length);
        string tooltip = Tooltip(Device.At(null, id: name));

        Assert.Equal(100, tooltip.Length);
        Assert.DoesNotContain('…', tooltip);
    }

    [Fact]
    public void TheCapIsWhatTheFrameworkWillAccept()
    {
        // The one failure that would be an exception rather than an ugly string: Refresh sets
        // this on every poll, so a cap above what NotifyIcon takes would throw fifteen seconds
        // after launch and every fifteen seconds after that.
        //
        // This constructs a NotifyIcon, which the rest of the suite is careful not to do. It
        // stays on the right side of that line because the icon is never made visible: nothing
        // is registered with the shell and no window is created, and the Text setter under test
        // is a length check on a stored string.
        using var icon = new NotifyIcon();

        icon.Text = new string('x', MaxTooltipLength);

        Assert.Throws<ArgumentOutOfRangeException>(() => icon.Text = new string('x', MaxTooltipLength + 1));
    }
}
