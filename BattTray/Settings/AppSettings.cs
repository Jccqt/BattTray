using System.Text.Json.Serialization;

namespace BattTray.Settings;

/// <summary>
/// User-adjustable behaviour, persisted as JSON.
/// </summary>
/// <remarks>
/// "Start with Windows" is deliberately absent: it lives in the registry Run key, and
/// duplicating it here would create two answers to one question that drift apart the
/// first time the user edits the key or another tool removes it. See
/// <see cref="AutoStart"/>.
/// </remarks>
internal sealed record AppSettings
{
    /// <summary>Thresholds offered in the UI, on 10-step boundaries.</summary>
    /// <remarks>
    /// HFP headsets report battery in coarse buckets rather than true percentages, so a
    /// threshold of 25% would mean "the 3rd bucket" in practice and mislead about its own
    /// precision. Staying on boundaries keeps the setting honest about what it can do.
    /// </remarks>
    public static readonly int[] Thresholds = [10, 20, 30];

    /// <summary>Refresh intervals offered in the UI, in seconds.</summary>
    /// <remarks>
    /// A scan costs about two milliseconds, so even the fastest option is free; the
    /// setting exists because connect/disconnect lag is a responsiveness question, not a
    /// cost one. Event-driven refresh would remove the trade-off entirely.
    /// </remarks>
    public static readonly int[] RefreshIntervals = [15, 30, 60, 300];

    public bool LowBatteryNotifications { get; init; } = true;

    public int LowBatteryThreshold { get; init; } = 20;

    /// <summary>
    /// Hides devices that are only showing a cached reading from an earlier session.
    /// </summary>
    public bool HideDisconnected { get; init; }

    public int RefreshIntervalSeconds { get; init; } = 60;

    /// <summary>Clamps deserialized values onto the offered options, in case the file was edited.</summary>
    public AppSettings Normalized() => this with
    {
        LowBatteryThreshold = Nearest(LowBatteryThreshold, Thresholds),
        RefreshIntervalSeconds = Nearest(RefreshIntervalSeconds, RefreshIntervals),
    };

    static int Nearest(int value, int[] options)
    {
        int best = options[0];
        foreach (int option in options)
        {
            if (Math.Abs(option - value) < Math.Abs(best - value))
                best = option;
        }

        return best;
    }
}

/// <summary>
/// Source-generated serialization, so the settings file costs no reflection machinery at
/// startup — the app's whole appeal is that it idles cheaply.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsContext : JsonSerializerContext;
