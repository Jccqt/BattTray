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
/// known percentage after a device disconnects, so a headset that was at 15% four days ago
/// would otherwise alert forever. Latches are kept for devices that vanish, because a
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
    /// </remarks>
    const int ReArmMargin = 15;

    readonly Action<string, string> _showAlert = showAlert;

    /// <summary>Device ids that have alerted and not yet re-armed.</summary>
    readonly HashSet<string> _latched = [];

    public void Evaluate(IReadOnlyList<Peripheral> peripherals, AppSettings settings)
    {
        var newlyLow = new List<Peripheral>();

        foreach (var device in peripherals)
        {
            // A cached percentage from a disconnected device says nothing about now.
            if (!device.IsConnected || device.BatteryPercent is not { } percent)
                continue;

            if (device.ChargeState == ChargeState.Charging)
            {
                // Plugged in and climbing: the warning has served its purpose.
                _latched.Remove(device.Id);
                continue;
            }

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
            devices.Select(d => $"{d.Name} is at {d.BatteryPercent}%."));

        _showAlert(title, body);
    }
}
