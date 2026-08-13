using BattTray.Devices;
using BattTray.Settings;

namespace BattTray.Tray;

/// <summary>
/// Raises a balloon tip when a connected device drops to the configured threshold.
/// </summary>
/// <remarks>
/// The interesting part is what it refuses to do. A naive check fires on every poll for as
/// long as the device stays low, which trains the user to dismiss the warning that matters,
/// so each device latches after alerting and only re-arms once it has climbed well clear of
/// the threshold. Stale readings are ignored outright: Windows keeps reporting the last
/// known percentage after a Bluetooth device disconnects, so a headset that was at 15% four
/// days ago would otherwise alert forever. Latches are kept for devices that vanish, because a
/// disconnect followed by a reconnect at the same low level is the same discharge, not a
/// new one.
/// </remarks>
/// <param name="showAlert">
/// Presents a title and body. Injected rather than taking the <see cref="NotifyIcon"/>
/// directly so the latching rules above — the part with all the edge cases — can be
/// exercised without a tray icon or a balloon appearing on someone's desktop.
/// </param>
internal sealed class LowBatteryNotifier(Action<string, string> showAlert)
{
    /// <summary>How far above the threshold a device must climb before it can alert again.</summary>
    /// <remarks>
    /// Measured from the threshold rather than from the level that triggered the alert, so
    /// the re-arm point does not wander with each discharge. Wide enough to sit outside the
    /// coarse buckets that some devices report in, which would otherwise let a single
    /// bucket flip re-arm the alert.
    ///
    /// A device reporting a band rather than a percentage is compared on the stand-in number
    /// its provider supplies, which is what makes this margin that provider's problem: the
    /// numbers have to be spaced so that climbing a band clears it. See the mapping in
    /// XInputGamepadProvider, where four levels have to survive this rule.
    /// </remarks>
    internal const int ReArmMargin = 15;

    readonly Action<string, string> _showAlert = showAlert;

    /// <summary>Device ids that have alerted and not yet re-armed.</summary>
    readonly HashSet<string> _latched = [];

    public void Evaluate(IReadOnlyList<Peripheral> peripherals, AppSettings settings)
    {
        var newlyLow = new List<Peripheral>();

        foreach (var device in peripherals)
        {
            // Never taken: no provider sets Charging, XInput's WIRED having turned out to
            // mean "USB-attached, no battery information" rather than "on a cable". Kept
            // because the rule is the right one for a source that can say it — a charge
            // signal ends the discharge the warning was about.
            //
            // Read before the reading is, because charge arrives with the link and not with
            // the number: a device on a cable carrying a percentage from its last wireless
            // session is charging now whatever that number is about, and the two cases the
            // guard below drops — a stale reading, no reading at all — are exactly the ones a
            // correlated or charge-only source would show up with. Connectedness is required
            // for the same reason it beats charging in the menu row: a charge claim from a
            // device that is no longer attached is a claim about a link that is gone.
            if (device is { IsConnected: true, ChargeState: ChargeState.Charging })
            {
                _latched.Remove(device.Id);
                continue;
            }

            // A leftover percentage says nothing about now, and a device publishing no level
            // has nothing to threshold. The first test is the reading's age and not the link
            // state, which it used to stand in for: what disqualifies a number is that it is
            // old, so a device that is present while its reading is not stays quiet, and one
            // that hands over a current reading on its way out is still worth a warning.
            if (device.IsStale || device.BatteryPercent is not { } percent)
                continue;

            if (percent > settings.LowBatteryThreshold)
            {
                if (percent >= settings.LowBatteryThreshold + ReArmMargin)
                    _latched.Remove(device.Id);

                continue;
            }

            if (_latched.Add(device.Id))
                newlyLow.Add(device);
        }

        // Latching happens whether or not a balloon is shown, so turning notifications
        // back on does not replay every alert the user opted out of.
        if (newlyLow.Count > 0 && settings.LowBatteryNotifications)
            Show(newlyLow);
    }

    void Show(List<Peripheral> devices)
    {
        // Named, always. An unattributed "battery low" is exactly the ambiguity that ruled
        // out putting a level on the tray icon. Balloons replace rather than queue, so
        // several devices going low together become one tip instead of a flicker.
        string title = devices.Count == 1 ? "Battery low" : "Batteries low";
        string body = string.Join(
            Environment.NewLine,
            // A band reads as a state and a percentage as a quantity, so they take different
            // verbs: "is low" against "is at 15%". The alternative — one sentence covering
            // both — produces "is at low", which is the sort of phrasing that makes a reader
            // wonder whether the number went missing.
            devices.Select(d => d.BatteryBand is { } band
                ? $"{d.Name} is {band}."
                : $"{d.Name} is at {d.BatteryPercent}%."));

        _showAlert(title, body);
    }
}
